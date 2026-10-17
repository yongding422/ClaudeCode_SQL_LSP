namespace SqlLsp.Mcp.IntegrationTests;

public class IndexReuseTests
{
    [Fact]
    public async Task Live_process_reuses_navigation_and_asts_but_rebuilds_after_edit_and_competing_declaration()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;");
        var first = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        Assert.False(first.GetProperty("timings").GetProperty("navigationReused").GetBoolean());
        await Task.Delay(350); // Let startup-gap reconciliation read the unchanged file.
        var warm = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        Assert.True(warm.GetProperty("timings").GetProperty("navigationReused").GetBoolean());
        Assert.Equal(first.GetProperty("generation").GetString(), warm.GetProperty("generation").GetString());
        Assert.Equal(1, warm.GetProperty("coverage").GetProperty("parseAttempts").GetInt32());
        await File.WriteAllTextAsync(client.SourceFile, "CREATE PROC dbo.P AS SELECT 2;");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var current = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
            if (current.GetProperty("generation").GetString() != first.GetProperty("generation").GetString()) break;
            await Task.Delay(100, deadline.Token);
        }
        await File.WriteAllTextAsync(Path.Combine(client.Root, "duplicate.sql"), "CREATE PROC dbo.P AS SELECT 3;");
        while (true)
        {
            var current = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
            if (current.GetProperty("totalMatches").GetInt32() == 2)
            {
                Assert.All(current.GetProperty("modules").EnumerateArray(), m => Assert.Equal("Ambiguous", m.GetProperty("status").GetString()));
                break;
            }
            await Task.Delay(100, deadline.Token);
        }
    }
}
