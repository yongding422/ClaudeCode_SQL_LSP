using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SqlLsp.Mcp.IntegrationTests;

public class PluginSettingsTests
{
    private const string Sql = "CREATE PROC dbo.P AS SELECT 1;";
    private const string Enabled = """{"version":1,"metrics":{"enabled":true,"directory":"local 中文/metrics"},"analysisCache":{"enabled":true,"directory":"local 中文/cache"}}""";

    [Fact]
    public async Task Marketplace_stdio_discovers_project_settings_and_reuses_reports_and_metrics_after_restart()
    {
        var root = NewRoot();
        try
        {
            await Settings(root, Enabled, new UTF8Encoding(true));
            await using (var first = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true))
            {
                var before = await Metrics(first);
                Assert.True(before.GetProperty("enabled").GetBoolean());
                Assert.Equal(Path.Combine(root, "local 中文", "metrics"), before.GetProperty("directory").GetString());
                Assert.False(Directory.Exists(Path.Combine(root, "local 中文")));
                Assert.Equal("Stored", (await Analyze(first)).GetProperty("cache").GetProperty("status").GetString());
                Assert.Equal(2, (await Metrics(first)).GetProperty("observedInProcess").GetInt32());
            }
            await using (var second = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true))
            {
                Assert.Equal(2, (await Metrics(second, true)).GetProperty("report").GetProperty("requests").GetInt32());
                Assert.Equal("Hit", (await Analyze(second)).GetProperty("cache").GetProperty("status").GetString());
                // Changes take effect on restart, not partway through a request/session.
                await Settings(root, """{"version":1,"metrics":{"enabled":false},"analysisCache":{"enabled":false}}""");
                Assert.True((await Metrics(second)).GetProperty("enabled").GetBoolean());
            }
            await using var disabled = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true);
            Assert.False((await Metrics(disabled)).GetProperty("enabled").GetBoolean());
            Assert.Equal("Disabled", (await Analyze(disabled)).GetProperty("cache").GetProperty("status").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Enabled_sections_have_local_defaults_and_metrics_inspection_does_not_initialize_sql()
    {
        var root = NewRoot();
        try
        {
            await Settings(root, """{"version":1,"metrics":{"enabled":true},"analysisCache":{"enabled":true}}""");
            await using (var metricsOnly = await McpClient.Start(Sql, "{\"sql\":{\"mode\":\"invalid\"}}", rootDirectory: root, preserveWorkspace: true))
            {
                Assert.Equal(Path.Combine(root, ".sqlscriptdom", "metrics"), (await Metrics(metricsOnly)).GetProperty("directory").GetString());
                Assert.False(Directory.Exists(Path.Combine(root, ".sqlscriptdom")));
            }
            await using (var analysis = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true))
            {
                Assert.Equal("Stored", (await Analyze(analysis)).GetProperty("cache").GetProperty("status").GetString());
                Assert.True(Directory.Exists(Path.Combine(root, ".sqlscriptdom", "cache", "analysis-v1")));
            }
            var summary = await Run(root, ["--metrics-summary"]);
            Assert.Equal(0, summary.Code);
            using var json = JsonDocument.Parse(summary.Output);
            Assert.Equal(2, json.RootElement.GetProperty("requests").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"metrics\":{\"directory\":\"unused\"},\"analysisCache\":{}}")]
    [InlineData("{\"version\":1,\"metrics\":{\"enabled\":false,\"directory\":\"unused\"},\"analysisCache\":{\"enabled\":false}}")]
    public async Task Missing_or_disabled_sections_do_not_opt_in(string settings)
    {
        var root = NewRoot();
        try
        {
            await Settings(root, settings);
            await using var client = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true);
            Assert.False((await Metrics(client)).GetProperty("enabled").GetBoolean());
            Assert.Equal("Disabled", (await Analyze(client)).GetProperty("cache").GetProperty("status").GetString());
            Assert.False(Directory.Exists(Path.Combine(root, "unused")));
            Assert.False(Directory.Exists(Path.Combine(root, ".sqlscriptdom")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Standalone_workspace_selection_and_explicit_settings_resolve_paths_from_the_selected_file()
    {
        var root = NewRoot();
        try
        {
            var workspace = Path.Combine(root, "sql"); Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(Path.Combine(workspace, "p.sql"), Sql);
            // An invalid file in cwd must not override --workspace discovery.
            await Settings(root, "PRIVATE_SQL_CANARY");
            await Settings(workspace, Enabled);
            var automatic = await Run(root, ["--workspace", workspace, "--build-cache"]);
            Assert.Equal(0, automatic.Code);
            Assert.True(Directory.Exists(Path.Combine(workspace, "local 中文", "cache", "analysis-v1")));
            var selected = Path.Combine(root, "settings"); Directory.CreateDirectory(selected);
            await Settings(selected, Enabled);
            var explicitResult = await Run(root, ["--workspace", workspace, "--settings", "settings/.sqlscriptdom.json", "--build-cache"]);
            Assert.Equal(0, explicitResult.Code);
            Assert.True(Directory.Exists(Path.Combine(selected, "local 中文", "cache", "analysis-v1")));
            Assert.True(Directory.Exists(Path.Combine(selected, "local 中文", "metrics", "metrics-v1")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Explicit_flags_override_environment_which_overrides_file_including_disabled_sections()
    {
        var root = NewRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "p.sql"), Sql);
            await Settings(root, Enabled);
            var environment = new Dictionary<string, string>
            {
                ["SQLLSP_METRICS_DIR"] = Path.Combine(root, "env-metrics"),
                ["SQLLSP_ANALYSIS_CACHE_DIR"] = Path.Combine(root, "env-cache")
            };
            Assert.Equal(0, (await Run(root, ["--build-cache"], environment)).Code);
            Assert.True(Directory.Exists(Path.Combine(root, "env-metrics", "metrics-v1")));
            Assert.True(Directory.Exists(Path.Combine(root, "env-cache", "analysis-v1")));
            Assert.False(Directory.Exists(Path.Combine(root, "local 中文")));
            await Settings(root, """{"version":1,"metrics":{"enabled":false},"analysisCache":{"enabled":false}}""");
            Assert.Equal(0, (await Run(root, ["--build-cache"], environment)).Code);
            Assert.Equal(0, (await Run(root, ["--build-cache", "--metrics-directory", Path.Combine(root, "cli-metrics"), "--cache-directory", Path.Combine(root, "cli-cache")], environment)).Code);
            Assert.True(Directory.Exists(Path.Combine(root, "cli-metrics", "metrics-v1")));
            Assert.True(Directory.Exists(Path.Combine(root, "cli-cache", "analysis-v1")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":1,\"PRIVATE_SQL_CANARY\":1}")]
    [InlineData("{\"version\":1,\"metrics\":{\"enabled\":\"PRIVATE_SQL_CANARY\"}}")]
    [InlineData("{\"version\":1,\"metrics\":{\"enabled\":true,\"directory\":\"  \"}}")]
    [InlineData("{\"version\":1,\"metrics\":{\"enabled\":false,\"enabled\":true}}")]
    [InlineData("{\"version\":1,\"version\":1}")]
    [InlineData("{\"version\":1,\"analysisCache\":{\"enabled\":1}}")]
    [InlineData("{\"version\":1,\"sql\":{\"mode\":\"online\"}}")]
    [InlineData("{\"version\":1,\"metrics\":{\"enabeld\":true}}")]
    [InlineData("{\"version\":1,")]
    public async Task Invalid_files_fail_before_protocol_start_without_echoing_private_values(string settings)
    {
        var root = NewRoot();
        try
        {
            await Settings(root, settings);
            var result = await Run(root, ["--stdio"]);
            Assert.Equal(1, result.Code);
            Assert.Empty(result.Output);
            Assert.Equal("SQL adapter command failed: invalid options/settings or unavailable sources/metrics.", result.Error.Trim());
            Assert.False(Directory.Exists(Path.Combine(root, ".sqlscriptdom")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Oversized_and_missing_explicit_files_fail_without_creating_storage()
    {
        var root = NewRoot();
        try
        {
            await Settings(root, new string(' ', 65_537));
            Assert.Equal(1, (await Run(root, ["--stdio"])).Code);
            File.Delete(Path.Combine(root, ".sqlscriptdom.json"));
            Assert.Equal(1, (await Run(root, ["--stdio", "--settings", "PRIVATE_SQL_CANARY-missing.json"])).Code);
            Assert.Equal(1, (await Run(root, ["--stdio", "--settings"])).Code);
            Assert.False(Directory.Exists(Path.Combine(root, ".sqlscriptdom")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(TestPaths.TempRoot, "plugin settings 中文 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static Task Settings(string root, string settings, Encoding? encoding = null) => File.WriteAllTextAsync(Path.Combine(root, ".sqlscriptdom.json"), settings, encoding ?? new UTF8Encoding(false));
    private static async Task<JsonElement> Metrics(McpClient client, bool previous = false) => McpClient.Data(await client.Tool("get_metrics", new { includePreviousSessions = previous }));
    private static async Task<JsonElement> Analyze(McpClient client)
    {
        var selected = McpClient.Data(await client.Tool("find_procedures", new { query = "dbo.P" })).GetProperty("modules")[0];
        return McpClient.Data(await client.Tool("analyze_procedure", new
        {
            documentUri = selected.GetProperty("target").GetProperty("uri").GetString(),
            line = selected.GetProperty("selection").GetProperty("line").GetInt32(),
            character = selected.GetProperty("selection").GetProperty("character").GetInt32()
        }));
    }
    private static async Task<(int Code, string Output, string Error)> Run(string root, string[] args, Dictionary<string, string>? environment = null)
    {
        var info = McpClient.Command(root);
        foreach (var argument in args) info.ArgumentList.Add(argument);
        if (environment != null) foreach (var item in environment) info.Environment[item.Key] = item.Value;
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var stderr = await error;
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", stderr);
        return (process.ExitCode, await output, stderr);
    }
}
