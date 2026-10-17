using System.Collections.Immutable;
using SqlLsp.Core;

namespace SqlLsp.Core.Tests;

public class ProcedureAnalysisTests
{
    private static readonly Uri Uri = new("file:///project/modules.sql");
    private static async Task<(DocumentSnapshot Document, SqlNavigationIndex Index)> Parse(string sql, ResolutionOptions? options = null)
    {
        var document = new DocumentSnapshot(Uri, null, SqlSourceKind.Workspace,
            await new SqlParser(new(), options).ParseAsync(new SourceText(sql), CancellationToken.None));
        Assert.Empty(document.Parse.Diagnostics);
        return (document, new([document], _ => "project", options ?? new()));
    }
    private static SqlProcedureReport Analyze(SqlNavigationIndex index, DocumentSnapshot document, string name = "dbo.P", int depth = 2, int modules = 20) =>
        index.AnalyzeProcedure(Uri, document.Parse.Source.PositionAt(document.Parse.Source.Text.IndexOf(name, StringComparison.Ordinal)), CancellationToken.None, depth, modules);
    private const string Tables = "\nGO\nCREATE TABLE dbo.A(id int, state varchar(2));\nGO\nCREATE TABLE dbo.B(id int, state varchar(2));\nGO\nCREATE TABLE audit.A(id int, state varchar(2));";

    [Fact]
    public async Task Every_supported_dml_operation_has_verified_identity_and_exact_source_span()
    {
        var (document, index) = await Parse("CREATE PROC dbo.P @state varchar(2) AS BEGIN\n" +
            "SELECT a.id FROM dbo.A a JOIN audit.A other ON other.id=a.id WHERE a.state=@state;\n" +
            "INSERT dbo.B(id) SELECT id FROM dbo.A;\n" +
            "UPDATE b SET state=@state FROM dbo.B b JOIN dbo.A a ON a.id=b.id;\n" +
            "DELETE b FROM dbo.B b WHERE b.id=1;\n" +
            "TRUNCATE TABLE dbo.B; END;" + Tables);
        var report = Analyze(index, document);
        Assert.Equal(ResolutionStatus.Resolved, report.Status);
        var module = Assert.Single(report.Modules);
        Assert.True(module.BodyAvailable);
        Assert.Empty(module.Issues);
        Assert.Equal("@state", Assert.Single(module.Parameters).Name);
        Assert.Contains(module.Accesses, a => a.Operation == SqlAccessOperation.Read && a.Target?.Identity?.Schema == "audit");
        Assert.Equal("B", Assert.Single(module.Accesses.Where(a => a.Operation == SqlAccessOperation.Update)).Target!.Identity!.Name);
        Assert.Equal("B", Assert.Single(module.Accesses.Where(a => a.Operation == SqlAccessOperation.Delete)).Target!.Identity!.Name);
        Assert.Equal("B", Assert.Single(module.Accesses.Where(a => a.Operation == SqlAccessOperation.Insert)).Target!.Identity!.Name);
        Assert.Single(module.Accesses.Where(a => a.Operation == SqlAccessOperation.Truncate));
        Assert.All(module.Accesses, a =>
        {
            Assert.Equal(ResolutionStatus.Resolved, a.Status);
            Assert.NotNull(a.Target?.Identity);
            var text = document.Parse.Source.Text[document.Parse.Source.OffsetAt(a.Location.Range.Start)..document.Parse.Source.OffsetAt(a.Location.Range.End)];
            Assert.Contains(text, new[] { "dbo.A", "audit.A", "dbo.B", "b" });
        });
    }

    [Fact]
    public async Task Merge_reports_possible_actions_and_output_destination_without_claiming_execution()
    {
        var (document, index) = await Parse("CREATE PROC dbo.P AS MERGE dbo.B AS t USING dbo.A AS s ON t.id=s.id " +
            "WHEN MATCHED AND s.id>0 THEN UPDATE SET state=s.state " +
            "WHEN MATCHED AND s.id<0 THEN DELETE WHEN NOT MATCHED THEN INSERT(id,state) VALUES(s.id,s.state) " +
            "OUTPUT inserted.id INTO audit.A(id);" + Tables);
        var module = Assert.Single(Analyze(index, document).Modules);
        Assert.Empty(module.Issues);
        Assert.Equal(3, module.Accesses.Count(a => a.Target?.Identity?.Name == "B"));
        Assert.Equal(2, module.Accesses.Count(a => a.Operation == SqlAccessOperation.Insert));
        Assert.Contains(module.Accesses, a => a.Operation == SqlAccessOperation.Insert && a.Target?.Identity?.Schema == "audit");
        Assert.Contains(module.Accesses, a => a.Operation == SqlAccessOperation.Read && a.Target?.Identity?.Name == "A" && a.Target.Identity.Schema == "dbo");
    }

    [Fact]
    public async Task Temp_tables_table_variables_and_select_into_stay_local_and_insert_exec_preserves_call()
    {
        var (document, index) = await Parse("CREATE PROC dbo.P AS BEGIN\nCREATE TABLE #created(id int);\n" +
            "DECLARE @rows TABLE(id int);\nSELECT id INTO #selected FROM dbo.A;\nINSERT @rows SELECT id FROM #selected;\n" +
            "INSERT #created EXEC dbo.Q; SELECT * FROM @rows r; DROP TABLE #created; END;\nGO\nCREATE PROC dbo.Q AS SELECT 1;" + Tables);
        var report = Analyze(index, document);
        var module = report.Modules[0];
        Assert.Empty(module.Issues);
        var locals = module.Accesses.Where(a => a.Local != null).ToArray();
        Assert.True(locals.Length >= 7);
        Assert.All(locals, a => { Assert.Equal(ResolutionStatus.Resolved, a.Status); Assert.Null(a.Target!.Identity); });
        Assert.Contains(locals, a => a.Operation == SqlAccessOperation.Create && a.Local!.Name == "#selected");
        Assert.Contains(locals, a => a.Operation == SqlAccessOperation.Insert && a.Local!.Name == "@rows");
        Assert.Equal("Q", Assert.Single(module.Calls).Target!.Identity!.Name);
        Assert.Equal(2, report.Modules.Length);
    }

    [Fact]
    public async Task Cte_reads_include_underlying_relations_but_cte_writes_remain_explicitly_unknown()
    {
        var (document, index) = await Parse("CREATE PROC dbo.P AS BEGIN " +
            "WITH x AS (SELECT id,state FROM dbo.A) SELECT * FROM x; " +
            "WITH x AS (SELECT id,state FROM dbo.B) UPDATE x SET state='OH'; END;" + Tables);
        var module = Assert.Single(Analyze(index, document).Modules);
        Assert.Contains(module.Accesses, a => a.Operation == SqlAccessOperation.Read && a.Target?.Identity?.Name == "A");
        Assert.Contains(module.Accesses, a => a.Operation == SqlAccessOperation.Read && a.Local?.Kind == SqlObjectKind.Cte);
        Assert.Equal(ResolutionStatus.Unsupported, Assert.Single(module.Accesses.Where(a => a.Operation == SqlAccessOperation.Update)).Status);
        Assert.Contains(module.Issues, i => i.Code == "relation-unsupported");
    }

    [Fact]
    public async Task Views_calls_cycles_and_limits_preserve_direct_effects_in_separate_modules()
    {
        var (document, index) = await Parse("CREATE PROC dbo.P AS BEGIN SELECT * FROM dbo.V; EXEC dbo.Q; END;\nGO\n" +
            "CREATE PROC dbo.Q AS BEGIN UPDATE dbo.B SET state='OH'; EXEC dbo.P; END;\nGO\n" +
            "CREATE VIEW dbo.V AS SELECT * FROM dbo.A;" + Tables);
        var report = Analyze(index, document);
        Assert.Equal(3, report.Modules.Length);
        Assert.Empty(report.ExpansionIssues);
        Assert.DoesNotContain(report.Modules[0].Accesses, a => a.Operation == SqlAccessOperation.Update);
        Assert.Contains(report.Modules.Single(m => m.Identity.Name == "Q").Accesses, a => a.Operation == SqlAccessOperation.Update);
        Assert.Contains(report.Modules.Single(m => m.Identity.Name == "V").Accesses, a => a.Target?.Identity?.Name == "A");
        var limited = Analyze(index, document, depth: 0);
        Assert.Single(limited.Modules);
        Assert.Equal(2, limited.ExpansionIssues.Length);
        Assert.Single(Analyze(index, document, modules: 1).Modules);
    }

    [Fact]
    public async Task Noise_is_excluded_and_dynamic_remote_ambiguous_missing_and_opaque_sources_are_reported()
    {
        var (document, index) = await Parse("CREATE PROC dbo.P AS BEGIN\n-- UPDATE dbo.A SET id=1;\n" +
            "SELECT N'DELETE dbo.B'; EXEC(N'UPDATE dbo.A SET id=2'); EXEC dbo.Missing; " +
            "SELECT * FROM Missing; SELECT * FROM A; SELECT * FROM remote.Db.dbo.A; " +
            "SELECT * FROM OPENQUERY(remote, 'SELECT * FROM A'); END;" + Tables);
        var module = Assert.Single(Analyze(index, document).Modules);
        Assert.DoesNotContain(module.Accesses, a => a.Operation != SqlAccessOperation.Read);
        Assert.Contains(module.Issues, i => i.Code == "call-unsupported");
        Assert.Contains(module.Issues, i => i.Code == "call-unresolved");
        Assert.Contains(module.Issues, i => i.Code == "relation-ambiguous");
        Assert.Contains(module.Issues, i => i.Code == "relation-unresolved");
        Assert.Contains(module.Issues, i => i.Code == "relation-unsupported");
        Assert.Contains(module.Issues, i => i.Code == "opaque-relation");
    }

    [Fact]
    public async Task Changed_revision_invalid_positions_duplicates_and_cancellation_never_silently_select()
    {
        var (document, index) = await Parse("CREATE PROC dbo.P AS SELECT 1;");
        var revision = Assert.Single(Analyze(index, document).Modules).Revision;
        Assert.Equal(ResolutionStatus.Resolved, index.AnalyzeProcedure(Uri, new(0, 16), CancellationToken.None, expectedRevision: revision).Status);
        Assert.Equal(ResolutionStatus.Unresolved, index.AnalyzeProcedure(Uri, new(0, 16), CancellationToken.None, expectedRevision: "stale").Status);
        Assert.Equal(ResolutionStatus.Unsupported, index.AnalyzeProcedure(Uri, new(-1, 0), CancellationToken.None).Status);
        var (_, duplicate) = await Parse("CREATE PROC dbo.P AS SELECT 1;\nGO\nCREATE PROC dbo.P AS SELECT 2;");
        Assert.Equal(ResolutionStatus.Ambiguous, duplicate.AnalyzeProcedure(Uri, new(0, 16), CancellationToken.None).Status);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => AnalyzeCancelled());
        void AnalyzeCancelled() => index.AnalyzeProcedure(Uri, new(0, 16), cancellation.Token);
    }

    [Fact]
    public async Task Utf16_crlf_ranges_and_configured_case_rules_are_preserved()
    {
        var (document, index) = await Parse("-- 😀 中文\r\nCREATE PROC dbo.P AS SELECT * FROM [dbo].[a];" + Tables, new(CaseSensitive: false));
        var module = Assert.Single(Analyze(index, document).Modules);
        Assert.Equal(1, module.Definition.Range.Start.Line);
        var access = Assert.Single(module.Accesses);
        Assert.Equal(ResolutionStatus.Resolved, access.Status);
        Assert.Equal("A", access.Target!.Identity!.Name);
        var split = index.AnalyzeProcedure(Uri, new(0, 4), CancellationToken.None);
        Assert.Equal(ResolutionStatus.Unsupported, split.Status);
    }

    [Fact]
    public async Task Malformed_module_is_not_selected_and_parse_coverage_remains_visible()
    {
        var document = new DocumentSnapshot(Uri, null, SqlSourceKind.Workspace,
            await new SqlParser(new()).ParseAsync(new SourceText("CREATE PROC dbo.P AS SELECT FROM;"), CancellationToken.None));
        var index = new SqlNavigationIndex([document], _ => "project", new());
        var report = index.AnalyzeProcedure(Uri, new(0, 16), CancellationToken.None);
        Assert.Empty(report.Modules);
        Assert.Contains(report.ExpansionIssues, i => i.Code == "parse-error");
        Assert.NotEmpty(index.SourceParseIssues(CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_footprint_has_visible_fact_limit_instead_of_a_false_complete_report()
    {
        var (document, index) = await Parse("CREATE PROC dbo.P AS BEGIN " + string.Concat(Enumerable.Repeat("SELECT * FROM dbo.A;", 1005)) + " END;" + Tables);
        var module = Assert.Single(Analyze(index, document).Modules);
        Assert.Equal(1000, module.Accesses.Length);
        Assert.True(module.Truncated);
        Assert.Contains(module.Issues, i => i.Code == "fact-limit");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1005)]
    public async Task Failed_analysis_scopes_parse_issues_before_applying_the_fact_limit(int selectedErrors)
    {
        var parser = new SqlParser(new());
        var noise = new DocumentSnapshot(new("file:///project/a.sql"), null, SqlSourceKind.Workspace,
            await parser.ParseAsync(new SourceText(string.Concat(Enumerable.Repeat("SELECT FROM;\nGO\n", 1100))), CancellationToken.None));
        var selected = new DocumentSnapshot(new("file:///project/z.sql"), null, SqlSourceKind.Workspace,
            await parser.ParseAsync(new SourceText(string.Concat(Enumerable.Repeat("SELECT FROM;\nGO\n", selectedErrors))), CancellationToken.None));
        Assert.True(noise.Parse.Diagnostics.Length > 1000);
        var index = new SqlNavigationIndex([noise, selected], _ => "project", new());
        var report = index.AnalyzeProcedure(selected.Uri, new(0, 0), CancellationToken.None);
        Assert.Empty(report.Modules);
        var errors = report.ExpansionIssues.Where(i => i.Code == "parse-error").ToArray();
        Assert.Equal(Math.Min(1000, selected.Parse.Diagnostics.Length), errors.Length);
        Assert.All(errors, issue => Assert.Equal(selected.Uri, issue.Location!.Uri));
        Assert.Equal(selected.Parse.Diagnostics.Length > 1000, report.ExpansionIssues.Any(i => i.Code == "parse-issue-limit"));
        // All details remain recoverable from the explicitly scoped discovery page.
        Assert.Equal(selected.Parse.Diagnostics.Length, index.ParseIssuePage(selected.Uri, 0, int.MaxValue, CancellationToken.None).Count());
    }

    [Fact]
    public async Task Duplicate_local_creations_do_not_attach_to_an_earlier_declaration_on_the_same_line()
    {
        var (document, index) = await Parse("CREATE PROC dbo.P AS BEGIN CREATE TABLE #x(id int); CREATE TABLE #x(id int); END;");
        var module = Assert.Single(Analyze(index, document).Modules);
        var creates = module.Accesses.Where(a => a.Operation == SqlAccessOperation.Create).ToArray();
        Assert.Equal(2, creates.Length);
        Assert.Equal(ResolutionStatus.Resolved, creates[0].Status);
        Assert.Equal(ResolutionStatus.Unsupported, creates[1].Status);
        Assert.Contains(module.Issues, i => i.Code == "relation-unsupported");
    }
}
