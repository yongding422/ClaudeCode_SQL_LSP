namespace SqlLsp.Server.IntegrationTests;

public class HybridConfigurationTests
{
    [Theory]
    [InlineData("no-mapping")]
    [InlineData("unknown-profile")]
    [InlineData("disabled-profile")]
    [InlineData("unregistered-root")]
    [InlineData("nonfile-root")]
    [InlineData("duplicate-root")]
    [InlineData("invalid-default")]
    [InlineData("stale-opt-in")]
    [InlineData("invalid-path")]
    public async Task Unsafe_or_incomplete_hybrid_mappings_fail_closed(string scenario)
    {
        var root = Path.Combine(TestPaths.TempRoot, "hybrid-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var uri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
            var map = new Dictionary<string, object> { ["workspaceRoot"] = uri, ["databaseProfiles"] = new[] { "test" } };
            var resolution = new Dictionary<string, object> { ["workspaceMappings"] = new[] { map } };
            var profile = Profile();
            switch (scenario)
            {
                case "no-mapping": resolution.Clear(); break;
                case "unknown-profile": map["databaseProfiles"] = new[] { "unapproved" }; break;
                case "disabled-profile": profile["enabled"] = false; break;
                case "unregistered-root": map["workspaceRoot"] = new Uri(TestPaths.TempRoot).AbsoluteUri; break;
                case "nonfile-root": map["workspaceRoot"] = "https://example.invalid/root"; break;
                case "duplicate-root": resolution["workspaceMappings"] = new[] { map, map }; break;
                case "invalid-default": map["defaultDatabaseProfile"] = "unapproved"; break;
                case "stale-opt-in": resolution["allowStaleDatabaseCache"] = true; break;
                case "invalid-path": map["workspaceRoot"] = "file:///C:/invalid%00path/"; break;
            }
            await using var client = new LspClient();
            var response = await client.InitializeAsync(uri, new { sql = new { mode = "hybrid" }, databases = new[] { profile }, resolution });
            Assert.False(response.GetProperty("result").GetProperty("capabilities").GetProperty("definitionProvider").GetBoolean());
            await client.NotifyAsync("initialized", new { });
            var error = await client.NotificationAsync("window/showMessage");
            Assert.Contains("initializationOptions", error.GetProperty("message").GetString());
            await client.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Dictionary<string, object> Profile() => new()
    {
        ["id"] = "test",
        ["connectionStringEnv"] = "SQLLSP_HYBRID_INTENTIONALLY_ABSENT",
        ["environment"] = "test",
        ["server"] = "127.0.0.1,1",
        ["database"] = "Fixture",
        ["readOnly"] = true,
        ["enabled"] = true,
        ["connectTimeoutSeconds"] = 1,
        ["loadTimeoutSeconds"] = 1
    };

    [Fact]
    public async Task Hybrid_missing_secret_degrades_to_local_navigation_and_unmapped_roots_remain_isolated()
    {
        var parent = Path.Combine(TestPaths.TempRoot, "hybrid-roots-" + Guid.NewGuid().ToString("N"));
        var mapped = Path.Combine(parent, "mapped");
        var unmapped = Path.Combine(parent, "unmapped");
        Directory.CreateDirectory(mapped); Directory.CreateDirectory(unmapped);
        try
        {
            var localPath = Path.Combine(mapped, "proc.sql");
            await File.WriteAllTextAsync(localPath, "CREATE PROCEDURE dbo.P AS SELECT 1;");
            var roots = new[] { new Uri(mapped + Path.DirectorySeparatorChar).AbsoluteUri, new Uri(unmapped + Path.DirectorySeparatorChar).AbsoluteUri };
            await using var client = new LspClient();
            await client.InitializeAsync(initializationOptions: new
            {
                sql = new { mode = "hybrid" },
                databases = new[] { Profile() },
                resolution = new { workspaceMappings = new[] { new { workspaceRoot = roots[0], databaseProfiles = new[] { "test" } } } }
            }, workspaceFolderUris: roots);
            await client.NotifyAsync("initialized", new { });
            var search = await client.RequestAsync("workspace/symbol", new { query = "dbo.P" });
            var symbol = Assert.Single(search.GetProperty("result").EnumerateArray());
            Assert.Equal("Workspace", symbol.GetProperty("data").GetProperty("source").GetString());
            Assert.Equal("Unavailable", symbol.GetProperty("data").GetProperty("drift").GetString());
            var status = await client.NotificationAsync("window/logMessage");
            Assert.Contains("missing-secret", status.GetProperty("message").GetString());
            foreach (var folder in new[] { mapped, unmapped })
            {
                var uri = new Uri(Path.Combine(folder, "query.sql")).AbsoluteUri;
                await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sql", version = 1, text = "EXEC dbo.P;" } });
                await client.DiagnosticsAsync(uri, 1);
                var definition = await client.RequestAsync("textDocument/definition", new { textDocument = new { uri }, position = new { line = 0, character = 6 } });
                var result = definition.GetProperty("result");
                if (folder == unmapped) Assert.Empty(result.EnumerateArray());
                else
                {
                    if (result.ValueKind == System.Text.Json.JsonValueKind.Array) result = Assert.Single(result.EnumerateArray());
                    Assert.Equal(new Uri(localPath), new Uri(result.GetProperty("uri").GetString()!));
                }
            }
            await client.StopAsync();
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task Offline_mode_accepts_saved_mappings_with_disabled_profiles_and_never_loads_them()
    {
        var root = Path.Combine(TestPaths.TempRoot, "hybrid-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "proc.sql"), "CREATE PROCEDURE dbo.P AS SELECT 1;");
            var uri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
            var profile = Profile(); profile["enabled"] = false;
            await using var client = new LspClient();
            var response = await client.InitializeAsync(uri, new
            {
                sql = new { mode = "offline" },
                databases = new[] { profile },
                resolution = new { workspaceMappings = new[] { new { workspaceRoot = uri, databaseProfiles = new[] { "test" } } } }
            });
            var definitionProvider = response.GetProperty("result").GetProperty("capabilities").GetProperty("definitionProvider");
            Assert.True(definitionProvider.ValueKind is System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.True);
            await client.NotifyAsync("initialized", new { });
            var search = await client.RequestAsync("workspace/symbol", new { query = "P" });
            Assert.Single(search.GetProperty("result").EnumerateArray());
            await client.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Drift_reporting_can_be_disabled_without_disabling_local_analysis()
    {
        var root = Path.Combine(TestPaths.TempRoot, "hybrid-drift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "proc.sql"), "CREATE PROCEDURE dbo.P AS SELECT 1;");
            var uri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
            await using var client = new LspClient();
            await client.InitializeAsync(uri, new
            {
                sql = new { mode = "hybrid" },
                databases = new[] { Profile() },
                resolution = new { reportDefinitionDrift = false, workspaceMappings = new[] { new { workspaceRoot = uri, databaseProfiles = new[] { "test" } } } }
            });
            await client.NotifyAsync("initialized", new { });
            var search = await client.RequestAsync("workspace/symbol", new { query = "P" });
            var symbol = Assert.Single(search.GetProperty("result").EnumerateArray());
            var data = symbol.GetProperty("data");
            Assert.True(!data.TryGetProperty("drift", out var drift) || drift.ValueKind == System.Text.Json.JsonValueKind.Null);
            await client.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
