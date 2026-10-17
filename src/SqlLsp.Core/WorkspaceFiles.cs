using System.Buffers;

namespace SqlLsp.Core;

public sealed record WorkspaceReadResult(string Status, string? Text, long Bytes, string? Encoding, double ReadMs, long ReadBytes = 0, string? ContentHash = null);

public sealed record WorkspaceOptions(int MaxFileSizeBytes = 2_097_152)
{
    public System.Collections.Immutable.ImmutableArray<OfflineProjectScope> ProjectScopes { get; init; } = [];
    public bool AutoDetectProjects { get; init; } = true;
    public void Validate()
    {
        if (MaxFileSizeBytes is < 1 or > 16_777_216)
            throw new ArgumentException("maxFileSizeBytes must be between 1 and 16777216.");
        if (ProjectScopes.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != ProjectScopes.Length ||
            ProjectScopes.Any(s => string.IsNullOrWhiteSpace(s.Id) || !s.Root.IsFile || s.Root.Query.Length != 0 || s.Root.Fragment.Length != 0))
            throw new ArgumentException("Project scopes require unique IDs and absolute physical directory URIs.");
    }
}

public sealed class WorkspaceFiles(WorkspaceOptions options)
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git" };
    private readonly List<string> roots = new();
    public WorkspaceContexts Contexts { get; } = new(options.ProjectScopes, options.AutoDetectProjects);
    public IReadOnlyList<string> Roots => roots;
    public string ContextId(Uri uri)
    {
        if (!uri.IsFile) return uri.AbsoluteUri;
        var root = uri.IsFile ? roots.Where(r => IsContained(r, uri.LocalPath)).OrderByDescending(r => r.Length).FirstOrDefault() : null;
        var fallback = root == null ? new Uri(uri, ".").AbsoluteUri : new Uri(root).AbsoluteUri;
        return Contexts.Describe(uri, fallback).Id;
    }

    public IEnumerable<Uri> EnumerateUris()
    {
        Contexts.Configure(roots.Where(Directory.Exists).SelectMany(r => Enumerate(r, ".sqlproj")));
        return roots.Where(Directory.Exists).SelectMany(r => Enumerate(r)).Distinct().ToArray();
    }

    public bool CanTraverse(Uri uri) => uri.IsFile && roots.Any(root => IsContained(root, uri.LocalPath) &&
        !Path.GetRelativePath(root, uri.LocalPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ExcludedDirectories.Contains));

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public IEnumerable<Uri> Configure(IEnumerable<Uri> folders)
    {
        options.Validate();
        roots.Clear();
        foreach (var uri in folders.Where(u => u.IsFile))
        {
            var path = Path.GetFullPath(uri.LocalPath);
            if (Directory.Exists(path) && !HasLinkedAncestor(path)) roots.Add(path);
        }
        if (options.ProjectScopes.Any(s => !roots.Any(root => IsContained(root, s.Root.LocalPath)) || HasLinkedAncestor(s.Root.LocalPath)))
            throw new ArgumentException("Project scopes must remain in initialized physical workspace roots.");
        return EnumerateUris().OrderBy(u => u.AbsoluteUri, StringComparer.Ordinal).ToArray();
    }

    public bool CanRead(Uri uri)
    {
        if (!uri.IsFile || !string.Equals(Path.GetExtension(uri.LocalPath), ".sql", StringComparison.OrdinalIgnoreCase) || !Contexts.Included(uri)) return false;
        var path = Path.GetFullPath(uri.LocalPath);
        foreach (var root in roots)
        {
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(ExcludedDirectories.Contains)) continue;
            var current = root;
            if (HasLinkedAncestor(current)) continue;
            foreach (var part in parts)
            {
                current = Path.Combine(current, part);
                if (IsLink(current)) return false;
            }
            return true;
        }
        return false;
    }

    public async Task<string?> ReadAsync(Uri uri, CancellationToken token) => (await ReadWithStatusAsync(uri, token)).Text;

    public async Task<WorkspaceReadResult> ReadWithStatusAsync(Uri uri, CancellationToken token)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        long length = 0;
        long readBytes = 0;
        WorkspaceReadResult Excluded(string status) => new(status, null, length, null, timer.Elapsed.TotalMilliseconds, readBytes);
        if (!CanRead(uri)) return Excluded("ExcludedPolicy");
        try
        {
            await using var stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous);
            length = stream.Length;
            if (stream.Length > options.MaxFileSizeBytes) return Excluded("ExcludedSize");
            // Bounded read even if a file grows after opening.
            using var contents = new MemoryStream((int)stream.Length);
            var bytes = ArrayPool<byte>.Shared.Rent((int)Math.Min(65_536, Math.Max(1, stream.Length + 1)));
            try
            {
                while (true)
                {
                    var remaining = options.MaxFileSizeBytes + 1 - (int)contents.Length;
                    var count = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, remaining)), token);
                    readBytes += count;
                    if (count == 0) break;
                    if (contents.Length + count > options.MaxFileSizeBytes) return Excluded("ExcludedSize");
                    contents.Write(bytes, 0, count);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
            contents.Position = 0;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contents.GetBuffer().AsSpan(0, (int)contents.Length)));
            using var reader = new StreamReader(contents, detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(token);
            return new("Read", text, contents.Length, reader.CurrentEncoding.WebName, timer.Elapsed.TotalMilliseconds, readBytes, hash);
        }
        catch (FileNotFoundException) { return Excluded("Deleted"); }
        catch (DirectoryNotFoundException) { return Excluded("Deleted"); }
        catch (IOException) { return Excluded("Unreadable"); }
        catch (UnauthorizedAccessException) { return Excluded("Unreadable"); }
    }

    private static IEnumerable<Uri> Enumerate(string root, string extension = ".sql")
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if (HasLinkedAncestor(directory)) continue;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (IsLink(entry)) continue;
                if (Directory.Exists(entry))
                {
                    if (!ExcludedDirectories.Contains(Path.GetFileName(entry))) pending.Push(entry);
                }
                else if (string.Equals(Path.GetExtension(entry), extension, StringComparison.OrdinalIgnoreCase)) yield return new Uri(entry);
            }
        }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static bool HasLinkedAncestor(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (IsLink(current.FullName)) return true;
        return false;
    }
}
