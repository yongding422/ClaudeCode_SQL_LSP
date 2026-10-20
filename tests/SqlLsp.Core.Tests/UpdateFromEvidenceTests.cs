using SqlLsp.Core;

namespace SqlLsp.Core.Tests;

public class UpdateFromEvidenceTests
{
    private static async Task<(DocumentSnapshot Document, SqlModuleAnalysis Module)> Analyze(string sql)
    {
        var document = new DocumentSnapshot(new Uri("file:///update/evidence.sql"), null, SqlSourceKind.Workspace,
            await new SqlParser(new()).ParseAsync(new SourceText(sql), CancellationToken.None));
        var index = new SqlNavigationIndex([document], _ => "test", new());
        return (document, index.AnalyzeProcedure(document.Uri, index.Symbols.Single(s => s.Identity.Name == "P").Target.Range.Start, CancellationToken.None).Modules[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" WHERE s.OrderID=@id")]
    public async Task Clauses_and_compound_assignment_keep_source_spans_without_inventing_cardinality(string predicate)
    {
        var (document, module) = await Analyze("-- 😀\nCREATE PROC dbo.P @id int AS UPDATE t SET n += s.n FROM dbo.T t JOIN dbo.S s ON t.id=s.id" + predicate + ";");
        Assert.Empty(document.Parse.Diagnostics);
        var fact = Assert.Single(module.Observations, o => o.Kind == "UpdateFrom");
        var syntax = fact.UpdateFrom!;
        Assert.Equal("t", syntax.Target.Preview);
        Assert.Equal(predicate.Length != 0, syntax.Where != null);
        Assert.True(syntax.SyntaxComplete);
        Assert.True(syntax.AssignmentsComplete);
        Assert.Contains("+=", Assert.Single(syntax.Assignments).Preview);
        Assert.Equal(2, fact.Inputs.Length);
        foreach (var expression in fact.Expressions())
        {
            var range = expression.Location.Range;
            Assert.Equal(expression.Preview, document.Parse.Source.Text[document.Parse.Source.OffsetAt(range.Start)..document.Parse.Source.OffsetAt(range.End)]);
            Assert.False(expression.Truncated);
        }
        Assert.Contains("unproved", fact.Detail);
    }

    [Fact]
    public async Task A_plain_update_does_not_gain_an_update_from_observation()
    {
        var (_, module) = await Analyze("CREATE PROC dbo.P AS UPDATE dbo.T SET n=1 WHERE id=2;");
        Assert.DoesNotContain(module.Observations, o => o.Kind == "UpdateFrom");
    }

    [Fact]
    public async Task Assignment_limit_and_parse_recovery_are_explicit()
    {
        var assignments = string.Join(",", Enumerable.Range(1, 10).Select(i => $"n{i}=s.n{i}"));
        var (_, module) = await Analyze("CREATE PROC dbo.P AS UPDATE t SET " + assignments + " FROM dbo.T t JOIN dbo.S s ON t.id=s.id;\nGO\nSELECT FROM;");
        var fact = Assert.Single(module.Observations, o => o.Kind == "UpdateFrom");
        Assert.Equal(8, fact.UpdateFrom!.Assignments.Length);
        Assert.False(fact.UpdateFrom.AssignmentsComplete);
        Assert.False(fact.UpdateFrom.SyntaxComplete);
        Assert.True(module.Truncated);
    }
}
