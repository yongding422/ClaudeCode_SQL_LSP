using System.Text;
using System.Text.Json;

namespace SqlLsp.Mcp.IntegrationTests;

public class ExpansionContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expansion_reuses_report_context_preserves_citations_and_checks_current_source(bool persisted)
    {
        var root = Path.Combine(TestPaths.TempRoot, "expansion-context-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "project 中文");
        Directory.CreateDirectory(folder);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, "{\"version\":1,\"responses\":{\"analysisMaxBytes\":16384},\"indexCache\":{\"enabled\":" + (persisted ? "true" : "false") + "}}");
        var source = Path.Combine(folder, "p.sql");
        await File.WriteAllTextAsync(source, "-- 😀\nCREATE PROC dbo.P @n int AS BEGIN " + string.Concat(Enumerable.Repeat("IF @n=0 RETURN; ", 100)) + "END;");
        var other = Path.Combine(folder, "r.sql");
        await File.WriteAllTextAsync(other, "CREATE PROC dbo.R AS SELECT 1;");
        await using var client = await McpClient.Start("SELECT 1;", rootDirectory: root, settingsFile: settings);
        var initial = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri = new Uri(source).AbsoluteUri, declaration = "P", scopeUri = new Uri(folder).AbsoluteUri }));
        var reportRef = initial.GetProperty("report").GetProperty("reportRef").GetString();
        var generation = initial.GetProperty("report").GetProperty("generation").GetString();
        Assert.True(initial.TryGetProperty("coverage", out _));
        Assert.Contains("project", initial.GetProperty("scope").GetProperty("directoryUri").GetString());
        // A different analysis evicts the first rich body in the materialized path.
        McpClient.Data(await client.Tool("analyze_procedure", new { documentUri = new Uri(other).AbsoluteUri, declaration = "R" }));
        var before = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        var section = initial.GetProperty("brief").GetProperty("facts")[0].GetProperty("section").GetString();
        var wire = await client.Tool("analyze_procedure", new { reportRef, section });
        Assert.InRange(Encoding.UTF8.GetByteCount(wire.GetRawText()), 1, 16384);
        var page = McpClient.Data(wire);
        Assert.Equal("report-expansion", page.GetProperty("responseKind").GetString());
        Assert.Equal(reportRef, page.GetProperty("contextRef").GetString());
        Assert.Contains("coverage", page.GetProperty("context").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("brief").ValueKind);
        Assert.False(page.TryGetProperty("coverage", out _));
        Assert.False(page.TryGetProperty("sourceContext", out _));
        Assert.Equal(generation, page.GetProperty("report").GetProperty("generation").GetString());
        Assert.NotEmpty(page.GetProperty("report").GetProperty("items").EnumerateArray());
        var citations = McpClient.Data(await client.Tool("analyze_procedure", new { reportRef, section = "/evidence" }));
        Assert.Contains("p.sql:", citations.GetProperty("report").GetRawText());
        var after = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        if (persisted)
            Assert.Equal(before.GetProperty("indexCache").GetProperty("bodyParses").GetInt64(), after.GetProperty("indexCache").GetProperty("bodyParses").GetInt64());

        // Same-length edit with the timestamp restored must still invalidate a materialized page.
        var stamp = File.GetLastWriteTimeUtc(source);
        var text = await File.ReadAllTextAsync(source);
        await File.WriteAllTextAsync(source, text.Replace("@n=0", "@n=1", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(source, stamp);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (McpClient.Data(await client.Tool("find_procedures", new { query = "P" })).GetProperty("generation").GetString() == before.GetProperty("generation").GetString())
            await Task.Delay(100, deadline.Token);
        var stale = await client.Tool("analyze_procedure", new { reportRef, section });
        Assert.True(stale.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("stale", stale.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
