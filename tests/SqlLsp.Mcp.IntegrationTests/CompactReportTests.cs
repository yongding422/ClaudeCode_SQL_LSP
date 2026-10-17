using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace SqlLsp.Mcp.IntegrationTests;

public class CompactReportTests(ITestOutputHelper output)
{
    private static async Task<JsonElement> Find(McpClient client, string query = "dbo.P") =>
        McpClient.Data(await client.Tool("find_procedures", new { query })).GetProperty("modules")[0];

    private static Task<JsonElement> Analyze(McpClient client, JsonElement selected, string detail = "compact", string[]? focus = null, int maxDepth = 2) =>
        client.AnalyzeExpanded(new
        {
            documentUri = selected.GetProperty("target").GetProperty("uri").GetString(),
            line = selected.GetProperty("selection").GetProperty("line").GetInt32(),
            character = selected.GetProperty("selection").GetProperty("character").GetInt32(),
            expectedRevision = selected.GetProperty("revision").GetString(),
            detail,
            focus,
            maxDepth
        });

    [Fact]
    public async Task Citations_are_one_based_utf16_with_exact_exclusive_ends_and_reusable_callee_references()
    {
        const string sql = "-- 中文 😀\r\nCREATE PROC dbo.P @State varchar(2) AS BEGIN\r\n    IF @State='OH' /*😀*/ EXEC dbo.Q @Region=@State; END;\r\nGO\r\n" +
            "CREATE PROC dbo.Q @Region varchar(2) AS SELECT 1;";
        await using var client = await McpClient.Start(sql);
        var data = McpClient.Data(await Analyze(client, await Find(client)));
        var report = data.GetProperty("report");
        Assert.Equal("compact-v1", report.GetProperty("format").GetString());
        var modules = report.GetProperty("modules");
        var call = Assert.Single(modules[0].GetProperty("calls").EnumerateArray());
        Assert.Equal(modules[1].GetProperty("id").GetString(), call.GetProperty("target").GetProperty("module").GetString());
        var at = report.GetProperty("evidence").GetProperty(call.GetProperty("at").GetString()!);
        Assert.Equal("modules.sql:3", at.GetProperty("text").GetString());
        Assert.Equal(3, at.GetProperty("line").GetInt32());
        var column = sql.Split('\n')[2].IndexOf("dbo.Q", StringComparison.Ordinal) + 1;
        Assert.Equal(column, at.GetProperty("column").GetInt32());
        Assert.Equal(column + "dbo.Q".Length, at.GetProperty("endColumn").GetInt32());
        Assert.Equal(3, at.GetProperty("endLine").GetInt32());
        var source = report.GetProperty("sources").GetProperty(at.GetProperty("source").GetString()!);
        Assert.Equal(new Uri(client.SourceFile).AbsoluteUri, source.GetProperty("uri").GetString());
        Assert.Equal("modules.sql", source.GetProperty("path").GetString());
        CheckReferences(report);
    }

    [Fact]
    public async Task Focus_preserves_renamed_dependencies_all_effects_unknowns_and_expanded_formals_without_dangling_ids()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P @State varchar(2),@Other int AS BEGIN " +
            "DECLARE @unused int=@Other; SET @unused=8; DECLARE @Region varchar(2)=@State; " +
            "IF @Other=1 SET @Region='CA'; EXEC dbo.Q @Region=@Region; " +
            "INSERT dbo.Missing VALUES(1); EXEC(N'PRIVATE_SQL_CANARY'); END;\nGO\n" +
            "CREATE PROC dbo.Q @Region varchar(2) AS SELECT * FROM dbo.Missing WHERE Code=@Region;");
        var selected = await Find(client);
        var focused = McpClient.Data(await Analyze(client, selected, focus: ["@sTaTe"])).GetProperty("report");
        var full = McpClient.Data(await Analyze(client, selected, "full")).GetProperty("report");
        Assert.True(focused.GetProperty("focusMatched").GetBoolean());
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", focused.ToString());
        var root = focused.GetProperty("modules")[0];
        Assert.True(root.GetProperty("valueFlow").GetProperty("omittedFacts").GetInt32() > 0);
        Assert.DoesNotContain(root.GetProperty("valueFlow").GetProperty("variables").EnumerateArray(), v => v.GetProperty("name").GetString() == "@unused");
        var callee = focused.GetProperty("modules")[1];
        var where = Assert.Single(callee.GetProperty("valueFlow").GetProperty("facts").EnumerateArray(), f => f.GetProperty("role").GetString() == "Where");
        Assert.Equal("@Region", where.GetProperty("inputs")[0].GetProperty("name").GetString());
        foreach (var (compact, original) in focused.GetProperty("modules").EnumerateArray().Zip(full.GetProperty("modules").EnumerateArray()))
        {
            Assert.Equal(original.GetProperty("accesses").GetArrayLength(), compact.GetProperty("accesses").GetArrayLength());
            Assert.Equal(original.GetProperty("calls").GetArrayLength(), compact.GetProperty("calls").GetArrayLength());
            Assert.Equal(original.GetProperty("issues").GetArrayLength(), compact.GetProperty("issues").GetArrayLength());
        }
        CheckReferences(focused);
    }

    [Theory]
    [InlineData("RoutePayroll")]
    [InlineData("DefaultRoute")]
    [InlineData("OutputRoute")]
    [InlineData("SelectRetention")]
    [InlineData("LoopRoute")]
    [InlineData("InvalidRoute")]
    public async Task Public_value_cases_preserve_full_fact_coverage_with_smaller_compact_payloads(string name)
    {
        await using var client = await McpClient.Start(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "value-tracing.sql")));
        var selected = await Find(client, "dbo." + name);
        var full = McpClient.Data(await Analyze(client, selected, "full"));
        var compact = McpClient.Data(await Analyze(client, selected));
        var fullReport = full.GetProperty("report"); var compactReport = compact.GetProperty("report");
        var fullBytes = Encoding.UTF8.GetByteCount(fullReport.GetRawText()); var compactBytes = Encoding.UTF8.GetByteCount(compactReport.GetRawText());
        output.WriteLine($"{name}: full={fullBytes}, compact={compactBytes}, reduction={1.0 - (double)compactBytes / fullBytes:P1} UTF-8 report bytes (not model tokens).");
        Assert.True(compactBytes < fullBytes, $"Compact report grew: {compactBytes} >= {fullBytes}");
        Assert.Equal(fullReport.GetProperty("modules").GetArrayLength(), compactReport.GetProperty("modules").GetArrayLength());
        foreach (var (original, projected) in fullReport.GetProperty("modules").EnumerateArray().Zip(compactReport.GetProperty("modules").EnumerateArray()))
        {
            Assert.Equal(original.GetProperty("revision").GetString(), projected.GetProperty("revision").GetString());
            Assert.Equal(original.GetProperty("truncated").GetBoolean(), projected.GetProperty("truncated").GetBoolean());
            foreach (var field in new[] { "parameters", "accesses", "calls", "issues" })
                Assert.Equal(original.GetProperty(field).GetArrayLength(), projected.GetProperty(field).GetArrayLength());
            foreach (var field in new[] { "variables", "facts", "calls", "issues" })
                Assert.Equal(original.GetProperty("valueFlow").GetProperty(field).GetArrayLength(), projected.GetProperty("valueFlow").GetProperty(field).GetArrayLength());
            Assert.Equal(0, projected.GetProperty("valueFlow").GetProperty("omittedFacts").GetInt32());
        }
        CheckReferences(compactReport);
    }

    [Fact]
    public async Task Unmatched_focus_keeps_call_boundaries_and_coverage_instead_of_claiming_no_behavior()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P @n int AS BEGIN DECLARE @local int=@n; EXEC dbo.Q @local; END;\nGO\nCREATE PROC dbo.Q @q int AS SELECT * FROM dbo.Missing WHERE id=@q;");
        var report = McpClient.Data(await Analyze(client, await Find(client), focus: ["no-such-value"])).GetProperty("report");
        Assert.False(report.GetProperty("focusMatched").GetBoolean());
        var callee = report.GetProperty("modules")[1];
        Assert.Single(callee.GetProperty("accesses").EnumerateArray());
        Assert.NotEmpty(callee.GetProperty("issues").EnumerateArray());
        Assert.True(callee.GetProperty("valueFlow").GetProperty("omittedFacts").GetInt32() > 0);
        Assert.Contains("not absent behavior", report.GetProperty("selection").GetString());
        CheckReferences(report);
    }

    [Fact]
    public async Task Timings_distinguish_first_initialization_from_warm_requests_and_invalid_options_keep_server_alive()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;");
        var first = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        Assert.True(first.GetProperty("timings").GetProperty("coldStart").GetBoolean());
        var second = McpClient.Data(await Analyze(client, first.GetProperty("modules")[0]));
        Assert.False(second.GetProperty("timings").GetProperty("coldStart").GetBoolean());
        foreach (var property in second.GetProperty("timings").EnumerateObject().Where(p => p.Name.EndsWith("Ms", StringComparison.Ordinal)))
            Assert.True(property.Value.GetDouble() >= 0);
        var selected = first.GetProperty("modules")[0];
        foreach (var (detail, focus) in new[] { ("bad", Array.Empty<string>()), ("full", new[] { "@n" }), ("compact", new[] { "" }),
            ("compact", new[] { new string('a',65) }), ("compact", Enumerable.Repeat("n",9).ToArray()) })
        {
            var response = await Analyze(client, selected, detail, focus);
            Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean());
        }
        Assert.True((await client.Call("ping", new { })).TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Compact_stale_ambiguous_and_expansion_limited_results_retain_explicit_status_and_issues()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P @n int AS EXEC dbo.Q @n;\nGO\nCREATE PROC dbo.Q @q int AS SELECT 1;");
        var report = McpClient.Data(await Analyze(client, await Find(client), maxDepth: 0)).GetProperty("report");
        Assert.Single(report.GetProperty("modules").EnumerateArray());
        Assert.NotEmpty(report.GetProperty("expansionIssues").EnumerateArray());
        var call = report.GetProperty("modules")[0].GetProperty("valueFlow").GetProperty("calls")[0];
        Assert.False(call.TryGetProperty("targetModule", out _));
        Assert.Equal("Q", call.GetProperty("target").GetProperty("identity").GetProperty("name").GetString());
        Assert.True(call.TryGetProperty("targetRevision", out _));
        CheckReferences(report);
        var stale = McpClient.Data(await client.AnalyzeExpanded(new { documentUri = new Uri(client.SourceFile).AbsoluteUri, line = 0, character = 16, expectedRevision = "stale", detail = "compact" })).GetProperty("report");
        Assert.Equal("Unresolved", stale.GetProperty("status").GetString());
        Assert.Empty(stale.GetProperty("modules").EnumerateArray());
        await using var duplicate = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;\nGO\nCREATE PROC dbo.P AS SELECT 2;");
        var ambiguous = McpClient.Data(await Analyze(duplicate, await Find(duplicate))).GetProperty("report");
        Assert.Equal("Ambiguous", ambiguous.GetProperty("status").GetString());
        Assert.Equal(2, ambiguous.GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public async Task Focus_keeps_unsupported_return_outputs_uncertain_sources_and_budget_coverage()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P @n int AS BEGIN DECLARE @r int=0,@status int; " +
            "EXEC @status=dbo.Q @r OUTPUT; SELECT @n=id FROM dbo.Missing; IF @n=2 RETURN; " +
            string.Concat(Enumerable.Repeat("IF @n=0 RETURN; ", 100)) + "END;\nGO\nCREATE PROC dbo.Q @q int OUTPUT AS SET @q=1;");
        var report = McpClient.Data(await Analyze(client, await Find(client), focus: ["no-such-value"])).GetProperty("report");
        Assert.False(report.GetProperty("focusMatched").GetBoolean());
        var module = report.GetProperty("modules")[0];
        Assert.True(module.GetProperty("truncated").GetBoolean());
        Assert.Contains(module.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "control-flow-limit");
        var facts = module.GetProperty("valueFlow").GetProperty("facts").EnumerateArray().ToArray();
        Assert.Contains(facts, f => f.GetProperty("kind").GetString() == "CallReturn" && f.GetProperty("status").GetString() == "Unsupported");
        Assert.Contains(facts, f => f.GetProperty("kind").GetString() == "CallOutput" && f.GetProperty("status").GetString() == "Unsupported");
        Assert.Contains(facts, f => f.GetProperty("role").GetString() == "If" && !f.GetProperty("inputs")[0].GetProperty("sourcesComplete").GetBoolean());
        CheckReferences(report);
    }

    private static void CheckReferences(JsonElement report)
    {
        var evidence = report.GetProperty("evidence"); var notes = report.GetProperty("notes");
        foreach (var module in report.GetProperty("modules").EnumerateArray())
        {
            var flow = module.GetProperty("valueFlow");
            var facts = flow.GetProperty("facts").EnumerateArray().ToDictionary(f => f.GetProperty("id").GetString()!);
            var variables = flow.GetProperty("variables").EnumerateArray().Select(v => v.GetProperty("id").GetString()).ToHashSet();
            foreach (var fact in facts.Values)
            {
                Assert.True(evidence.TryGetProperty(fact.GetProperty("at").GetString()!, out _));
                Assert.True(notes.TryGetProperty(fact.GetProperty("note").GetString()!, out _));
                if (fact.TryGetProperty("targetVariableId", out var target)) Assert.Contains(target.GetString(), variables);
                foreach (var input in fact.GetProperty("inputs").EnumerateArray())
                {
                    if (input.TryGetProperty("variableId", out var variable)) Assert.Contains(variable.GetString(), variables);
                    foreach (var source in input.GetProperty("sourceFacts").EnumerateArray()) Assert.True(facts.ContainsKey(source.GetString()!));
                }
            }
            foreach (var call in flow.GetProperty("calls").EnumerateArray())
                foreach (var binding in call.GetProperty("bindings").EnumerateArray())
                {
                    Assert.True(facts.ContainsKey(binding.GetProperty("inputFactId").GetString()!));
                    if (binding.TryGetProperty("outputFactId", out var result)) Assert.True(facts.ContainsKey(result.GetString()!));
                }
        }
    }
}
