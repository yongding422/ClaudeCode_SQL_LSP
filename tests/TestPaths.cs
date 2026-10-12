namespace SqlLsp.Tests;

internal static class TestPaths
{
    // macOS commonly exposes its temp directory through /var -> /private/var.
    // Fixtures use the physical path so production's linked-root rejection is still exercised honestly.
    public static string TempRoot { get; } = ResolveTempRoot();

    private static string ResolveTempRoot()
    {
        var path = Path.GetFullPath(Path.GetTempPath());
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
        return current;
    }
}
