namespace SqlLsp.Core.Tests;

public class CtasRegressionTests
{
    [Theory]
    [InlineData("CREATE TABLE dbo.copy WITH (DISTRIBUTION = HASH(id)) AS SELECT id FROM dbo.original;")]
    [InlineData("CREATE TABLE dbo.copy AS SELECT 1 AS id;")]
    public async Task Create_table_as_select_remains_indexed_without_a_column_definition(string sql)
    {
        var parsed = await new SqlParser(new()).ParseAsync(new SourceText(sql), CancellationToken.None);
        Assert.Empty(parsed.Diagnostics);
        var table = Assert.Single(parsed.Declarations);
        Assert.Equal(SqlObjectKind.Table, table.Kind);
        Assert.Contains(table.Documentation!.Details, d => d.Contains("not inferred", StringComparison.Ordinal));
    }
}
