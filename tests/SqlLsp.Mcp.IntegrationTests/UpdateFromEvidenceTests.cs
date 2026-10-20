using System.Text;

namespace SqlLsp.Mcp.IntegrationTests;

public class UpdateFromEvidenceTests
{
    [Fact]
    public async Task First_response_contains_query_clauses_and_compact_pages_preserve_citations()
    {
        await using var client = await McpClient.Start("-- 😀\nCREATE PROC dbo.P @id int AS BEGIN UPDATE t SET n += s.n FROM dbo.T t JOIN dbo.S s ON t.id=s.id WHERE s.OrderID=@id; END;");
        var wire = await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri });
        Assert.InRange(Encoding.UTF8.GetByteCount(wire.GetRawText()), 1, 32768);
        var data = McpClient.Data(wire);
        var brief = Assert.Single(data.GetProperty("brief").GetProperty("facts").EnumerateArray(), f => f.GetProperty("kind").GetString() == "UpdateFrom");
        Assert.Contains("modules.sql:2", brief.GetProperty("citation").GetString());
        var query = brief.GetProperty("data").GetProperty("updateFrom");
        Assert.True(query.GetProperty("wherePresent").GetBoolean());
        Assert.Contains("OrderID=@id", query.GetProperty("where").GetProperty("preview").GetString());
        var full = McpClient.Data(await client.AnalyzeExpanded(new { documentUri = new Uri(client.SourceFile).AbsoluteUri }));
        var fact = Assert.Single(full.GetProperty("report").GetProperty("modules")[0].GetProperty("observations").EnumerateArray(), o => o.GetProperty("kind").GetString() == "UpdateFrom");
        var at = fact.GetProperty("updateFrom").GetProperty("where").GetProperty("at").GetString()!;
        Assert.Contains("modules.sql:2", full.GetProperty("report").GetProperty("evidence").GetProperty(at).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Focus_omits_unrelated_update_previews_but_keeps_a_visible_omission()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P @wanted int AS BEGIN SELECT @wanted; UPDATE t SET n=N'PRIVATE_SQL_CANARY' FROM dbo.T t JOIN dbo.S s ON t.id=s.id; END;");
        var wire = await client.AnalyzeExpanded(new { documentUri = new Uri(client.SourceFile).AbsoluteUri, focus = new[] { "@wanted" } });
        var data = McpClient.Data(wire);
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", data.GetRawText());
        var fact = Assert.Single(data.GetProperty("report").GetProperty("modules")[0].GetProperty("observations").EnumerateArray(), o => o.GetProperty("kind").GetString() == "UpdateFrom");
        Assert.True(fact.GetProperty("expressionOmitted").GetBoolean());
        Assert.False(fact.TryGetProperty("updateFrom", out _));
    }
}
