using System.Text.Json;

namespace SqlLsp.Mcp.IntegrationTests;

public class ProcedureTests
{
    [Fact]
    public async Task Parse_errors_stay_visible_even_when_the_malformed_module_cannot_be_discovered()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT FROM;");
        var discovery = McpClient.Data(await client.Tool("find_procedures", new { }));
        Assert.Empty(discovery.GetProperty("modules").EnumerateArray());
        Assert.NotEmpty(discovery.GetProperty("sourceIssues").EnumerateArray());
        var report = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, line = 0, character = 16 })).GetProperty("report");
        Assert.Empty(report.GetProperty("modules").EnumerateArray());
        Assert.Contains(report.GetProperty("expansionIssues").EnumerateArray(), issue => issue.GetProperty("code").GetString() == "parse-error");
    }

    [Fact]
    public async Task Discovery_and_real_analysis_publish_readonly_tools_and_visible_coverage()
    {
        await using var client = await McpClient.Start("-- 😀 中文\r\nCREATE PROC dbo.P @state varchar(2) AS BEGIN " +
            "SELECT * FROM dbo.A WHERE state=@state; INSERT dbo.B SELECT * FROM dbo.A; EXEC(N'PRIVATE_SQL_CANARY'); END;\nGO\n" +
            "CREATE TABLE dbo.A(state varchar(2));\nGO\nCREATE TABLE dbo.B(state varchar(2));");
        var tools = (await client.Call("tools/list", new { })).GetProperty("result").GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
        Assert.All(tools.EnumerateArray(), tool => Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));
        var discovery = McpClient.Data(await client.Tool("find_procedures", new { query = "dbo.P" }));
        var selected = Assert.Single(discovery.GetProperty("modules").EnumerateArray());
        Assert.Equal(1, selected.GetProperty("selection").GetProperty("line").GetInt32());
        var data = McpClient.Data(await Analyze(client, selected));
        var report = data.GetProperty("report");
        Assert.Equal("Resolved", report.GetProperty("status").GetString());
        var module = Assert.Single(report.GetProperty("modules").EnumerateArray());
        Assert.Contains(module.GetProperty("accesses").EnumerateArray(), a => a.GetProperty("operation").GetString() == "Insert" &&
            a.GetProperty("target").GetProperty("identity").GetProperty("name").GetString() == "B");
        Assert.Contains(module.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "call-unsupported");
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", data.ToString());
        Assert.NotEmpty(report.GetProperty("limitations").EnumerateArray());
        Assert.Contains("does not receive LSP unsaved", data.GetProperty("source").GetString());
        Assert.True((await client.Call("ping", new { })).TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Expansion_is_bounded_cycles_stop_and_each_module_keeps_its_own_effects()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS EXEC dbo.Q;\nGO\n" +
            "CREATE PROC dbo.Q AS BEGIN EXEC dbo.P; UPDATE dbo.B SET id=1; END;\nGO\nCREATE TABLE dbo.B(id int);");
        var selected = McpClient.Data(await client.Tool("find_procedures", new { query = "dbo.P" })).GetProperty("modules")[0];
        var full = McpClient.Data(await Analyze(client, selected)).GetProperty("report");
        Assert.Equal(2, full.GetProperty("modules").GetArrayLength());
        Assert.Empty(full.GetProperty("modules")[0].GetProperty("accesses").EnumerateArray());
        var limited = McpClient.Data(await Analyze(client, selected, 0)).GetProperty("report");
        Assert.Single(limited.GetProperty("modules").EnumerateArray());
        Assert.Single(limited.GetProperty("expansionIssues").EnumerateArray());
    }

    [Fact]
    public async Task Duplicate_identity_and_invalid_unindexed_inputs_never_pick_an_arbitrary_module()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;\nGO\nCREATE PROC dbo.P AS SELECT 2;");
        var discovered = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        Assert.Equal(2, discovered.GetProperty("modules").GetArrayLength());
        var report = McpClient.Data(await Analyze(client, discovered.GetProperty("modules")[0])).GetProperty("report");
        Assert.Equal("Ambiguous", report.GetProperty("status").GetString());
        Assert.Empty(report.GetProperty("modules").EnumerateArray());
        var outside = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri = "file:///outside/procedure.sql", line = 0, character = 0 }));
        Assert.Equal("Unresolved", outside.GetProperty("report").GetProperty("status").GetString());
        var invalid = await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, line = -1, character = 0 });
        Assert.True(invalid.GetProperty("result").GetProperty("isError").GetBoolean());
        var limit = await client.Tool("find_procedures", new { maxResults = 1000 });
        Assert.True(limit.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Saved_edits_change_revision_and_stale_selection_is_rejected()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;");
        var old = McpClient.Data(await client.Tool("find_procedures", new { query = "P" })).GetProperty("modules")[0];
        await File.WriteAllTextAsync(client.SourceFile, "CREATE PROC dbo.P AS SELECT * FROM dbo.NewTable;");
        JsonElement current = default;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        do
        {
            await Task.Delay(100, timeout.Token);
            current = McpClient.Data(await client.Tool("find_procedures", new { query = "P" })).GetProperty("modules")[0];
        } while (current.GetProperty("revision").GetString() == old.GetProperty("revision").GetString());
        var stale = McpClient.Data(await Analyze(client, old)).GetProperty("report");
        Assert.Equal("Unresolved", stale.GetProperty("status").GetString());
        Assert.Empty(stale.GetProperty("modules").EnumerateArray());
        var fresh = McpClient.Data(await Analyze(client, current)).GetProperty("report");
        Assert.Contains(fresh.GetProperty("modules")[0].GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "relation-unresolved");
    }

    [Theory]
    [InlineData("{\"sql\":{\"mode\":\"wrong-PRIVATE_SQL_CANARY\"}}")]
    [InlineData("not-json-PRIVATE_SQL_CANARY")]
    public async Task Invalid_configuration_does_not_leak_values_or_break_the_protocol(string config)
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;", config);
        var response = await client.Tool("find_procedures", new { });
        Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", response.ToString());
        Assert.True((await client.Call("ping", new { })).TryGetProperty("result", out _));
    }

    private static Task<JsonElement> Analyze(McpClient client, JsonElement selected, int depth = 2) =>
        client.Tool("analyze_procedure", new
        {
            documentUri = selected.GetProperty("target").GetProperty("uri").GetString(),
            line = selected.GetProperty("selection").GetProperty("line").GetInt32(),
            character = selected.GetProperty("selection").GetProperty("character").GetInt32(),
            maxDepth = depth,
            expectedRevision = selected.GetProperty("revision").GetString()
        });

    [Fact]
    public async Task Explicit_online_mode_with_unavailable_metadata_reports_status_without_workspace_fallback()
    {
        var config = JsonSerializer.Serialize(new
        {
            sql = new { mode = "online" },
            databases = new[] { new {
            id = "unavailable", enabled = true, readOnly = true, environment = "wire-test", server = "127.0.0.1,1", database = "Fixture",
            connectionStringEnv = "SQLLSP_MISSING_" + Guid.NewGuid().ToString("N") } }
        });
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;", config);
        var data = McpClient.Data(await client.Tool("find_procedures", new { }));
        Assert.Empty(data.GetProperty("modules").EnumerateArray());
        Assert.Contains(data.GetProperty("sourceStatuses").EnumerateArray(), s => s.GetString()!.Contains("Unavailable", StringComparison.Ordinal));
        Assert.Equal(0, data.GetProperty("indexedDocuments").GetInt32());
    }
}
