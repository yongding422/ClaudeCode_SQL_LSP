using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SqlLsp.Server.IntegrationTests;

// Measurements are explicit opt-in; wall-clock targets are reported, never flaky CI assertions.
public class PerformanceTests
{
    [BenchmarkFact]
    public async Task Measure_real_stdio_diagnostics_definition_and_large_workspace_indexing()
    {
        var measurements = new List<object>();
        using (var fixture = new NavigationWorkspace())
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(LspClient.RepositoryRoot, "samples", "sql-workspace"), "*.sql"))
                File.Copy(file, Path.Combine(fixture.Root, Path.GetFileName(file)));
            var clock = Stopwatch.StartNew();
            await using var client = new LspClient { RequestTimeout = TimeSpan.FromSeconds(60) };
            await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
            measurements.Add(new { operation = "27-file startup including process launch", elapsedMs = clock.Elapsed.TotalMilliseconds, targetMs = 2000 });
            await client.NotifyAsync("initialized", new { });
            const string statement = "SELECT PayrollId FROM dbo.Payroll WHERE PayrollId = @id;\n";
            var prefix = "CREATE PROCEDURE dbo.Benchmark @id int AS BEGIN\n" + string.Concat(Enumerable.Repeat(statement, 3500)) + "END;\n";
            var sql = prefix + "/*" + new string(' ', 200_000 - Encoding.UTF8.GetByteCount(prefix) - 4) + "*/";
            Assert.Equal(200_000, Encoding.UTF8.GetByteCount(sql));
            var uri = new Uri(Path.Combine(fixture.Root, "benchmark.sql")).AbsoluteUri;
            await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sql", version = 1, text = sql } });
            Assert.Empty((await client.DiagnosticsAsync(uri, 1)).GetProperty("diagnostics").EnumerateArray());
            var timings = new List<double>();
            for (var version = 2; version <= 31; version++)
            {
                clock.Restart();
                await client.NotifyAsync("textDocument/didChange", new { textDocument = new { uri, version }, contentChanges = new[] { new { text = sql } } });
                Assert.Empty((await client.DiagnosticsAsync(uri, version)).GetProperty("diagnostics").EnumerateArray());
                timings.Add(clock.Elapsed.TotalMilliseconds);
            }
            measurements.Add(new { operation = "200KB full-buffer edit to diagnostics", samples = timings.Count, p50Ms = Percentile(timings, .5), p95Ms = Percentile(timings, .95), targetP95Ms = 300 });
            await client.StopAsync();
        }
        foreach (var count in new[] { 1000, 10_000 })
        {
            using var fixture = new NavigationWorkspace();
            for (var i = 0; i < count; i++) fixture.Write($"table{i}.sql", $"CREATE TABLE dbo.T{i} (id int, name nvarchar(100));");
            var use = fixture.Write("use.sql", $"SELECT * FROM dbo.T{count - 1};");
            var clock = Stopwatch.StartNew();
            await using var client = new LspClient { RequestTimeout = TimeSpan.FromSeconds(60) };
            await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
            var indexedMs = clock.Elapsed.TotalMilliseconds;
            await client.NotifyAsync("initialized", new { });
            async Task DefinitionAsync()
            {
                var response = await client.RequestAsync("textDocument/definition", new { textDocument = new { uri = new Uri(use).AbsoluteUri }, position = new { line = 0, character = 20 } });
                var result = response.GetProperty("result");
                var location = result.ValueKind == JsonValueKind.Object ? result : Assert.Single(result.EnumerateArray());
                Assert.Equal(new Uri(Path.Combine(fixture.Root, $"table{count - 1}.sql")), new Uri(location.GetProperty("uri").GetString()!));
            }
            await DefinitionAsync(); // First binding/JIT is measured by startup, not the warm request target.
            var timings = new List<double>();
            for (var i = 0; i < 50; i++)
            {
                clock.Restart();
                await DefinitionAsync();
                timings.Add(clock.Elapsed.TotalMilliseconds);
            }
            measurements.Add(new
            {
                operation = "workspace index and warm definition",
                files = count + 1,
                indexMs = indexedMs,
                filesPerSecond = (count + 1) / (indexedMs / 1000),
                workingSetBytes = client.WorkingSetBytes,
                samples = timings.Count,
                p50Ms = Percentile(timings, .5),
                p95Ms = Percentile(timings, .95),
                targetP95Ms = count == 1000 ? (int?)100 : null
            });
            await client.StopAsync();
        }
        var report = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            runtime = Environment.Version.ToString(),
            measurements
        };
        var output = Path.Combine(LspClient.RepositoryRoot, "artifacts", "performance.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double Percentile(List<double> values, double percentile) => values.Order().ElementAt((int)Math.Ceiling(values.Count * percentile) - 1);
}

public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SQLLSP_RUN_BENCHMARKS") != "1") Skip = "Explicit benchmark opt-in: SQLLSP_RUN_BENCHMARKS=1.";
    }
}
