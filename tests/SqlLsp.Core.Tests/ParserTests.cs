namespace SqlLsp.Core.Tests;

public class ParserTests
{
    [Theory]
    [InlineData("80")]
    [InlineData("90")]
    [InlineData("100")]
    [InlineData("110")]
    [InlineData("120")]
    [InlineData("130")]
    [InlineData("140")]
    [InlineData("150")]
    [InlineData("160")]
    [InlineData("170")]
    [InlineData("180")]
    public async Task Every_advertised_dialect_constructs_a_real_parser(string version)
    {
        Assert.Empty((await new SqlParser(new(version)).ParseAsync(new SourceText("SELECT 1;"), CancellationToken.None)).Diagnostics);
    }

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.p AS SELECT 1;", "dbo.p", SqlObjectKind.Procedure)]
    [InlineData("ALTER PROCEDURE dbo.p AS SELECT 2;", "dbo.p", SqlObjectKind.Procedure)]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.p AS SELECT 2;", "dbo.p", SqlObjectKind.Procedure)]
    [InlineData("CREATE VIEW dbo.v AS SELECT 1 AS n;", "dbo.v", SqlObjectKind.View)]
    [InlineData("ALTER VIEW dbo.v AS SELECT 2 AS n;", "dbo.v", SqlObjectKind.View)]
    [InlineData("CREATE OR ALTER VIEW dbo.v AS SELECT 2 AS n;", "dbo.v", SqlObjectKind.View)]
    [InlineData("CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1; END;", "dbo.f", SqlObjectKind.Function)]
    [InlineData("ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 2; END;", "dbo.f", SqlObjectKind.Function)]
    [InlineData("CREATE OR ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 2; END;", "dbo.f", SqlObjectKind.Function)]
    [InlineData("CREATE TABLE dbo.t (id int);", "dbo.t", SqlObjectKind.Table)]
    [InlineData("ALTER TABLE dbo.t ADD n int;", "dbo.t", SqlObjectKind.Table)]
    [InlineData("CREATE TABLE [Sales].[a]]b] (id int);", "Sales.a]b", SqlObjectKind.Table)]
    [InlineData("CREATE VIEW \"dbo\".\"员工\" AS SELECT 1 AS n;", "dbo.员工", SqlObjectKind.View)]
    public async Task Declarations_have_real_ast_names_and_contained_identifier_ranges(string sql, string name, SqlObjectKind kind)
    {
        var result = await new SqlParser(new()).ParseAsync(new SourceText(sql), CancellationToken.None);
        Assert.Empty(result.Diagnostics);
        var symbol = Assert.Single(result.Declarations);
        Assert.Equal(name, symbol.Name);
        Assert.Equal(kind, symbol.Kind);
        Assert.Equal(new TextPosition(0, 0), symbol.Range.Start);
        Assert.Equal(new TextPosition(0, sql.Length), symbol.Range.End);
        var start = result.Source.OffsetAt(symbol.SelectionRange.Start);
        var end = result.Source.OffsetAt(symbol.SelectionRange.End);
        Assert.True(start > 0 && end > start && end < sql.Length);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public async Task Diagnostics_count_utf16_code_units_not_bytes_or_code_points(string newline)
    {
        const string prefix = "/* 中文 😀 */\tSELECT ";
        var sql = "-- Header" + newline + prefix + "FROM dbo.t;";
        var result = await new SqlParser(new()).ParseAsync(new SourceText(sql), CancellationToken.None);
        var error = result.Diagnostics[0];
        Assert.Equal(new TextPosition(1, prefix.Length), error.Range.Start);
        Assert.Equal(new TextPosition(1, prefix.Length + 4), error.Range.End);
        Assert.NotEmpty(result.Tokens);
    }

    [Fact]
    public async Task Batches_preserve_good_declarations_and_do_not_index_local_tables()
    {
        const string sql = "CREATE PROC dbo.p AS BEGIN CREATE TABLE #local (id int); SELECT 1; END;\nGO\nSELECT FROM;\nGO\nCREATE TABLE dbo.t (id int);";
        var result = await new SqlParser(new()).ParseAsync(new SourceText(sql), CancellationToken.None);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal(new[] { "dbo.p", "dbo.t" }, result.Declarations.Select(s => s.Name));
    }

    [Fact]
    public async Task Dialect_is_configurable_and_invalid_versions_fail_validation()
    {
        const string sql = "CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1;";
        Assert.NotEmpty((await new SqlParser(new("120")).ParseAsync(new SourceText(sql), CancellationToken.None)).Diagnostics);
        Assert.Empty((await new SqlParser(new("170")).ParseAsync(new SourceText(sql), CancellationToken.None)).Diagnostics);
        Assert.Throws<ArgumentException>(() => new SqlParser(new("999")));
    }

    [Fact]
    public async Task Quoted_identifier_can_be_disabled_and_cancellation_is_honored()
    {
        Assert.NotEmpty((await new SqlParser(new("170", false)).ParseAsync(
            new SourceText("CREATE TABLE \"dbo\".\"t\" (id int);"), CancellationToken.None)).Diagnostics);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqlParser(new()).ParseAsync(new SourceText("SELECT 1;"), cancellation.Token));
    }
}
