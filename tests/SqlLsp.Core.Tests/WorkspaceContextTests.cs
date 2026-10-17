using System.Collections.Immutable;

namespace SqlLsp.Core.Tests;

public class WorkspaceContextTests
{
    [Fact]
    public async Task Conditional_imported_and_overlapping_membership_never_selects_an_arbitrary_project()
    {
        var root = Path.Combine(TestPaths.TempRoot, "projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = new Uri(Path.Combine(root, "m.sql"));
            var a = new Uri(Path.Combine(root, "a.sqlproj"));
            var b = new Uri(Path.Combine(root, "b.sqlproj"));
            await File.WriteAllTextAsync(a.LocalPath, "<Project><ItemGroup Condition=\"'$(X)'=='true'\"><Build Include=\"m.sql\" /></ItemGroup><Import Project=\"unknown.targets\" /></Project>");
            var contexts = new WorkspaceContexts([], true);
            contexts.Configure([a]);
            var unknown = contexts.Describe(file, "root");
            Assert.False(unknown.MembershipVerified);
            Assert.Equal(file.AbsoluteUri, unknown.Id);
            await File.WriteAllTextAsync(a.LocalPath, "<Project><ItemGroup><Build Include=\"**/*.sql\" /></ItemGroup></Project>");
            await File.WriteAllTextAsync(b.LocalPath, "<Project><ItemGroup><Build Include=\"m.sql\" /></ItemGroup></Project>");
            contexts.Configure([a, b]);
            Assert.False(contexts.Describe(file, "root").MembershipVerified);
            contexts.Configure([a]);
            Assert.True(contexts.Describe(file, "root").MembershipVerified);
            var explicitScopes = ImmutableArray.Create(new OfflineProjectScope("one", new Uri(root)),
                new OfflineProjectScope("two", new Uri(root + Path.DirectorySeparatorChar)));
            contexts = new(explicitScopes, true);
            Assert.False(contexts.Included(file));
        }
        finally { Directory.Delete(root, true); }
    }
}
