using System.Text.Json;

namespace SqlLsp.Mcp.IntegrationTests;

public class ProcedureTests
{
    [Fact]
    public async Task Value_bindings_and_definition_links_survive_the_wire_without_shared_callee_substitution()
    {
        await using var client = await McpClient.Start("-- 😀\r\nCREATE PROC dbo.P @State varchar(2) AS BEGIN DECLARE @local varchar(2)=@State; " +
            "EXEC dbo.Q @Region=@local; SET @local='CA'; EXEC dbo.Q @Region=@local; END;\nGO\n" +
            "CREATE PROC dbo.Q @Region varchar(2),@Mode int=7 AS SELECT * FROM dbo.A WHERE StateCode=@Region;\nGO\nCREATE TABLE dbo.A(StateCode varchar(2));");
        var selected = McpClient.Data(await client.Tool("find_procedures", new { query = "dbo.P" })).GetProperty("modules")[0];
        var modules = McpClient.Data(await Analyze(client, selected)).GetProperty("report").GetProperty("modules");
        var flow = modules[0].GetProperty("valueFlow");
        var facts = flow.GetProperty("facts").EnumerateArray().ToArray();
        var calls = flow.GetProperty("calls").EnumerateArray().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, c => Assert.Equal("Resolved", c.GetProperty("bindingStatus").GetString()));
        Assert.Equal(modules[1].GetProperty("id").GetString(), calls[0].GetProperty("targetModuleId").GetString());
        var firstInput = facts.Single(f => f.GetProperty("id").GetString() == calls[0].GetProperty("bindings")[0].GetProperty("inputFactId").GetString());
        var declarationId = firstInput.GetProperty("inputs")[0].GetProperty("sourceFacts")[0].GetString();
        Assert.Equal("Declaration", facts.Single(f => f.GetProperty("id").GetString() == declarationId).GetProperty("kind").GetString());
        var secondInput = facts.Single(f => f.GetProperty("id").GetString() == calls[1].GetProperty("bindings")[0].GetProperty("inputFactId").GetString());
        var assignmentId = secondInput.GetProperty("inputs")[0].GetProperty("sourceFacts")[0].GetString();
        Assert.Equal("'CA'", facts.Single(f => f.GetProperty("id").GetString() == assignmentId).GetProperty("expression").GetProperty("preview").GetString());
        Assert.Equal("OmittedDefault", calls[0].GetProperty("bindings")[1].GetProperty("mode").GetString());
        var callee = modules[1].GetProperty("valueFlow");
        var where = Assert.Single(callee.GetProperty("facts").EnumerateArray(), f => f.GetProperty("role").GetString() == "Where");
        Assert.Equal(calls[0].GetProperty("bindings")[0].GetProperty("parameterVariableId").GetString(), where.GetProperty("inputs")[0].GetProperty("variableId").GetString());
        Assert.True(where.GetProperty("inputs")[0].GetProperty("sourcesComplete").GetBoolean());
        Assert.NotEmpty(callee.GetProperty("limitations").EnumerateArray());
    }

    [Fact]
    public async Task Invalid_value_binding_and_select_unknowns_are_explicit_in_mcp_results()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P @n int AS BEGIN SELECT @n=1; EXEC dbo.Q @Wrong=@n; IF @n=2 RETURN; END;\nGO\nCREATE PROC dbo.Q @Right int AS SELECT 1;");
        var selected = McpClient.Data(await client.Tool("find_procedures", new { query = "P" })).GetProperty("modules")[0];
        var flow = McpClient.Data(await Analyze(client, selected)).GetProperty("report").GetProperty("modules")[0].GetProperty("valueFlow");
        Assert.Equal("Unsupported", flow.GetProperty("calls")[0].GetProperty("bindingStatus").GetString());
        Assert.Empty(flow.GetProperty("calls")[0].GetProperty("bindings").EnumerateArray());
        Assert.Contains(flow.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "value-call-binding");
        var use = Assert.Single(flow.GetProperty("facts").EnumerateArray(), f => f.GetProperty("role").GetString() == "If");
        Assert.False(use.GetProperty("inputs")[0].GetProperty("sourcesComplete").GetBoolean());
    }

    [Fact]
    public async Task Structural_flow_survives_the_wire_and_guards_calls_without_claiming_transaction_outcomes()
    {
        await using var client = await McpClient.Start("-- 😀 中文\r\nCREATE PROC dbo.P @state varchar(2) AS BEGIN\r\n" +
            "BEGIN TRY BEGIN TRANSACTION Payroll; IF @state='OH' EXEC dbo.Q; ELSE RETURN; COMMIT TRANSACTION Payroll; END TRY\r\n" +
            "BEGIN CATCH ROLLBACK TRANSACTION Payroll; THROW; END CATCH; END;\r\nGO\r\n" +
            "CREATE PROC dbo.Q AS UPDATE dbo.A SET id=1;\r\nGO\r\nCREATE TABLE dbo.A(id int);");
        var selected = McpClient.Data(await client.Tool("find_procedures", new { query = "dbo.P" })).GetProperty("modules")[0];
        var report = McpClient.Data(await Analyze(client, selected)).GetProperty("report");
        var root = report.GetProperty("modules")[0];
        var flow = root.GetProperty("controlFlow");
        var nodes = flow.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.True(flow.GetProperty("bodyAvailable").GetBoolean());
        Assert.False(flow.GetProperty("truncated").GetBoolean());
        var branch = Assert.Single(nodes, n => n.GetProperty("kind").GetString() == "If");
        Assert.Equal("@state='OH'", branch.GetProperty("predicate").GetProperty("preview").GetString());
        Assert.Equal(2, branch.GetProperty("predicate").GetProperty("location").GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        var call = Assert.Single(root.GetProperty("calls").EnumerateArray());
        var contexts = call.GetProperty("flowContext").EnumerateArray().Select(id => nodes.Single(n => n.GetProperty("id").GetString() == id.GetString()).GetProperty("kind").GetString());
        Assert.Equal(new[] { "TryCatch", "Try", "If", "Then" }, contexts);
        Assert.Contains(nodes, n => n.GetProperty("kind").GetString() == "RollbackTransaction" && n.GetProperty("subject").GetProperty("preview").GetString() == "Payroll");
        Assert.Contains(nodes, n => n.GetProperty("kind").GetString() == "Throw");
        var callee = report.GetProperty("modules")[1];
        Assert.Empty(Assert.Single(callee.GetProperty("accesses").EnumerateArray()).GetProperty("flowContext").EnumerateArray());
        Assert.Contains(flow.GetProperty("limitations").EnumerateArray(), l => l.GetString()!.Contains("actual commit/rollback outcomes are not inferred", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Truncated_control_map_has_visible_coverage_and_no_dangling_context_ids()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P @n int AS BEGIN " +
            string.Concat(Enumerable.Repeat("IF @n=0 RETURN; ", 100)) + "END;");
        var selected = McpClient.Data(await client.Tool("find_procedures", new { query = "P" })).GetProperty("modules")[0];
        var module = McpClient.Data(await Analyze(client, selected)).GetProperty("report").GetProperty("modules")[0];
        Assert.True(module.GetProperty("truncated").GetBoolean());
        var flow = module.GetProperty("controlFlow");
        Assert.True(flow.GetProperty("truncated").GetBoolean());
        var nodes = flow.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(256, nodes.Length);
        var ids = nodes.Select(n => n.GetProperty("id").GetString()).ToHashSet();
        Assert.All(nodes, n => Assert.True(n.GetProperty("parentId").ValueKind == JsonValueKind.Null || ids.Contains(n.GetProperty("parentId").GetString())));
        Assert.Contains(module.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "control-flow-limit");
    }

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
            "SELECT * FROM dbo.A WHERE state=@state; INSERT dbo.B SELECT * FROM dbo.A; EXEC(N'PRIVATE_SQL_CANARY'); " +
            "EXEC sys.sp_executesql N'PRIVATE_SQL_CANARY',N'@p varchar(2)',@p=@state; END;\nGO\n" +
            "CREATE TABLE dbo.A(state varchar(2));\nGO\nCREATE TABLE dbo.B(state varchar(2));");
        var tools = (await client.Call("tools/list", new { })).GetProperty("result").GetProperty("tools");
        Assert.Equal(3, tools.GetArrayLength());
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
