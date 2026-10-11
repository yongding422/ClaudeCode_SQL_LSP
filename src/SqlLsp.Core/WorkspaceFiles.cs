namespace SqlLsp.Core;

public sealed record WorkspaceOptions(int MaxFileSizeBytes = 2_097_152)
{
    public void Validate()
    {
        if (MaxFileSizeBytes is < 1 or > 16_777_216)
            throw new ArgumentException("maxFileSizeBytes must be between 1 and 16777216.");
    }
}

public sealed class WorkspaceFiles(WorkspaceOptions options)
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git" };
    private readonly List<string> roots = new();

    public IEnumerable<Uri> Configure(IEnumerable<Uri> folders)
    {
        options.Validate();
        roots.Clear();
        foreach (var uri in folders.Where(u => u.IsFile))
        {
            var path = Path.GetFullPath(uri.LocalPath);
            if (Directory.Exists(path) && !IsLink(path)) roots.Add(path);
        }
        return roots.SelectMany(Enumerate).Distinct().OrderBy(u => u.AbsoluteUri, StringComparer.Ordinal).ToArray();
    }

    public bool CanRead(Uri uri)
    {
        if (!uri.IsFile || !string.Equals(Path.GetExtension(uri.LocalPath), ".sql", StringComparison.OrdinalIgnoreCase)) return false;
        var path = Path.GetFullPath(uri.LocalPath);
        foreach (var root in roots)
        {
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(ExcludedDirectories.Contains)) continue;
            var current = root;
            if (IsLink(current)) continue;
            foreach (var part in parts)
            {
                current = Path.Combine(current, part);
                if (IsLink(current)) return false;
            }
            return true;
        }
        return false;
    }

    public async Task<string?> ReadAsync(Uri uri, CancellationToken token)
    {
        if (!CanRead(uri)) return null;
        try
        {
            await using var stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous);
            if (stream.Length > options.MaxFileSizeBytes) return null;
            // Bounded read even if a file grows after opening.
            var bytes = new byte[options.MaxFileSizeBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(length), token);
                if (count == 0) break;
                length += count;
            }
            if (length > options.MaxFileSizeBytes) return null;
            using var reader = new StreamReader(new MemoryStream(bytes, 0, length), detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(token);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<Uri> Enumerate(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
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
                else if (string.Equals(Path.GetExtension(entry), ".sql", StringComparison.OrdinalIgnoreCase)) yield return new Uri(entry);
            }
        }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
