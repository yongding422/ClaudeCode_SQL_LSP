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
    private readonly List<Task> work = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public event Action<DiagnosticPublication>? Diagnostics;
    public event Action<string>? Fault;

    public ImmutableArray<DocumentSnapshot> Index
    {
        get { lock (gate) return index.Values.OrderBy(s => s.Uri.AbsoluteUri, StringComparer.Ordinal).ToImmutableArray(); }
    }

    public async Task InitializeAsync(IEnumerable<Uri> roots, CancellationToken token)
    {
        foreach (var uri in files.Configure(roots))
        {
            token.ThrowIfCancellationRequested();
            var snapshot = await LoadDiskAsync(uri, token);
            if (snapshot == null) continue;
            lock (gate)
            {
                disk[uri] = snapshot;
                if (!overlays.ContainsKey(uri)) index[uri] = snapshot;
            }
        }
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
            Task? pending;
            lock (gate)
            {
                pending = overlays.TryGetValue(uri, out var overlay) ? overlay.Pending : null;
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
        var snapshot = await LoadDiskAsync(uri, token);
        lock (gate)
        {
            if (snapshot == null) disk.Remove(uri); else disk[uri] = snapshot;
            if (!overlays.ContainsKey(uri) && snapshot != null) index[uri] = snapshot;
        }
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

    private async Task<DocumentSnapshot?> LoadDiskAsync(Uri uri, CancellationToken token)
    {
        var text = await files.ReadAsync(uri, token);
        return text == null ? null : new DocumentSnapshot(uri, null, SqlSourceKind.Workspace,
            await parser.ParseAsync(new SourceText(text), token));
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
}
