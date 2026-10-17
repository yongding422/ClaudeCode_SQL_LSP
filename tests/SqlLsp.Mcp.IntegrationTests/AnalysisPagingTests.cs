using System.Text;

namespace SqlLsp.Mcp.IntegrationTests;

public class AnalysisPagingTests
{
    [Fact]
    public async Task Known_file_declaration_needs_no_discovery_and_default_is_compact_with_small_expansion()
    {
        await using var client = await McpClient.Start("-- 😀\nCREATE PROC dbo.P AS EXEC dbo.Q;\nGO\nCREATE PROC dbo.Q AS SELECT 1;");
        var wire = await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = "dbo.P" });
        Assert.InRange(Encoding.UTF8.GetByteCount(wire.GetRawText()), 1, 32768);
        var report = McpClient.Data(wire).GetProperty("report");
        Assert.Equal("compact-v1", report.GetProperty("format").GetString());
        Assert.Equal("Resolved", report.GetProperty("status").GetString());
        Assert.Equal(1, report.GetProperty("maxDepth").GetInt32());
        Assert.Equal(8, report.GetProperty("maxModules").GetInt32());
        var missing = await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = "Missing" });
        Assert.True(missing.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Short_module_references_bind_the_whole_revision_and_reject_a_changed_generation()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;");
        var discovery = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        var moduleRef = discovery.GetProperty("modules")[0].GetProperty("moduleRef").GetString();
        Assert.InRange(moduleRef!.Length, 1, 32);
        var report = McpClient.Data(await client.Tool("analyze_procedure", new { moduleRef })).GetProperty("report");
        Assert.Equal("Resolved", report.GetProperty("status").GetString());
        await File.AppendAllTextAsync(client.SourceFile, "\n-- changed");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (McpClient.Data(await client.Tool("find_procedures", new { query = "P" })).GetProperty("generation").GetString() == discovery.GetProperty("generation").GetString())
            await Task.Delay(100, deadline.Token);
        var stale = await client.Tool("analyze_procedure", new { moduleRef });
        Assert.True(stale.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("stale", stale.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Full_reports_have_bounded_wire_pages_and_reconstruct_every_retained_fact_and_span()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P @n int AS BEGIN " + string.Concat(Enumerable.Repeat("IF @n=0 RETURN; ", 100)) + "END;");
        var request = new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = "P", detail = "full" };
        var wire = await client.Tool("analyze_procedure", request);
        Assert.InRange(Encoding.UTF8.GetByteCount(wire.GetRawText()), 1, 32768);
        var summary = McpClient.Data(wire).GetProperty("report");
        Assert.Equal("paged-report-v1", summary.GetProperty("format").GetString());
        var reportRef = summary.GetProperty("reportRef").GetString();
        var count = 0;
        var offset = 0;
        do
        {
            wire = await client.Tool("analyze_procedure", new { reportRef, section = "/modules/0/controlFlow/nodes", offset, maxItems = 100 });
            Assert.InRange(Encoding.UTF8.GetByteCount(wire.GetRawText()), 1, 32768);
            var page = McpClient.Data(wire).GetProperty("report");
            count += page.GetProperty("returned").GetInt32();
            if (page.GetProperty("nextOffset").ValueKind == System.Text.Json.JsonValueKind.Null) break;
            offset = page.GetProperty("nextOffset").GetInt32();
        } while (true);
        Assert.Equal(256, count);
        var full = McpClient.Data(await client.AnalyzeExpanded(request)).GetProperty("report");
        Assert.True(full.GetProperty("modules")[0].GetProperty("truncated").GetBoolean());
        Assert.Equal(256, full.GetProperty("modules")[0].GetProperty("controlFlow").GetProperty("nodes").GetArrayLength());
        Assert.Contains(full.GetProperty("modules")[0].GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "control-flow-limit");
        Assert.Equal(0, full.GetProperty("modules")[0].GetProperty("definition").GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }
}
