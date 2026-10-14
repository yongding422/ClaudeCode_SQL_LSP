using System.Text.Json;

namespace SqlLsp.Server.IntegrationTests;

public class HoverTests
{
    private static async Task<JsonElement> Hover(LspClient client, string uri, int line, int character) =>
        (await client.RequestAsync("textDocument/hover", new { textDocument = new { uri }, position = new { line, character } })).GetProperty("result");

    [Fact]
    public async Task Real_hover_advertisement_cross_file_signature_incremental_overlay_and_close_restoration()
    {
        using var workspace = new NavigationWorkspace();
        var declaration = workspace.Write("procedure.sql", "CREATE PROCEDURE dbo.P @saved int AS SELECT 1;");
        var use = workspace.Write("query.sql", "-- 😀\r\nEXEC dbo.P;");
        var uri = new Uri(use).AbsoluteUri;
        var declUri = new Uri(declaration).AbsoluteUri;
        await using var client = new LspClient();
        var initialize = await client.InitializeAsync(new Uri(workspace.Root).AbsoluteUri);
        Assert.True(initialize.GetProperty("result").GetProperty("capabilities").GetProperty("hoverProvider").ValueKind is JsonValueKind.Object or JsonValueKind.True);
        await client.NotifyAsync("initialized", new { });
        var hover = await Hover(client, uri, 1, 8);
        Assert.Equal("plaintext", hover.GetProperty("contents").GetProperty("kind").GetString());
        Assert.Contains("@saved int", hover.GetProperty("contents").GetProperty("value").GetString());
        Assert.Equal(5, hover.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(10, hover.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await Hover(client, uri, 0, 0)).ValueKind);
        await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri = declUri, version = 1, languageId = "sql", text = "CREATE PROCEDURE dbo.P @changed bigint AS SELECT 2;" } });
        await client.DiagnosticsAsync(declUri, 1);
        hover = await Hover(client, uri, 1, 9);
        Assert.Contains("@changed bigint", hover.ToString());
        Assert.Contains("Source: UnsavedBuffer", hover.ToString());
        await client.NotifyAsync("textDocument/didChange", new { textDocument = new { uri = declUri, version = 2 }, contentChanges = new[] { new { text = "CREATE PROCEDURE dbo.P @latest nvarchar(20) AS SELECT 3;" } } });
        await client.DiagnosticsAsync(declUri, 2);
        Assert.Contains("@latest nvarchar(20)", (await Hover(client, uri, 1, 9)).ToString());
        await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = declUri } });
        await client.DiagnosticsAsync(declUri);
        Assert.Contains("@saved int", (await Hover(client, uri, 1, 9)).ToString());
        await client.StopAsync();
    }

    [Theory]
    [InlineData("plaintext", "plaintext")]
    [InlineData("markdown", "markdown")]
    [InlineData("empty", "plaintext")]
    public async Task Content_negotiation_keeps_hostile_identifiers_inside_plain_text_or_a_safe_fence(string requested, string expected)
    {
        using var workspace = new NavigationWorkspace();
        var declaration = workspace.Write("hostile.sql", "CREATE TABLE dbo.[```<img src=x onerror=alert(1)>](id int);");
        var uri = new Uri(declaration).AbsoluteUri;
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(workspace.Root).AbsoluteUri, hoverFormats: requested == "empty" ? [] : [requested, "plaintext"]);
        await client.NotifyAsync("initialized", new { });
        var hover = await Hover(client, uri, 0, 20);
        var contents = hover.GetProperty("contents");
        Assert.Equal(expected, contents.GetProperty("kind").GetString());
        var text = contents.GetProperty("value").GetString()!;
        Assert.Contains("```<img", text);
        if (expected == "markdown")
        {
            Assert.StartsWith("````text\n", text);
            Assert.EndsWith("\n````", text);
        }
        await client.StopAsync();
    }

    [Fact]
    public async Task Invalid_configuration_disables_hover_and_ambiguous_missing_or_unsupported_uses_return_null()
    {
        await using (var invalid = new LspClient())
        {
            var response = await invalid.InitializeAsync(initializationOptions: new { sql = new { mode = "unsupported" } });
            Assert.False(response.GetProperty("result").GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
            await invalid.NotifyAsync("initialized", new { });
            Assert.Equal(JsonValueKind.Null, (await Hover(invalid, "file:///absent.sql", 0, 0)).ValueKind);
            await invalid.StopAsync();
        }
        using var workspace = new NavigationWorkspace();
        workspace.Write("first.sql", "CREATE PROCEDURE dbo.P AS SELECT 1;");
        workspace.Write("duplicate.sql", "CREATE PROCEDURE dbo.P AS SELECT 2;");
        var uri = new Uri(workspace.Write("query.sql", "EXEC dbo.P;\nEXEC dbo.Missing;\nEXEC server.Db.dbo.P;")).AbsoluteUri;
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(workspace.Root).AbsoluteUri);
        await client.NotifyAsync("initialized", new { });
        foreach (var line in Enumerable.Range(0, 3)) Assert.Equal(JsonValueKind.Null, (await Hover(client, uri, line, 8)).ValueKind);
        await client.StopAsync();
    }

    [Fact]
    public async Task Pending_hover_can_be_cancelled_without_closing_the_buffer_or_stopping_the_server()
    {
        const string uri = "file:///hover-cancellation/large.sql";
        await using var client = new LspClient();
        await client.InitializeAsync();
        await client.NotifyAsync("initialized", new { });
        await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sql", version = 1, text = string.Concat(Enumerable.Repeat("SELECT 1;\n", 100_000)) } });
        var request = await client.StartRequestAsync("textDocument/hover", new { textDocument = new { uri }, position = new { line = 0, character = 0 } });
        await client.NotifyAsync("$/cancelRequest", new { id = request.Id });
        var result = await request.Response.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(-32800, result.GetProperty("error").GetProperty("code").GetInt32());
        await client.NotifyAsync("textDocument/didChange", new { textDocument = new { uri, version = 2 }, contentChanges = new[] { new { text = "CREATE TABLE dbo.AfterCancel(id int);" } } });
        await client.DiagnosticsAsync(uri, 2);
        Assert.Contains("[dbo].[AfterCancel]", (await Hover(client, uri, 0, 20)).ToString());
        await client.StopAsync();
    }
}
