namespace SqlLsp.Mcp.IntegrationTests;

public class ScopeRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_offers_actionable_physical_choices_without_selecting_or_crossing_boundaries(bool persisted)
    {
        var root = Path.Combine(TestPaths.TempRoot, "scope-choice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, persisted ? "{\"version\":1,\"indexCache\":{\"enabled\":true}}" : "{\"version\":1}");
        foreach (var project in new[] { "OLTP copy 中文", "DW" })
        {
            var folder = Path.Combine(root, "samples", project);
            Directory.CreateDirectory(Path.Combine(folder, "procs"));
            Directory.CreateDirectory(Path.Combine(folder, "functions"));
            await File.WriteAllTextAsync(Path.Combine(folder, "procs", "p.sql"), "CREATE PROC dbo.P AS BEGIN EXEC dbo.Q; EXEC dbo.Outside; END;");
            await File.WriteAllTextAsync(Path.Combine(folder, "functions", "q.sql"), "CREATE PROC dbo.Q AS SELECT 1;");
        }
        await File.WriteAllTextAsync(Path.Combine(root, "samples", "DW", "procs", "outside.sql"), "CREATE PROC dbo.Outside AS SELECT 99;");
        await using var client = await McpClient.Start("SELECT 1;", rootDirectory: root, settingsFile: settings);
        var found = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        Assert.Equal(2, found.GetProperty("returned").GetInt32());
        var selected = found.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("target").GetProperty("uri").GetString()!.Contains("OLTP"));
        Assert.Equal("Ambiguous", selected.GetProperty("status").GetString());
        var unscoped = McpClient.Data(await client.Tool("analyze_procedure", new { moduleRef = selected.GetProperty("moduleRef").GetString() }));
        Assert.Equal("Ambiguous", unscoped.GetProperty("report").GetProperty("status").GetString());
        var choice = selected.GetProperty("scopeChoice");
        Assert.Equal("samples/OLTP copy 中文/", choice.GetProperty("directory").GetString()!.TrimEnd('/') + "/");
        Assert.Contains("Not verified", choice.GetProperty("evidence").GetString());
        var moduleRef = choice.GetProperty("moduleRef").GetString();
        var scoped = McpClient.Data(await client.AnalyzeExpanded(new { moduleRef, detail = "full" }));
        Assert.Equal(choice.GetProperty("directoryUri").GetString(), scoped.GetProperty("scope").GetProperty("directoryUri").GetString());
        var report = scoped.GetProperty("report");
        Assert.Equal("Resolved", report.GetProperty("status").GetString());
        Assert.Equal(2, report.GetProperty("modules").GetArrayLength());
        Assert.DoesNotContain("/DW/", report.GetRawText());
        var calls = report.GetProperty("modules")[0].GetProperty("calls").EnumerateArray().ToArray();
        Assert.Contains(calls, c => c.GetProperty("status").GetString() == "Resolved" &&
            c.GetProperty("target").GetProperty("identity").GetProperty("name").GetString() == "Q");
        Assert.Equal("[dbo].[Outside]", Assert.Single(calls, c => c.GetProperty("status").GetString() == "Unresolved").GetProperty("name").GetString());
        var conflict = await client.Tool("analyze_procedure", new { moduleRef, scopeUri = new Uri(Path.Combine(root, "samples", "DW")).AbsoluteUri });
        Assert.True(conflict.GetProperty("result").GetProperty("isError").GetBoolean());
        await File.WriteAllTextAsync(Path.Combine(root, "samples", "OLTP copy 中文", "functions", "q.sql"), "CREATE PROC dbo.Q AS SELECT 2;");
        var until = DateTime.UtcNow.AddSeconds(10);
        while (McpClient.Data(await client.Tool("find_procedures", new { query = "P" })).GetProperty("generation").GetString() == found.GetProperty("generation").GetString())
        { Assert.True(DateTime.UtcNow < until); await Task.Delay(30); }
        var stale = await client.Tool("analyze_procedure", new { moduleRef });
        Assert.True(stale.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_file_duplicates_and_verified_project_duplicates_have_no_narrowing_choice(bool project)
    {
        await using var client = await McpClient.Start("SELECT 1;");
        var folder = Path.Combine(client.Root, "application");
        Directory.CreateDirectory(Path.Combine(folder, "one"));
        Directory.CreateDirectory(Path.Combine(folder, "two"));
        await File.WriteAllTextAsync(Path.Combine(folder, "one", "p.sql"), project ? "CREATE PROC dbo.P AS SELECT 1;" : "CREATE PROC dbo.P AS SELECT 1;\nGO\nCREATE PROC dbo.P AS SELECT 2;");
        if (project)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "two", "p.sql"), "CREATE PROC dbo.P AS SELECT 2;");
            await File.WriteAllTextAsync(Path.Combine(folder, "application.sqlproj"), "<Project><ItemGroup><Build Include=\"one/p.sql;two/p.sql\" /></ItemGroup></Project>");
        }
        var found = McpClient.Data(await client.Tool("find_procedures", new { query = "P" }));
        Assert.Equal(2, found.GetProperty("returned").GetInt32());
        Assert.All(found.GetProperty("modules").EnumerateArray(), m =>
        {
            Assert.Equal("Ambiguous", m.GetProperty("status").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, m.GetProperty("scopeChoice").ValueKind);
        });
    }
}
