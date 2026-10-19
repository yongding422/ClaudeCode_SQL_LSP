namespace SqlLsp.Mcp.IntegrationTests;

public class UnderstandingEvidenceTests
{
    [Fact]
    public async Task Compact_throw_and_source_observations_have_named_fields_and_citeable_evidence()
    {
        await using var client = await McpClient.Start("-- 😀\nCREATE PROC dbo.P AS BEGIN DECLARE @sql nvarchar(max)=N'CREATE PROC dbo.Future AS SELECT 1;'; " +
            "DECLARE @n int; SET @n=NEXT VALUE FOR dbo.S; BEGIN TRAN; THROW 51000,N'failure',2; RETURN 1; END;");
        var wire = await client.AnalyzeExpanded(new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = "P" });
        if (wire.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean())
        {
            await Task.Delay(50); // Allow stderr diagnostics to arrive; no retry of the failed request.
            Assert.Fail("Analysis failed; safe server exception types/frames: " + client.FailureDiagnostics);
        }
        var data = McpClient.Data(wire);
        var report = data.GetProperty("report");
        var module = report.GetProperty("modules")[0];
        var thrown = Assert.Single(module.GetProperty("controlFlow").GetProperty("nodes").EnumerateArray(), n => n.GetProperty("kind").GetString() == "Throw");
        Assert.Equal("NewError", thrown.GetProperty("throw").GetProperty("kind").GetString());
        Assert.Equal("2", thrown.GetProperty("throw").GetProperty("state").GetProperty("preview").GetString());
        var at = thrown.GetProperty("throw").GetProperty("state").GetProperty("at").GetString()!;
        Assert.Contains("modules.sql:2", report.GetProperty("evidence").GetProperty(at).GetProperty("text").GetString());
        Assert.Contains(module.GetProperty("observations").EnumerateArray(), o => o.GetProperty("kind").GetString() == "EmbeddedModuleDefinitionText");
        Assert.Contains(module.GetProperty("observations").EnumerateArray(), o => o.GetProperty("kind").GetString() == "SequenceAllocation" && o.GetProperty("beforeFirstExplicitTransaction").GetBoolean());
        Assert.Contains(module.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "return-after-throw");
    }
}
