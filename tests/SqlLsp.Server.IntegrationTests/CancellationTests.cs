namespace SqlLsp.Server.IntegrationTests;

public class CancellationTests
{
    [Fact]
    public async Task Canceling_pending_symbol_request_returns_request_cancelled_and_server_stays_usable()
    {
        const string uri = "file:///sql-lsp-tests/large.sql";
        await using var client = new LspClient();
        await client.InitializeAsync();
        await client.NotifyAsync("initialized", new { });
        var text = string.Concat(Enumerable.Repeat("SELECT 1;\n", 100_000));
        await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sql", version = 1, text } });
        var request = await client.StartRequestAsync("textDocument/documentSymbol", new { textDocument = new { uri } });
        await client.NotifyAsync("$/cancelRequest", new { id = request.Id });
        var response = await request.Response.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(-32800, response.GetProperty("error").GetProperty("code").GetInt32());
        // Cancel old document analysis with a cheap newer buffer; cancellation of a request does not close it.
        await client.NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = "CREATE TABLE dbo.afterCancellation (id int);" } }
        });
        await client.DiagnosticsAsync(uri, 2);
        var symbols = await client.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri } });
        Assert.Equal("dbo.afterCancellation", symbols.GetProperty("result")[0].GetProperty("name").GetString());
        await client.StopAsync();
    }
}
