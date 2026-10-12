using System.Text.Json;

namespace SqlLsp.Server.IntegrationTests;

public class HardeningTests
{
    [Fact]
    public async Task Unexpected_invalid_workspace_path_fails_closed_and_still_answers_initialize()
    {
        await using var client = new LspClient();
        var response = await client.InitializeAsync("file:///workspace%00PRIVATE_SENTINEL");
        var capabilities = response.GetProperty("result").GetProperty("capabilities");
        Assert.False(capabilities.GetProperty("definitionProvider").GetBoolean());
        await client.NotifyAsync("initialized", new { });
        var message = await client.NotificationAsync("window/showMessage");
        Assert.Contains("initialization failed", message.ToString());
        Assert.DoesNotContain("PRIVATE_SENTINEL", message.ToString());
        Assert.Empty((await client.RequestAsync("workspace/symbol", new { query = "" })).GetProperty("result").EnumerateArray());
        await client.StopAsync();
        Assert.DoesNotContain("PRIVATE_SENTINEL", await client.StderrAsync());
    }

    [Fact]
    public async Task Environment_secret_is_redacted_from_process_logs_and_wire_status()
    {
        const string sentinel = "SQLLSP_PRIVATE_SENTINEL_093153";
        var environment = new Dictionary<string, string?> { ["SQLLSP_REDACTION_TEST"] = "Password=" + sentinel + ";Bogus=" + sentinel };
        using var fixture = new NavigationWorkspace();
        await using var client = new LspClient(environment, fixture.Root);
        var initialized = await client.InitializeAsync(initializationOptions: new
        {
            sql = new { mode = "online" },
            databases = new[] { new { id = "test", connectionStringEnv = "SQLLSP_REDACTION_TEST", environment = "test",
                server = "localhost", database = "Fixture", readOnly = true, enabled = true } }
        });
        Assert.DoesNotContain(sentinel, initialized.ToString());
        await client.NotifyAsync("initialized", new { });
        var search = await client.RequestAsync("workspace/symbol", new { query = "" });
        Assert.Empty(search.GetProperty("result").EnumerateArray());
        var status = await client.NotificationAsync("window/logMessage");
        Assert.Contains("invalid-connection-settings", status.ToString());
        Assert.DoesNotContain(sentinel, status.ToString());
        await client.StopAsync();
        Assert.DoesNotContain(sentinel, await client.StderrAsync());
    }

    [Fact]
    public async Task Startup_in_unrelated_directory_with_spaces_and_unicode_needs_no_dotnet_for_apphost()
    {
        using var fixture = new NavigationWorkspace();
        var cwd = Path.Combine(fixture.Root, "working directory 中文");
        Directory.CreateDirectory(cwd);
        var environment = new Dictionary<string, string?> { ["DOTNET_ROOT"] = Path.Combine(cwd, "absent-runtime"), ["DOTNET_MULTILEVEL_LOOKUP"] = "0" };
        // The packaged run uses an absolute apphost with an empty PATH: no machine dotnet discovery.
        if (Environment.GetEnvironmentVariable("SQLLSP_TEST_SERVER_EXE") != null) environment["PATH"] = "";
        await using var client = new LspClient(environment, cwd);
        await client.InitializeAsync(new Uri(cwd).AbsoluteUri);
        await client.NotifyAsync("initialized", new { });
        var uri = new Uri(Path.Combine(cwd, "query.sql")).AbsoluteUri;
        await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sql", version = 1, text = "SELECT FROM;" } });
        Assert.NotEmpty((await client.DiagnosticsAsync(uri, 1)).GetProperty("diagnostics").EnumerateArray());
        await client.StopAsync();
    }

    [Fact]
    public async Task Unknown_method_and_concurrent_requests_do_not_corrupt_stdio_or_prevent_shutdown()
    {
        using var fixture = new NavigationWorkspace();
        fixture.Write("definition.sql", "CREATE TABLE dbo.Target (id int);");
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
        await client.NotifyAsync("initialized", new { });
        var missing = await client.RequestAsync("sqlscriptdom/unsupported", new { });
        Assert.Equal(-32601, missing.GetProperty("error").GetProperty("code").GetInt32());
        var responses = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => client.RequestAsync("workspace/symbol", new { query = "Target" })));
        Assert.All(responses, r => Assert.Single(r.GetProperty("result").EnumerateArray()));
        await client.StopAsync();
    }

    [Fact]
    public async Task Rapid_multi_document_replacements_and_two_roots_keep_latest_snapshots_and_isolation()
    {
        using var first = new NavigationWorkspace();
        using var second = new NavigationWorkspace();
        var target = first.Write("target.sql", "CREATE TABLE dbo.Target (id int);");
        second.Write("target.sql", "CREATE TABLE dbo.Target (id int);");
        var uses = Enumerable.Range(0, 16).Select(i => first.Write($"use{i}.sql", "SELECT * FROM dbo.Target;")).ToArray();
        await using var client = new LspClient();
        await client.InitializeAsync(workspaceFolderUris: [new Uri(first.Root).AbsoluteUri, new Uri(second.Root).AbsoluteUri]);
        await client.NotifyAsync("initialized", new { });
        foreach (var path in uses)
        {
            var uri = new Uri(path).AbsoluteUri;
            await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sql", version = 1, text = "SELECT FROM;" } });
            for (var version = 2; version <= 20; version++)
                await client.NotifyAsync("textDocument/didChange", new { textDocument = new { uri, version }, contentChanges = new[] { new { text = version == 20 ? "SELECT * FROM dbo.Target;" : "SELECT FROM;" } } });
        }
        var results = await Task.WhenAll(uses.Select(path => client.RequestAsync("textDocument/definition", new
        { textDocument = new { uri = new Uri(path).AbsoluteUri }, position = new { line = 0, character = 20 } })));
        Assert.All(results, r =>
        {
            var result = r.GetProperty("result");
            var location = result.ValueKind == JsonValueKind.Object ? result : Assert.Single(result.EnumerateArray());
            Assert.Equal(new Uri(target), new Uri(location.GetProperty("uri").GetString()!));
        });
        foreach (var path in uses) await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = new Uri(path).AbsoluteUri } });
        await client.StopAsync();
    }
}
