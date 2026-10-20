using System.Collections.Immutable;

namespace SqlLsp.Core.Tests;

public class UnderstandingRegressionTests
{
    private static string Slice(DocumentSnapshot document, TextRange range) => document.Parse.Source.Text[
        document.Parse.Source.OffsetAt(range.Start)..document.Parse.Source.OffsetAt(range.End)];
    private static async Task<(DocumentSnapshot Document, SqlProcedureReport Report)> Analyze(string sql, string name = "dbo.P")
    {
        var document = new DocumentSnapshot(new Uri("file:///understanding/modules.sql"), null, SqlSourceKind.Workspace,
            await new SqlParser(new()).ParseAsync(new SourceText(sql), CancellationToken.None));
        Assert.Empty(document.Parse.Diagnostics);
        var index = new SqlNavigationIndex([document], _ => "test", new());
        var selected = index.Symbols.Single(s => s.Declaration.Name == name);
        return (document, index.AnalyzeProcedure(document.Uri, selected.Target.Range.Start, CancellationToken.None));
    }

    [Fact]
    public async Task Throw_has_named_state_not_severity_and_direct_following_return_is_an_observation()
    {
        var (document, report) = await Analyze("CREATE PROC dbo.P AS BEGIN BEGIN TRY THROW 51000, N'error', 2; RETURN 1; END TRY BEGIN CATCH THROW; END CATCH END;");
        var module = Assert.Single(report.Modules);
        var thrown = module.ControlFlow!.Nodes.Where(n => n.Kind == SqlFlowKind.Throw).ToArray();
        Assert.Equal("NewError", thrown[0].Throw!.Kind);
        Assert.Equal("51000", thrown[0].Throw!.ErrorNumber!.Preview);
        Assert.Equal("2", thrown[0].Throw!.State!.Preview);
        Assert.Equal("N'error'", thrown[0].Throw!.Message!.Preview);
        Assert.Contains("never severity", thrown[0].Detail);
        Assert.Equal("Rethrow", thrown[1].Throw!.Kind);
        Assert.Null(thrown[1].Throw!.State);
        var issue = Assert.Single(module.ControlFlow.Issues, i => i.Code == "return-after-throw");
        Assert.Equal("RETURN 1;", Slice(document, issue.Location!.Range));
        var (_, conditional) = await Analyze("CREATE PROC dbo.P @n int AS BEGIN IF @n=1 THROW 51000,N'x',2; RETURN 1; END;");
        Assert.DoesNotContain(conditional.Modules[0].ControlFlow!.Issues, i => i.Code == "return-after-throw");
    }

    [Fact]
    public async Task Dynamic_routine_definition_text_does_not_become_an_immediate_caller_edge()
    {
        var (document, report) = await Analyze("CREATE PROC dbo.P AS BEGIN DECLARE @sql nvarchar(max)=N'CREATE PROCEDURE dbo.Future AS SELECT dbo.Price();'; EXEC(@sql); END;\nGO\nCREATE FUNCTION dbo.Price() RETURNS int AS BEGIN RETURN 30; END;");
        var module = report.Modules[0];
        var observed = Assert.Single(module.Observations, o => o.Kind == "EmbeddedModuleDefinitionText");
        Assert.Contains("CREATE PROCEDURE dbo.Future", Slice(document, observed.Location.Range));
        Assert.DoesNotContain(module.Calls, c => c.Target?.Identity?.Name == "Price");
        Assert.Contains("separate from invoking", observed.Detail);
    }

    [Theory]
    [InlineData(" create\tOr\nALTER\tprocedure dbo.Future AS SELECT 1;", "PROCEDURE")]
    [InlineData("CREATE PROC dbo.Future AS SELECT 1;", "PROC")]
    [InlineData("CREATE VIEW dbo.Future AS SELECT 1;", "VIEW")]
    [InlineData("CREATE FUNCTION dbo.Future() RETURNS int AS BEGIN RETURN 1; END;", "FUNCTION")]
    [InlineData("CREATE OR PROC dbo.Future", null)]
    [InlineData("CREATEX PROC dbo.Future", null)]
    [InlineData("CREATE PROC_foo dbo.Future", null)]
    [InlineData("CREATE PROC1 dbo.Future", null)]
    [InlineData("CREATE PROC漢 dbo.Future", null)]
    [InlineData("PRINT 1; CREATE PROC dbo.Future", null)]
    public async Task Literal_module_prefix_classification_requires_keywords_and_word_boundaries(string text, string? kind)
    {
        var (_, report) = await Analyze("CREATE PROC dbo.P AS PRINT N'" + text.Replace("'", "''", StringComparison.Ordinal) + "';");
        var facts = report.Modules[0].Observations.Where(o => o.Kind == "EmbeddedModuleDefinitionText").ToArray();
        if (kind == null) Assert.Empty(facts);
        else Assert.Equal(kind, Assert.Single(facts).Name);
        Assert.Empty(report.Modules[0].Calls);
    }

    [Fact]
    public async Task Sequence_and_select_loop_observations_keep_transaction_and_retention_unknowns()
    {
        var (_, report) = await Analyze("CREATE PROC dbo.P AS BEGIN DECLARE @id int; SET @id=NEXT VALUE FOR dbo.S; BEGIN TRAN; WHILE @id<10 BEGIN SELECT TOP 1 @id=id FROM dbo.T; END; ROLLBACK; END;");
        var observations = report.Modules[0].Observations;
        var sequence = Assert.Single(observations, o => o.Kind == "SequenceAllocation");
        Assert.True(sequence.BeforeFirstExplicitTransaction);
        Assert.Contains("independently of transaction rollback", sequence.Detail);
        var assignment = Assert.Single(observations, o => o.Kind == "SelectAssignment");
        Assert.Contains("retains the previous", assignment.Detail);
        Assert.Contains("scalar subquery", assignment.Detail);
        Assert.Contains(report.Modules[0].ValueFlow!.Facts, f => f.Kind == SqlValueFactKind.SelectAssignment && f.Status == ResolutionStatus.Unsupported);
    }

    [Fact]
    public async Task Atomic_and_external_sources_never_prove_unknown_ambient_or_engine_effects_empty()
    {
        var (_, atomic) = await Analyze("CREATE PROC dbo.P WITH NATIVE_COMPILATION, SCHEMABINDING, EXECUTE AS OWNER AS BEGIN ATOMIC WITH(TRANSACTION ISOLATION LEVEL=SNAPSHOT,LANGUAGE=N'us_english') SELECT 1; END;");
        Assert.Contains(atomic.Modules[0].Observations, o => o.Kind == "AtomicTransaction" && o.Detail.Contains("session transaction"));
        var (document, external) = await Analyze("CREATE PROC dbo.P @H nvarchar(max) AS EXTERNAL NAME SqlClrCurl.Curl.Post;");
        var observation = Assert.Single(external.Modules[0].Observations, o => o.Kind == "ExternalImplementation");
        Assert.False(external.Modules[0].BodyAvailable);
        Assert.False(external.Modules[0].ControlFlow!.BodyAvailable);
        Assert.Equal("External", external.Modules[0].ImplementationKind);
        Assert.Equal("SqlClrCurl.Curl.Post", observation.Name);
        Assert.Contains("not a known-empty", observation.Detail);
        Assert.Contains(external.Limitations, l => l.Contains("temporal/history"));
        var index = new SqlNavigationIndex([document], _ => "test", new());
        var node = index.PrepareCallHierarchy(document.Uri, document.Parse.Declarations[0].SelectionRange.Start, CancellationToken.None);
        Assert.NotNull(node);
        Assert.False(node.Coverage.BodyAvailable);
    }

    [Fact]
    public async Task Literal_percentage_formula_and_explicit_json_paths_keep_exact_source_evidence()
    {
        const string formula = "ROUND(@UnitPrice * @HighestDiscountPercentage / 100.0, 2)";
        var (document, report) = await Analyze("CREATE PROC dbo.P @UnitPrice decimal(18,2),@HighestDiscountPercentage int,@json nvarchar(max) AS BEGIN " +
            "DECLARE @p decimal(18,2); SET @p=" + formula + "; SELECT @p=Price FROM OPENJSON(@json) WITH (Price money N'strict $.\"Price\"', priceLower money N'$.\"price\"'); END;");
        var assignment = Assert.Single(report.Modules[0].ValueFlow!.Facts, f => f.Kind == SqlValueFactKind.Assignment);
        Assert.Equal(formula, assignment.Expression!.Preview);
        Assert.Equal(formula, Slice(document, assignment.Expression.Location.Range));
        Assert.False(assignment.Expression.Truncated);
        var paths = report.Modules[0].Observations.Where(o => o.Kind == "OpenJsonColumn").ToArray();
        Assert.Equal(2, paths.Length);
        Assert.Equal("strict $.\"Price\"", paths[0].JsonPath);
        Assert.Equal("$.\"price\"", paths[1].JsonPath);
        Assert.All(paths, p => Assert.Equal("BIN2-case-sensitive", p.JsonKeyComparison));
        Assert.Contains("N'strict $.\"Price\"'", Slice(document, paths[0].Location.Range));
        Assert.Contains(report.Modules[0].ValueFlow!.Limitations, l => l.Contains("not observed values"));
    }
}
