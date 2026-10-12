using System.Text.Json;

namespace SqlLsp.Server.IntegrationTests;

public class VerticalSliceTests
{
    private const string BufferUri = "file:///sql-lsp-tests/unsaved.sql";

    [Fact]
    public async Task Broken_sql_reports_exact_range_incremental_fix_clears_and_close_clears()
    {
        await using var client = new LspClient();
        await client.InitializeAsync();
        await client.NotifyAsync("initialized", new { });
        const string sql = "-- 中文 😀\r\nCREATE PROCEDURE dbo.ProcessPayroll @PayrollId int AS\r\nBEGIN\r\n    SELECT FROM dbo.Payroll;\r\nEND;";
        await OpenAsync(client, BufferUri, sql);
        var broken = await client.DiagnosticsAsync(BufferUri, 1);
        var error = broken.GetProperty("diagnostics")[0];
        Assert.Equal("SqlScriptDOM", error.GetProperty("source").GetString());
        Assert.Equal(1, error.GetProperty("severity").GetInt32());
        AssertPosition(error.GetProperty("range").GetProperty("start"), 3, 11);
        AssertPosition(error.GetProperty("range").GetProperty("end"), 3, 15);
        await client.NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri = BufferUri, version = 2 },
            contentChanges = new[] { new { range = new { start = new { line = 3, character = 11 }, end = new { line = 3, character = 11 } }, text = "PayrollId " } }
        });
        Assert.Empty((await client.DiagnosticsAsync(BufferUri, 2)).GetProperty("diagnostics").EnumerateArray());
        var symbols = await SymbolsAsync(client, BufferUri);
        var symbol = Assert.Single(symbols.EnumerateArray());
        Assert.Equal("dbo.ProcessPayroll", symbol.GetProperty("name").GetString());
        Assert.Equal(12, symbol.GetProperty("kind").GetInt32());
        AssertPosition(symbol.GetProperty("selectionRange").GetProperty("start"), 1, 17);
        await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = BufferUri } });
        Assert.Empty((await client.DiagnosticsAsync(BufferUri)).GetProperty("diagnostics").EnumerateArray());
        Assert.Empty((await SymbolsAsync(client, BufferUri)).EnumerateArray());
        await client.StopAsync();
    }

    [Fact]
    public async Task Full_and_multiple_incremental_edits_update_unsaved_symbols_and_reject_old_versions()
    {
        await using var client = new LspClient();
        await client.InitializeAsync();
        await client.NotifyAsync("initialized", new { });
        await OpenAsync(client, BufferUri, "CREATE TABLE dbo.before (id int);");
        await client.DiagnosticsAsync(BufferUri, 1);
        await ChangeFullAsync(client, BufferUri, 5, "CREATE VIEW dbo.after AS SELECT 1 AS n;");
        await client.DiagnosticsAsync(BufferUri, 5);
        await ChangeFullAsync(client, BufferUri, 4, "SELECT FROM;");
        Assert.Equal("dbo.after", (await SymbolsAsync(client, BufferUri))[0].GetProperty("name").GetString());
        await client.NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri = BufferUri, version = 6 },
            contentChanges = new[]
            {
                new { range = new { start = new { line = 0, character = 16 }, end = new { line = 0, character = 21 } }, text = "edited" },
                new { range = new { start = new { line = 0, character = 33 }, end = new { line = 0, character = 34 } }, text = "2" }
            }
        });
        Assert.Empty((await client.DiagnosticsAsync(BufferUri, 6)).GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("dbo.edited", (await SymbolsAsync(client, BufferUri))[0].GetProperty("name").GetString());
        await client.StopAsync();
    }

    [Fact]
    public async Task Sample_workspace_indexes_unopened_files_and_handles_parallel_symbol_requests()
    {
        var root = Path.Combine(LspClient.RepositoryRoot, "samples", "sql-workspace");
        var files = Directory.GetFiles(root, "*.sql");
        Assert.True(files.Length >= 20);
        await using var client = new LspClient();
        var initialize = await client.InitializeAsync(new Uri(root).AbsoluteUri);
        var capabilities = initialize.GetProperty("result").GetProperty("capabilities");
        Assert.Equal(2, capabilities.GetProperty("textDocumentSync").GetProperty("change").GetInt32());
        Assert.True(capabilities.GetProperty("documentSymbolProvider").ValueKind is JsonValueKind.True or JsonValueKind.Object);
        foreach (var name in new[] { "renameProvider", "completionProvider" })
            Assert.True(!capabilities.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.False or JsonValueKind.Null);
        if (capabilities.TryGetProperty("positionEncoding", out var encoding)) Assert.Equal("utf-16", encoding.GetString());
        await client.NotifyAsync("initialized", new { });
        var results = await Task.WhenAll(files.Select(file => SymbolsAsync(client, new Uri(file).AbsoluteUri)));
        Assert.All(results, result => Assert.Single(result.EnumerateArray()));
        foreach (var file in files)
        {
            var uri = new Uri(file).AbsoluteUri;
            await OpenAsync(client, uri, await File.ReadAllTextAsync(file));
            Assert.Empty((await client.DiagnosticsAsync(uri, 1)).GetProperty("diagnostics").EnumerateArray());
        }
        await client.StopAsync();
    }

    [Fact]
    public async Task Rapid_changes_and_close_reopen_keep_latest_buffer_identity()
    {
        await using var client = new LspClient();
        await client.InitializeAsync();
        await client.NotifyAsync("initialized", new { });
        await OpenAsync(client, BufferUri, "SELECT FROM;");
        for (var version = 2; version <= 20; version++)
            await ChangeFullAsync(client, BufferUri, version, $"CREATE PROC dbo.p{version} AS SELECT {version};");
        await client.DiagnosticsAsync(BufferUri, 20);
        Assert.Equal("dbo.p20", (await SymbolsAsync(client, BufferUri))[0].GetProperty("name").GetString());
        await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = BufferUri } });
        await client.DiagnosticsAsync(BufferUri);
        await OpenAsync(client, BufferUri, "CREATE PROC dbo.reopened AS SELECT 1;");
        await client.DiagnosticsAsync(BufferUri, 1);
        Assert.Equal("dbo.reopened", (await SymbolsAsync(client, BufferUri))[0].GetProperty("name").GetString());
        await client.StopAsync();
    }

    [Fact]
    public async Task Flat_symbol_clients_receive_symbol_information_and_unknown_requests_return_method_not_found()
    {
        await using var client = new LspClient();
        await client.InitializeAsync(hierarchical: false);
        await client.NotifyAsync("initialized", new { });
        await OpenAsync(client, BufferUri, "CREATE TABLE dbo.t (id int);");
        await client.DiagnosticsAsync(BufferUri, 1);
        var symbol = Assert.Single((await SymbolsAsync(client, BufferUri)).EnumerateArray());
        Assert.Equal(BufferUri, symbol.GetProperty("location").GetProperty("uri").GetString());
        Assert.False(symbol.TryGetProperty("selectionRange", out _));
        var response = await client.RequestAsync("textDocument/rename", new { textDocument = new { uri = BufferUri }, position = new { line = 0, character = 20 }, newName = "new" });
        Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
        await client.StopAsync();
    }

    [Fact]
    public async Task Initialize_options_select_parser_dialect()
    {
        await using var client = new LspClient();
        await client.InitializeAsync(initializationOptions: new { sql = new { mode = "offline", parserVersion = "120", quotedIdentifier = true } });
        await client.NotifyAsync("initialized", new { });
        await OpenAsync(client, BufferUri, "CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1;");
        Assert.NotEmpty((await client.DiagnosticsAsync(BufferUri, 1)).GetProperty("diagnostics").EnumerateArray());
        await client.StopAsync();
    }

    [Fact]
    public async Task Close_restores_disk_and_saved_content_over_an_unsaved_overlay()
    {
        var root = Path.Combine(TestPaths.TempRoot, "sql-lsp-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "saved file.sql");
            await File.WriteAllTextAsync(path, "CREATE PROC dbo.disk AS SELECT 1;");
            var uri = new Uri(path).AbsoluteUri;
            await using var client = new LspClient();
            await client.InitializeAsync(new Uri(root).AbsoluteUri);
            await client.NotifyAsync("initialized", new { });
            Assert.Equal("dbo.disk", (await SymbolsAsync(client, uri))[0].GetProperty("name").GetString());
            await OpenAsync(client, uri, "CREATE PROC dbo.unsaved AS SELECT 1;");
            await client.DiagnosticsAsync(uri, 1);
            Assert.Equal("dbo.unsaved", (await SymbolsAsync(client, uri))[0].GetProperty("name").GetString());
            await File.WriteAllTextAsync(path, "CREATE PROC dbo.saved AS SELECT 1;");
            await client.NotifyAsync("textDocument/didSave", new { textDocument = new { uri } });
            await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri } });
            await client.DiagnosticsAsync(uri);
            Assert.Equal("dbo.saved", (await SymbolsAsync(client, uri))[0].GetProperty("name").GetString());
            await client.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Unicode_comment_on_same_line_uses_utf16_and_oversize_document_clears_analysis()
    {
        await using var client = new LspClient();
        await client.InitializeAsync(initializationOptions: new { workspace = new { maxFileSizeBytes = 100 } });
        await client.NotifyAsync("initialized", new { });
        const string prefix = "/* 中文 😀 */\tSELECT ";
        await OpenAsync(client, BufferUri, prefix + "FROM;");
        var error = (await client.DiagnosticsAsync(BufferUri, 1)).GetProperty("diagnostics")[0];
        AssertPosition(error.GetProperty("range").GetProperty("start"), 0, prefix.Length);
        await ChangeFullAsync(client, BufferUri, 2, new string('x', 200));
        Assert.Empty((await client.DiagnosticsAsync(BufferUri, 2)).GetProperty("diagnostics").EnumerateArray());
        Assert.Empty((await SymbolsAsync(client, BufferUri)).EnumerateArray());
        await ChangeFullAsync(client, BufferUri, 3, "CREATE TABLE dbo.recovered (id int);");
        Assert.Empty((await client.DiagnosticsAsync(BufferUri, 3)).GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("dbo.recovered", (await SymbolsAsync(client, BufferUri))[0].GetProperty("name").GetString());
        await client.StopAsync();
    }

    private static Task OpenAsync(LspClient client, string uri, string text) => client.NotifyAsync("textDocument/didOpen",
        new { textDocument = new { uri, languageId = "sql", version = 1, text } });
    private static Task ChangeFullAsync(LspClient client, string uri, int version, string text) => client.NotifyAsync("textDocument/didChange",
        new { textDocument = new { uri, version }, contentChanges = new[] { new { text } } });
    private static async Task<JsonElement> SymbolsAsync(LspClient client, string uri)
    {
        var response = await client.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri } });
        Assert.False(response.TryGetProperty("error", out var error), error.ToString());
        return response.GetProperty("result");
    }
    private static void AssertPosition(JsonElement position, int line, int character)
    {
        Assert.Equal(line, position.GetProperty("line").GetInt32());
        Assert.Equal(character, position.GetProperty("character").GetInt32());
    }
}
