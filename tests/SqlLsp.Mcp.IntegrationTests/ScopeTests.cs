using System.Text.Json;

namespace SqlLsp.Mcp.IntegrationTests;

public class ScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_request_folder_scope_separates_SQL_only_copies_and_is_preserved_by_module_reference(bool persisted)
    {
        var root = Path.Combine(TestPaths.TempRoot, "request-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, persisted ? "{\"version\":1,\"indexCache\":{\"enabled\":true}}" : "{\"version\":1}");
        await using var client = await McpClient.Start("SELECT 1;", rootDirectory: root, settingsFile: settings);
        foreach (var folder in new[] { "A", "B" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            await File.WriteAllTextAsync(Path.Combine(root, folder, "p.sql"), "CREATE PROC dbo.P AS EXEC dbo.Q;");
            await File.WriteAllTextAsync(Path.Combine(root, folder, "q.sql"), "CREATE PROC dbo.Q AS SELECT 1;");
        }
        var unscoped = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        Assert.All(unscoped.GetProperty("modules").EnumerateArray(), m => Assert.Equal("Ambiguous", m.GetProperty("status").GetString()));
        var scopeUri = new Uri(Path.Combine(root, "A")).AbsoluteUri;
        var scoped = McpClient.Data(await client.Tool("find_procedures", new { query = "P", scopeUri }));
        var selected = Assert.Single(scoped.GetProperty("modules").EnumerateArray());
        Assert.Equal("Resolved", selected.GetProperty("status").GetString());
        var report = McpClient.Data(await client.AnalyzeExpanded(new { moduleRef = selected.GetProperty("moduleRef").GetString(), detail = "full" })).GetProperty("report");
        Assert.Equal("Resolved", report.GetProperty("status").GetString());
        Assert.Equal(2, report.GetProperty("modules").GetArrayLength());
        Assert.DoesNotContain("/B/", report.GetRawText());
        var outside = await client.Tool("analyze_procedure", new { documentUri = new Uri(Path.Combine(root, "B", "p.sql")).AbsoluteUri, declaration = "P", scopeUri });
        Assert.True(outside.GetProperty("result").GetProperty("isError").GetBoolean());
        var invalid = await client.Tool("find_procedures", new { scopeUri = new Uri(Path.GetDirectoryName(root)!).AbsoluteUri });
        Assert.True(invalid.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Literal_project_membership_separates_identical_objects_and_scoped_discovery()
    {
        await using var client = await McpClient.Start("SELECT 1;");
        foreach (var name in new[] { "A", "B" })
        {
            var project = Path.Combine(client.Root, name);
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Combine(project, name + ".sqlproj"), "<Project><ItemGroup><Build Include=\"modules.sql\" /></ItemGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(project, "modules.sql"), "CREATE PROC dbo.P AS SELECT * FROM dbo.Product;\nGO\nCREATE TABLE dbo.Product(id int);");
        }
        var found = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        Assert.Equal(2, found.GetProperty("totalMatches").GetInt32());
        foreach (var module in found.GetProperty("modules").EnumerateArray())
        {
            var scopeId = module.GetProperty("identity").GetProperty("sourceContextId").GetString();
            Assert.EndsWith(".sqlproj", scopeId);
            var scoped = McpClient.Data(await client.Tool("find_procedures", new { query = "P", scopeId }));
            Assert.Single(scoped.GetProperty("modules").EnumerateArray());
            Assert.Equal(1, scoped.GetProperty("scope").GetProperty("indexedDocuments").GetInt32());
            var analyzed = McpClient.Data(await client.AnalyzeExpanded(new
            {
                documentUri = module.GetProperty("target").GetProperty("uri").GetString(),
                line = 0,
                character = 16,
                detail = "full"
            })).GetProperty("report");
            Assert.Equal("Resolved", analyzed.GetProperty("status").GetString());
            var access = Assert.Single(analyzed.GetProperty("modules")[0].GetProperty("accesses").EnumerateArray());
            Assert.Equal("Resolved", access.GetProperty("status").GetString());
            Assert.Equal(scopeId, access.GetProperty("target").GetProperty("identity").GetProperty("sourceContextId").GetString());
        }
    }

    [Fact]
    public async Task Explicit_scopes_apply_database_schema_comparison_exclusions_and_qualified_reference_mapping()
    {
        var root = Path.Combine(TestPaths.TempRoot, "scopes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "A"));
        Directory.CreateDirectory(Path.Combine(root, "B"));
        var config = JsonSerializer.Serialize(new
        {
            workspace = new
            {
                projectScopes = new object[] {
                new { id = "a", root = new Uri(Path.Combine(root,"A")).AbsoluteUri, database = "First", defaultSchema = "dbo", caseSensitive = false, exclude = new[] { "excluded.sql" }, references = new[] { "b" } },
                new { id = "b", root = new Uri(Path.Combine(root,"B")).AbsoluteUri, database = "Second", defaultSchema = "dbo" }
            }
            }
        });
        await using var client = await McpClient.Start("SELECT 1;", config, rootDirectory: root);
        await File.WriteAllTextAsync(Path.Combine(root, "A", "caller.sql"), "CREATE PROC P AS BEGIN EXEC q @VALUE=1; EXEC Second.dbo.Q; END;");
        await File.WriteAllTextAsync(Path.Combine(root, "A", "q.sql"), "CREATE PROC dbo.Q @value int AS SELECT @value;");
        await File.WriteAllTextAsync(Path.Combine(root, "A", "excluded.sql"), "CREATE PROC dbo.Hidden AS SELECT 1;");
        await File.WriteAllTextAsync(Path.Combine(root, "B", "q.sql"), "CREATE PROC dbo.Q AS SELECT 2;");
        var discovery = McpClient.Data(await client.Tool("find_procedures", new { scopeId = "a" }));
        Assert.Equal(2, discovery.GetProperty("totalMatches").GetInt32());
        Assert.Equal(1, discovery.GetProperty("coverage").GetProperty("excludedPolicy").GetInt32());
        var analyzed = McpClient.Data(await client.AnalyzeExpanded(new { documentUri = new Uri(Path.Combine(root, "A", "caller.sql")).AbsoluteUri, line = 0, character = 12, detail = "full" })).GetProperty("report");
        var calls = analyzed.GetProperty("modules")[0].GetProperty("calls");
        Assert.Equal(2, calls.GetArrayLength());
        Assert.All(calls.EnumerateArray(), c => Assert.Equal("Resolved", c.GetProperty("status").GetString()));
        Assert.Equal("First", calls[0].GetProperty("target").GetProperty("identity").GetProperty("database").GetString());
        Assert.Equal("Second", calls[1].GetProperty("target").GetProperty("identity").GetProperty("database").GetString());
        Assert.All(analyzed.GetProperty("modules")[0].GetProperty("valueFlow").GetProperty("calls").EnumerateArray(),
            c => Assert.Equal("Resolved", c.GetProperty("bindingStatus").GetString()));
    }
}
