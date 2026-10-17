using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlLsp.Mcp.IntegrationTests;

public class PersistentCacheTests
{
    private const string Sql = "CREATE PROC dbo.P @n int AS BEGIN DECLARE @v int=@n; IF @n>0 SET @v=2; EXEC dbo.Q @q=@v; END;\nGO\nCREATE PROC dbo.Q @q int AS SELECT * FROM dbo.A WHERE id=@q;\nGO\nCREATE TABLE dbo.A(id int);";
    private static async Task<JsonElement> Select(McpClient client) => McpClient.Data(await client.Tool("find_procedures", new { query = "dbo.P" })).GetProperty("modules")[0];
    private static async Task<JsonElement> Analyze(McpClient client, JsonElement selected, string? revision = null, string detail = "full") =>
        McpClient.Data(await client.AnalyzeExpanded(new
        {
            documentUri = selected.GetProperty("target").GetProperty("uri").GetString(),
            line = selected.GetProperty("selection").GetProperty("line").GetInt32(),
            character = selected.GetProperty("selection").GetProperty("character").GetInt32(),
            expectedRevision = revision ?? selected.GetProperty("revision").GetString(),
            detail
        }));

    [Fact]
    public async Task Disk_report_survives_a_real_process_restart_and_compact_view_reuses_the_same_canonical_result()
    {
        var root = Path.Combine(TestPaths.TempRoot, "persistent 中文 " + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "saved-cache");
        try
        {
            string expected;
            await using (var first = await McpClient.Start(Sql, cacheDirectory: cache, rootDirectory: root, preserveWorkspace: true))
            {
                var result = await Analyze(first, await Select(first));
                Assert.Equal("Stored", result.GetProperty("cache").GetProperty("status").GetString());
                Assert.True(File.Exists(result.GetProperty("cache").GetProperty("file").GetString()));
                expected = result.GetProperty("report").GetRawText();
            }
            await using (var second = await McpClient.Start(Sql, cacheDirectory: cache, rootDirectory: root, preserveWorkspace: true))
            {
                var selected = await Select(second);
                var result = await Analyze(second, selected);
                Assert.Equal("Hit", result.GetProperty("cache").GetProperty("status").GetString());
                Assert.Equal(0, result.GetProperty("timings").GetProperty("analysisMs").GetDouble());
                Assert.Equal(expected, result.GetProperty("report").GetRawText());
                var compact = await Analyze(second, selected, detail: "compact");
                Assert.Equal("Hit", compact.GetProperty("cache").GetProperty("status").GetString());
                Assert.Equal("compact-v1", compact.GetProperty("report").GetProperty("format").GetString());
                var stale = await Analyze(second, selected, revision: "stale");
                Assert.Equal("NotStored", stale.GetProperty("cache").GetProperty("status").GetString());
                Assert.Equal("Unresolved", stale.GetProperty("report").GetProperty("status").GetString());
            }
            Assert.Single(Directory.GetFiles(Path.Combine(cache, "analysis-v1"), "report-*.json"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Dependency_changes_new_ambiguity_and_resolution_configuration_invalidate_saved_reports()
    {
        var root = Path.Combine(TestPaths.TempRoot, "invalidate " + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "saved-cache");
        try
        {
            foreach (var (sql, config) in new[] { (Sql, (string?)null), (Sql.Replace("SELECT * FROM dbo.A", "DELETE dbo.A", StringComparison.Ordinal), (string?)null),
                (Sql + "\nGO\nCREATE PROC dbo.Q @q int AS SELECT 1;", (string?)null), (Sql, "{\"resolution\":{\"caseSensitive\":false}}") })
            {
                await using var client = await McpClient.Start(sql, config, cache, root, true);
                var result = await Analyze(client, await Select(client));
                Assert.Equal("Stored", result.GetProperty("cache").GetProperty("status").GetString());
                if (sql.Contains("DELETE", StringComparison.Ordinal))
                    Assert.Contains(result.GetProperty("report").GetProperty("modules")[1].GetProperty("accesses").EnumerateArray(), a => a.GetProperty("operation").GetString() == "Delete");
                if (sql.Contains("AS SELECT 1", StringComparison.Ordinal))
                    Assert.Equal("Ambiguous", result.GetProperty("report").GetProperty("modules")[0].GetProperty("calls")[0].GetProperty("status").GetString());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Corrupt_cache_is_rebuilt_and_unavailable_storage_does_not_break_analysis()
    {
        await using var client = await McpClient.Start(Sql, cacheDirectory: Path.Combine(TestPaths.TempRoot, "corrupt-cache-" + Guid.NewGuid().ToString("N")));
        var selected = await Select(client);
        var first = await Analyze(client, selected);
        var path = first.GetProperty("cache").GetProperty("file").GetString()!;
        var cacheRoot = Directory.GetParent(path)!.Parent!.FullName;
        try
        {
            await File.WriteAllTextAsync(path, "not-json-PRIVATE_SQL_CANARY");
            var rebuilt = await Analyze(client, selected);
            Assert.Equal("Stored", rebuilt.GetProperty("cache").GetProperty("status").GetString());
            Assert.Equal(first.GetProperty("report").GetRawText(), rebuilt.GetProperty("report").GetRawText());
            var envelope = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(1, envelope.RootElement.GetProperty("format").GetInt32());
            using var blocked = new FileStream(Path.Combine(Path.GetDirectoryName(path)!, "cache.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            await File.WriteAllTextAsync(path, "bad");
            var unavailable = await Analyze(client, selected);
            Assert.Equal("UnavailableOrInvalid", unavailable.GetProperty("cache").GetProperty("status").GetString());
            Assert.Equal("Resolved", unavailable.GetProperty("report").GetProperty("status").GetString());
        }
        finally { Directory.Delete(cacheRoot, true); }
    }

    [Fact]
    public async Task Persistence_is_disabled_by_default_and_opaque_exec_payload_is_not_written_to_cache()
    {
        await using var disabled = await McpClient.Start(Sql);
        Assert.Equal("Disabled", (await Analyze(disabled, await Select(disabled))).GetProperty("cache").GetProperty("status").GetString());
        var root = Path.Combine(TestPaths.TempRoot, "opaque-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var enabled = await McpClient.Start("CREATE PROC dbo.P AS EXEC sys.sp_executesql N'PRIVATE_SQL_CANARY';", cacheDirectory: root);
            var result = await Analyze(enabled, await Select(enabled));
            Assert.Equal("Stored", result.GetProperty("cache").GetProperty("status").GetString());
            var contents = await File.ReadAllTextAsync(result.GetProperty("cache").GetProperty("file").GetString()!);
            Assert.DoesNotContain("PRIVATE_SQL_CANARY", contents);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(result.GetProperty("cache").GetProperty("file").GetString()!));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(root, "analysis-v1")));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Bulk_cache_command_persists_folder_reports_reuses_them_and_reports_unavailable_storage()
    {
        var root = Path.Combine(TestPaths.TempRoot, "bulk-cache 中文 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "modules.sql"), Sql);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var (code, result) = await Build(root, Path.Combine(root, "saved"));
                Assert.Equal(0, code);
                Assert.Equal(2, result.GetProperty("entries").GetArrayLength());
                Assert.All(result.GetProperty("entries").EnumerateArray(), e =>
                {
                    Assert.True(e.GetProperty("retained").GetBoolean());
                    Assert.Equal(attempt == 0 ? "Stored" : "Hit", e.GetProperty("cache").GetProperty("status").GetString());
                });
                if (attempt == 1) Assert.Equal(0, result.GetProperty("timings").GetProperty("analysisMs").GetDouble());
            }
            var blocked = Path.Combine(root, "file-not-directory");
            await File.WriteAllTextAsync(blocked, "not a directory");
            var partial = await Build(root, blocked);
            Assert.Equal(2, partial.Code);
            Assert.All(partial.Result.GetProperty("entries").EnumerateArray(), e => Assert.False(e.GetProperty("retained").GetBoolean()));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Entry_count_is_bounded_eviction_leaves_unowned_files_and_invalid_payloads_are_not_trusted()
    {
        var root = Path.Combine(TestPaths.TempRoot, "bounded-cache " + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "saved", "analysis-v1");
        Directory.CreateDirectory(cache);
        for (var i = 0; i < 257; i++)
        {
            var file = Path.Combine(cache, "report-" + i.ToString("x64") + ".json");
            await File.WriteAllTextAsync(file, "{}");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-1).AddSeconds(i));
        }
        await File.WriteAllTextAsync(Path.Combine(cache, "keep.txt"), "owned by the caller");
        try
        {
            await using var client = await McpClient.Start(Sql, cacheDirectory: Path.GetDirectoryName(cache), rootDirectory: root, preserveWorkspace: true);
            var selected = await Select(client);
            var first = await Analyze(client, selected);
            Assert.Equal("Stored", first.GetProperty("cache").GetProperty("status").GetString());
            Assert.Equal(256, Directory.GetFiles(cache, "report-*.json").Length);
            Assert.True(File.Exists(Path.Combine(cache, "keep.txt")));
            var path = first.GetProperty("cache").GetProperty("file").GetString()!;
            var injected = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            injected["report"]!["modules"]![0]!["identity"]!["name"] = "INJECTED";
            await File.WriteAllTextAsync(path, injected.ToJsonString());
            var rebuilt = await Analyze(client, selected);
            Assert.Equal("Stored", rebuilt.GetProperty("cache").GetProperty("status").GetString());
            Assert.Equal("P", rebuilt.GetProperty("report").GetProperty("modules")[0].GetProperty("identity").GetProperty("name").GetString());
            await using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write)) oversized.SetLength(8 * 1024 * 1024 + 1);
            Assert.Equal("Stored", (await Analyze(client, selected)).GetProperty("cache").GetProperty("status").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [UnixCacheFact]
    public async Task Linked_cache_paths_are_bypassed_without_writing_through_the_link()
    {
        var root = Path.Combine(TestPaths.TempRoot, "linked-cache " + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside"); var link = Path.Combine(root, "link");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            await using var client = await McpClient.Start(Sql, cacheDirectory: link, rootDirectory: root, preserveWorkspace: true);
            var result = await Analyze(client, await Select(client));
            Assert.Equal("UnavailableOrInvalid", result.GetProperty("cache").GetProperty("status").GetString());
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(link); Directory.Delete(root, true); }
    }

    private static async Task<(int Code, JsonElement Result)> Build(string root, string cache)
    {
        var info = McpClient.Command(root);
        info.ArgumentList.Add("--build-cache"); info.ArgumentList.Add("--cache-directory"); info.ArgumentList.Add(cache);
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", await errors);
        using var json = JsonDocument.Parse(await output);
        return (process.ExitCode, json.RootElement.Clone());
    }
}

public sealed class UnixCacheFactAttribute : FactAttribute
{
    public UnixCacheFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Link creation is unavailable on the Windows test host; exercised on Unix CI."; }
}
