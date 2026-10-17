using System.Collections.Immutable;
using System.Text;

namespace SqlLsp.Core;

public sealed record DocumentSnapshot(Uri Uri, int? Version, SqlSourceKind SourceKind, SqlParseResult Parse);
public sealed record DiagnosticPublication(Uri Uri, int? Version, ImmutableArray<SqlParseDiagnostic> Diagnostics);

// The same document-index replacement path serves disk snapshots and unsaved overlays.
public sealed class DocumentStore(ISqlParser parser, WorkspaceFiles files, WorkspaceOptions options) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Uri, Overlay> overlays = new();
    private readonly Dictionary<Uri, DocumentSnapshot> disk = new();
    private readonly Dictionary<Uri, DocumentSnapshot> index = new();
    private readonly Dictionary<Uri, DiskWork> diskWork = new();
    private readonly Dictionary<Uri, WorkspaceFileCoverage> coverage = new();
    private long readBytes, parseAttempts;
    private double enumerationMs, readMs, parseMs;
    private readonly List<Task> work = new();
    private ImmutableArray<DocumentSnapshot> published = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public event Action<DiagnosticPublication>? Diagnostics;
    public event Action<string>? Fault;

    public int PendingDiskReadCount { get { lock (gate) return diskWork.Count; } }

    public WorkspaceCoverage Coverage
    {
        get
        {
            lock (gate)
            {
                int Count(string status) => coverage.Values.Count(f => f.Status == status);
                return new(coverage.Count, Count("Indexed"), coverage.Values.Count(f => f.ParseErrors > 0),
                    coverage.Values.Sum(f => f.ParseErrors), Count("ExcludedSize"), Count("ExcludedPolicy"),
                    Count("Unreadable"), Count("Failed"), Count("Deleted"), Count("Pending"), readBytes, parseAttempts,
                    enumerationMs, readMs, parseMs);
            }
        }
    }

    public WorkspaceFileCoverage[] CoverageFiles(int offset = 0, int limit = 100, Uri? uri = null)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate) return coverage.Values.Where(f => uri == null || f.Uri == uri).OrderBy(f => f.Uri.AbsoluteUri, StringComparer.Ordinal).Skip(offset).Take(limit).ToArray();
    }

    public ImmutableArray<DocumentSnapshot> Index
    {
        get { lock (gate) return PublishIndex(); }
    }

    // Preserve the immutable array identity while all document objects remain the same.
    private ImmutableArray<DocumentSnapshot> PublishIndex()
    {
        if (published.Length != index.Count || published.Any(d => !ReferenceEquals(index.GetValueOrDefault(d.Uri), d)))
            published = index.Values.OrderBy(s => s.Uri.AbsoluteUri, StringComparer.Ordinal).ToImmutableArray();
        return published;
    }

    public async Task InitializeAsync(IEnumerable<Uri> roots, CancellationToken token)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var uris = files.Configure(roots).ToArray();
        lock (gate)
        {
            enumerationMs += timer.Elapsed.TotalMilliseconds;
            foreach (var uri in uris) coverage[uri] = new(uri, "Pending");
        }
        await Parallel.ForEachAsync(uris, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4), CancellationToken = token },
            async (uri, cancellation) => await RefreshAsync(uri, cancellation));
    }

    public bool Open(Uri uri, int version, string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > options.MaxFileSizeBytes) { Exclude(uri, version, new SourceText(text)); return false; }
        lock (gate)
        {
            if (overlays.Remove(uri, out var previous)) Cancel(previous);
            Begin(uri, version, new SourceText(text));
            return true;
        }
    }

    public bool Change(Uri uri, int version, IEnumerable<TextEdit> edits)
    {
        lock (gate)
        {
            if (!overlays.TryGetValue(uri, out var previous) || version <= previous.Version) return false;
            SourceText source;
            try { source = previous.Source.Apply(edits); }
            catch (ArgumentException) { Fault?.Invoke("Rejected invalid UTF-16 edit range."); return false; }
            if (Encoding.UTF8.GetByteCount(source.Text) > options.MaxFileSizeBytes) { Exclude(uri, version, source); return false; }
            Cancel(previous);
            Begin(uri, version, source);
            return true;
        }
    }

    public async Task<DocumentSnapshot?> GetAsync(Uri uri, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task? pending;
            lock (gate)
            {
                pending = overlays.TryGetValue(uri, out var overlay) ? overlay.Pending : diskWork.GetValueOrDefault(uri)?.Pending;
                if (pending == null || pending.IsCompleted) return index.GetValueOrDefault(uri);
            }
            await pending.WaitAsync(token);
        }
    }

    public async Task CloseAsync(Uri uri, CancellationToken token)
    {
        lock (gate)
        {
            if (overlays.Remove(uri, out var previous)) Cancel(previous);
            index.Remove(uri);
            Diagnostics?.Invoke(new DiagnosticPublication(uri, null, []));
        }
        await RefreshAsync(uri, token);
    }

    public async Task<ImmutableArray<DocumentSnapshot>> GetIndexAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task[] pending;
            lock (gate)
            {
                pending = overlays.Values.Select(o => o.Pending).Concat(diskWork.Where(d => !overlays.ContainsKey(d.Key)).Select(d => d.Value.Pending))
                    .OfType<Task>().Where(t => !t.IsCompleted).ToArray();
                if (pending.Length == 0) return PublishIndex();
            }
            await Task.WhenAll(pending).WaitAsync(token);
        }
    }

    public Task RefreshAsync(Uri uri, CancellationToken token)
    {
        lock (gate)
        {
            if (disposed) return Task.CompletedTask;
            if (diskWork.TryGetValue(uri, out var previous) && !previous.Completed) previous.Cancellation.Cancel();
            var job = new DiskWork(CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token));
            diskWork[uri] = job;
            coverage[uri] = new(uri, "Pending");
            if (!overlays.ContainsKey(uri)) index.Remove(uri);
            job.Pending = Task.Run(async () =>
            {
                WorkspaceReadResult? read = null;
                try
                {
                    read = await files.ReadWithStatusAsync(uri, job.Cancellation.Token);
                    DocumentSnapshot? snapshot = null;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var parsed = false;
                    try
                    {
                        if (read.Text != null)
                        {
                            // Verify decoded contents, not size/mtime. Reconciliation still reads every file,
                            // but an unchanged source keeps its immutable AST and semantic extraction.
                            lock (gate) snapshot = disk.GetValueOrDefault(uri);
                            if (snapshot?.Parse.Source.Text != read.Text)
                            {
                                lock (gate) parseAttempts++;
                                parsed = true;
                                snapshot = new(uri, null, SqlSourceKind.Workspace,
                                    await parser.ParseAsync(new SourceText(read.Text), job.Cancellation.Token));
                            }
                        }
                    }
                    finally
                    {
                        lock (gate)
                        {
                            readBytes += read.ReadBytes;
                            readMs += read.ReadMs;
                            if (parsed) parseMs += timer.Elapsed.TotalMilliseconds;
                        }
                    }
                    lock (gate)
                    {
                        if (!ReferenceEquals(diskWork.GetValueOrDefault(uri), job) || job.Cancellation.IsCancellationRequested) return;
                        coverage[uri] = new(uri, snapshot == null ? read.Status : "Indexed", read.Bytes, read.Encoding,
                            snapshot?.Parse.Diagnostics.Length ?? 0);
                        if (snapshot == null) disk.Remove(uri); else disk[uri] = snapshot;
                        if (!overlays.ContainsKey(uri))
                        {
                            if (snapshot == null) index.Remove(uri); else index[uri] = snapshot;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (gate)
                        if (ReferenceEquals(diskWork.GetValueOrDefault(uri), job)) coverage[uri] = new(uri, "Pending");
                }
                catch (Exception error)
                {
                    lock (gate)
                        if (ReferenceEquals(diskWork.GetValueOrDefault(uri), job))
                        {
                            disk.Remove(uri);
                            if (!overlays.ContainsKey(uri)) index.Remove(uri);
                            coverage[uri] = new(uri, "Failed", read?.Bytes ?? 0, read?.Encoding, FailureKind: error.GetType().Name);
                        }
                    Fault?.Invoke("Workspace parsing failed; source and details suppressed.");
                }
                finally
                {
                    lock (gate)
                    {
                        job.Completed = true;
                        job.Cancellation.Dispose();
                        if (ReferenceEquals(diskWork.GetValueOrDefault(uri), job)) diskWork.Remove(uri);
                    }
                }
            });
            work.RemoveAll(t => t.IsCompleted);
            work.Add(job.Pending);
            return job.Pending;
        }
    }

    public async Task RescanAsync(CancellationToken token)
    {
        Uri[] known;
        lock (gate) known = disk.Keys.Concat(diskWork.Keys).ToArray();
        await Parallel.ForEachAsync(files.EnumerateUris().Concat(known).Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4), CancellationToken = token },
            async (uri, cancellation) => await RefreshAsync(uri, cancellation));
    }

    private void Begin(Uri uri, int version, SourceText source)
    {
        var overlay = new Overlay(version, source, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
        overlays[uri] = overlay;
        index.Remove(uri); // Never return a declaration from a superseded buffer.
        overlay.Pending = Task.Run(async () =>
        {
            try
            {
                var result = await parser.ParseAsync(source, overlay.Cancellation.Token);
                lock (gate)
                {
                    if (!overlays.TryGetValue(uri, out var current) || !ReferenceEquals(current, overlay) || overlay.Cancellation.IsCancellationRequested) return;
                    index[uri] = new DocumentSnapshot(uri, version, SqlSourceKind.UnsavedBuffer, result);
                    Diagnostics?.Invoke(new DiagnosticPublication(uri, version, result.Diagnostics));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Fault?.Invoke("Document parsing failed; details and SQL text suppressed."); }
            finally { lock (gate) { overlay.Completed = true; overlay.Cancellation.Dispose(); } }
        });
        work.RemoveAll(task => task.IsCompleted);
        work.Add(overlay.Pending);
    }

    private void Exclude(Uri uri, int version, SourceText source)
    {
        lock (gate)
        {
            if (overlays.Remove(uri, out var previous)) Cancel(previous);
            var excluded = new Overlay(version, source, new CancellationTokenSource()) { Completed = true, Pending = Task.CompletedTask };
            excluded.Cancellation.Dispose();
            overlays[uri] = excluded;
            index.Remove(uri);
            Diagnostics?.Invoke(new DiagnosticPublication(uri, version, []));
            Fault?.Invoke("Document exceeds maxFileSizeBytes; analysis excluded.");
        }
    }

    private static void Cancel(Overlay overlay)
    {
        if (!overlay.Completed) overlay.Cancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            pending = work.ToArray();
        }
        await Task.WhenAll(pending);
        lifetime.Dispose();
    }

    private sealed class Overlay(int version, SourceText source, CancellationTokenSource cancellation)
    {
        public int Version { get; } = version;
        public SourceText Source { get; } = source;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Pending { get; set; }
        public bool Completed { get; set; }
    }

    private sealed class DiskWork(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Pending { get; set; }
        public bool Completed { get; set; }
    }
}
