using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using SqlLsp.Core;
using SqlLsp.Server;

namespace SqlLsp.Mcp;

// Offline-only persisted fact index. Every request verifies current contents; rich ASTs are separate and bounded.
internal sealed class MaterializedWorkspace(McpLaunchOptions launch, ServerConfiguration configuration, string stamp, long priorBodyParses = 0)
{
    public WorkspaceFiles Files { get; } = new(configuration.Workspace);
    public SqlMaterializedIndex Index { get; private set; } = new([]);
    public WorkspaceCoverage Coverage { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private readonly MaterializedSnapshotStore store = new(launch.IndexCacheDirectory!, launch.Workspace, configuration.Workspace.ScanScope);
    private WorkspaceFileCoverage[] ledger = [];
    private Dictionary<Uri, SqlMaterializedFile>? prior;
    private readonly Dictionary<Uri, (SqlMaterializedFile Facts, DocumentSnapshot Document)> parsed = new();
    private long reads, parseAttempts, bodyParses;
    public long TotalBodyParses => priorBodyParses + bodyParses;
    private double readMs, parseMs, enumerationMs;
    private int reused, extracted;
    public object Use => new
    {
        enabled = true,
        status = store.Status,
        verification = "Current file bytes hashed; metadata alone is never trusted",
        reusedFiles = reused,
        extractedFiles = extracted,
        bodyParses = TotalBodyParses,
        parsedBodiesRetained = parsed.Count,
        format = "materialized-v1",
        onlineCatalogs = false
    };

    public WorkspaceFileCoverage[] CoverageFiles(int offset, int limit, Uri? uri) => ledger.Where(f => uri == null || f.Uri == uri).Skip(offset).Take(limit).ToArray();
    public ImmutableArray<WorkspaceFileCoverage> CoverageSnapshot() => ledger.ToImmutableArray();

    public async Task RefreshAsync(CancellationToken token)
    {
        if (configuration.Mode != "offline") throw new McpException("indexCache supports offline sources only. Disable it for online/hybrid catalog analysis.");
        var timer = Stopwatch.StartNew();
        var uris = Files.Configure([new Uri(Path.GetFullPath(launch.Workspace))]).ToArray();
        if (Files.Roots.Count != 1) throw new McpException("Workspace must be an accessible physical directory.");
        if (uris.Length > 10000) throw new McpException("Verified index exceeds 10,000 input files; configure a smaller source scope.");
        enumerationMs += timer.Elapsed.TotalMilliseconds;
        var key = SqlAnalysisCacheContext.Hash(stamp + Path.GetFullPath(launch.Workspace) + configuration.Workspace.ScanScope?.AbsoluteUri +
            typeof(SqlParser).Module.ModuleVersionId + typeof(MaterializedWorkspace).Module.ModuleVersionId +
            typeof(Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragment).Module.ModuleVersionId + "materialized-v1");
        prior ??= (await store.ReadAsync(key, token)).GroupBy(f => f.Uri).ToDictionary(g => g.Key, g => g.First());
        var facts = new ConcurrentDictionary<Uri, SqlMaterializedFile>();
        var entries = new ConcurrentDictionary<Uri, WorkspaceFileCoverage>();
        long validationBytes = 0;
        reused = extracted = 0;
        await Parallel.ForEachAsync(uris, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (uri, cancellation) =>
        {
            var read = await Files.ReadWithStatusAsync(uri, cancellation);
            if (Interlocked.Add(ref validationBytes, read.ReadBytes) > 128L * 1024 * 1024)
                throw new McpException("Verified source inventory exceeds 128 MiB; configure a smaller source scope.");
            Interlocked.Add(ref reads, read.ReadBytes);
            lock (entries) readMs += read.ReadMs;
            if (read.Text == null) { entries[uri] = new(uri, read.Status, read.Bytes); return; }
            var context = Files.ContextId(uri);
            var rules = SqlMaterializedRules.From(Files.Contexts.Rules(context, configuration.Resolution));
            SqlMaterializedFile file;
            if (prior.TryGetValue(uri, out var saved) && saved.RawHash == read.ContentHash && saved.Encoding == read.Encoding && saved.ContextId == context && saved.Rules == rules)
            {
                file = saved;
                Interlocked.Increment(ref reused);
            }
            else
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    Interlocked.Increment(ref parseAttempts);
                    var parsedSource = await new SqlParser(configuration.Parser, rules.Options).ParseAsync(new SourceText(read.Text), cancellation);
                    var document = new DocumentSnapshot(uri, null, SqlSourceKind.Workspace, parsedSource);
                    file = new(uri, read.ContentHash!, SqlNavigationIndex.AnalysisRevision(document), read.Bytes, read.Encoding!, context, rules,
                        parsedSource.Declarations, parsedSource.Semantics, parsedSource.Diagnostics);
                    Interlocked.Increment(ref extracted);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    entries[uri] = new(uri, "Failed", read.Bytes, read.Encoding, FailureKind: error.GetType().Name);
                    return;
                }
                finally { lock (entries) parseMs += watch.Elapsed.TotalMilliseconds; }
            }
            facts[uri] = file;
            entries[uri] = new(uri, "Indexed", read.Bytes, read.Encoding, file.Diagnostics.Length);
        });
        token.ThrowIfCancellationRequested();
        var changed = prior.Count != facts.Count || facts.Any(p => !ReferenceEquals(prior.GetValueOrDefault(p.Key), p.Value));
        var rebuild = changed || Index.Files.Length != facts.Count || Index.Files.Any(f => !ReferenceEquals(facts.GetValueOrDefault(f.Uri), f));
        var index = rebuild ? new SqlMaterializedIndex(facts.Values.ToImmutableArray(), Files.Contexts.ReferenceMappings()) : Index;
        ledger = entries.Values.OrderBy(f => f.Uri.AbsoluteUri, StringComparer.Ordinal).ToArray();
        Index = index;
        prior = facts.ToDictionary(p => p.Key, p => p.Value);
        int Count(string status) => ledger.Count(f => f.Status == status);
        Coverage = new(ledger.Length, Count("Indexed"), ledger.Count(f => f.ParseErrors > 0), ledger.Sum(f => f.ParseErrors), Count("ExcludedSize"), Count("ExcludedPolicy"),
            Count("Unreadable"), Count("Failed"), Count("Deleted"), Count("Pending"), reads, parseAttempts, enumerationMs, readMs, parseMs);
        foreach (var obsolete in parsed.Where(p => !prior.TryGetValue(p.Key, out var f) || f.RawHash != p.Value.Facts.RawHash || f.Rules != p.Value.Facts.Rules).Select(p => p.Key).ToArray()) parsed.Remove(obsolete);
        if (changed || extracted > 0) await store.WriteAsync(key, Index.Files.ToArray(), token);
    }

    public async Task<SqlNavigationIndex> NavigationAsync(Uri selected, int depth, int modules, CancellationToken token, Uri? scope = null)
    {
        var snapshot = scope == null ? Index : (SqlMaterializedIndex)Index.Within(scope);
        var facts = snapshot.Files.ToDictionary(f => f.Uri);
        var loaded = new Dictionary<Uri, DocumentSnapshot>();
        var queue = new Queue<(Uri Uri, int Depth)>();
        queue.Enqueue((selected, 0));
        long bytes = 0;
        while (queue.TryDequeue(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (loaded.ContainsKey(next.Uri) || !facts.TryGetValue(next.Uri, out var file) || loaded.Count >= 64 || bytes + file.Bytes > 32 * 1024 * 1024) continue;
            if (!parsed.TryGetValue(next.Uri, out var cached) || cached.Facts.RawHash != file.RawHash || cached.Facts.Rules != file.Rules)
            {
                var read = await Files.ReadWithStatusAsync(next.Uri, token);
                if (read.Text == null || read.ContentHash != file.RawHash) throw new McpException("Source changed or became unavailable during analysis; request current discovery/analysis once.");
                var parse = await new SqlParser(configuration.Parser, file.Rules.Options).ParseAsync(new SourceText(read.Text), token);
                cached = (file, new(next.Uri, null, SqlSourceKind.Workspace, parse));
                parsed[next.Uri] = cached;
                bodyParses++;
            }
            loaded[next.Uri] = cached.Document;
            bytes += file.Bytes;
            // One extra level supplies original procedure signatures even when report expansion stops.
            if (next.Depth > depth) continue;
            foreach (var reference in file.Semantics.References.Where(r => r.IsInvocation || r.Kind is SqlReferenceKind.Relation or SqlReferenceKind.Table))
            {
                var resolution = snapshot.Resolve(file, reference);
                if (resolution.Target?.Identity?.Kind is SqlObjectKind.Procedure or SqlObjectKind.Function or SqlObjectKind.View)
                    queue.Enqueue((resolution.Target.Uri, next.Depth + 1));
            }
        }
        foreach (var old in parsed.Keys.Except(loaded.Keys).ToArray()) parsed.Remove(old);
        return new(loaded.Values.OrderBy(d => d.Uri.AbsoluteUri, StringComparer.Ordinal).ToImmutableArray(),
            uri => facts.GetValueOrDefault(uri)?.ContextId ?? uri.AbsoluteUri, configuration.Resolution, snapshot.Symbols, indexDeclarations: false,
            resolutionFor: uri => facts.GetValueOrDefault(uri)?.Rules.Options ?? configuration.Resolution,
            canReference: Files.Contexts.CanReference);
    }
}
