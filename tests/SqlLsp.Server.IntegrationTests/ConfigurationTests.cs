namespace SqlLsp.Server.IntegrationTests;

public class ConfigurationTests
{
    [Theory]
    [InlineData("unsupported-mode")]
    [InlineData("unsupported-parser")]
    [InlineData("invalid-size")]
    [InlineData("wrong-type")]
    [InlineData("unknown-setting")]
    public async Task Invalid_options_disable_analysis_and_report_schema_errors_without_database_access(string scenario)
    {
        object options = scenario switch
        {
            "unsupported-mode" => new { sql = new { mode = "online" } },
            "unsupported-parser" => new { sql = new { parserVersion = "999" } },
            "invalid-size" => new { workspace = new { maxFileSizeBytes = 0 } },
            "wrong-type" => new { sql = new { quotedIdentifier = "yes" } },
            _ => new { unexpected = true }
        };
        await using var client = new LspClient();
        var response = await client.InitializeAsync(initializationOptions: options);
        var capabilities = response.GetProperty("result").GetProperty("capabilities");
        Assert.False(capabilities.GetProperty("documentSymbolProvider").GetBoolean());
        Assert.True(!capabilities.TryGetProperty("textDocumentSync", out var sync) || sync.ValueKind == System.Text.Json.JsonValueKind.Null);
        await client.NotifyAsync("initialized", new { });
        var message = await client.NotificationAsync("window/showMessage");
        Assert.Equal(1, message.GetProperty("type").GetInt32());
        Assert.Contains("initializationOptions", message.GetProperty("message").GetString());
        await client.StopAsync();
    }
}
