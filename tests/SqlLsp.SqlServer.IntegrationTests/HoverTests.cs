using System.Collections.Immutable;
using System.Text.Json;
using SqlLsp.Core;
using SqlLsp.Providers.SqlServer;
using SqlLsp.Server.IntegrationTests;

namespace SqlLsp.SqlServer.IntegrationTests;

public class CatalogHoverTests
{
    [Fact]
    public async Task Catalog_hover_uses_authoritative_renamed_identity_hidden_metadata_and_visible_type_arguments()
    {
        var catalog = ProviderTests.Snapshot with
        {
            Objects = ProviderTests.Snapshot.Objects.Select(o => o.ObjectId == 3 ? o with
            {
                Parameters = [new(1, "@n", "sys", "nvarchar", 80, 0, 0, false), new(2, "@out", "sys", "decimal", 9, 12, 2, true),
                    new(3, "@alias", "custom", "AmountType", 9, 12, 2, false)]
            } : o).ToImmutableArray()
        };
        using var storage = new CatalogDocuments();
        var snapshot = await storage.BuildAsync(catalog, new(), CancellationToken.None);
        var query = new DocumentSnapshot(new Uri("file:///query.sql"), 1, SqlSourceKind.UnsavedBuffer,
            await new SqlParser(new()).ParseAsync(new("EXEC dbo.CurrentName; EXEC dbo.Hidden; SELECT * FROM dbo.Payroll;"), CancellationToken.None));
        var index = new SqlNavigationIndex(snapshot.Documents.Add(query), _ => "fixture", new(DefaultDatabase: "Fixture"), snapshot.Symbols, indexDeclarations: false);
        var renamed = index.Hover(query.Uri, query.Parse.Semantics.References[0].Range.Start, CancellationToken.None)!.Text;
        Assert.Contains("[Fixture].[dbo].[CurrentName]", renamed);
        Assert.DoesNotContain("OldName", renamed);
        var hidden = index.Hover(query.Uri, query.Parse.Semantics.References[1].Range.Start, CancellationToken.None)!.Text;
        Assert.Contains("@n [sys].[nvarchar](40)", hidden);
        Assert.Contains("@out [sys].[decimal](12,2) OUTPUT", hidden);
        Assert.Contains("@alias [custom].[AmountType]", hidden);
        Assert.DoesNotContain("[AmountType](", hidden);
        Assert.Contains("Original source unavailable", hidden);
        Assert.Contains("Catalog coverage: Partial", hidden);
        Assert.Contains(catalog.CapturedAt.ToString("O"), hidden);
        var hiddenSymbol = snapshot.Symbols.Single(s => s.Identity.Name == "Hidden");
        var direct = index.Hover(hiddenSymbol.Target.Uri, hiddenSymbol.Target.Range.Start, CancellationToken.None)!;
        Assert.Contains("Kind: Procedure", direct.Text);
        Assert.Contains("[Hidden]", direct.Text);
        var table = index.Hover(query.Uri, query.Parse.Semantics.References[2].Range.Start, CancellationToken.None)!.Text;
        Assert.Contains("[PayrollId] [sys].[int] NOT NULL", table);
        Assert.Contains("Visible catalog columns", table);
    }

    [Fact]
    public async Task Limited_parameter_metadata_does_not_fabricate_a_complete_parameterless_signature()
    {
        using var storage = new CatalogDocuments();
        var catalog = ProviderTests.Snapshot with { Coverage = ["parameter-limit"], Objects = [ProviderTests.Snapshot.Objects[2] with { Parameters = [] }] };
        var snapshot = await storage.BuildAsync(catalog, new(), CancellationToken.None);
        var index = new SqlNavigationIndex(snapshot.Documents, _ => "fixture", new(), snapshot.Symbols, indexDeclarations: false);
        var symbol = snapshot.Symbols.Single();
        var text = index.Hover(symbol.Target.Uri, symbol.Target.Range.Start, CancellationToken.None)!.Text;
        Assert.Contains("[Hidden] (unknown)", text);
        Assert.Contains("Parameter metadata may be incomplete", text);
    }
}

[Collection("Disposable SQL Server")]
public class LiveHoverTests(SqlServerFixture fixture)
{
    private object OnlineOptions(bool limited = false) => new
    {
        sql = new { mode = "online" },
        databases = new[] { new { id = fixture.Profile.Id, enabled = true, readOnly = true,
            environment = "disposable-test", server = fixture.Endpoint, database = fixture.Profile.Database,
            connectionStringEnv = limited ? "SQLLSP_HOVER_LIMITED" : fixture.Profile.ConnectionStringEnv } }
    };

    [SqlServerFact]
    public async Task Online_hover_exposes_real_parameter_column_return_and_encrypted_renamed_metadata_over_stdio()
    {
        await using var client = new LspClient();
        await client.InitializeAsync(initializationOptions: OnlineOptions());
        await client.NotifyAsync("initialized", new { });
        const string uri = "file:///hover-online/query.sql";
        await client.NotifyAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri,
                languageId = "sql",
                version = 1,
                text = "EXEC dbo.ProcessPayroll;\nSELECT * FROM dbo.Payroll;\nSELECT dbo.PayrollTotal(1);\nEXEC dbo.EncryptedPayroll;\nEXEC dbo.RenamedPayroll;"
            }
        });
        await client.DiagnosticsAsync(uri, 1);
        var proc = await Hover(client, uri, 0, 12);
        Assert.Contains("@Note [sys].[nvarchar](40)", proc);
        Assert.Contains("@Result [sys].[int] OUTPUT", proc);
        Assert.Contains("Source: Database", proc);
        Assert.Contains("Catalog captured:", proc);
        var table = await Hover(client, uri, 1, 20);
        Assert.Contains("[Amount] [sys].[decimal](12,2) NOT NULL", table);
        Assert.Contains("[Note] [sys].[nvarchar](40) NULL", table);
        Assert.Contains("RETURNS [sys].[decimal](12,2)", await Hover(client, uri, 2, 14));
        Assert.Contains("Original source unavailable", await Hover(client, uri, 3, 12));
        var renamed = await Hover(client, uri, 4, 12);
        Assert.Contains("[RenamedPayroll]", renamed);
        Assert.DoesNotContain("OriginalName", renamed);
        await client.StopAsync();
    }

    [SqlServerFact]
    public async Task Restricted_account_hover_uses_partial_metadata_without_fabricated_original_source()
    {
        await using var client = new LspClient(new Dictionary<string, string?> { ["SQLLSP_HOVER_LIMITED"] = fixture.LimitedConnection });
        await client.InitializeAsync(initializationOptions: OnlineOptions(limited: true));
        await client.NotifyAsync("initialized", new { });
        const string uri = "file:///hover-limited/query.sql";
        await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sql", version = 1, text = "EXEC dbo.ProcessPayroll;" } });
        await client.DiagnosticsAsync(uri, 1);
        var proc = await Hover(client, uri, 0, 12);
        Assert.Contains("Catalog coverage: Partial", proc);
        Assert.Contains("Original source unavailable", proc);
        Assert.DoesNotContain("SELECT *", proc);
        await client.StopAsync();
    }

    private static async Task<string> Hover(LspClient client, string uri, int line, int character)
    {
        var result = (await client.RequestAsync("textDocument/hover", new { textDocument = new { uri }, position = new { line, character } })).GetProperty("result");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        return result.GetProperty("contents").GetProperty("value").GetString()!;
    }
}
