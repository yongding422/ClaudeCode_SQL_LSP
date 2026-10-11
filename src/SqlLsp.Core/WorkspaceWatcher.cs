using System.Threading.Channels;

namespace SqlLsp.Core;

public sealed class WorkspaceWatcher : IAsyncDisposable
{
    private readonly WorkspaceFiles files;
    private readonly DocumentStore documents;
    private readonly Channel<Uri?> events = Channel.CreateUnbounded<Uri?>();
    private readonly CancellationTokenSource stop = new();
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly Task pump;
    private readonly Action<string> log;

    public WorkspaceWatcher(WorkspaceFiles files, DocumentStore documents, Action<string> log)
    {
        this.files = files;
        this.documents = documents;
        this.log = log;
        foreach (var root in files.Roots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Created += Changed;
                watcher.Changed += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
                watcher.Error += (_, _) => { log("Workspace watcher overflow; reconciling workspace files."); events.Writer.TryWrite(null); };
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                log("Native workspace watcher unavailable; client file notifications and re-open refresh remain available.");
            }
        }
        pump = RunAsync();
        events.Writer.TryWrite(null); // Close the scan-to-watcher startup gap.
    }

    private void Changed(object sender, FileSystemEventArgs e) => Queue(e.FullPath);
    private void Queue(string path)
    {
        var uri = new Uri(path);
        if (!files.CanTraverse(uri)) return;
        var folder = new Uri(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        if (Directory.Exists(path) || documents.Index.Any(d => d.Uri.IsFile && folder.IsBaseOf(d.Uri)))
            events.Writer.TryWrite(null);
        else if (string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase)) events.Writer.TryWrite(uri);
    }

    private async Task RunAsync()
    {
        try
        {
            while (await events.Reader.WaitToReadAsync(stop.Token))
            {
                await Task.Delay(100, stop.Token);
                var pending = new HashSet<Uri>();
                var rescan = false;
                while (events.Reader.TryRead(out var uri))
                {
                    if (uri == null || !string.Equals(Path.GetExtension(uri.LocalPath), ".sql", StringComparison.OrdinalIgnoreCase)) rescan = true;
                    else pending.Add(uri);
                }
                try
                {
                    if (rescan) await documents.RescanAsync(stop.Token);
                    else foreach (var uri in pending) await documents.RefreshAsync(uri, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception) { log("Workspace refresh failed; source and details suppressed."); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var watcher in watchers) watcher.Dispose();
        stop.Cancel();
        await pump;
        stop.Dispose();
    }
}
