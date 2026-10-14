using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlLsp.Server.IntegrationTests;

public class CallHierarchyTests
{
    [Fact]
    public async Task Real_prepare_outgoing_incoming_requests_group_call_sites_and_are_recursively_usable()
    {
        using var fixture = new NavigationWorkspace();
        var caller = fixture.Write("caller.sql", "CREATE PROC dbo.P AS BEGIN EXEC dbo.Q; EXEC dbo.Q; SELECT dbo.F(1); END;");
        fixture.Write("callee.sql", "CREATE PROC dbo.Q AS EXEC dbo.R;\nGO\nCREATE PROC dbo.R AS SELECT 1;\nGO\nCREATE FUNCTION dbo.F(@i int) RETURNS int AS BEGIN RETURN @i; END;");
        await using var client = new LspClient();
        var initialize = await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
        Assert.True(initialize.GetProperty("result").GetProperty("capabilities").GetProperty("callHierarchyProvider").ValueKind is JsonValueKind.True or JsonValueKind.Object);
        await client.NotifyAsync("initialized", new { });
        var item = await Prepare(client, caller, 0, 16);
        Assert.Equal("dbo.P", item.GetProperty("name").GetString());
        Assert.Equal(12, item.GetProperty("kind").GetInt32());
        var outgoing = await Calls(client, item, "outgoing");
        Assert.Equal(2, outgoing.GetArrayLength());
        var q = outgoing.EnumerateArray().Single(e => e.GetProperty("to").GetProperty("name").GetString() == "dbo.Q");
        Assert.Equal(2, q.GetProperty("fromRanges").GetArrayLength());
        Assert.Equal(0, q.GetProperty("fromRanges")[0].GetProperty("start").GetProperty("line").GetInt32());
        var next = Assert.Single((await Calls(client, q.GetProperty("to"), "outgoing")).EnumerateArray());
        Assert.Equal("dbo.R", next.GetProperty("to").GetProperty("name").GetString());
        var incoming = Assert.Single((await Calls(client, q.GetProperty("to"), "incoming")).EnumerateArray());
        Assert.Equal(new Uri(caller), new Uri(incoming.GetProperty("from").GetProperty("uri").GetString()!));
        Assert.Equal(2, incoming.GetProperty("fromRanges").GetArrayLength());
        await client.StopAsync();
    }

    [Fact]
    public async Task Unsaved_edits_invalidate_old_items_and_close_restores_disk_graph()
    {
        using var fixture = new NavigationWorkspace();
        const string disk = "CREATE PROC dbo.P AS EXEC dbo.Q;";
        var path = fixture.Write("p.sql", disk);
        fixture.Write("q.sql", "CREATE PROC dbo.Q AS SELECT 1;");
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
        await client.NotifyAsync("initialized", new { });
        var old = await Prepare(client, path, 0, 16);
        await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri = new Uri(path).AbsoluteUri, languageId = "sql", version = 1, text = "CREATE PROC dbo.P AS SELECT 1;" } });
        await client.DiagnosticsAsync(new Uri(path).AbsoluteUri, 1);
        Assert.Empty((await Calls(client, old, "outgoing")).EnumerateArray());
        var changed = await Prepare(client, path, 0, 16);
        Assert.Contains("UnsavedBuffer", changed.GetProperty("detail").GetString());
        Assert.Empty((await Calls(client, changed, "outgoing")).EnumerateArray());
        await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = new Uri(path).AbsoluteUri } });
        await client.DiagnosticsAsync(new Uri(path).AbsoluteUri);
        Assert.Single((await Calls(client, await Prepare(client, path, 0, 16), "outgoing")).EnumerateArray());
        await client.StopAsync();
    }

    [Fact]
    public async Task Coverage_counts_ambiguity_missing_dynamic_and_tampered_items_do_not_fabricate_graphs()
    {
        using var fixture = new NavigationWorkspace();
        var path = fixture.Write("p.sql", "CREATE PROC dbo.P AS BEGIN EXEC Q; EXEC dbo.Missing; EXEC(N'EXEC dbo.Q'); END;\nGO\nCREATE PROC dbo.Q AS SELECT 1;\nGO\nCREATE PROC audit.Q AS SELECT 2;");
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
        await client.NotifyAsync("initialized", new { });
        var item = await Prepare(client, path, 0, 16);
        Assert.Contains("ambiguous 1, missing 1, unsupported 1", item.GetProperty("detail").GetString());
        Assert.Empty((await Calls(client, item, "outgoing")).EnumerateArray());
        var tampered = JsonNode.Parse(item.GetRawText())!;
        tampered["data"]!["revision"] = "wrong";
        Assert.Empty((await Calls(client, JsonSerializer.SerializeToElement(tampered), "outgoing")).EnumerateArray());
        tampered["data"]!["identity"] = "not an object";
        Assert.Empty((await Calls(client, JsonSerializer.SerializeToElement(tampered), "incoming")).EnumerateArray());
        await client.StopAsync();
    }

    [Fact]
    public async Task Invalid_configuration_disables_call_hierarchy_and_answers_without_method_not_found()
    {
        await using var client = new LspClient();
        var initialize = await client.InitializeAsync(initializationOptions: new { unexpected = true });
        Assert.False(initialize.GetProperty("result").GetProperty("capabilities").GetProperty("callHierarchyProvider").GetBoolean());
        await client.NotifyAsync("initialized", new { });
        var response = await client.RequestAsync("textDocument/prepareCallHierarchy", new { textDocument = new { uri = "file:///missing.sql" }, position = new { line = 0, character = 0 } });
        Assert.Empty(response.GetProperty("result").EnumerateArray());
        await client.StopAsync();
    }

    [Fact]
    public async Task Utf16_crlf_quoted_names_and_non_callable_or_split_surrogate_positions_are_handled()
    {
        using var fixture = new NavigationWorkspace();
        var path = fixture.Write("modules 中文.sql", "CREATE PROC [dbo].[P😀] AS BEGIN\r\n EXEC [dbo].[Q😀];\r\nEND;\r\nGO\r\nCREATE PROC [dbo].[Q😀] AS SELECT 1;\r\nGO\r\nCREATE TABLE dbo.T(id int);");
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
        await client.NotifyAsync("initialized", new { });
        var item = await Prepare(client, path, 0, 19);
        Assert.Equal("dbo.P😀", item.GetProperty("name").GetString());
        var call = Assert.Single((await Calls(client, item, "outgoing")).EnumerateArray());
        Assert.Equal("dbo.Q😀", call.GetProperty("to").GetProperty("name").GetString());
        var range = call.GetProperty("fromRanges")[0];
        Assert.Equal(1, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(6, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(17, range.GetProperty("end").GetProperty("character").GetInt32());
        foreach (var position in new[] { new { line = 0, character = 21 }, new { line = 6, character = 15 } })
            Assert.Empty((await client.RequestAsync("textDocument/prepareCallHierarchy", new
            { textDocument = new { uri = new Uri(path).AbsoluteUri }, position })).GetProperty("result").EnumerateArray());
        await client.StopAsync();
    }

    private static async Task<JsonElement> Prepare(LspClient client, string path, int line, int character) =>
        Assert.Single((await client.RequestAsync("textDocument/prepareCallHierarchy", new
        { textDocument = new { uri = new Uri(path).AbsoluteUri }, position = new { line, character } })).GetProperty("result").EnumerateArray());

    private static async Task<JsonElement> Calls(LspClient client, JsonElement item, string direction) =>
        (await client.RequestAsync("callHierarchy/" + direction + "Calls", new { item })).GetProperty("result");
}
