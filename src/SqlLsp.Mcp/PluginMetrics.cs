using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using SqlLsp.Core;

namespace SqlLsp.Mcp;

// Fixed fields only. No queries, focus text, identities, paths, SQL or exception messages.
internal sealed record MetricEvent(int Format, string Version, string Build, string Session, DateTimeOffset At,
    string Operation, string Detail, string Outcome, string SourceMode, bool? ColdStart, double DurationMs,
    int? IndexedDocuments, long? PayloadUtf8Bytes, long? ReportUtf8Bytes, string? ResolutionStatus,
    int Modules, int Calls, int ResolvedCalls, int Accesses, int ResolvedAccesses, int Issues, int TruncatedModules,
    int CacheEnabled, Dictionary<string, long> CacheStatuses, Dictionary<string, double> Timings)
{
    public WorkspaceCoverage? Coverage { get; init; }
    // Conservative estimated JSON-RPC envelope; actual transport is asserted in integration tests.
    public long? ResultFrameUtf8Bytes { get; init; }
}

internal sealed class PluginMetrics(string? directory)
{
    internal const string Version = "0.15.0";
    internal static readonly string[] PhaseNames = ["queueMs", "initializationMs", "navigationMs", "analysisMs", "presentationMs", "cacheValidationMs", "cacheReadMs", "cacheWriteMs"];
    internal static readonly string[] CacheNames = ["Disabled", "Hit", "Stored", "NotStored", "UnavailableOrInvalid", "TooLarge", "UnsupportedSourceContext"];
    private readonly object sync = new();
    private readonly Queue<MetricEvent> recent = new();
    private readonly LocalMetricStore? store = directory == null ? null : new(directory);
    private readonly string session = Guid.NewGuid().ToString("N");
    private long observed, writeFailures;
    internal string SourceMode { get; set; } = "NotInitialized";
    internal Scope Begin(string operation, string detail, CancellationToken token) => new(this, operation, detail, token);

    internal sealed class Scope(PluginMetrics owner, string operation, string detail, CancellationToken token) : IDisposable
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private JsonElement? data;
        private long? frameBytes;
        private SqlProcedureReport? analysis;
        public void AnalyzerReport(SqlProcedureReport report) { if (owner.store != null) analysis = report; }
        public CallToolResult Complete(CallToolResult result)
        {
            data = result.StructuredContent;
            if (owner.store != null) frameBytes = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = long.MaxValue, result }, ProcedureTools.JsonOptions).Length;
            return result;
        }
        public void Dispose()
        {
            if (owner.store == null) return;
            try { Record(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or NotSupportedException)
            {
                lock (owner.sync) owner.writeFailures++;
            }
        }
        private void Record()
        {
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var payload = data;
            var timings = new Dictionary<string, double>(StringComparer.Ordinal);
            bool? cold = null;
            if (payload is { } p && p.TryGetProperty("timings", out var phase))
            {
                if (phase.TryGetProperty("coldStart", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False) cold = c.GetBoolean();
                foreach (var name in PhaseNames)
                    if (phase.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0) timings[name] = number;
            }
            var statuses = new Dictionary<string, long>(StringComparer.Ordinal);
            var cacheEnabled = 0;
            void Cache(JsonElement cache)
            {
                if (cache.ValueKind != JsonValueKind.Object) return;
                if (cache.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True) cacheEnabled++;
                if (cache.TryGetProperty("status", out var status) && CacheNames.Contains(status.GetString(), StringComparer.Ordinal))
                {
                    var key = status.GetString()!;
                    statuses[key] = statuses.GetValueOrDefault(key) + 1;
                }
            }
            var modules = 0; var calls = 0; var resolvedCalls = 0; var accesses = 0; var resolvedAccesses = 0; var issues = 0; var truncated = 0;
            long? reportBytes = null;
            string? resolution = null;
            if (payload is { } content)
            {
                if (content.TryGetProperty("cache", out var cache)) Cache(cache);
                if (content.TryGetProperty("entries", out var entries)) foreach (var entry in entries.EnumerateArray()) Cache(entry.GetProperty("cache"));
                if (content.TryGetProperty("sourceIssues", out var sourceIssues)) issues += sourceIssues.GetArrayLength();
                if (content.TryGetProperty("report", out var report))
                {
                    reportBytes = Encoding.UTF8.GetByteCount(report.GetRawText());
                    if (report.TryGetProperty("status", out var status) && Enum.TryParse<ResolutionStatus>(status.GetString(), out var parsed)) resolution = parsed.ToString();
                    if (report.TryGetProperty("expansionIssues", out var expansionIssues)) issues += expansionIssues.GetArrayLength();
                    foreach (var module in report.TryGetProperty("modules", out var reportModules) ? reportModules.EnumerateArray() : Enumerable.Empty<JsonElement>())
                    {
                        modules++;
                        if (module.GetProperty("truncated").GetBoolean()) truncated++;
                        var moduleCalls = module.GetProperty("calls").EnumerateArray().ToArray();
                        calls += moduleCalls.Length;
                        resolvedCalls += moduleCalls.Count(c => c.GetProperty("status").GetString() == "Resolved");
                        var moduleAccesses = module.GetProperty("accesses").EnumerateArray().ToArray();
                        accesses += moduleAccesses.Length;
                        resolvedAccesses += moduleAccesses.Count(a => a.GetProperty("status").GetString() == "Resolved");
                        issues += module.GetProperty("issues").GetArrayLength();
                        foreach (var section in new[] { "controlFlow", "valueFlow" })
                            if (module.TryGetProperty(section, out var summary) && summary.ValueKind == JsonValueKind.Object) issues += summary.GetProperty("issues").GetArrayLength();
                    }
                }
            }
            if (analysis != null)
            {
                resolution = analysis.Status.ToString();
                modules = analysis.Modules.Length;
                calls = analysis.Modules.Sum(m => m.Calls.Length);
                resolvedCalls = analysis.Modules.Sum(m => m.Calls.Count(c => c.Status == ResolutionStatus.Resolved));
                accesses = analysis.Modules.Sum(m => m.Accesses.Length);
                resolvedAccesses = analysis.Modules.Sum(m => m.Accesses.Count(a => a.Status == ResolutionStatus.Resolved));
                issues = analysis.ExpansionIssues.Length + analysis.Modules.Sum(m => m.Issues.Length + (m.ControlFlow?.Issues.Length ?? 0) + (m.ValueFlow?.Issues.Length ?? 0));
                truncated = analysis.Modules.Count(m => m.Truncated);
            }
            var metric = new MetricEvent(1, Version, typeof(ProcedureTools).Module.ModuleVersionId.ToString("N"), owner.session, DateTimeOffset.UtcNow,
                operation, detail is "full" or "compact" or "discovery" or "prebuild" ? detail : "Invalid",
                payload != null ? "Success" : token.IsCancellationRequested ? "Canceled" : "Error", owner.SourceMode, cold, elapsed,
                payload is { } item && item.TryGetProperty("indexedDocuments", out var docs) ? docs.GetInt32() : null,
                payload is { } json ? Encoding.UTF8.GetByteCount(json.GetRawText()) : null,
                reportBytes, resolution, modules, calls, resolvedCalls, accesses, resolvedAccesses, issues, truncated, cacheEnabled, statuses, timings)
            {
                Coverage = payload is { } cp && cp.TryGetProperty("coverage", out var cv) && cv.ValueKind == JsonValueKind.Object
                    ? cv.Deserialize<WorkspaceCoverage>(ProcedureTools.JsonOptions) : null,
                ResultFrameUtf8Bytes = frameBytes
            };
            lock (owner.sync)
            {
                owner.observed++;
                owner.recent.Enqueue(metric);
                while (owner.recent.Count > 1024) owner.recent.Dequeue();
                if (!owner.store!.Append(metric)) owner.writeFailures++;
            }
        }
    }

    internal object Get(bool includePreviousSessions)
    {
        lock (sync)
        {
            return new
            {
                enabled = store != null,
                scope = includePreviousSessions ? "Retained files in the configured metrics directory" : "Most recent 1024 requests in this MCP process",
                observedInProcess = store == null ? (long?)null : observed,
                persistenceWriteFailures = store == null ? (long?)null : writeFailures,
                directory,
                report = store == null ? null : includePreviousSessions ? MetricReports.Read(directory!) : MetricReports.Summarize(recent.ToArray(), 0, observed > recent.Count, "Memory"),
                limitations = "Backend measurements only; no model tokens, Grep activity or final-answer correctness observed. Bytes are not tokens. Counts cover reported facts, not complete SQL behavior. Metrics reads do not initialize SQL or count themselves."
            };
        }
    }
}

internal static class MetricReports
{
    internal static object Read(string directory)
    {
        var read = new LocalMetricStore(directory).Read();
        return Summarize(read.Events, read.IgnoredRecords, read.WindowLimited, read.Status);
    }

    internal static object Summarize(IReadOnlyList<MetricEvent> events, int ignored, bool limited, string status) => new
    {
        format = "sqlscriptdom-metrics-summary-v1",
        storageStatus = status,
        ignoredRecords = ignored,
        windowLimited = limited,
        requests = events.Count,
        sessions = events.Select(e => e.Session).Distinct().Count(),
        versions = events.Select(e => e.Version).Distinct().Order().ToArray(),
        builds = events.Select(e => e.Build).Distinct().Order().ToArray(),
        sourceModes = events.GroupBy(e => e.SourceMode).ToDictionary(g => g.Key, g => g.Count()),
        firstAt = events.Count == 0 ? (DateTimeOffset?)null : events.Min(e => e.At),
        lastAt = events.Count == 0 ? (DateTimeOffset?)null : events.Max(e => e.At),
        groups = events.GroupBy(Key).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Group(g.Key, g.ToArray())).ToArray(),
        modelTokens = (long?)null,
        finalAnswerCorrectness = (double?)null,
        limitations = "Retained observations only, not lifetime totals. Per-request time excludes metric recording, transport and model inference. Percentiles use nearest-rank successful requests. Cold means this request initialized source state, not an OS/model cache claim. Reported facts/issues are not correctness or completeness scores."
    };

    private static string Key(MetricEvent e) => e.Operation + ":" + (e.ColdStart is true ? "cold" : e.ColdStart is false ? "warm" : "unknown");
    internal static double? Percentile(double[] samples, double fraction)
    {
        if (samples.Length == 0) return null;
        Array.Sort(samples);
        return samples[Math.Max(0, (int)Math.Ceiling(samples.Length * fraction) - 1)];
    }
    private static object Group(string key, MetricEvent[] events)
    {
        var successful = events.Where(e => e.Outcome == "Success").ToArray();
        var enabled = events.Sum(e => e.CacheEnabled);
        var hits = events.Sum(e => e.CacheStatuses.GetValueOrDefault("Hit"));
        return new
        {
            key,
            requests = events.Length,
            successfulRequests = successful.Length,
            errors = events.Count(e => e.Outcome == "Error"),
            canceled = events.Count(e => e.Outcome == "Canceled"),
            details = events.GroupBy(e => e.Detail).ToDictionary(g => g.Key, g => g.Count()),
            meanMs = successful.Length == 0 ? (double?)null : successful.Average(e => e.DurationMs),
            p50Ms = Percentile(successful.Select(e => e.DurationMs).ToArray(), 0.5),
            p95Ms = Percentile(successful.Select(e => e.DurationMs).ToArray(), 0.95),
            phases = PluginMetrics.PhaseNames.ToDictionary(name => name, name =>
            {
                var samples = events.Where(e => e.Timings.ContainsKey(name)).Select(e => e.Timings[name]).ToArray();
                return new { measuredRequests = samples.Length, totalMs = samples.Length == 0 ? (double?)null : samples.Sum() };
            }),
            reports = events.Count(e => e.ReportUtf8Bytes.HasValue),
            meanReportUtf8Bytes = events.Where(e => e.ReportUtf8Bytes.HasValue).Select(e => (double?)e.ReportUtf8Bytes).DefaultIfEmpty().Average(),
            totalPayloadUtf8Bytes = events.Where(e => e.PayloadUtf8Bytes.HasValue).Sum(e => e.PayloadUtf8Bytes!.Value),
            cacheEnabledReports = enabled,
            cacheHits = hits,
            cacheHitRate = enabled == 0 ? (double?)null : (double)hits / enabled,
            cacheStatuses = events.SelectMany(e => e.CacheStatuses).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value)),
            resolutions = events.Where(e => e.ResolutionStatus != null).GroupBy(e => e.ResolutionStatus!).ToDictionary(g => g.Key, g => g.Count()),
            latestIndexedDocuments = events.OrderBy(e => e.At).LastOrDefault(e => e.IndexedDocuments.HasValue)?.IndexedDocuments,
            reportedModules = events.Sum(e => (long)e.Modules),
            reportedCalls = events.Sum(e => (long)e.Calls),
            resolvedCalls = events.Sum(e => (long)e.ResolvedCalls),
            reportedAccesses = events.Sum(e => (long)e.Accesses),
            resolvedAccesses = events.Sum(e => (long)e.ResolvedAccesses),
            issueOccurrences = events.Sum(e => (long)e.Issues),
            truncatedModules = events.Sum(e => (long)e.TruncatedModules)
        };
    }

    internal static object Compare(string currentDirectory, string baselineDirectory)
    {
        var current = JsonSerializer.SerializeToElement(Read(currentDirectory), ProcedureTools.JsonOptions);
        var baseline = JsonSerializer.SerializeToElement(Read(baselineDirectory), ProcedureTools.JsonOptions);
        var baselineGroups = baseline.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("key").GetString()!);
        double? Reduction(JsonElement c, JsonElement b, string property) =>
            c.GetProperty(property).ValueKind == JsonValueKind.Number && b.GetProperty(property).ValueKind == JsonValueKind.Number && b.GetProperty(property).GetDouble() > 0
                ? 1 - c.GetProperty(property).GetDouble() / b.GetProperty(property).GetDouble() : null;
        return new
        {
            format = "sqlscriptdom-metrics-comparison-v1",
            current,
            baseline,
            groups = current.GetProperty("groups").EnumerateArray().Where(c => baselineGroups.ContainsKey(c.GetProperty("key").GetString()!)).Select(c =>
            {
                var key = c.GetProperty("key").GetString()!;
                var b = baselineGroups[key];
                return new
                {
                    key,
                    meanTimeReduction = Reduction(c, b, "meanMs"),
                    medianTimeReduction = Reduction(c, b, "p50Ms"),
                    meanReportByteReduction = Reduction(c, b, "meanReportUtf8Bytes")
                };
            }).ToArray(),
            limitations = "Descriptive ratios, not a controlled benchmark. Match questions, source revisions, model/client settings and reviewed answer quality externally. Details, builds and source modes may differ. Negative reductions are regressions; missing/zero baselines stay null. No model-token reduction is inferred."
        };
    }
}
