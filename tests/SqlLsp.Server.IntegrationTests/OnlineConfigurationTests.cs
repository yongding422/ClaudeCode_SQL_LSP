namespace SqlLsp.Server.IntegrationTests;

public class OnlineConfigurationTests
{
    private static Dictionary<string, object> Profile(string id = "test") => new()
    {
        ["id"] = id,
        ["connectionStringEnv"] = "SQLLSP_INTENTIONALLY_ABSENT_SECRET",
        ["environment"] = "test",
        ["server"] = "127.0.0.1,1",
        ["database"] = "Fixture",
        ["readOnly"] = true,
        ["enabled"] = true,
        ["connectTimeoutSeconds"] = 1,
        ["loadTimeoutSeconds"] = 1
    };

    [Theory]
    [InlineData("no-enabled-profile")]
    [InlineData("multiple-unmapped")]
    [InlineData("duplicate-id")]
    [InlineData("disabled-mapping")]
    [InlineData("inline-secret")]
    [InlineData("writable")]
    [InlineData("unbounded-timeout")]
    public async Task Invalid_online_profiles_fail_closed_without_echoing_supplied_secrets(string scenario)
    {
        var profiles = new[] { Profile() };
        object? resolution = null;
        switch (scenario)
        {
            case "no-enabled-profile": profiles[0]["enabled"] = false; break;
            case "multiple-unmapped": profiles = [Profile("first"), Profile("second")]; break;
            case "duplicate-id": profiles = [Profile(), Profile()]; break;
            case "disabled-mapping": profiles[0]["enabled"] = false; resolution = new { defaultDatabaseProfile = "test" }; break;
            case "inline-secret": profiles[0]["connectionString"] = "Password=do-not-echo"; break;
            case "writable": profiles[0]["readOnly"] = false; break;
            case "unbounded-timeout": profiles[0]["commandTimeoutSeconds"] = 0; break;
        }
        var options = new Dictionary<string, object> { ["sql"] = new { mode = "online" }, ["databases"] = profiles };
        if (resolution != null) options["resolution"] = resolution;
        await using var client = new LspClient();
        var response = await client.InitializeAsync(initializationOptions: options);
        Assert.False(response.GetProperty("result").GetProperty("capabilities").GetProperty("definitionProvider").GetBoolean());
        await client.NotifyAsync("initialized", new { });
        var message = await client.NotificationAsync("window/showMessage");
        Assert.Contains("initializationOptions", message.GetProperty("message").GetString());
        Assert.DoesNotContain("do-not-echo", message.ToString());
        await client.StopAsync();
    }

    [Fact]
    public async Task Offline_ignores_enabled_profiles_and_online_missing_secret_reports_status_without_scanning()
    {
        var root = Path.Combine(TestPaths.TempRoot, "online-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "local.sql"), "CREATE TABLE dbo.LocalOnly (id int);");
            var uri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
            await using (var offline = new LspClient())
            {
                await offline.InitializeAsync(uri, new { sql = new { mode = "offline" }, databases = new[] { Profile() } });
                await offline.NotifyAsync("initialized", new { });
                var symbols = await offline.RequestAsync("workspace/symbol", new { query = "LocalOnly" });
                Assert.Single(symbols.GetProperty("result").EnumerateArray());
                await offline.StopAsync();
            }
            await using (var online = new LspClient())
            {
                await online.InitializeAsync(uri, new { sql = new { mode = "online" }, databases = new[] { Profile() } });
                await online.NotifyAsync("initialized", new { });
                var symbols = await online.RequestAsync("workspace/symbol", new { query = "LocalOnly" });
                Assert.Empty(symbols.GetProperty("result").EnumerateArray());
                var status = await online.NotificationAsync("window/logMessage");
                Assert.Contains("Unavailable (missing-secret)", status.GetProperty("message").GetString());
                await online.StopAsync();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Explicit_mapping_connects_only_the_selected_profile_and_scan_is_opt_in()
    {
        var root = Path.Combine(TestPaths.TempRoot, "online-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "local.sql"), "CREATE TABLE dbo.LocalOnly (id int);");
            await using var client = new LspClient();
            await client.InitializeAsync(new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri, new
            {
                sql = new { mode = "online" },
                workspace = new { scanInOnline = true },
                databases = new[] { Profile("selected"), Profile("ignored") },
                resolution = new { defaultDatabaseProfile = "selected" }
            });
            await client.NotifyAsync("initialized", new { });
            var symbols = await client.RequestAsync("workspace/symbol", new { query = "LocalOnly" });
            Assert.Single(symbols.GetProperty("result").EnumerateArray());
            var status = await client.NotificationAsync("window/logMessage");
            Assert.Contains("profile selected", status.GetProperty("message").GetString());
            Assert.DoesNotContain("ignored", status.ToString());
            await client.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
