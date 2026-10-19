using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using SqlLsp.Core;
using SqlLsp.Server;

namespace SqlLsp.Mcp;

public sealed record McpLaunchOptions(string Workspace, string? ConfigurationFile, string? CacheDirectory = null, bool BuildCache = false,
    string? MetricsDirectory = null, bool MetricsSummary = false, string? CompareMetricsDirectory = null, string? EvaluationCsv = null)
{
    public bool AutoInitializeSettings { get; init; }
    public int DiscoveryMaxBytes { get; init; } = 16384;
    public string? IndexCacheDirectory { get; init; }
    public int AnalysisMaxBytes { get; init; } = 32768;

    public static McpLaunchOptions Parse(string[] args)
    {
        var workspace = Directory.GetCurrentDirectory();
        var config = Environment.GetEnvironmentVariable("SQLLSP_MCP_CONFIG");
        var cache = Environment.GetEnvironmentVariable("SQLLSP_ANALYSIS_CACHE_DIR");
        var metrics = Environment.GetEnvironmentVariable("SQLLSP_METRICS_DIR");
        string? settingsFile = null;
        string? compare = null;
        string? evaluation = null;
        var summary = false;
        var build = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--stdio") continue;
            if (args[i] == "--build-cache") { build = true; continue; }
            if (args[i] == "--metrics-summary") { summary = true; continue; }
            if (args[i] == "--workspace" && i + 1 < args.Length) workspace = args[++i];
            else if (args[i] == "--config" && i + 1 < args.Length) config = args[++i];
            else if (args[i] == "--settings" && i + 1 < args.Length) settingsFile = args[++i];
            else if (args[i] == "--cache-directory" && i + 1 < args.Length) cache = args[++i];
            else if (args[i] == "--metrics-directory" && i + 1 < args.Length) metrics = args[++i];
            else if (args[i] == "--compare-metrics" && i + 1 < args.Length) compare = args[++i];
            else if (args[i] == "--evaluation-summary" && i + 1 < args.Length) evaluation = args[++i];
            else throw new ArgumentException("Unknown/missing argument.");
        }
        workspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(workspace)) throw new ArgumentException("Workspace must exist.");
        cache = string.IsNullOrWhiteSpace(cache) ? null : cache;
        if (cache != null && !Path.IsPathFullyQualified(cache)) throw new ArgumentException("Cache directory must be absolute.");
        metrics = string.IsNullOrWhiteSpace(metrics) ? null : metrics;
        if (metrics != null && !Path.IsPathFullyQualified(metrics)) throw new ArgumentException("Metric directory must be absolute.");
        if (settingsFile != null && string.IsNullOrWhiteSpace(settingsFile)) throw new ArgumentException("Settings file must not be blank.");
        var settings = PluginSettings.Read(workspace, settingsFile);
        cache ??= settings.CacheDirectory;
        metrics ??= settings.MetricsDirectory;
        if (build && (cache == null || args.Contains("--stdio", StringComparer.Ordinal))) throw new ArgumentException("Build-cache requires a cache directory and cannot use stdio mode.");
        if (compare != null && !Path.IsPathFullyQualified(compare)) throw new ArgumentException("Metric directories must be absolute.");
        if (summary && (metrics == null || build || args.Contains("--stdio", StringComparer.Ordinal)) || compare != null && !summary) throw new ArgumentException("Metrics summary requires a directory and standalone mode; comparison requires summary.");
        if (evaluation != null && (!Path.IsPathFullyQualified(evaluation) || summary || build || args.Contains("--stdio", StringComparer.Ordinal))) throw new ArgumentException("Evaluation summary requires an absolute CSV path and standalone mode.");
        return new(workspace, string.IsNullOrWhiteSpace(config) ? null : Path.GetFullPath(config), cache == null ? null : Path.GetFullPath(cache), build,
            metrics == null ? null : Path.GetFullPath(metrics), summary, compare == null ? null : Path.GetFullPath(compare), evaluation)
        {
            AutoInitializeSettings = settingsFile == null,
            DiscoveryMaxBytes = settings.DiscoveryMaxBytes,
            IndexCacheDirectory = settings.IndexCacheDirectory,
            AnalysisMaxBytes = settings.AnalysisMaxBytes
        };
    }
}

[McpServerToolType]
public sealed class ProcedureTools(McpLaunchOptions launch) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private ServerState? state;
    private string configurationStamp = "{}";
    private readonly PersistentAnalysisCache? cache = launch.CacheDirectory == null ? null : new(launch.CacheDirectory);
    private readonly PluginMetrics metrics = new(launch.MetricsDirectory);
    private readonly DiscoveryCursor discoveryCursors = new();
    private readonly ModuleSelections moduleSelections = new();
    private readonly AnalysisPages analysisPages = new();
    private MaterializedWorkspace? materialized;
    private WorkspaceCoverage? Coverage => materialized?.Coverage ?? state?.Documents?.Coverage;
    private WorkspaceFiles? SourceFiles => materialized?.Files ?? state?.Files;
    private IEnumerable<string> SourceStatuses => materialized == null ? state?.SourceStatuses ?? [] : ["Offline verified materialized facts; rich bodies parsed on demand."];
    private WorkspaceFileCoverage[] CoverageFiles(int offset, int limit, Uri? uri) =>
        materialized?.CoverageFiles(offset, limit, uri) ?? state?.Documents?.CoverageFiles(offset, limit, uri) ?? [];
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool(Name = "find_procedures", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Find saved T-SQL modules. Ambiguous SQL-only copies may offer scopeChoice with a ready-to-use moduleRef: choose it only when its physical directory matches the requested source. It restricts dependencies to that directory; it is not inferred project membership. Original unscoped handles remain ambiguous. Reuse nextCursor with identical options; section=issues/files pages coverage. Known files can be analyzed directly. No SQL execution.")]
    public async Task<CallToolResult> FindProcedures([Description("Name substring; empty lists indexed modules.")] string query = "",
        [Description("Maximum entries per page, 1..100; byte budget may return fewer.")] int maxResults = 30,
        CancellationToken cancellationToken = default, string? cursor = null, string section = "modules", string? documentUri = null,
        [Description("Exact sourceContextId from a previous result; limits modules and issues. File-ledger pages use documentUri instead.")] string? scopeId = null,
        [Description("Optional physical folder URI from the developer's requested scope, useful for SQL-only copies without project files.")] string? scopeUri = null)
    {
        using var observation = metrics.Begin("find_procedures", "discovery", cancellationToken);
        if (query.Length > 256 || maxResults is < 1 or > 100 || section is not ("modules" or "issues" or "files")) throw new McpException("Query/limit/section outside supported bounds.");
        if (scopeId?.Length > 4096 || section == "files" && (scopeId != null || scopeUri != null)) throw new McpException("Invalid scope; file-ledger pages support documentUri only.");
        Uri? file = null;
        if (documentUri != null && (!Uri.TryCreate(documentUri, UriKind.Absolute, out file) || !file.IsFile || file.Query.Length != 0 || file.Fragment.Length != 0))
            throw new McpException("documentUri must be an absolute file URI.");
        Task<CallToolResult> Discover(ISqlDiscoveryIndex index, RequestTimings timings)
        {
            var timer = Stopwatch.StartNew();
            var root = RequestScope(scopeUri);
            var generation = ScopedGeneration(index.Generation, root);
            var fileLedger = section == "files" ? materialized?.CoverageSnapshot() ?? state?.Documents?.CoverageSnapshot() ?? [] : [];
            if (section == "files") generation = SqlAnalysisCacheContext.Hash(generation + JsonSerializer.Serialize(fileLedger, JsonOptions));
            if (root != null) index = index.Within(root);
            var options = JsonSerializer.Serialize(new { query, maxResults, section, documentUri, scopeId, scopeUri });
            var offset = discoveryCursors.Decode(cursor, generation, options);
            var symbols = index.Search(query, cancellationToken).Where(s => s.Identity.Kind is SqlObjectKind.Procedure or SqlObjectKind.Function or SqlObjectKind.View)
                .Where(s => file == null || s.Target.Uri == file)
                .Where(s => scopeId == null || s.Identity.SourceContextId == scopeId)
                .OrderBy(s => s.Target.Uri.AbsoluteUri, StringComparer.Ordinal).ThenBy(s => s.Target.Range.Start.Line).ThenBy(s => s.Target.Range.Start.Character).ToArray();
            object Module(IndexedSqlSymbol symbol)
            {
                var metadata = index.ModuleMetadata(symbol, cancellationToken);
                var context = SourceFiles?.Contexts.Describe(symbol.Target.Uri, symbol.Identity.SourceContextId);
                object? choice = null;
                if (root == null && symbol.Target.SourceKind == SqlSourceKind.Workspace && SourceFiles is { } files &&
                    DiscoveryScopeChoice.DirectoryFor(symbol.Target.Uri, metadata, files, context, cancellationToken) is { } directory)
                {
                    var scopedMetadata = index.Within(directory).ModuleMetadata(symbol, cancellationToken);
                    if (scopedMetadata.Status == ResolutionStatus.Resolved)
                        choice = new
                        {
                            directoryUri = directory.AbsoluteUri,
                            directory = Path.GetRelativePath(launch.Workspace, directory.LocalPath).Replace('\\', '/'),
                            moduleRef = moduleSelections.Register(new(ScopedGeneration(index.Generation, directory), symbol.Target.Uri,
                                symbol.Target.Range.Start, scopedMetadata.Revision, directory)),
                            evidence = "Physical directory filter only; choose when it matches the requested source. Not verified project/database membership; outside dependencies stay unresolved."
                        };
                }
                return new
                {
                    symbol.Identity,
                    moduleRef = moduleSelections.Register(new(generation, symbol.Target.Uri, symbol.Target.Range.Start, metadata.Revision, root)),
                    context,
                    symbol.Target,
                    selection = symbol.Target.Range.Start,
                    revision = metadata.Revision,
                    status = metadata.Status,
                    scopeChoice = choice,
                    selectionHint = metadata.Status == ResolutionStatus.Ambiguous ?
                        choice != null ? "Choose scopeChoice.moduleRef if its directory matches the requested source; otherwise supply explicit scopeUri. This unscoped moduleRef preserves ambiguity." :
                        "Duplicate identity: supply the developer's explicit scopeUri. No directory choice can safely distinguish this candidate; an unscoped moduleRef preserves ambiguity." : null,
                    signature = symbol.Declaration.Documentation?.Signature
                };
            }
            var counts = index.ParseIssueCounts(file, scopeId);
            var coverage = Coverage;
            var total = section == "issues" ? counts.Count : section == "files" ?
                fileLedger.Count(f => file == null || f.Uri == file) : symbols.Length;
            var entries = new List<object>();
            var consumed = 0;
            var omittedOversized = 0;
            string? recoveryModuleRef = null;
            object Payload() => new
            {
                formatVersion = 2,
                source = "Saved snapshots; no LSP unsaved buffers. Coverage is limited to indexed sources.",
                indexedDocuments = index.DocumentCount,
                coverage,
                scope = new { id = scopeId, documentUri, directoryUri = root?.AbsoluteUri, indexedDocuments = index.DocumentCountFor(scopeId, file) },
                generation,
                sourceStatuses = SourceStatuses.Take(4).Select(s => s.Length <= 256 ? s : s[..256] + "…").ToArray(),
                sourceStatusCount = SourceStatuses.Count(),
                indexCache = materialized?.Use,
                sourceIssues = section == "issues" ? entries : new List<object>(),
                sourceIssueSummary = new { count = counts.Count, affectedFiles = counts.Files, details = "Request section=issues, optionally documentUri, with pagination." },
                timings,
                modules = section == "modules" ? entries : new List<object>(),
                files = section == "files" ? entries : new List<object>(),
                section,
                totalMatches = total,
                returned = entries.Count,
                omittedOversized,
                recoveryModuleRef,
                truncated = offset + consumed < total || omittedOversized > 0,
                hasMore = offset + consumed < total,
                nextCursor = offset + consumed < total ? discoveryCursors.Encode(generation, options, offset + consumed) : null,
                truncationReason = omittedOversized > 0 ? "entry-exceeds-budget" : offset + consumed < total ? "page-or-byte-limit" : null,
                recovery = omittedOversized > 0 ? "An entry exceeds the response budget. Analyze recoveryModuleRef when supplied, or inspect a known source file. Omitted count is explicit; do not retry this identical query." : null
            };
            var candidates = section == "issues" ? index.ParseIssuePage(file, offset, maxResults, cancellationToken, scopeId).Cast<object>() :
                section == "files" ? fileLedger.Where(f => file == null || f.Uri == file).Skip(offset).Take(maxResults).Cast<object>() :
                symbols.Skip(offset).Take(maxResults).Select(Module);
            foreach (var entry in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(entry);
                consumed++;
                if (FitsWireBudget(Result(Payload()), launch.DiscoveryMaxBytes)) continue;
                entries.RemoveAt(entries.Count - 1);
                if (entries.Count == 0)
                {
                    omittedOversized++;
                    if (section == "modules") recoveryModuleRef = JsonSerializer.SerializeToElement(entry, JsonOptions).GetProperty("moduleRef").GetString();
                    break;
                }
                consumed--;
                break;
            }
            timings.AnalysisMs = timer.Elapsed.TotalMilliseconds;
            // Timing digits and the SDK envelope need headroom beyond the measured content.
            return Task.FromResult(Result(Payload()));
        }
        return observation.Complete(await WithDiscovery(Discover, cancellationToken));
    }

    [McpServerTool(Name = "analyze_procedure", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Explain saved T-SQL with citeable evidence. Known file: documentUri plus declaration (P, dbo.P or quoted identifiers), or zero-based UTF-16 position. When the project folder is known pass scopeUri; duplicate identities need scoped discovery then moduleRef. First response includes brief facts/citations; intent selects overview, transactions or dependencies. Compact report remains available through reportRef/section pages. Read/Grep covers literals and scripts; omitted facts never prove no effects. No SQL execution or LSP required.")]
    public async Task<CallToolResult> AnalyzeProcedure(string documentUri = "", int line = 0, int character = 0,
        [Description("Expansion levels, 0..4; default 1.")] int maxDepth = 1,
        [Description("Maximum expanded modules, 1..40; default 8.")] int maxModules = 8,
        [Description("Optional exact revision; copy the entire returned value including suffixes.")] string? expectedRevision = null,
        [Description("compact (default); full retains original DTO fields. Both use deliberate pages when oversized.")] string detail = "compact",
        [Description("Compact only: up to 8 variable names or expression snippets, each 1..64 characters. Case-insensitive literal substring selection and connected value dependencies; not semantic search. Omitted counts remain visible.")] string[]? focus = null,
        CancellationToken cancellationToken = default, string? declaration = null, string? moduleRef = null,
        [Description("Expand saved evidence. No selector needed; a redundant matching root moduleRef is accepted.")] string? reportRef = null,
        [Description("Copy a returned JSON Pointer section; empty selects the report root.")] string section = "", int offset = 0, int maxItems = 20,
        [Description("Physical folder URI explicitly defining this investigation's source boundary. Outside dependencies stay unresolved.")] string? scopeUri = null,
        [Description("Prioritize the first-response brief: overview, transactions or dependencies. Does not change analysis, expansion limits or retained report facts.")] string intent = "overview")
    {
        using var observation = metrics.Begin(reportRef == null ? "analyze_procedure" : "expand_report", detail, cancellationToken);
        ModuleSelection? selectedModule = moduleRef == null ? null : moduleSelections.Get(moduleRef);
        if (selectedModule != null)
        {
            if (documentUri.Length != 0 && !Uri.TryCreate(documentUri, UriKind.Absolute, out var supplied) || declaration != null)
                throw new McpException("Use moduleRef, or a file/declaration selection; report pages need reportRef and section.");
            if (documentUri.Length != 0 && new Uri(documentUri) != selectedModule.Uri) throw new McpException("moduleRef and documentUri disagree.");
            documentUri = selectedModule.Uri.AbsoluteUri;
            line = selectedModule.Position.Line; character = selectedModule.Position.Character;
            expectedRevision ??= selectedModule.Revision;
            if (scopeUri != null && scopeUri != selectedModule.Scope?.AbsoluteUri) throw new McpException("moduleRef already binds its source scope.");
            scopeUri = selectedModule.Scope?.AbsoluteUri;
        }
        if (reportRef != null)
        {
            if (declaration != null) throw new McpException("Report pages use reportRef and section; omit declaration.");
            if (selectedModule != null) analysisPages.ValidateModule(reportRef, selectedModule.Uri, selectedModule.Position, selectedModule.Scope);
            else if (documentUri.Length != 0 && (!Uri.TryCreate(documentUri, UriKind.Absolute, out var supplied) || supplied != analysisPages.Source(reportRef)))
                throw new McpException("documentUri does not match this report's source. Expand using reportRef and section.");
            documentUri = analysisPages.Source(reportRef).AbsoluteUri;
            if (scopeUri != null && scopeUri != analysisPages.Scope(reportRef)?.AbsoluteUri) throw new McpException("reportRef already binds its source scope.");
            scopeUri = analysisPages.Scope(reportRef)?.AbsoluteUri;
        }
        if (!Uri.TryCreate(documentUri, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            line < 0 || character < 0 || maxDepth is < 0 or > 4 || maxModules is < 1 or > 40 ||
            detail is not ("full" or "compact") || focus is { Length: > 8 } ||
            focus?.Any(f => string.IsNullOrWhiteSpace(f) || f.Length > 64) == true || (detail == "full" && focus is { Length: > 0 }) ||
            declaration?.Length > 256 || offset < 0 || maxItems is < 1 or > 100 || section.Length > 2048 ||
            intent is not ("overview" or "transactions" or "dependencies"))
            throw new McpException("Expected an indexed file URI, zero-based UTF-16 position and bounded expansion limits.");
        var selector = declaration == null ? null : ModuleSelector.Parse(declaration);
        return observation.Complete(await WithIndex(async (index, timings) =>
        {
            var root = RequestScope(scopeUri);
            var generation = ScopedGeneration(materialized?.Index.Generation ?? index.Generation, root);
            if (root != null)
            {
                if (!root.IsBaseOf(uri)) throw new McpException("Selected file is outside the requested scope.");
                index = index.RestrictedTo(root);
            }
            if (selectedModule != null && selectedModule.Generation != generation) throw new McpException("moduleRef is stale: source generation changed. Request current discovery or select file/declaration.");
            if (selector != null)
            {
                var rules = SourceFiles?.Contexts.Rules(SourceFiles.ContextId(uri), state?.Resolution ?? new()) ?? new();
                var candidates = index.Symbols.Where(s => s.Target.Uri == uri && selector.Matches(s.Identity, rules.Comparer) &&
                    s.Identity.Kind is SqlObjectKind.Procedure or SqlObjectKind.Function or SqlObjectKind.View).ToArray();
                if (candidates.Length != 1) throw new McpException("File/declaration selector is missing or ambiguous; use scoped discovery for explicit candidates.");
                line = candidates[0].Target.Range.Start.Line; character = candidates[0].Target.Range.Start.Character;
            }
            object? brief = null;
            CallToolResult Envelope(object reportValue, AnalysisCacheUse? use = null) => Result(new
            {
                formatVersion = 2,
                source = "Saved snapshots; this process does not receive LSP unsaved buffers or execute SQL. Effects are static possibilities.",
                indexedDocuments = materialized?.Index.DocumentCount ?? index.DocumentCount,
                parsedDocuments = index.DocumentCount,
                coverage = Coverage,
                sourceContext = SourceFiles?.Contexts.Describe(uri, SourceFiles.ContextId(uri)),
                scope = new
                {
                    directoryUri = root?.AbsoluteUri,
                    indexedDocuments = root == null ? materialized?.Index.DocumentCount ?? index.DocumentCount : index.DocumentCount,
                    limitation = root == null ? null : "Only this explicit folder is searched; outside dependencies remain unresolved."
                },
                sourceStatuses = SourceStatuses.Take(4).Select(s => s.Length > 256 ? s[..256] + "…" : s).ToArray(),
                indexCache = materialized?.Use,
                timings,
                cache = use,
                brief,
                report = reportValue,
                drift = index.Drifts.Where(d => d.Local.Uri == uri).Take(8).ToArray()
            });
            if (reportRef != null)
            {
                var page = analysisPages.Page(reportRef, generation, section, offset, maxItems, value => FitsWireBudget(Envelope(value), launch.AnalysisMaxBytes));
                var response = Envelope(page);
                if (!FitsWireBudget(response, launch.AnalysisMaxBytes)) throw new McpException("Section exceeds the wire budget; choose a shorter parent section or a smaller page.");
                return response;
            }
            var context = CacheContext(index, timings, cancellationToken, root?.AbsoluteUri);
            var analyzed = await AnalyzeCached(index, context, uri, new(line, character), maxDepth, maxModules, expectedRevision, timings, cancellationToken);
            var report = analyzed.Report;
            observation.AnalyzerReport(report);
            var timer = Stopwatch.StartNew();
            var briefBuilder = new ProcedureBrief(report, launch.Workspace, intent, cancellationToken, focus);
            brief = briefBuilder.Create(8);
            var presented = detail == "compact" ? new CompactProcedureReport(report, launch.Workspace, focus ?? [], cancellationToken).Create() : (object)report;
            timings.PresentationMs = timer.Elapsed.TotalMilliseconds;
            var responseValue = Envelope(presented, analyzed.Use);
            if (FitsWireBudget(responseValue, launch.AnalysisMaxBytes)) return responseValue;
            var reference = analysisPages.Register(generation, uri, presented, root, report.Candidates.Length == 1 ? report.Candidates[0] : null);
            for (var count = Math.Min(8, report.Modules.Length); count >= 0; count--)
            {
                responseValue = Envelope(analysisPages.Overview(reference, generation, count), analyzed.Use);
                if (FitsWireBudget(responseValue, launch.AnalysisMaxBytes)) return responseValue;
            }
            for (var count = 7; count >= 0; count--)
            {
                brief = briefBuilder.Create(count);
                responseValue = Envelope(analysisPages.Overview(reference, generation, 0), analyzed.Use);
                if (FitsWireBudget(responseValue, launch.AnalysisMaxBytes)) return responseValue;
            }
            throw new McpException("Analysis metadata exceeds wire budget; use a narrower source/depth or direct source inspection.");
        }, cancellationToken, uri, maxDepth, maxModules, scopeUri));
    }

    [McpServerTool(Name = "get_metrics", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect opt-in local SQL analysis metrics without initializing or scanning SQL. Shows cold/warm request times, cache hits, serialized report bytes, reported facts/issues and failures. No model tokens, Grep timing or answer correctness are observed. Previous sessions are limited to retained files; metrics reads do not count themselves.")]
    public CallToolResult GetMetrics([Description("Include retained sessions from the configured metrics directory instead of only this process's recent requests.")] bool includePreviousSessions = false) =>
        Result(metrics.Get(includePreviousSessions));

    // Explicit CLI prewarm only; not a third MCP tool. One validated source snapshot, at most 256 modules.
    public async Task<CallToolResult> BuildCache(CancellationToken token)
    {
        using var observation = metrics.Begin("build_cache", "prebuild", token);
        return observation.Complete(await WithIndex(async (index, timings) =>
        {
            var context = CacheContext(index, timings, token);
            var symbols = index.Symbols.Where(s => s.Identity.Kind is SqlObjectKind.Procedure or SqlObjectKind.Function or SqlObjectKind.View)
                .OrderBy(s => s.Target.Uri.AbsoluteUri, StringComparer.Ordinal).ThenBy(s => s.Target.Range.Start.Line).ThenBy(s => s.Target.Range.Start.Character).ToArray();
            var entries = new List<(SqlObjectIdentity Identity, ResolutionStatus Status, AnalysisCacheUse Cache)>();
            foreach (var symbol in symbols.Take(256))
            {
                token.ThrowIfCancellationRequested();
                var result = await AnalyzeCached(index, context, symbol.Target.Uri, symbol.Target.Range.Start, 2, 20, null, timings, token);
                entries.Add((symbol.Identity, result.Report.Status, result.Use));
            }
            return Result(new
            {
                source = "Saved workspace/catalog snapshots; no analyzed SQL executed.",
                indexedDocuments = index.DocumentCount,
                sourceStatuses = state!.SourceStatuses,
                sourceIssues = index.SourceParseIssues(token),
                timings,
                sourcesAvailable = !state.SourceStatuses.Any(s => s.Contains(": Unavailable", StringComparison.Ordinal)),
                entries = entries.Select(e => new
                {
                    e.Identity,
                    e.Status,
                    cache = e.Cache,
                    retained = e.Cache.Status is "Stored" or "Hit" && e.Cache.File != null && File.Exists(e.Cache.File)
                }).ToArray(),
                totalModules = symbols.Length,
                truncated = symbols.Length > 256,
                retention = "At most 256 reports / 128 MiB, 8 MiB per report. Older reports may be evicted; retained is checked at summary creation."
            });
        }, token));
    }

    private SqlAnalysisCacheContext? CacheContext(SqlNavigationIndex index, RequestTimings timings, CancellationToken token, string? scope = null)
    {
        if (cache == null) return null;
        var timer = Stopwatch.StartNew();
        try
        {
            if (materialized != null)
                return new(SqlAnalysisCacheContext.Hash(configurationStamp + scope + materialized.Index.Generation + typeof(ProcedureTools).Module.ModuleVersionId),
                    materialized.Index.Files.Select(f => new SqlAnalysisCacheSource(f.Uri, f.Uri, f.Revision, null)).ToImmutableArray());
            return index.CreateAnalysisCacheContext(configurationStamp + scope + state!.AnalysisCacheCatalogStamp() +
                typeof(ProcedureTools).Module.ModuleVersionId + typeof(ServerState).Module.ModuleVersionId, token);
        }
        catch (InvalidOperationException) { return null; }
        finally { timings.CacheValidationMs = timer.Elapsed.TotalMilliseconds; }
    }

    private async Task<(SqlProcedureReport Report, AnalysisCacheUse Use)> AnalyzeCached(SqlNavigationIndex index, SqlAnalysisCacheContext? context,
        Uri uri, TextPosition position, int depth, int modules, string? expectedRevision, RequestTimings timings, CancellationToken token)
    {
        SqlProcedureReport Fresh()
        {
            var timer = Stopwatch.StartNew();
            var result = index.AnalyzeProcedure(uri, position, token, depth, modules, expectedRevision);
            timings.AnalysisMs += timer.Elapsed.TotalMilliseconds;
            return result;
        }
        if (cache == null || context == null) return (Fresh(), new(cache != null, cache == null ? "Disabled" : "UnsupportedSourceContext"));
        var key = context.RequestKey(uri, position, depth, modules);
        var timer = Stopwatch.StartNew();
        var cached = await cache.ReadAsync(context, key, token);
        timings.CacheReadMs += timer.Elapsed.TotalMilliseconds;
        if (cached.Report != null && (expectedRevision == null || cached.Report.Modules[0].Revision == expectedRevision)) return (cached.Report, cached.Use);
        var report = Fresh();
        if (report.Status != ResolutionStatus.Resolved) return (report, new(true, "NotStored", cached.Use.File));
        timer.Restart();
        var use = await cache.WriteAsync(context, key, report, token);
        timings.CacheWriteMs += timer.Elapsed.TotalMilliseconds;
        return (report, use);
    }

    private sealed class RequestTimings
    {
        public bool ColdStart { get; set; }
        public double QueueMs { get; set; }
        public double InitializationMs { get; set; }
        public double NavigationMs { get; set; }
        public bool NavigationReused { get; set; }
        public double AnalysisMs { get; set; }
        public double PresentationMs { get; set; }
        public double CacheValidationMs { get; set; }
        public double CacheReadMs { get; set; }
        public double CacheWriteMs { get; set; }
    }

    private async Task<CallToolResult> WithDiscovery(Func<ISqlDiscoveryIndex, RequestTimings, Task<CallToolResult>> action, CancellationToken token)
    {
        if (launch.IndexCacheDirectory == null) return await WithIndex((index, timings) => action(index, timings), token);
        var timer = Stopwatch.StartNew();
        await gate.WaitAsync(token);
        var timings = new RequestTimings { QueueMs = timer.Elapsed.TotalMilliseconds, ColdStart = materialized == null };
        try
        {
            timer.Restart();
            await RefreshMaterialized(token);
            timings.InitializationMs = timer.Elapsed.TotalMilliseconds;
            return await action(materialized!.Index, timings);
        }
        finally { gate.Release(); }
    }

    private async Task RefreshMaterialized(CancellationToken token)
    {
        if (materialized != null) { await materialized.RefreshAsync(token); return; }
        JObject? options = null;
        if (launch.ConfigurationFile != null)
        {
            if (new FileInfo(launch.ConfigurationFile).Length > 1_048_576) throw new McpException("Configuration exceeds size limit.");
            options = JObject.Parse(await File.ReadAllTextAsync(launch.ConfigurationFile, token));
        }
        var config = ServerConfiguration.Parse(options);
        metrics.SourceMode = config.Mode;
        configurationStamp = options?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";
        var candidate = new MaterializedWorkspace(launch, config, configurationStamp);
        await candidate.RefreshAsync(token);
        materialized = candidate;
    }

    private async Task<CallToolResult> WithIndex(Func<SqlNavigationIndex, RequestTimings, Task<CallToolResult>> action, CancellationToken token,
        Uri? selected = null, int maxDepth = 2, int maxModules = 20, string? scopeUri = null)
    {
        var timer = Stopwatch.StartNew();
        await gate.WaitAsync(token);
        var timings = new RequestTimings { QueueMs = timer.Elapsed.TotalMilliseconds, ColdStart = state == null };
        try
        {
            timer.Restart();
            if (launch.IndexCacheDirectory != null && selected != null)
            {
                timings.ColdStart = materialized == null;
                await RefreshMaterialized(token);
                timings.InitializationMs = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                var selectedIndex = await materialized!.NavigationAsync(selected, maxDepth, maxModules, token, RequestScope(scopeUri));
                timings.NavigationMs = timer.Elapsed.TotalMilliseconds;
                return await action(selectedIndex, timings);
            }
            if (state == null)
            {
                var candidate = new ServerState();
                try
                {
                    JObject? options = null;
                    if (launch.ConfigurationFile != null)
                    {
                        if (new FileInfo(launch.ConfigurationFile).Length > 1_048_576) throw new McpException("Configuration exceeds size limit.");
                        options = JObject.Parse(await File.ReadAllTextAsync(launch.ConfigurationFile, token));
                    }
                    var config = ServerConfiguration.Parse(options);
                    metrics.SourceMode = config.Mode;
                    configurationStamp = options?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";
                    var rootPath = launch.Workspace.EndsWith(Path.DirectorySeparatorChar) ? launch.Workspace : launch.Workspace + Path.DirectorySeparatorChar;
                    await candidate.InitializeSourcesAsync(config, [new Uri(rootPath)], token);
                    if ((config.Mode != "online" || config.ScanInOnline) && candidate.Files?.Roots.Count != 1)
                        throw new McpException("Workspace is linked or inaccessible; use its physical directory.");
                    state = candidate;
                }
                catch { await candidate.DisposeAsync(); throw; }
            }
            timings.InitializationMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var priorBuilds = state.NavigationBuilds;
            var index = await state.NavigationAsync(token);
            timings.NavigationMs = timer.Elapsed.TotalMilliseconds;
            timings.NavigationReused = state.NavigationBuilds == priorBuilds && metrics.SourceMode == "offline";
            return await action(index, timings);
        }
        catch (OperationCanceledException) { throw; }
        catch (McpException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Newtonsoft.Json.JsonException or OmniSharp.Extensions.JsonRpc.RpcErrorException)
        {
            throw new McpException("Analysis sources/configuration are unavailable or invalid; details suppressed.");
        }
        finally { gate.Release(); }
    }

    private static CallToolResult Result(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        using var document = JsonDocument.Parse(json);
        return new() { StructuredContent = document.RootElement.Clone(), Content = [new TextContentBlock { Text = json }] };
    }

    private static bool FitsWireBudget(CallToolResult result, int budget) =>
        JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = long.MaxValue, result }, JsonOptions).Length <= budget - 1024;

    private Uri? RequestScope(string? value)
    {
        if (value == null) return null;
        if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0 || SourceFiles?.CanScope(uri) != true)
            throw new McpException("scopeUri must name a physical directory inside the initialized workspace.");
        return new Uri(Path.TrimEndingDirectorySeparator(Path.GetFullPath(uri.LocalPath)) + Path.DirectorySeparatorChar);
    }
    private static string ScopedGeneration(string generation, Uri? scope) => scope == null ? generation : SqlAnalysisCacheContext.Hash(generation + "|" + scope.AbsoluteUri);

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (state != null) await state.DisposeAsync(); }
        finally { gate.Release(); gate.Dispose(); }
    }
}
