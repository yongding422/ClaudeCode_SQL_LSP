namespace SqlLsp.Core.Tests;

public class WorkspaceReuseTests
{
    [Fact]
    public async Task Reconciliation_reuses_verified_asts_and_detects_same_size_same_time_edits_and_added_candidates()
    {
        var root = Path.Combine(TestPaths.TempRoot, "reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var uri = new Uri(Path.Combine(root, "p.sql"));
            await File.WriteAllTextAsync(uri.LocalPath, "CREATE PROC dbo.P AS SELECT 1;");
            var options = new WorkspaceOptions();
            var files = new WorkspaceFiles(options);
            await using var store = new DocumentStore(new SqlParser(new()), files, options);
            await store.InitializeAsync([new Uri(root)], CancellationToken.None);
            var original = await store.GetIndexAsync(CancellationToken.None);
            await store.RescanAsync(CancellationToken.None);
            var unchanged = await store.GetIndexAsync(CancellationToken.None);
            Assert.Same(original[0], unchanged[0]);
            Assert.Equal(1, store.Coverage.ParseAttempts);
            var stamp = File.GetLastWriteTimeUtc(uri.LocalPath);
            await File.WriteAllTextAsync(uri.LocalPath, "CREATE PROC dbo.P AS SELECT 2;");
            File.SetLastWriteTimeUtc(uri.LocalPath, stamp);
            await store.RescanAsync(CancellationToken.None);
            var changed = await store.GetIndexAsync(CancellationToken.None);
            Assert.NotSame(original[0], changed[0]);
            Assert.Equal(2, store.Coverage.ParseAttempts);
            Assert.NotEqual(SqlNavigationIndex.AnalysisRevision(original[0]), SqlNavigationIndex.AnalysisRevision(changed[0]));
            await File.WriteAllTextAsync(Path.Combine(root, "other.sql"), "CREATE PROC dbo.P AS SELECT 3;");
            await store.RescanAsync(CancellationToken.None);
            var index = new SqlNavigationIndex(await store.GetIndexAsync(CancellationToken.None), files.ContextId, new());
            Assert.Equal(ResolutionStatus.Ambiguous, index.AnalyzeProcedure(uri, new(0, 16), CancellationToken.None).Status);
            Assert.Equal(3, store.Coverage.ParseAttempts);
        }
        finally { Directory.Delete(root, true); }
    }
}
