using System.Collections.Immutable;

namespace SqlLsp.Core.Tests;

public class NavigationTests
{
    private static readonly Uri DeclUri = new("file:///workspace/definitions.sql");
    private static readonly Uri UseUri = new("file:///workspace/uses.sql");
    private static async Task<DocumentSnapshot> Document(Uri uri, string sql, ResolutionOptions? options = null) =>
        new(uri, null, SqlSourceKind.Workspace, await new SqlParser(new(), options).ParseAsync(new SourceText(sql), CancellationToken.None));
    private static SqlNavigationIndex Index(IEnumerable<DocumentSnapshot> docs, ResolutionOptions? options = null, Func<Uri, string>? context = null) =>
        new(docs.ToImmutableArray(), context ?? (_ => "project"), options ?? new());
    private static TextPosition At(DocumentSnapshot doc, string text, int occurrence = 0)
    {
        var offset = -1;
        for (var i = 0; i <= occurrence; i++) offset = doc.Parse.Source.Text.IndexOf(text, offset + 1, StringComparison.Ordinal);
        Assert.True(offset >= 0);
        return doc.Parse.Source.PositionAt(offset);
    }

    [Fact]
    public async Task Cross_file_procedure_definition_and_references_have_precise_locations_and_provenance()
    {
        var declared = await Document(DeclUri, "CREATE PROCEDURE dbo.ProcessPayroll AS SELECT 1;");
        var used = await Document(UseUri, "-- EXEC dbo.ProcessPayroll\nSELECT 'EXEC dbo.ProcessPayroll';\nEXEC dbo.ProcessPayroll;\nEXEC dbo.ProcessPayroll;");
        var index = Index([declared, used]);
        var reference = Assert.Single(used.Parse.Semantics.References.Where(r => r.Range.Start.Line == 2));
        var result = index.Resolve(used, reference);
        Assert.Equal(ResolutionStatus.Resolved, result.Status);
        var target = Assert.Single(result.Candidates);
        Assert.Equal(DeclUri, target.Uri);
        Assert.Equal(new TextPosition(0, 17), target.Range.Start);
        Assert.Equal("project", target.Identity!.SourceContextId);
        Assert.Equal("dbo", target.Identity.Schema);
        Assert.Equal("Workspace AST", target.Provenance);
        var references = index.References(DeclUri, At(declared, "dbo.ProcessPayroll"), false, CancellationToken.None);
        Assert.Equal(new[] { 2, 3 }, references.Select(r => r.Range.Start.Line));
        Assert.Equal(3, index.References(UseUri, At(used, "dbo.ProcessPayroll", 2), true, CancellationToken.None).Length);
    }

    [Theory]
    [InlineData("CREATE TABLE [schema.dot].[a]]b] (id int);", "SELECT * FROM [schema.dot].[a]]b];", "schema.dot", "a]b")]
    [InlineData("CREATE TABLE \"schema.dot\".\"a\"\"b\" (id int);", "SELECT * FROM \"schema.dot\".\"a\"\"b\";", "schema.dot", "a\"b")]
    public async Task Quoted_identifier_components_are_decoded_without_splitting_embedded_dots(string ddl, string sql, string schema, string name)
    {
        var declared = await Document(DeclUri, ddl);
        var used = await Document(UseUri, sql);
        var index = Index([declared, used]);
        Assert.Equal(schema, Assert.Single(index.Symbols).Identity.Schema);
        Assert.Equal(name, index.Symbols[0].Identity.Name);
        Assert.Equal(ResolutionStatus.Resolved, index.Resolve(used, Assert.Single(used.Parse.Semantics.References)).Status);
    }

    [Fact]
    public async Task Schema_collisions_are_ambiguous_qualified_names_and_explicit_defaults_resolve()
    {
        var declared = await Document(DeclUri, "CREATE TABLE dbo.Employee (id int); CREATE TABLE sales.Employee (id int);");
        var used = await Document(UseUri, "SELECT * FROM Employee; SELECT * FROM sales.Employee;");
        var index = Index([declared, used]);
        var ambiguous = index.Resolve(used, used.Parse.Semantics.References[0]);
        Assert.Equal(ResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(2, ambiguous.Candidates.Length);
        Assert.Empty(index.References(UseUri, At(used, "Employee"), false, CancellationToken.None));
        Assert.Equal("sales", index.Resolve(used, used.Parse.Semantics.References[1]).Candidates[0].Identity!.Schema);
        Assert.Equal("dbo", Index([declared, used], new(DefaultSchema: "dbo")).Resolve(used, used.Parse.Semantics.References[0]).Candidates[0].Identity!.Schema);
    }

    [Fact]
    public async Task Database_USE_and_source_context_never_merge_unmapped_objects()
    {
        var declared = await Document(DeclUri, "USE Payroll;\nGO\nCREATE TABLE dbo.Employee (id int);\nGO\nUSE Other;\nGO\nCREATE TABLE dbo.Employee (id int);");
        var used = await Document(UseUri, "USE Payroll; SELECT * FROM dbo.Employee; SELECT * FROM Other.dbo.Employee; SELECT * FROM Payroll..Employee; SELECT * FROM remote.Payroll.dbo.Employee;");
        var index = Index([declared, used]);
        Assert.Equal("Payroll", index.Resolve(used, used.Parse.Semantics.References[0]).Candidates[0].Identity!.Database);
        Assert.Equal("Other", index.Resolve(used, used.Parse.Semantics.References[1]).Candidates[0].Identity!.Database);
        Assert.Equal("Payroll", index.Resolve(used, used.Parse.Semantics.References[2]).Candidates[0].Identity!.Database);
        Assert.Equal(ResolutionStatus.Unsupported, index.Resolve(used, used.Parse.Semantics.References[3]).Status);
        Assert.Equal(ResolutionStatus.Unresolved, Index([declared, used], context: u => u == DeclUri ? "other-project" : "project").Resolve(used, used.Parse.Semantics.References[0]).Status);
        var unknownDb = await Document(UseUri, "SELECT * FROM dbo.Employee;");
        Assert.Equal(ResolutionStatus.Unresolved, Index([declared, unknownDb]).Resolve(unknownDb, unknownDb.Parse.Semantics.References[0]).Status);
    }

    [Fact]
    public async Task Identifier_case_is_explicit_and_case_insensitive_collisions_remain_ambiguous()
    {
        var declared = await Document(DeclUri, "CREATE TABLE dbo.Employee (id int);");
        var used = await Document(UseUri, "SELECT * FROM dbo.employee;");
        Assert.Equal(ResolutionStatus.Unresolved, Index([declared, used]).Resolve(used, used.Parse.Semantics.References[0]).Status);
        Assert.Equal(ResolutionStatus.Resolved, Index([declared, used], new(CaseSensitive: false)).Resolve(used, used.Parse.Semantics.References[0]).Status);
        var collision = await Document(new Uri("file:///workspace/other.sql"), "CREATE TABLE dbo.employee (id int);");
        Assert.Equal(ResolutionStatus.Ambiguous, Index([declared, used, collision], new(CaseSensitive: false)).Resolve(used, used.Parse.Semantics.References[0]).Status);
    }

    [Fact]
    public async Task Cte_shadowing_aliases_and_statement_boundaries_do_not_pollute_global_references()
    {
        var declared = await Document(DeclUri, "CREATE TABLE dbo.Employee (id int);");
        var used = await Document(UseUri, "WITH Employee AS (SELECT 1 AS id) SELECT e.id FROM Employee e;\nSELECT * FROM Employee;");
        var index = Index([declared, used]);
        var local = index.Definition(UseUri, At(used, "e.id"), CancellationToken.None);
        Assert.Equal(ResolutionStatus.Resolved, local.Status);
        Assert.Equal(UseUri, local.Candidates[0].Uri);
        Assert.Equal(new TextPosition(0, 5), local.Candidates[0].Range.Start);
        Assert.Null(local.Candidates[0].Identity);
        var globalUses = index.References(DeclUri, At(declared, "dbo.Employee"), false, CancellationToken.None);
        Assert.Single(globalUses);
        Assert.Equal(1, globalUses[0].Range.Start.Line);
    }

    [Fact]
    public async Task Aliases_are_scoped_to_queries_including_nested_and_correlated_subqueries()
    {
        var declared = await Document(DeclUri, "CREATE TABLE dbo.Real (id int); CREATE TABLE dbo.Other (id int); CREATE TABLE dbo.e (id int);");
        var used = await Document(UseUri, "SELECT e.id, (SELECT e.id FROM dbo.Other e) FROM dbo.Real e;\nSELECT e.id;\nSELECT e.id FROM dbo.Real e WHERE EXISTS (SELECT 1 FROM dbo.Other o WHERE o.id=e.id);");
        var index = Index([declared, used]);
        Assert.Equal("Real", index.Definition(UseUri, At(used, "e.id"), CancellationToken.None).Candidates[0].Identity!.Name);
        Assert.Equal("Other", index.Definition(UseUri, At(used, "e.id", 1), CancellationToken.None).Candidates[0].Identity!.Name);
        Assert.Equal(ResolutionStatus.Unsupported, index.Definition(UseUri, At(used, "e.id", 2), CancellationToken.None).Status);
        Assert.Equal("Real", index.Definition(UseUri, At(used, "e.id", 4), CancellationToken.None).Candidates[0].Identity!.Name);
    }

    [Fact]
    public async Task Temporary_tables_and_variables_are_local_and_never_global_objects()
    {
        var used = await Document(UseUri, "DECLARE @value int=1; SELECT @value; CREATE TABLE #Employee (id int); SELECT t.id FROM #Employee t;\nGO\nSELECT @value; SELECT * FROM #Employee;");
        var index = Index([used]);
        Assert.Empty(index.Symbols);
        Assert.Equal(UseUri, index.Definition(UseUri, At(used, "@value", 1), CancellationToken.None).Candidates[0].Uri);
        Assert.Equal(ResolutionStatus.Unsupported, index.Definition(UseUri, At(used, "@value", 2), CancellationToken.None).Status);
        Assert.Equal(SqlObjectKind.TempTable, used.Parse.Semantics.References.First(r => r.Name?.Name == "#Employee").LocalTarget!.Kind);
        Assert.Equal(ResolutionStatus.Unsupported, index.Resolve(used, used.Parse.Semantics.References.Last()).Status);
    }

    [Fact]
    public async Task Stored_module_parameters_do_not_capture_batch_variables_or_other_modules()
    {
        var used = await Document(UseUri, "DECLARE @outer int=1;\nGO\nCREATE PROC dbo.a @p int AS BEGIN SELECT @p; SELECT @outer; END;\nGO\nCREATE PROC dbo.b AS SELECT @p;");
        var refs = used.Parse.Semantics.References.Where(r => r.Kind == SqlReferenceKind.Variable).ToArray();
        Assert.Equal(SqlObjectKind.Parameter, refs[0].LocalTarget!.Kind);
        Assert.Null(refs[1].LocalTarget);
        Assert.Null(refs[2].LocalTarget);
    }

    [Fact]
    public async Task Static_functions_are_kind_filtered_and_dynamic_EXEC_is_explicitly_unsupported()
    {
        var declared = await Document(DeclUri, "CREATE FUNCTION dbo.Increment(@n int) RETURNS int AS BEGIN RETURN @n+1; END;\nGO\nCREATE FUNCTION dbo.Rows() RETURNS TABLE AS RETURN SELECT 1 AS id;\nGO\nCREATE PROCEDURE dbo.p AS SELECT 1;");
        var used = await Document(UseUri, "SELECT dbo.Increment(1), COUNT(*) FROM dbo.Rows(); DECLARE @sql nvarchar(100)=N'EXEC dbo.p'; EXEC(@sql); EXEC(N'EXEC dbo.p'); EXEC dbo.p;");
        var index = Index([declared, used]);
        Assert.Equal(2, used.Parse.Semantics.References.Count(r => r.Kind == SqlReferenceKind.Function));
        Assert.All(used.Parse.Semantics.References.Where(r => r.Kind == SqlReferenceKind.Function), r => Assert.Equal(ResolutionStatus.Resolved, index.Resolve(used, r).Status));
        Assert.All(used.Parse.Semantics.References.Where(r => r.Kind == SqlReferenceKind.Dynamic), r => Assert.Equal(ResolutionStatus.Unsupported, index.Resolve(used, r).Status));
        Assert.Single(used.Parse.Semantics.References.Where(r => r.Kind == SqlReferenceKind.Procedure));
    }

    [Fact]
    public async Task DML_target_aliases_resolve_to_FROM_source_not_a_global_table_with_the_alias_name()
    {
        var declared = await Document(DeclUri, "CREATE TABLE dbo.Real (id int); CREATE TABLE dbo.r (id int);");
        var used = await Document(UseUri, "UPDATE r SET id=1 FROM dbo.Real r; DELETE r FROM dbo.Real r WHERE r.id=1;");
        var index = Index([declared, used]);
        Assert.All(used.Parse.Semantics.References, r => Assert.Equal("Real", Assert.Single(index.Resolve(used, r).Candidates).Identity!.Name));
    }

    [Fact]
    public async Task ALTER_TABLE_is_a_use_and_duplicate_definition_files_are_ambiguous()
    {
        var declared = await Document(DeclUri, "CREATE TABLE dbo.t (id int); ALTER TABLE dbo.t ADD n int;");
        var used = await Document(UseUri, "SELECT * FROM dbo.t;");
        var index = Index([declared, used]);
        Assert.Single(index.Search("dbo.t", CancellationToken.None));
        Assert.Equal(2, index.References(DeclUri, At(declared, "dbo.t"), false, CancellationToken.None).Length);
        var duplicate = await Document(new Uri("file:///workspace/duplicate.sql"), "CREATE TABLE dbo.t (id int);");
        Assert.Equal(ResolutionStatus.Ambiguous, Index([declared, duplicate, used]).Resolve(used, used.Parse.Semantics.References[0]).Status);
    }

    [Fact]
    public async Task Duplicate_aliases_and_forward_CTEs_do_not_fall_back_to_global_objects()
    {
        var declared = await Document(DeclUri, "CREATE TABLE dbo.a (id int); CREATE TABLE dbo.b (id int); CREATE TABLE dbo.future (id int);");
        var used = await Document(UseUri, "SELECT x.id FROM dbo.a x JOIN dbo.b x ON 1=1;\nWITH firstCte AS (SELECT * FROM future), future AS (SELECT 1 AS id) SELECT * FROM firstCte;");
        var index = Index([declared, used]);
        Assert.Equal(ResolutionStatus.Unsupported, index.Definition(UseUri, At(used, "x.id"), CancellationToken.None).Status);
        var forward = used.Parse.Semantics.References.Single(r => r.RawName == "future");
        Assert.Equal(ResolutionStatus.Unsupported, index.Resolve(used, forward).Status);
    }

    [Fact]
    public async Task Temp_drop_and_recreation_separate_lifetimes_and_table_variables_bind_to_declarations()
    {
        var used = await Document(UseUri, "CREATE TABLE #t (id int); SELECT * FROM #t; DROP TABLE #t; SELECT * FROM #t; CREATE TABLE #t (id int); SELECT * FROM #t;\nDECLARE @rows TABLE(id int); SELECT r.id FROM @rows r;");
        var index = Index([used]);
        var references = used.Parse.Semantics.References.Where(r => r.Name?.Name == "#t").ToArray();
        Assert.Equal(ResolutionStatus.Resolved, index.Resolve(used, references[0]).Status);
        Assert.Equal(ResolutionStatus.Unsupported, index.Resolve(used, references[2]).Status);
        Assert.NotEqual(references[0].LocalTarget!.Range, references[3].LocalTarget!.Range);
        var variable = index.Definition(UseUri, At(used, "r.id"), CancellationToken.None);
        Assert.Equal(ResolutionStatus.Resolved, variable.Status);
        Assert.Equal(new TextPosition(1, 8), variable.Candidates[0].Range.Start);
    }

    [Fact]
    public async Task Missing_table_variables_do_not_fabricate_local_declarations_and_requests_honor_cancellation()
    {
        var used = await Document(UseUri, "SELECT r.id FROM @rows r;");
        var index = Index([used]);
        Assert.Equal(ResolutionStatus.Unsupported, index.Definition(UseUri, At(used, "r.id"), CancellationToken.None).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => index.Search("", cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => index.Definition(UseUri, new(0, 0), cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => index.References(UseUri, new(0, 0), false, cancellation.Token));
    }

    [Fact]
    public async Task Named_EXEC_parameter_labels_do_not_count_as_caller_variable_references()
    {
        var used = await Document(UseUri, "DECLARE @p int=1; EXEC dbo.p @p=@p;");
        var variable = Assert.Single(used.Parse.Semantics.References.Where(r => r.Kind == SqlReferenceKind.Variable));
        Assert.Equal(used.Parse.Source.Text.LastIndexOf("@p", StringComparison.Ordinal), used.Parse.Source.OffsetAt(variable.Range.Start));
    }

    [Fact]
    public async Task Table_only_statements_cannot_resolve_view_declarations()
    {
        var declared = await Document(DeclUri, "CREATE VIEW dbo.v AS SELECT 1 AS id;");
        var used = await Document(UseUri, "ALTER TABLE dbo.v ADD n int; DROP TABLE dbo.v;");
        var index = Index([declared, used]);
        Assert.All(used.Parse.Semantics.References, r => Assert.Equal(ResolutionStatus.Unresolved, index.Resolve(used, r).Status));
    }

    [Fact]
    public async Task Aliases_inside_JOIN_predicate_subqueries_do_not_leak_into_outer_query()
    {
        var declared = await Document(DeclUri, "CREATE TABLE dbo.Real (id int); CREATE TABLE dbo.Other (id int);");
        var used = await Document(UseUri, "SELECT o.id FROM dbo.Real r JOIN dbo.Real s ON EXISTS (SELECT 1 FROM dbo.Other o WHERE o.id=r.id);");
        var index = Index([declared, used]);
        Assert.Equal(ResolutionStatus.Unsupported, index.Definition(UseUri, At(used, "o.id"), CancellationToken.None).Status);
        Assert.Equal("Other", index.Definition(UseUri, At(used, "o.id", 1), CancellationToken.None).Candidates[0].Identity!.Name);
    }

    [Fact]
    public async Task Numbered_procedures_are_not_collapsed_into_an_ordinary_procedure_identity()
    {
        var declared = await Document(DeclUri, "CREATE PROCEDURE dbo.p;2 AS SELECT 1;");
        var used = await Document(UseUri, "EXEC dbo.p;1;");
        Assert.Empty(declared.Parse.Diagnostics);
        Assert.Empty(used.Parse.Diagnostics);
        var index = Index([declared, used]);
        Assert.Empty(index.Symbols);
        Assert.Equal(ResolutionStatus.Unsupported, index.Resolve(used, Assert.Single(used.Parse.Semantics.References)).Status);
    }
}
