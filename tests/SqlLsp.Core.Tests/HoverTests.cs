using System.Collections.Immutable;

namespace SqlLsp.Core.Tests;

public class HoverTests
{
    private static readonly Uri DeclarationUri = new("file:///workspace/decl.sql"), QueryUri = new("file:///workspace/query.sql");
    private static async Task<DocumentSnapshot> Document(Uri uri, string sql, SqlSourceKind kind = SqlSourceKind.Workspace) =>
        new(uri, kind == SqlSourceKind.UnsavedBuffer ? 9 : null, kind, await new SqlParser(new()).ParseAsync(new(sql), CancellationToken.None));
    private static TextPosition At(DocumentSnapshot document, string text) => document.Parse.Source.PositionAt(document.Parse.Source.Text.IndexOf(text, StringComparison.Ordinal));
    private static SqlNavigationIndex Index(params DocumentSnapshot[] documents) => new(documents.ToImmutableArray(), _ => "workspace", new());

    [Fact]
    public async Task Procedure_hover_uses_the_definition_signature_and_request_range_without_defaults_or_body()
    {
        var declaration = await Document(DeclarationUri, "CREATE PROCEDURE dbo.P @id int = 42, @name nvarchar(40) = N'SECRET_DEFAULT' OUTPUT AS SELECT 'SECRET_BODY';");
        var query = await Document(QueryUri, "EXEC dbo.P @id=1;");
        var hover = Index(declaration, query).Hover(QueryUri, At(query, "dbo.P"), CancellationToken.None)!;
        Assert.Equal(query.Parse.Semantics.References[0].Range, hover.Range);
        Assert.Contains("Kind: Procedure", hover.Text);
        Assert.Contains("[dbo].[P] (@id int, @name nvarchar(40) OUTPUT)", hover.Text);
        Assert.Contains("Source: Workspace", hover.Text);
        Assert.Contains(DeclarationUri.AbsoluteUri, hover.Text);
        Assert.Contains("Declared syntax", hover.Text);
        Assert.DoesNotContain("SECRET", hover.Text);
        Assert.DoesNotContain("42", hover.Text);
    }

    [Fact]
    public async Task Function_return_table_columns_and_view_projection_limits_are_syntax_based()
    {
        var declaration = await Document(DeclarationUri, """
            CREATE FUNCTION dbo.F(@id int) RETURNS decimal(12,2) AS BEGIN RETURN @id; END;
            GO
            CREATE TABLE dbo.T (id int PRIMARY KEY, name nvarchar(40) NULL, computed AS id+1);
            GO
            CREATE VIEW dbo.V(named) AS SELECT id FROM dbo.T;
            """);
        var query = await Document(QueryUri, "SELECT dbo.F(1); SELECT * FROM dbo.T; SELECT * FROM dbo.V;");
        var index = Index(declaration, query);
        Assert.Contains("RETURNS decimal(12,2)", index.Hover(QueryUri, At(query, "dbo.F"), CancellationToken.None)!.Text);
        var table = index.Hover(QueryUri, At(query, "dbo.T"), CancellationToken.None)!.Text;
        Assert.Contains("[id] int (nullability unspecified)", table);
        Assert.Contains("[name] nvarchar(40) NULL", table);
        Assert.Contains("[computed] computed (type not inferred)", table);
        var view = index.Hover(QueryUri, At(query, "dbo.V"), CancellationToken.None)!.Text;
        Assert.Contains("[named]", view);
        Assert.Contains("Projected column types are not inferred", view);
    }

    [Fact]
    public async Task Parameter_variable_and_cte_hover_follow_lexical_binding_without_column_type_inference()
    {
        var document = await Document(QueryUri, """
            CREATE PROCEDURE dbo.P @id bigint AS
            BEGIN
              DECLARE @name nvarchar(20) = N'SECRET';
              WITH c AS (SELECT @id AS id) SELECT c.id, @name FROM c;
            END;
            """, SqlSourceKind.UnsavedBuffer);
        var index = Index(document);
        var parameterReference = document.Parse.Semantics.References.First(r => r.RawName == "@id");
        var parameter = index.Hover(QueryUri, parameterReference.Range.Start, CancellationToken.None)!;
        Assert.Contains("Kind: Parameter", parameter.Text);
        Assert.Contains("Declared type: bigint", parameter.Text);
        Assert.Contains("Buffer version: 9", parameter.Text);
        var variableReference = document.Parse.Semantics.References.First(r => r.RawName == "@name");
        var variable = index.Hover(QueryUri, variableReference.Range.Start, CancellationToken.None)!;
        Assert.Contains("Declared type: nvarchar(20)", variable.Text);
        Assert.DoesNotContain("SECRET", variable.Text);
        Assert.Contains("Kind: Cte", index.Hover(QueryUri, At(document, "c.id"), CancellationToken.None)!.Text);
        var id = document.Parse.Source.PositionAt(document.Parse.Source.OffsetAt(At(document, "c.id")) + 2);
        Assert.Null(index.Hover(QueryUri, id, CancellationToken.None));
    }

    [Theory]
    [InlineData("EXEC dbo.Missing;", "dbo.Missing")]
    [InlineData("EXEC dbo.P; EXEC(N'EXEC dbo.P;');", "EXEC(N")]
    [InlineData("EXEC server.Db.dbo.P;", "server.Db")]
    [InlineData("-- dbo.P\nSELECT 'dbo.P';", "dbo.P")]
    [InlineData("SELECT 'dbo.P';", "dbo.P")]
    [InlineData("SELECT * FROM;", "FROM")]
    public async Task Missing_dynamic_remote_comment_literal_and_malformed_positions_have_no_hover(string sql, string needle)
    {
        var declaration = await Document(DeclarationUri, "CREATE PROCEDURE dbo.P AS SELECT 1;");
        var query = await Document(QueryUri, sql);
        Assert.Null(Index(declaration, query).Hover(QueryUri, At(query, needle), CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_global_targets_and_query_aliases_have_no_invented_hover()
    {
        var first = await Document(DeclarationUri, "CREATE TABLE dbo.T(id int);");
        var second = await Document(new("file:///workspace/duplicate.sql"), "CREATE TABLE dbo.T(id bigint);");
        var query = await Document(QueryUri, "SELECT * FROM dbo.T;");
        Assert.Null(Index(first, second, query).Hover(QueryUri, At(query, "dbo.T"), CancellationToken.None));
        query = await Document(QueryUri, "SELECT a.id FROM dbo.T a JOIN dbo.T a ON a.id=a.id;");
        Assert.Null(Index(first, query).Hover(QueryUri, At(query, "a.id"), CancellationToken.None));
    }

    [Theory]
    [InlineData(true, SqlSourceKind.Workspace)]
    [InlineData(true, SqlSourceKind.UnsavedBuffer)]
    [InlineData(false, SqlSourceKind.UnsavedBuffer)]
    public async Task Hybrid_hover_describes_only_the_selected_source_with_comparison_and_capture(bool preferLocal, SqlSourceKind kind)
    {
        var local = await Document(DeclarationUri, "CREATE PROCEDURE dbo.P @local int AS SELECT 2;", kind);
        var dbUri = new Uri("file:///catalog/P.sql");
        var deployed = await Document(dbUri, "CREATE PROCEDURE dbo.P @deployed bigint AS SELECT 1;", SqlSourceKind.Database);
        var declaration = deployed.Parse.Declarations.Single();
        var identity = new SqlObjectIdentity("dev", null, "Payroll", "dbo", "P", SqlObjectKind.Procedure);
        var captured = DateTimeOffset.Parse("2026-10-13T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var symbol = new IndexedSqlSymbol(identity, declaration, new(dbUri, declaration.SelectionRange, identity, SqlSourceKind.Database, "Database module AST"),
            SqlDefinitionFingerprint.Compute(deployed.Parse, declaration), captured);
        var query = await Document(QueryUri, "EXEC dbo.P;");
        var rules = new ResolutionOptions(DefaultDatabase: "Payroll", DefaultSchema: "dbo");
        var policy = new SqlHybridPolicy([new("dev", "Payroll", rules)], [new("workspace", ["dev"], "dev")], preferLocal);
        var index = new SqlNavigationIndex([local, deployed, query], uri => uri == dbUri ? "dev" : "workspace", rules, [symbol], hybrid: policy);
        var hover = index.Hover(QueryUri, At(query, "dbo.P"), CancellationToken.None)!.Text;
        Assert.Contains(preferLocal ? "@local int" : "@deployed bigint", hover);
        Assert.DoesNotContain(preferLocal ? "@deployed bigint" : "@local int", hover);
        Assert.Contains("Source: " + (preferLocal ? kind : SqlSourceKind.Database), hover);
        Assert.Contains("Definition comparison: Different", hover);
        if (!preferLocal) Assert.Contains(captured.ToString("O"), hover);
    }

    [Fact]
    public async Task Database_selected_hover_does_not_borrow_comparison_from_another_mapped_root()
    {
        var dbUri = new Uri("file:///catalog/P.sql");
        const string matchingSql = "CREATE PROCEDURE dbo.P @deployed bigint AS SELECT 1;";
        var deployed = await Document(dbUri, matchingSql, SqlSourceKind.Database);
        var otherRoot = await Document(new("file:///other/P.sql"), matchingSql);
        var local = await Document(DeclarationUri, "CREATE PROCEDURE dbo.P @different int AS SELECT 2;");
        var query = await Document(QueryUri, "EXEC dbo.P;");
        var declaration = deployed.Parse.Declarations.Single();
        var identity = new SqlObjectIdentity("dev", null, "Payroll", "dbo", "P", SqlObjectKind.Procedure);
        var symbol = new IndexedSqlSymbol(identity, declaration, new(dbUri, declaration.SelectionRange, identity, SqlSourceKind.Database, "Database module AST"),
            SqlDefinitionFingerprint.Compute(deployed.Parse, declaration), DateTimeOffset.UtcNow);
        var rules = new ResolutionOptions(DefaultDatabase: "Payroll", DefaultSchema: "dbo");
        var policy = new SqlHybridPolicy([new("dev", "Payroll", rules)], [new("other", ["dev"], "dev"), new("workspace", ["dev"], "dev")], false);
        var index = new SqlNavigationIndex([otherRoot, local, deployed, query], uri => uri == dbUri ? "dev" : uri == otherRoot.Uri ? "other" : "workspace", rules, [symbol], hybrid: policy);
        Assert.Equal(new[] { SqlDriftStatus.Match, SqlDriftStatus.Different }, index.Drifts.Select(d => d.Status));
        Assert.Contains("Definition comparison: Different", index.Hover(QueryUri, At(query, "dbo.P"), CancellationToken.None)!.Text);
        Assert.DoesNotContain("Definition comparison:", index.Hover(dbUri, declaration.SelectionRange.Start, CancellationToken.None)!.Text);
    }

    [Fact]
    public async Task Unicode_CRLF_identifiers_and_cursor_validation_use_UTF16_and_bounded_text()
    {
        var declaration = await Document(DeclarationUri, "CREATE PROCEDURE [schema.dot].[a]]😀b] @参数 int AS SELECT 1;");
        var query = await Document(QueryUri, "-- 😀\r\nEXEC [schema.dot].[a]]😀b];");
        var index = Index(declaration, query);
        var range = query.Parse.Semantics.References.Single().Range;
        Assert.Equal(range, index.Hover(QueryUri, range.Start, CancellationToken.None)!.Range);
        Assert.Contains("[schema.dot].[a]]😀b] (@参数 int)", index.Hover(QueryUri, range.Start, CancellationToken.None)!.Text);
        Assert.Null(index.Hover(QueryUri, new(-1, 0), CancellationToken.None));
        Assert.Null(index.Hover(QueryUri, new(999, 0), CancellationToken.None));
        var splitEmoji = query.Parse.Source.Text.IndexOf("😀", StringComparison.Ordinal) + 1;
        Assert.Null(index.Hover(QueryUri, new(0, splitEmoji), CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => index.Hover(QueryUri, range.Start, canceled.Token));
        Assert.Equal("a b", SqlSymbolDisplay.OneLine("a\u202Eb"));
        var many = await Document(DeclarationUri, "CREATE TABLE dbo.Many(" + string.Join(',', Enumerable.Range(0, 50).Select(i => $"c{i} int")) + ");");
        var manyHover = Index(many).Hover(DeclarationUri, many.Parse.Declarations.Single().SelectionRange.Start, CancellationToken.None)!;
        Assert.Contains("Additional columns omitted", manyHover.Text);
        Assert.DoesNotContain("[c49]", manyHover.Text);
        Assert.True(manyHover.Text.Length <= SqlHoverFormatting.MaxCharacters);
    }
}
