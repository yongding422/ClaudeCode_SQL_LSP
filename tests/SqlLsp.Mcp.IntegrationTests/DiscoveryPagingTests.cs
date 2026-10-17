using System.Text;

namespace SqlLsp.Mcp.IntegrationTests;

public class DiscoveryPagingTests
{
    [Fact]
    public async Task Validated_config_can_lower_discovery_budget_without_enabling_storage()
    {
        var root = Path.Combine(TestPaths.TempRoot, "budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, "{\"version\":1,\"responses\":{\"discoveryMaxBytes\":8192}}");
        await using var client = await McpClient.Start(string.Join("\nGO\n", Enumerable.Range(0, 20).Select(i => $"CREATE PROC dbo.P{i:D2} AS SELECT {i};")),
            rootDirectory: root, settingsFile: settings);
        var wire = await client.Tool("find_procedures", new { maxResults = 100 });
        Assert.InRange(Encoding.UTF8.GetByteCount(wire.GetRawText()), 1, 8192);
        Assert.True(McpClient.Data(wire).GetProperty("hasMore").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(root, ".sqlscriptdom")));
    }

    [Fact]
    public async Task Workspace_diagnostics_do_not_overflow_discovery_and_details_are_paged()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;");
        // Deterministic equivalent of the 672-issue corpus; source issues exceed the old safety cap too.
        await File.WriteAllTextAsync(Path.Combine(client.Root, "bad.sql"), string.Concat(Enumerable.Repeat("SELECT FROM;\nGO\n", 1200)));
        var wire = await client.Tool("find_procedures", new { query = "P" });
        Assert.InRange(Encoding.UTF8.GetByteCount(wire.GetRawText()), 1, 16 * 1024);
        var data = McpClient.Data(wire);
        Assert.Single(data.GetProperty("modules").EnumerateArray());
        Assert.Empty(data.GetProperty("sourceIssues").EnumerateArray());
        var total = data.GetProperty("sourceIssueSummary").GetProperty("count").GetInt32();
        Assert.True(total >= 1200);
        var count = 0;
        string? cursor = null;
        do
        {
            wire = await client.Tool("find_procedures", new { section = "issues", maxResults = 100, cursor });
            Assert.InRange(Encoding.UTF8.GetByteCount(wire.GetRawText()), 1, 16 * 1024);
            data = McpClient.Data(wire);
            Assert.Equal(total, data.GetProperty("totalMatches").GetInt32());
            count += data.GetProperty("returned").GetInt32();
            Assert.Equal(0, data.GetProperty("omittedOversized").GetInt32());
            cursor = data.GetProperty("nextCursor").GetString();
        } while (cursor != null);
        Assert.Equal(total, count);
    }

    [Fact]
    public async Task Module_pages_are_deterministic_and_reject_changed_query_or_generation()
    {
        await using var client = await McpClient.Start(string.Join("\nGO\n", Enumerable.Range(0, 30).Select(i => $"CREATE PROC dbo.P{i:D2} AS SELECT {i};")));
        var first = McpClient.Data(await client.Tool("find_procedures", new { maxResults = 3 }));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        var next = McpClient.Data(await client.Tool("find_procedures", new { maxResults = 3, cursor }));
        Assert.Equal(3, next.GetProperty("returned").GetInt32());
        Assert.NotEqual(first.GetProperty("modules")[0].GetProperty("selection"), next.GetProperty("modules")[0].GetProperty("selection"));
        var again = McpClient.Data(await client.Tool("find_procedures", new { maxResults = 3, cursor }));
        Assert.Equal(next.GetProperty("modules").GetRawText(), again.GetProperty("modules").GetRawText());
        var invalid = await client.Tool("find_procedures", new { query = "P", maxResults = 3, cursor });
        Assert.True(invalid.GetProperty("result").GetProperty("isError").GetBoolean());
        await File.AppendAllTextAsync(client.SourceFile, "\nGO\nCREATE PROC dbo.New AS SELECT 0;");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var current = McpClient.Data(await client.Tool("find_procedures", new { maxResults = 3 }));
            if (current.GetProperty("generation").GetString() != first.GetProperty("generation").GetString()) break;
            await Task.Delay(100, deadline.Token);
        }
        var stale = await client.Tool("find_procedures", new { maxResults = 3, cursor });
        Assert.True(stale.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("stale", stale.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
