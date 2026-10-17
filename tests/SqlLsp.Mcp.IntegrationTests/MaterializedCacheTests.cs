using System.Text.Json;

namespace SqlLsp.Mcp.IntegrationTests;

public class MaterializedCacheTests
{
    private const string Sql = "CREATE PROC dbo.P AS EXEC dbo.Q;";

    [Fact]
    public async Task Concurrent_writers_and_parser_changes_preserve_available_verified_facts()
    {
        var root = Path.Combine(TestPaths.TempRoot, "index-writers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(settings, "{\"version\":1,\"indexCache\":{\"enabled\":true}}");
            await using (var a = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;", rootDirectory: root, preserveWorkspace: true, settingsFile: settings))
            await using (var b = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;", rootDirectory: root, preserveWorkspace: true, settingsFile: settings))
            {
                var responses = await Task.WhenAll(a.Tool("find_procedures", new { query = "P" }), b.Tool("find_procedures", new { query = "P" }));
                Assert.All(responses, r => Assert.Single(McpClient.Data(r).GetProperty("modules").EnumerateArray()));
            }
            await using var changed = await McpClient.Start("CREATE PROC dbo.P AS SELECT 1;", "{\"sql\":{\"parserVersion\":\"180\"}}",
                rootDirectory: root, preserveWorkspace: true, settingsFile: settings);
            var result = McpClient.Data(await changed.Tool("find_procedures", new { query = "P" }));
            Assert.Equal(1, result.GetProperty("coverage").GetProperty("parseAttempts").GetInt32());
            Assert.Single(result.GetProperty("modules").EnumerateArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Restart_restores_verified_discovery_without_parsing_and_analysis_loads_only_selected_dependencies()
    {
        var root = Path.Combine(TestPaths.TempRoot, "materialized-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(settings, "{\"version\":1,\"indexCache\":{\"enabled\":true},\"analysisCache\":{\"enabled\":true}}");
            await File.WriteAllTextAsync(Path.Combine(root, "q.sql"), "CREATE PROC dbo.Q AS SELECT 1;");
            for (var i = 0; i < 10; i++) await File.WriteAllTextAsync(Path.Combine(root, $"other{i}.sql"), $"CREATE PROC dbo.Other{i} AS SELECT {i};");
            string revision;
            await using (var first = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true, settingsFile: settings))
            {
                var discovery = McpClient.Data(await first.Tool("find_procedures", new { query = "P" }));
                Assert.Equal(12, discovery.GetProperty("indexedDocuments").GetInt32());
                Assert.Equal(12, discovery.GetProperty("coverage").GetProperty("parseAttempts").GetInt32());
                revision = discovery.GetProperty("modules")[0].GetProperty("revision").GetString()!;
                var report = McpClient.Data(await first.Tool("analyze_procedure", new { documentUri = new Uri(first.SourceFile).AbsoluteUri, line = 0, character = 16 }));
                Assert.Equal(12, report.GetProperty("indexedDocuments").GetInt32());
                Assert.Equal(2, report.GetProperty("parsedDocuments").GetInt32());
                Assert.Equal(2, report.GetProperty("indexCache").GetProperty("bodyParses").GetInt32());
                Assert.Equal(2, report.GetProperty("report").GetProperty("modules").GetArrayLength());
                Assert.Equal("Stored", report.GetProperty("cache").GetProperty("status").GetString());
            }
            await using (var second = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true, settingsFile: settings))
            {
                var discovery = McpClient.Data(await second.Tool("find_procedures", new { query = "P" }));
                Assert.Equal(0, discovery.GetProperty("coverage").GetProperty("parseAttempts").GetInt32());
                Assert.Equal(12, discovery.GetProperty("indexCache").GetProperty("reusedFiles").GetInt32());
                Assert.Equal(revision, discovery.GetProperty("modules")[0].GetProperty("revision").GetString());
                var report = McpClient.Data(await second.Tool("analyze_procedure", new { documentUri = new Uri(second.SourceFile).AbsoluteUri, line = 0, character = 16 }));
                Assert.Equal("Hit", report.GetProperty("cache").GetProperty("status").GetString());
                var q = Path.Combine(root, "q.sql");
                var stamp = File.GetLastWriteTimeUtc(q);
                await File.WriteAllTextAsync(q, "CREATE PROC dbo.Q AS SELECT 2;");
                File.SetLastWriteTimeUtc(q, stamp);
                report = McpClient.Data(await second.Tool("analyze_procedure", new { documentUri = new Uri(second.SourceFile).AbsoluteUri, line = 0, character = 16 }));
                Assert.NotEqual("Hit", report.GetProperty("cache").GetProperty("status").GetString());
                Assert.Equal(1, report.GetProperty("coverage").GetProperty("parseAttempts").GetInt32());
                await File.WriteAllTextAsync(Path.Combine(root, "competing.sql"), "CREATE PROC dbo.Q AS SELECT 3;");
                report = McpClient.Data(await second.Tool("analyze_procedure", new { documentUri = new Uri(second.SourceFile).AbsoluteUri, line = 0, character = 16 }));
                Assert.Equal("Ambiguous", report.GetProperty("report").GetProperty("modules")[0].GetProperty("calls")[0].GetProperty("status").GetString());
                Assert.Single(report.GetProperty("report").GetProperty("modules").EnumerateArray());
                File.Delete(Path.Combine(root, "competing.sql"));
                File.Move(q, Path.Combine(root, "renamed.sql"));
                report = McpClient.Data(await second.Tool("analyze_procedure", new { documentUri = new Uri(second.SourceFile).AbsoluteUri, line = 0, character = 16 }));
                Assert.Equal("Resolved", report.GetProperty("report").GetProperty("modules")[0].GetProperty("calls")[0].GetProperty("status").GetString());
                Assert.Contains("renamed.sql", report.GetProperty("report").GetRawText());
            }
            var snapshot = Assert.Single(Directory.GetFiles(Path.Combine(root, ".sqlscriptdom", "index", "index-v1"), "workspace-*.json.gz"));
            await using (var stream = File.OpenRead(snapshot))
            await using (var compressed = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress))
            using (var reader = new StreamReader(compressed)) Assert.DoesNotContain("TSqlFragment", await reader.ReadToEndAsync());
            await File.WriteAllTextAsync(snapshot, "{corrupt");
            await using var third = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true, settingsFile: settings);
            var rebuilt = McpClient.Data(await third.Tool("find_procedures", new { query = "P" }));
            Assert.Equal(12, rebuilt.GetProperty("coverage").GetProperty("parseAttempts").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }
}
