namespace SqlLsp.Mcp.IntegrationTests;

public class ScopedLoadingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_scoped_request_loads_only_its_sql_and_unscoped_request_restores_full_ambiguity(bool persisted)
    {
        var root = Path.Combine(TestPaths.TempRoot, "scoped-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, persisted ? "{\"version\":1,\"indexCache\":{\"enabled\":true}}" : "{\"version\":1}");
        foreach (var folder in new[] { "A", "B" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            await File.WriteAllTextAsync(Path.Combine(root, folder, "p.sql"), "CREATE PROC dbo.P AS BEGIN EXEC dbo.Q; EXEC dbo.Outside; END;");
        }
        await File.WriteAllTextAsync(Path.Combine(root, "A", "q.sql"), "CREATE PROC dbo.Q AS SELECT 1;");
        await File.WriteAllTextAsync(Path.Combine(root, "B", "outside.sql"), "CREATE PROC dbo.Outside AS SELECT 2;");
        await using var client = await McpClient.Start("SELECT 1;", rootDirectory: root, settingsFile: settings);
        var scopeUri = new Uri(Path.Combine(root, "A")).AbsoluteUri;
        var scoped = McpClient.Data(await client.Tool("find_procedures", new { query = "P", scopeUri }));
        Assert.Equal(2, scoped.GetProperty("indexedDocuments").GetInt32());
        Assert.Equal(2, scoped.GetProperty("coverage").GetProperty("parseAttempts").GetInt32());
        var selected = Assert.Single(scoped.GetProperty("modules").EnumerateArray());
        var moduleRef = selected.GetProperty("moduleRef").GetString();
        var context = selected.GetProperty("identity").GetProperty("sourceContextId").GetString();
        var analyzed = McpClient.Data(await client.AnalyzeExpanded(new { moduleRef, detail = "full" }));
        var calls = analyzed.GetProperty("report").GetProperty("modules")[0].GetProperty("calls").EnumerateArray().ToArray();
        Assert.Contains(calls, c => c.GetProperty("status").GetString() == "Resolved");
        Assert.Contains(calls, c => c.GetProperty("name").GetString() == "[dbo].[Outside]" && c.GetProperty("status").GetString() == "Unresolved");
        Assert.Equal(2, analyzed.GetProperty("coverage").GetProperty("indexed").GetInt32());

        var full = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        Assert.Equal(5, full.GetProperty("indexedDocuments").GetInt32());
        Assert.All(full.GetProperty("modules").EnumerateArray(), m =>
        {
            Assert.Equal("Ambiguous", m.GetProperty("status").GetString());
            Assert.Equal(context, m.GetProperty("identity").GetProperty("sourceContextId").GetString());
        });
        // Switching inventory is not a source edit. The same scoped handle remains usable.
        var restored = McpClient.Data(await client.AnalyzeExpanded(new { moduleRef, detail = "full" }));
        Assert.Equal("Resolved", restored.GetProperty("report").GetProperty("status").GetString());
        var invalid = await client.Tool("find_procedures", new { scopeUri = new Uri(Path.GetDirectoryName(root)!).AbsoluteUri });
        Assert.True(invalid.GetProperty("result").GetProperty("isError").GetBoolean());
        var stillValid = McpClient.Data(await client.Tool("find_procedures", new { query = "P", scopeUri }));
        Assert.Equal(scoped.GetProperty("generation").GetString(), stillValid.GetProperty("generation").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Narrowing_preserves_project_membership_and_keeps_outside_project_dependencies_unknown(bool persisted)
    {
        var root = Path.Combine(TestPaths.TempRoot, "scoped-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "procs"));
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, persisted ? "{\"version\":1,\"indexCache\":{\"enabled\":true}}" : "{\"version\":1}");
        var project = Path.Combine(root, "application.sqlproj");
        await File.WriteAllTextAsync(project, "<Project><ItemGroup><Build Include=\"procs/p.sql;q.sql\" /></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(root, "procs", "p.sql"), "CREATE PROC dbo.P AS EXEC dbo.Q;");
        await File.WriteAllTextAsync(Path.Combine(root, "q.sql"), "CREATE PROC dbo.Q AS SELECT 1;");
        await using var client = await McpClient.Start("SELECT 1;", rootDirectory: root, settingsFile: settings);
        var args = new { documentUri = new Uri(Path.Combine(root, "procs", "p.sql")).AbsoluteUri, detail = "full" };
        var narrow = McpClient.Data(await client.AnalyzeExpanded(new { args.documentUri, args.detail, scopeUri = new Uri(Path.Combine(root, "procs")).AbsoluteUri }));
        Assert.Equal(1, narrow.GetProperty("coverage").GetProperty("parseAttempts").GetInt32());
        Assert.Equal(new Uri(project).AbsoluteUri, narrow.GetProperty("sourceContext").GetProperty("id").GetString());
        Assert.True(narrow.GetProperty("sourceContext").GetProperty("membershipVerified").GetBoolean());
        var call = Assert.Single(narrow.GetProperty("report").GetProperty("modules")[0].GetProperty("calls").EnumerateArray());
        Assert.Equal("Unresolved", call.GetProperty("status").GetString());
        var full = McpClient.Data(await client.AnalyzeExpanded(args));
        Assert.Equal("Resolved", full.GetProperty("report").GetProperty("modules")[0].GetProperty("calls")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Persisted_scopes_have_separate_snapshots_verify_bytes_and_reuse_after_restart()
    {
        var root = Path.Combine(TestPaths.TempRoot, "scoped-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "A"));
        try
        {
            var settings = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(settings, "{\"version\":1,\"indexCache\":{\"enabled\":true}}");
            var file = Path.Combine(root, "A", "p.sql");
            await File.WriteAllTextAsync(file, "CREATE PROC dbo.P AS SELECT 1;");
            var args = new { query = "P", scopeUri = new Uri(Path.Combine(root, "A")).AbsoluteUri };
            string generation;
            await using (var first = await McpClient.Start("SELECT 1;", rootDirectory: root, settingsFile: settings, preserveWorkspace: true))
            {
                var scoped = McpClient.Data(await first.Tool("find_procedures", args));
                generation = scoped.GetProperty("generation").GetString()!;
                await first.Tool("find_procedures", new { query = "P" });
            }
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, ".sqlscriptdom", "index", "index-v1"), "workspace-*.json.gz").Length);
            await using var second = await McpClient.Start("SELECT 1;", rootDirectory: root, settingsFile: settings, preserveWorkspace: true);
            var restored = McpClient.Data(await second.Tool("find_procedures", args));
            Assert.Equal(generation, restored.GetProperty("generation").GetString());
            Assert.Equal(0, restored.GetProperty("coverage").GetProperty("parseAttempts").GetInt32());
            Assert.Equal(1, restored.GetProperty("indexCache").GetProperty("reusedFiles").GetInt32());
            var time = File.GetLastWriteTimeUtc(file);
            await File.WriteAllTextAsync(file, "CREATE PROC dbo.P AS SELECT 2;");
            File.SetLastWriteTimeUtc(file, time);
            var changed = McpClient.Data(await second.Tool("find_procedures", args));
            Assert.NotEqual(generation, changed.GetProperty("generation").GetString());
            Assert.Equal(1, changed.GetProperty("coverage").GetProperty("parseAttempts").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }
}
