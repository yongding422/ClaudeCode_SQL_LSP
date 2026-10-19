using SqlLsp.Core;

namespace SqlLsp.Mcp;

// Offer a physical filter for explicit selection, never assert an inferred SQL project/database.
internal static class DiscoveryScopeChoice
{
    public static Uri? DirectoryFor(Uri source, SqlModuleMetadata metadata, WorkspaceFiles files,
        WorkspaceContextInfo? context, CancellationToken token)
    {
        if (context?.Evidence != "workspace-root" || metadata.Status != ResolutionStatus.Ambiguous ||
            metadata.CandidateUris.Count(uri => uri == source) != 1) return null;
        Uri? selected = null;
        var directory = new DirectoryInfo(Path.GetDirectoryName(source.LocalPath)!);
        for (var depth = 0; directory != null && depth < 64; depth++, directory = directory.Parent)
        {
            token.ThrowIfCancellationRequested();
            var uri = new Uri(Path.TrimEndingDirectorySeparator(directory.FullName) + Path.DirectorySeparatorChar);
            if (!files.CanScope(uri) || metadata.CandidateUris.Any(other => other != source && uri.IsBaseOf(other))) break;
            selected = uri;
        }
        return selected;
    }
}
