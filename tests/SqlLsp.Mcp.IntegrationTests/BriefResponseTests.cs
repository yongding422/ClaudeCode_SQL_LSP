using System.Text;
using System.Text.Json;

namespace SqlLsp.Mcp.IntegrationTests;

public class BriefResponseTests
{
    [Theory]
    [InlineData("dbo.P")]
    [InlineData("[dbo].[P]")]
    [InlineData("\"dbo\".\"P\"")]
    [InlineData(" [dbo] . [P] ")]
    [InlineData("[P]")]
    public async Task Quoted_declaration_selectors_resolve_without_discovery(string declaration)
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;");
        var response = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration }));
        Assert.Equal("Resolved", response.GetProperty("report").GetProperty("status").GetString());
        Assert.Equal("procedure-brief-v1", response.GetProperty("brief").GetProperty("format").GetString());
        Assert.Equal("compact-v1", response.GetProperty("report").GetProperty("format").GetString());
    }

    [Fact]
    public async Task Quoted_dots_and_escaped_brackets_preserve_identifier_boundaries_and_case_rules()
    {
        await using var client = await McpClient.Start("CREATE PROC [dbo].[a.b]]c] AS SELECT 1;\nGO\nCREATE PROC [dbo.a].[b]]c] AS SELECT 2;");
        foreach (var name in new[] { "[dbo].[a.b]]c]", "\"dbo\".\"a.b]c\"" })
        {
            var data = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = name }));
            Assert.Equal("a.b]c", data.GetProperty("report").GetProperty("modules")[0].GetProperty("identity").GetProperty("name").GetString());
        }
        var wrongCase = await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = "[DBO].[a.b]]c]" });
        Assert.True(wrongCase.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData("dbo.P; SELECT 1;")]
    [InlineData("dbo..P")]
    [InlineData("Db.dbo.P")]
    [InlineData("dbo.P -- comment")]
    [InlineData("'dbo.P'")]
    [InlineData("[dbo].[P")]
    public async Task Declaration_selector_rejects_nonidentifiers_instead_of_guessing(string declaration)
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;");
        var wire = await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration });
        Assert.True(wire.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("declaration must be", wire.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_SQL_only_projects_require_explicit_scope_and_reuse_it_in_handles(bool persisted)
    {
        var root = Path.Combine(TestPaths.TempRoot, "brief-scopes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "OLTP", "DW" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            await File.WriteAllTextAsync(Path.Combine(root, name, "p.sql"), "CREATE PROC dbo.P AS EXEC dbo.Q;");
            await File.WriteAllTextAsync(Path.Combine(root, name, "q.sql"), "CREATE PROC dbo.Q AS SELECT 1;");
        }
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, persisted ? "{\"version\":1,\"indexCache\":{\"enabled\":true}}" : "{\"version\":1}");
        await using var client = await McpClient.Start("SELECT 1;", rootDirectory: root, settingsFile: settings);
        var documentUri = new Uri(Path.Combine(root, "OLTP", "p.sql")).AbsoluteUri;
        var unscoped = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri, declaration = "[dbo].[P]" }));
        Assert.Equal("Ambiguous", unscoped.GetProperty("report").GetProperty("status").GetString());
        Assert.Contains("scopeUri", unscoped.GetProperty("brief").GetProperty("selectionRecovery").GetString());
        Assert.Empty(unscoped.GetProperty("brief").GetProperty("facts").EnumerateArray());
        var found = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        Assert.All(found.GetProperty("modules").EnumerateArray(), m => Assert.Contains("scopeUri", m.GetProperty("selectionHint").GetString()));

        var scopeUri = new Uri(Path.Combine(root, "OLTP")).AbsoluteUri;
        var direct = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri, declaration = "[dbo].[P]", scopeUri, intent = "dependencies" }));
        Assert.Equal("Resolved", direct.GetProperty("report").GetProperty("status").GetString());
        Assert.DoesNotContain("/DW/", direct.GetProperty("brief").GetRawText());
        Assert.Contains(direct.GetProperty("brief").GetProperty("facts").EnumerateArray(), f => f.GetProperty("kind").GetString() == "Call" && f.GetProperty("data").GetProperty("status").GetString() == "Resolved");
        var scoped = McpClient.Data(await client.Tool("find_procedures", new { query = "P", scopeUri }));
        var moduleRef = scoped.GetProperty("modules")[0].GetProperty("moduleRef").GetString();
        var reused = McpClient.Data(await client.Tool("analyze_procedure", new { moduleRef, intent = "transactions" }));
        Assert.Equal(direct.GetProperty("scope").GetProperty("directoryUri").GetString(), reused.GetProperty("scope").GetProperty("directoryUri").GetString());
        Assert.Equal("Resolved", reused.GetProperty("report").GetProperty("status").GetString());

        // Real duplicates inside the chosen scope must still be ambiguous.
        await File.WriteAllTextAsync(Path.Combine(root, "OLTP", "duplicate.sql"), "CREATE PROC dbo.P AS SELECT 2;");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (McpClient.Data(await client.Tool("find_procedures", new { query = "P", scopeUri })).GetProperty("totalMatches").GetInt32() != 2)
            await Task.Delay(100, deadline.Token);
        var duplicate = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri, declaration = "[dbo].[P]", scopeUri }));
        Assert.Equal("Ambiguous", duplicate.GetProperty("report").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(16384)]
    [InlineData(32768)]
    public async Task Oversized_reports_keep_critical_citeable_facts_in_the_first_bounded_response(int budget)
    {
        var root = Path.Combine(TestPaths.TempRoot, "brief-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, "{\"version\":1,\"responses\":{\"analysisMaxBytes\":" + budget + "}}");
        const string prefix = "-- 😀\nCREATE PROC dbo.P AS BEGIN\nDECLARE @id int; SET @id=NEXT VALUE FOR dbo.S;\nBEGIN TRAN;\nSELECT @id=id FROM dbo.T;\nTHROW 51000,N'failure',2; RETURN -1;\n";
        await using var client = await McpClient.Start(prefix + string.Concat(Enumerable.Repeat("SELECT @id=id FROM dbo.T;\n", 12)) +
            string.Concat(Enumerable.Repeat("IF @id=0 RETURN;\n", 100)) + "END;", rootDirectory: root, settingsFile: settings);
        var wire = await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = "[dbo].[P]", intent = "transactions" });
        Assert.InRange(Encoding.UTF8.GetByteCount(wire.GetRawText()), 1, budget);
        var data = McpClient.Data(wire);
        var brief = data.GetProperty("brief");
        Assert.Equal("paged-report-v1", data.GetProperty("report").GetProperty("format").GetString());
        var facts = brief.GetProperty("facts").EnumerateArray().ToArray();
        Assert.Equal("return-after-throw", facts[0].GetProperty("kind").GetString());
        Assert.Contains(facts, f => f.GetProperty("kind").GetString() == "SequenceAllocation" && f.GetProperty("data").GetProperty("beforeFirstExplicitTransaction").GetBoolean());
        Assert.Contains(facts, f => f.GetProperty("kind").GetString() == "SelectAssignment" && f.GetProperty("detail").GetString()!.Contains("retains"));
        Assert.True(brief.GetProperty("truncated").GetBoolean());
        Assert.True(brief.GetProperty("omittedFacts").GetInt32() > 0);
        foreach (var fact in facts)
        {
            Assert.Contains("modules.sql:", fact.GetProperty("citation").GetString());
            var source = brief.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("id").GetString() == fact.GetProperty("source").GetString());
            Assert.Equal(new Uri(client.SourceFile).AbsoluteUri, source.GetProperty("uri").GetString());
        }
        Assert.Equal(6, facts[0].GetProperty("line").GetInt32());
        Assert.False(string.IsNullOrEmpty(brief.GetProperty("modules")[0].GetProperty("revision").GetString()));
        var reportRef = data.GetProperty("report").GetProperty("reportRef").GetString();
        var expanded = McpClient.Data(await client.Tool("analyze_procedure", new { reportRef, section = facts[0].GetProperty("section").GetString() }));
        Assert.Equal(JsonValueKind.Null, expanded.GetProperty("brief").ValueKind);
        Assert.Contains("return-after-throw", expanded.GetProperty("report").GetRawText());
    }

    [Fact]
    public async Task Intent_changes_only_presentation_and_preserves_full_report_semantics()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS BEGIN DECLARE @id int; SELECT @id=id FROM dbo.T; BEGIN TRAN; SAVE TRAN s; COMMIT; BEGIN TRAN; ROLLBACK; THROW 51000,N'bad',2; RETURN -1; END;");
        async Task<JsonElement> Analyze(string intent) => McpClient.Data(await client.AnalyzeExpanded(new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = "P", detail = "full", intent }));
        var transactions = await Analyze("transactions");
        var dependencies = await Analyze("dependencies");
        Assert.True(JsonElement.DeepEquals(transactions.GetProperty("report"), dependencies.GetProperty("report")));
        Assert.Contains(dependencies.GetProperty("brief").GetProperty("facts").EnumerateArray(), f => f.GetProperty("kind").GetString() == "RelationAccess");
        var thrown = transactions.GetProperty("brief").GetProperty("facts").EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "Throw");
        Assert.Equal("2", thrown.GetProperty("data").GetProperty("throw").GetProperty("state").GetProperty("preview").GetString());
        var invalid = await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = "P", intent = "guess-runtime-values" });
        Assert.True(invalid.GetProperty("result").GetProperty("isError").GetBoolean());
    }
}
