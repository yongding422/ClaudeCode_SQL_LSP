using System.Diagnostics;
using System.Text.Json;

namespace SqlLsp.Mcp.IntegrationTests;

public class MetricsTests
{
    private const string Sql = "CREATE PROC dbo.PRIVATE_SQL_CANARY AS BEGIN IF 1=1 EXEC dbo.Q; EXEC(N'PRIVATE_SQL_CANARY'); END;\nGO\nCREATE PROC dbo.Q AS SELECT * FROM dbo.A;\nGO\nCREATE TABLE dbo.A(id int);";
    private static async Task<JsonElement> Find(McpClient client) => McpClient.Data(await client.Tool("find_procedures", new { query = "PRIVATE_SQL_CANARY" })).GetProperty("modules")[0];
    private static async Task<JsonElement> Analyze(McpClient client, JsonElement selected, string detail = "compact") => McpClient.Data(await client.Tool("analyze_procedure", new
    {
        documentUri = selected.GetProperty("target").GetProperty("uri").GetString(),
        line = selected.GetProperty("selection").GetProperty("line").GetInt32(),
        character = selected.GetProperty("selection").GetProperty("character").GetInt32(),
        detail
    }));
    private static Task<JsonElement> Metrics(McpClient client, bool previous = false) => Data(client.Tool("get_metrics", new { includePreviousSessions = previous }));
    private static async Task<JsonElement> Data(Task<JsonElement> message) => McpClient.Data(await message);

    [Fact]
    public async Task Metrics_are_disabled_by_default_and_inspection_does_not_initialize_sql()
    {
        await using var client = await McpClient.Start(Sql, "{\"sql\":{\"mode\":\"invalid\"}}");
        var metrics = await Metrics(client, true);
        Assert.False(metrics.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("report").ValueKind);
        var root = Path.Combine(TestPaths.TempRoot, "uncreated-metrics-" + Guid.NewGuid().ToString("N"));
        var summary = await Command(client.Root, "--metrics-summary", "--metrics-directory", root);
        Assert.Equal(0, summary.Code);
        Assert.Equal("Empty", summary.Data.GetProperty("storageStatus").GetString());
        Assert.Equal(0, summary.Data.GetProperty("requests").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.Data.GetProperty("modelTokens").ValueKind);
        Assert.False(Directory.Exists(root));
        await using var enabled = await McpClient.Start(Sql, "{\"sql\":{\"mode\":\"invalid\"}}", metricsDirectory: root);
        Assert.Equal(0, (await Metrics(enabled)).GetProperty("report").GetProperty("requests").GetInt32());
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Real_requests_record_private_numeric_metrics_cache_hits_errors_and_cold_warm_groups_across_restarts()
    {
        var root = Path.Combine(TestPaths.TempRoot, "metric PRIVATE_SQL_CANARY 中文 " + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "metrics");
        try
        {
            await using (var first = await McpClient.Start(Sql, cacheDirectory: Path.Combine(root, "cache"), rootDirectory: root, preserveWorkspace: true, metricsDirectory: directory, metricsViaEnvironment: true))
            {
                Assert.Equal(0, (await Metrics(first)).GetProperty("report").GetProperty("requests").GetInt32());
                var selected = await Find(first);
                var analysis = await Analyze(first, selected);
                Assert.Equal("Stored", analysis.GetProperty("cache").GetProperty("status").GetString());
                await Analyze(first, selected, "full");
                var invalid = await first.Tool("find_procedures", new { query = "PRIVATE_SQL_CANARY", maxResults = 1000 });
                Assert.True(invalid.GetProperty("result").GetProperty("isError").GetBoolean());
                var metrics = await Metrics(first);
                Assert.Equal(4, metrics.GetProperty("observedInProcess").GetInt32());
                Assert.Equal(0, metrics.GetProperty("persistenceWriteFailures").GetInt32());
                var report = metrics.GetProperty("report");
                Assert.Equal(4, report.GetProperty("requests").GetInt32());
                var warm = report.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("key").GetString() == "analyze_procedure:warm");
                Assert.Equal(2, warm.GetProperty("successfulRequests").GetInt32());
                Assert.Equal(1, warm.GetProperty("cacheHits").GetInt32());
                Assert.Equal(0.5, warm.GetProperty("cacheHitRate").GetDouble());
                Assert.True(warm.GetProperty("reportedCalls").GetInt32() >= 4);
                Assert.True(warm.GetProperty("issueOccurrences").GetInt32() > 0);
                Assert.True(warm.GetProperty("meanReportUtf8Bytes").GetDouble() > 0);
                Assert.True(warm.GetProperty("p95Ms").GetDouble() >= warm.GetProperty("p50Ms").GetDouble());
                Assert.Equal(1, report.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("key").GetString() == "find_procedures:unknown").GetProperty("errors").GetInt32());
                Assert.Equal(4, (await Metrics(first, true)).GetProperty("report").GetProperty("requests").GetInt32());
            }
            await using (var second = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true, metricsDirectory: directory))
            {
                var previous = await Metrics(second, true);
                Assert.Equal(4, previous.GetProperty("report").GetProperty("requests").GetInt32());
                Assert.Equal(0, previous.GetProperty("observedInProcess").GetInt32());
                Assert.Equal(0, (await Metrics(second)).GetProperty("report").GetProperty("requests").GetInt32());
                await Find(second);
                Assert.Equal(2, (await Metrics(second, true)).GetProperty("report").GetProperty("sessions").GetInt32());
            }
            var files = Directory.GetFiles(Path.Combine(directory, "metrics-v1"), "*.jsonl");
            Assert.Equal(2, files.Length);
            foreach (var file in files)
            {
                var contents = await File.ReadAllTextAsync(file);
                Assert.DoesNotContain("PRIVATE_SQL_CANARY", contents);
                Assert.DoesNotContain("documentUri", contents);
                Assert.DoesNotContain("modules.sql", contents);
                Assert.DoesNotContain("SELECT", contents);
                Assert.DoesNotContain("input_tokens", contents);
                if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            }
            var summary = await Command(root, "--metrics-summary", "--metrics-directory", directory);
            Assert.Equal(0, summary.Code);
            Assert.Equal(5, summary.Data.GetProperty("requests").GetInt32());
            Assert.Equal("Ready", summary.Data.GetProperty("storageStatus").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Unavailable_metric_storage_is_visible_and_does_not_break_analysis()
    {
        var root = Path.Combine(TestPaths.TempRoot, "blocked-metrics " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var blocked = Path.Combine(root, "not-directory"); await File.WriteAllTextAsync(blocked, "PRIVATE_SQL_CANARY");
        try
        {
            await using var client = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true, metricsDirectory: blocked);
            await Analyze(client, await Find(client));
            var metrics = await Metrics(client);
            Assert.Equal(2, metrics.GetProperty("observedInProcess").GetInt32());
            Assert.Equal(2, metrics.GetProperty("persistenceWriteFailures").GetInt32());
            Assert.Equal(2, metrics.GetProperty("report").GetProperty("requests").GetInt32());
            Assert.Equal("Unavailable", (await Metrics(client, true)).GetProperty("report").GetProperty("storageStatus").GetString());
            var versionCollision = Path.Combine(root, "version-collision"); Directory.CreateDirectory(versionCollision);
            await File.WriteAllTextAsync(Path.Combine(versionCollision, "metrics-v1"), "PRIVATE_SQL_CANARY");
            var unavailable = await Command(root, "--metrics-summary", "--metrics-directory", versionCollision);
            Assert.Equal(2, unavailable.Code);
            Assert.Equal("Unavailable", unavailable.Data.GetProperty("storageStatus").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Summary_comparison_preserves_negative_ratios_missing_baselines_corruption_and_retention_limits()
    {
        var root = Path.Combine(TestPaths.TempRoot, "compare-metrics " + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "current"); var baseline = Path.Combine(root, "baseline");
        Directory.CreateDirectory(root);
        try
        {
            await using (var client = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true, metricsDirectory: current)) await Find(client);
            var file = Assert.Single(Directory.GetFiles(Path.Combine(current, "metrics-v1"), "*.jsonl"));
            var original = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>((await File.ReadAllTextAsync(file)).Trim())!;
            object Event(int duration) => original.ToDictionary(p => p.Key, p => p.Key == "durationMs" ? JsonSerializer.SerializeToElement(duration) : p.Value);
            Directory.CreateDirectory(Path.Combine(baseline, "metrics-v1"));
            await File.WriteAllTextAsync(Path.Combine(baseline, "metrics-v1", Path.GetFileName(file)), JsonSerializer.Serialize(Event(10)) + "\n");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(Event(20)) + "\n");
            var compared = await Command(root, "--metrics-summary", "--metrics-directory", current, "--compare-metrics", baseline);
            Assert.Equal(0, compared.Code);
            var group = Assert.Single(compared.Data.GetProperty("groups").EnumerateArray());
            Assert.Equal(-1, group.GetProperty("medianTimeReduction").GetDouble());
            Assert.Equal(JsonValueKind.Null, group.GetProperty("meanReportByteReduction").ValueKind);
            await File.WriteAllTextAsync(Path.Combine(baseline, "metrics-v1", Path.GetFileName(file)), JsonSerializer.Serialize(Event(0)) + "\n");
            var zero = await Command(root, "--metrics-summary", "--metrics-directory", current, "--compare-metrics", baseline);
            Assert.Equal(JsonValueKind.Null, zero.Data.GetProperty("groups")[0].GetProperty("medianTimeReduction").ValueKind);
            await File.AppendAllTextAsync(file, "PRIVATE_SQL_CANARY\n{\"unfinished\":");
            var partial = await Command(root, "--metrics-summary", "--metrics-directory", current);
            Assert.Equal(2, partial.Code);
            Assert.Equal(1, partial.Data.GetProperty("requests").GetInt32());
            Assert.Equal(2, partial.Data.GetProperty("ignoredRecords").GetInt32());
            Assert.DoesNotContain("PRIVATE_SQL_CANARY", partial.Data.GetRawText());
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(Event(20)) + "\n");
            for (var i = 0; i < 33; i++) await File.WriteAllTextAsync(Path.Combine(current, "metrics-v1", $"session-{Guid.NewGuid():N}-{Guid.NewGuid():N}.jsonl"), "invalid\n");
            await File.WriteAllTextAsync(Path.Combine(current, "metrics-v1", "keep.txt"), "do not delete");
            await using (var client = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true, metricsDirectory: current)) await Find(client);
            Assert.Equal(32, Directory.GetFiles(Path.Combine(current, "metrics-v1"), "*.jsonl").Length);
            Assert.True(File.Exists(Path.Combine(current, "metrics-v1", "keep.txt")));
            await using (var large = new FileStream(file, FileMode.Create, FileAccess.Write)) large.SetLength(512 * 1024 + 1);
            Assert.Equal(2, (await Command(root, "--metrics-summary", "--metrics-directory", current)).Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [UnixCacheFact]
    public async Task Linked_metric_directories_are_rejected_without_writing_through_links()
    {
        var root = Path.Combine(TestPaths.TempRoot, "link-metrics " + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside"); var link = Path.Combine(root, "linked"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            await using var client = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true, metricsDirectory: link);
            await Find(client);
            Assert.Equal(1, (await Metrics(client)).GetProperty("persistenceWriteFailures").GetInt32());
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            Assert.Equal(2, (await Command(root, "--metrics-summary", "--metrics-directory", link)).Code);
        }
        finally { Directory.Delete(link); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Writer_contention_is_reported_and_full_segments_rotate_without_breaking_requests()
    {
        var root = Path.Combine(TestPaths.TempRoot, "rotate-metrics " + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "metrics"); var storage = Path.Combine(directory, "metrics-v1"); Directory.CreateDirectory(storage);
        try
        {
            await using var client = await McpClient.Start(Sql, rootDirectory: root, preserveWorkspace: true, metricsDirectory: directory);
            using (var blocked = new FileStream(Path.Combine(storage, "writer.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)) await Find(client);
            Assert.Equal(1, (await Metrics(client)).GetProperty("persistenceWriteFailures").GetInt32());
            await Find(client);
            var file = Assert.Single(Directory.GetFiles(storage, "*.jsonl"));
            var line = await File.ReadAllTextAsync(file);
            var repeats = 512 * 1024 / System.Text.Encoding.UTF8.GetByteCount(line);
            await File.WriteAllTextAsync(file, string.Concat(Enumerable.Repeat(line, repeats)));
            await Find(client);
            Assert.Equal(2, Directory.GetFiles(storage, "*.jsonl").Length);
            Assert.All(Directory.GetFiles(storage, "*.jsonl"), f => Assert.True(new FileInfo(f).Length <= 512 * 1024));
            var metrics = await Metrics(client);
            Assert.Equal(3, metrics.GetProperty("observedInProcess").GetInt32());
            Assert.Equal(1, metrics.GetProperty("persistenceWriteFailures").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Client_csv_comparison_uses_actual_usage_separates_cache_tokens_and_retains_failed_and_unmatched_runs()
    {
        var root = Path.Combine(TestPaths.TempRoot, "client-metrics " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var csv = Path.Combine(root, "metrics.csv");
        try
        {
            await File.WriteAllTextAsync(csv, "task_id,repeat,arm,model,model_observed,effort,repo_revision,cli_version,start_condition,sql_files,outcome,exit_code,elapsed_ms,input_tokens,output_tokens,cache_read_tokens,cache_write_tokens,reported_cost_usd,behavior_score\n" +
                "\"PRIVATE_SQL_CANARY,task\",1,grep,test,test,low,abc,2,cold,1,success,0,100,100,30,50,0,0.01,\n" +
                "\"PRIVATE_SQL_CANARY,task\",1,plugin,test,test,low,abc,2,cold,1,success,0,150,200,40,80,0,0.02,\n" +
                "failure,2,grep,test,test,low,abc,2,cold,1,success,0,100,100,30,0,0,0.01,\n" +
                "failure,2,plugin,test,test,low,abc,2,cold,1,timeout,-1,1000,,,,,,\n" +
                "different,3,grep,test,test,low,abc,2,cold,1,success,0,100,100,30,0,0,0.01,\n" +
                "different,3,plugin,other,other,low,abc,2,cold,1,success,0,10,10,10,0,0,0.01,\n");
            var result = await Command(root, "--evaluation-summary", csv);
            Assert.Equal(0, result.Code);
            Assert.Equal(1, result.Data.GetProperty("failedOrIncompleteMatchedPairs").GetInt32());
            Assert.Equal(1, result.Data.GetProperty("settingMismatchPairs").GetInt32());
            var compared = Assert.Single(result.Data.GetProperty("comparisonsWithGrep").EnumerateArray());
            Assert.Equal(1, compared.GetProperty("successfulMatchedPairs").GetInt32());
            Assert.Equal(-1, compared.GetProperty("reductions").GetProperty("input_tokens").GetProperty("median").GetDouble());
            Assert.Equal(-0.5, compared.GetProperty("reductions").GetProperty("elapsed_ms").GetProperty("median").GetDouble());
            var plugin = result.Data.GetProperty("arms").EnumerateArray().Single(a => a.GetProperty("arm").GetString() == "plugin");
            Assert.Equal(1, plugin.GetProperty("failedOrIncompleteRuns").GetInt32());
            Assert.Equal(JsonValueKind.Null, plugin.GetProperty("measures").GetProperty("behavior_score").GetProperty("mean").ValueKind);
            Assert.DoesNotContain("PRIVATE_SQL_CANARY", result.Data.GetRawText());
            await File.AppendAllTextAsync(csv, "bad,row\n");
            Assert.Equal(2, (await Command(root, "--evaluation-summary", csv)).Code);
            await File.AppendAllTextAsync(csv, "\"PRIVATE_SQL_CANARY,task\",1,plugin,test,test,low,abc,2,cold,1,success,0,150,200,40,80,0,0.02,\n");
            var duplicate = await Command(root, "--evaluation-summary", csv);
            Assert.Equal(2, duplicate.Code);
            Assert.Equal(1, duplicate.Data.GetProperty("duplicatePairKeys").GetInt32());
            Assert.Empty(duplicate.Data.GetProperty("comparisonsWithGrep").EnumerateArray());
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(int Code, JsonElement Data)> Command(string root, params string[] args)
    {
        var info = McpClient.Command(root); foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        process.StandardInput.Close(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token);
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", await error);
        using var parsed = JsonDocument.Parse(await output);
        return (process.ExitCode, parsed.RootElement.Clone());
    }
}
