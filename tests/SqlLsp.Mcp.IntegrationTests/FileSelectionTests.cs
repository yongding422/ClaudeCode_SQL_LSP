namespace SqlLsp.Mcp.IntegrationTests;

public class FileSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Declaration_selection_keeps_configured_case_rules_with_both_index_modes(bool persisted)
    {
        var root = Path.Combine(TestPaths.TempRoot, "case-selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, "{\"version\":1,\"indexCache\":{\"enabled\":" + (persisted ? "true" : "false") + "}}");
        await using var client = await McpClient.Start("CREATE PROC dbo.ExactCase AS SELECT 1;", "{\"resolution\":{\"caseSensitive\":true}}", rootDirectory: root, settingsFile: settings);
        var documentUri = new Uri(client.SourceFile).AbsoluteUri;
        var wrong = await client.Tool("analyze_procedure", new { documentUri, declaration = "dbo.exactcase" });
        Assert.True(wrong.GetProperty("result").GetProperty("isError").GetBoolean());
        var exact = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri, declaration = "dbo.ExactCase" }));
        Assert.Equal("Resolved", exact.GetProperty("report").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_only_selection_skips_blank_comments_but_explicit_zero_position_is_preserved(bool persisted)
    {
        var root = Path.Combine(TestPaths.TempRoot, "file-selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, "{\"version\":1,\"indexCache\":{\"enabled\":" + (persisted ? "true" : "false") + "}}");
        await using var client = await McpClient.Start("\n-- 😀 comment\nCREATE PROC [dbo].[P] AS SELECT 1;", rootDirectory: root, settingsFile: settings);
        var documentUri = new Uri(client.SourceFile).AbsoluteUri;
        var selected = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri }));
        Assert.Equal("Resolved", selected.GetProperty("report").GetProperty("status").GetString());
        Assert.Equal("P", selected.GetProperty("report").GetProperty("modules")[0].GetProperty("identity").GetProperty("name").GetString());
        foreach (var position in new object[] { new { documentUri, line = 0 }, new { documentUri, character = 0 } })
        {
            var explicitPosition = McpClient.Data(await client.Tool("analyze_procedure", position));
            Assert.Equal("Unresolved", explicitPosition.GetProperty("report").GetProperty("status").GetString());
            Assert.Contains("declaration", explicitPosition.GetProperty("brief").GetProperty("selectionRecovery").GetString());
        }
    }

    [Theory]
    [InlineData("CREATE VIEW dbo.V AS SELECT 1 AS n;", "V")]
    [InlineData("CREATE FUNCTION dbo.F() RETURNS int AS BEGIN RETURN 1; END;", "F")]
    public async Task Unique_views_and_functions_are_supported(string sql, string name)
    {
        await using var client = await McpClient.Start(sql);
        var data = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri }));
        Assert.Equal(name, data.GetProperty("report").GetProperty("modules")[0].GetProperty("identity").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("CREATE PROC dbo.P AS SELECT 1;\nGO\nCREATE PROC dbo.Q AS SELECT 2;")]
    [InlineData("CREATE PROC dbo.P AS SELECT 1;\nGO\nSELECT FROM;")]
    public async Task Missing_multiple_or_partially_parsed_declarations_return_actionable_recovery(string sql)
    {
        await using var client = await McpClient.Start(sql);
        var wire = await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri });
        Assert.True(wire.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("find_procedures", wire.ToString());
        Assert.Contains("No candidate was chosen", wire.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_selection_does_not_disambiguate_global_copies(bool persisted)
    {
        var root = Path.Combine(TestPaths.TempRoot, "file-copies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "other.sql"), "CREATE PROC dbo.P AS SELECT 2;");
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, "{\"version\":1,\"indexCache\":{\"enabled\":" + (persisted ? "true" : "false") + "}}");
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;", rootDirectory: root, settingsFile: settings);
        var data = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri }));
        Assert.Equal("Ambiguous", data.GetProperty("report").GetProperty("status").GetString());
        Assert.Equal(0, data.GetProperty("brief").GetProperty("facts").GetArrayLength());
    }
}
