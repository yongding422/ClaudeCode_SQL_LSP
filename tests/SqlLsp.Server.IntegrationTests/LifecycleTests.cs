using System.Text.Json;

namespace SqlLsp.Server.IntegrationTests;

public class LifecycleTests
{
    [Fact]
    public async Task Framed_initialize_shutdown_exit_has_clean_stdout()
    {
        await using var client = new LspClient();
        var initialize = await client.InitializeAsync();
        Assert.Equal(JsonValueKind.Object, initialize.GetProperty("result").GetProperty("capabilities").ValueKind);
        await client.NotifyAsync("initialized", new { });
        await client.StopAsync();
    }
}
