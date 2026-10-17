using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SqlLsp.Core;

public sealed record OfflineProjectScope(string Id, Uri Root, string? Database = null, string? DefaultSchema = null,
    bool? CaseSensitive = null, string[]? Include = null, string[]? Exclude = null, string[]? References = null);
public sealed record WorkspaceContextInfo(string Id, string Evidence, bool MembershipVerified, string? Limitation);

// Static paths and literal Build Include/Remove only. Never evaluates MSBuild, imports, SQLCMD or SQL.
public sealed class WorkspaceContexts(ImmutableArray<OfflineProjectScope> configured, bool detectProjects)
{
    private sealed record Project(Uri Uri, string Root, string[] Includes, string[] Removes, bool Partial);
    private ImmutableArray<Project> projects = [];
    public string Generation { get; private set; } = "";

    public void Configure(IEnumerable<Uri> projectFiles)
    {
        var loaded = ImmutableArray.CreateBuilder<Project>();
        if (detectProjects)
            foreach (var uri in projectFiles.OrderBy(u => u.AbsoluteUri, StringComparer.Ordinal))
            {
                try
                {
                    using var stream = File.OpenRead(uri.LocalPath);
                    if (stream.Length > 1_048_576)
                    {
                        loaded.Add(new(uri, Path.GetDirectoryName(uri.LocalPath)!, [], [], true));
                        continue;
                    }
                    using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576 });
                    var document = XDocument.Load(reader);
                    var build = document.Descendants().Where(e => e.Name.LocalName == "Build" &&
                        e.AncestorsAndSelf().All(p => p.Attribute("Condition") == null)).ToArray();
                    var includes = build.SelectMany(e => (e.Attribute("Include")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)).ToArray();
                    var removes = build.SelectMany(e => (e.Attribute("Remove")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)).ToArray();
                    var partial = document.Descendants().Any(e => e.Name.LocalName == "Import" || e.Attribute("Condition") != null) ||
                        includes.Concat(removes).Any(p => p.Contains('$') || p.Contains('@'));
                    loaded.Add(new(uri, Path.GetDirectoryName(uri.LocalPath)!, includes, removes, partial));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
                { loaded.Add(new(uri, Path.GetDirectoryName(uri.LocalPath)!, [], [], true)); }
            }
        projects = loaded.ToImmutable();
        Generation = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(projects)));
    }

    public WorkspaceContextInfo Describe(Uri uri, string fallback)
    {
        if (!uri.IsFile) return new(fallback, "non-file", false, null);
        var explicitScopes = configured.Where(s => Within(s.Root.LocalPath, uri.LocalPath)).ToArray();
        if (explicitScopes.Length > 0)
        {
            var length = explicitScopes.Max(s => Path.TrimEndingDirectorySeparator(s.Root.LocalPath).Length);
            var nearest = explicitScopes.Where(s => Path.TrimEndingDirectorySeparator(s.Root.LocalPath).Length == length).ToArray();
            if (nearest.Length != 1) return new(uri.AbsoluteUri, "overlapping-explicit-scopes", false, "Overlapping scopes require a unique nearest root; no cross-file binding.");
            var scope = nearest[0];
            var included = Matches(scope.Root.LocalPath, uri.LocalPath, scope.Include ?? ["**/*.sql"]) && !Matches(scope.Root.LocalPath, uri.LocalPath, scope.Exclude ?? []);
            return new(scope.Id, "configured", included, included ? null : "File excluded by configured include/exclude policy.");
        }
        var candidates = projects.Where(p => Within(p.Root, uri.LocalPath)).ToArray();
        if (candidates.Length == 0) return new(fallback, "workspace-root", false, "No verified project membership; resolution is limited to this configured workspace context.");
        var longest = candidates.Max(p => p.Root.Length);
        candidates = candidates.Where(p => p.Root.Length == longest && Matches(p.Root, uri.LocalPath, p.Includes) && !Matches(p.Root, uri.LocalPath, p.Removes)).ToArray();
        if (candidates.Length != 1) return new(uri.AbsoluteUri, "unknown-or-overlapping-project-membership", false,
            "No unique static Build membership. Imports/variables/conditions are not evaluated; no cross-file binding.");
        var project = candidates[0];
        return new(project.Uri.AbsoluteUri, "sqlproj-literal-build-membership", true,
            project.Partial ? "Project imports, conditions and property expansion are not evaluated; only literal membership is used." : null);
    }

    public ResolutionOptions Rules(string contextId, ResolutionOptions fallback)
    {
        var scope = configured.FirstOrDefault(s => s.Id == contextId);
        return scope == null ? fallback : fallback with
        {
            DefaultDatabase = scope.Database ?? fallback.DefaultDatabase,
            DefaultSchema = scope.DefaultSchema ?? fallback.DefaultSchema,
            CaseSensitive = scope.CaseSensitive ?? fallback.CaseSensitive
        };
    }

    public bool CanReference(string from, string to) => configured.FirstOrDefault(s => s.Id == from)?.References?.Contains(to, StringComparer.Ordinal) == true;
    public HashSet<(string From, string To)> ReferenceMappings() => configured
        .SelectMany(s => (s.References ?? []).Select(to => (s.Id, to))).ToHashSet();
    public bool Included(Uri uri) => !configured.Any(s => Within(s.Root.LocalPath, uri.LocalPath)) || Describe(uri, uri.AbsoluteUri).MembershipVerified;

    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static bool Matches(string root, string path, string[] patterns)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return patterns.Any(pattern =>
        {
            if (pattern.Length > 4096 || pattern.Contains('$') || pattern.Contains('@')) return false;
            var expression = Regex.Escape(pattern.Replace('\\', '/')).Replace("\\*\\*/", "(?:.*/)?").Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]");
            return Regex.IsMatch(relative, "^" + expression + "$", OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        });
    }
}
