using System.Collections.Immutable;
using SqlLsp.Core;
using SqlLsp.Providers.SqlServer;
using SqlLsp.Server.IntegrationTests;

namespace SqlLsp.SqlServer.IntegrationTests;

public class CatalogCallHierarchyTests
{
    [Fact]
    public async Task Renamed_catalog_callers_keep_authoritative_names_and_hidden_callees_have_unknown_bodies()
    {
        var catalog = ProviderTests.Snapshot with
        {
            Objects = ProviderTests.Snapshot.Objects.Select(o => o.ObjectId == 1
                ? o with { Definition = "CREATE PROC dbo.OldName AS EXEC dbo.Hidden;" } : o).ToImmutableArray()
        };
        using var storage = new CatalogDocuments();
        var snapshot = await storage.BuildAsync(catalog, new(), CancellationToken.None);
        var index = new SqlNavigationIndex(snapshot.Documents, _ => "fixture", new(DefaultDatabase: "Fixture"), snapshot.Symbols, indexDeclarations: false);
        var caller = snapshot.Symbols.Single(s => s.Identity.Name == "CurrentName");
        var node = index.PrepareCallHierarchy(caller.Target.Uri, caller.Target.Range.Start, CancellationToken.None)!;
        Assert.Equal("CurrentName", node.Symbol.Identity.Name);
        Assert.True(node.Coverage.BodyAvailable);
        var call = Assert.Single(index.OutgoingCalls(node, CancellationToken.None));
        Assert.Equal("Hidden", call.Node.Symbol.Identity.Name);
        Assert.False(call.Node.Coverage.BodyAvailable);
        Assert.Empty(index.OutgoingCalls(call.Node, CancellationToken.None));
        Assert.Equal("CurrentName", Assert.Single(index.IncomingCalls(call.Node, CancellationToken.None)).Node.Symbol.Identity.Name);
        var next = await storage.BuildAsync(catalog, new(), CancellationToken.None);
        var refreshed = new SqlNavigationIndex(next.Documents, _ => "fixture", new(DefaultDatabase: "Fixture"), next.Symbols, indexDeclarations: false);
        Assert.Empty(refreshed.OutgoingCalls(node, CancellationToken.None));
    }
}

[Collection("Disposable SQL Server")]
public class LiveCallHierarchyTests(SqlServerFixture fixture)
{
    [SqlServerFact]
    public async Task Catalog_prepare_outgoing_incoming_and_encrypted_body_limits_work_over_real_stdio()
    {
        await using var client = new LspClient();
        await client.InitializeAsync(initializationOptions: new
        {
            sql = new { mode = "online" },
            databases = new[] { new { id = fixture.Profile.Id, enabled = true, readOnly = true,
                environment = "disposable-test", server = fixture.Endpoint, database = fixture.Profile.Database,
                connectionStringEnv = fixture.Profile.ConnectionStringEnv } }
        });
        await client.NotifyAsync("initialized", new { });
        const string uri = "file:///call-hierarchy/query.sql";
        await client.NotifyAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri,
                languageId = "sql",
                version = 1,
                text = "EXEC dbo.RunPayroll;\nEXEC dbo.EncryptedPayroll;\nEXEC dbo.RenamedPayroll;"
            }
        });
        await client.DiagnosticsAsync(uri, 1);
        var prepared = (await client.RequestAsync("textDocument/prepareCallHierarchy", new { textDocument = new { uri }, position = new { line = 0, character = 12 } })).GetProperty("result");
        var item = Assert.Single(prepared.EnumerateArray());
        Assert.Contains("Database", item.GetProperty("detail").GetString());
        var call = Assert.Single((await client.RequestAsync("callHierarchy/outgoingCalls", new { item })).GetProperty("result").EnumerateArray());
        var callee = call.GetProperty("to");
        Assert.Equal("dbo.ProcessPayroll", callee.GetProperty("name").GetString());
        var incoming = Assert.Single((await client.RequestAsync("callHierarchy/incomingCalls", new { item = callee })).GetProperty("result").EnumerateArray());
        Assert.Equal("dbo.RunPayroll", incoming.GetProperty("from").GetProperty("name").GetString());
        var encrypted = Assert.Single((await client.RequestAsync("textDocument/prepareCallHierarchy", new { textDocument = new { uri }, position = new { line = 1, character = 12 } })).GetProperty("result").EnumerateArray());
        Assert.Contains("body unavailable", encrypted.GetProperty("detail").GetString());
        Assert.Empty((await client.RequestAsync("callHierarchy/outgoingCalls", new { item = encrypted })).GetProperty("result").EnumerateArray());
        var renamed = Assert.Single((await client.RequestAsync("textDocument/prepareCallHierarchy", new { textDocument = new { uri }, position = new { line = 2, character = 12 } })).GetProperty("result").EnumerateArray());
        Assert.Equal("dbo.RenamedPayroll", renamed.GetProperty("name").GetString());
        await client.StopAsync();
    }
}
