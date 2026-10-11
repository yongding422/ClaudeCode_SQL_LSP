using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core.Tests;

public class ApiSpikeTests
{
    [Fact]
    public void Actual_parser_visitor_tokens_and_error_offsets_work()
    {
        var parser = TSqlParser.CreateParser(SqlVersion.Sql170, initialQuotedIdentifiers: true);
        var ast = parser.Parse(new StringReader("CREATE PROCEDURE dbo.Payroll AS SELECT 1;"), out var errors);
        Assert.Empty(errors);
        var visitor = new ProcedureVisitor();
        ast.Accept(visitor);
        Assert.Equal("Payroll", visitor.Name);
        Assert.NotEmpty(ast.ScriptTokenStream);
        const string broken = "-- 中文 😀\r\nSELECT FROM dbo.Payroll;";
        parser.Parse(new StringReader(broken), out errors);
        Assert.NotEmpty(errors);
        Assert.Equal(broken.IndexOf("FROM", StringComparison.Ordinal), errors[0].Offset);
        Assert.Equal(2, errors[0].Line);
        Assert.Equal(8, errors[0].Column);
    }

    private sealed class ProcedureVisitor : TSqlFragmentVisitor
    {
        public string? Name { get; private set; }
        public override void ExplicitVisit(CreateProcedureStatement node) =>
            Name = node.ProcedureReference.Name.BaseIdentifier.Value;
    }
}
