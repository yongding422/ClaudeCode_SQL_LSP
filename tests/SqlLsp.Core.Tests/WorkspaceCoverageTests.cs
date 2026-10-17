using System.Text;

namespace SqlLsp.Core.Tests;

public class WorkspaceCoverageTests
{
    [Fact]
    public async Task Every_enumerated_file_has_a_terminal_status_and_encoding_and_failures_are_visible()
    {
        var root = Path.Combine(TestPaths.TempRoot, "coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "good.sql"), "SELECT 1;", Encoding.Unicode);
            await File.WriteAllTextAsync(Path.Combine(root, "bad.sql"), "SELECT FROM;");
            await File.WriteAllTextAsync(Path.Combine(root, "large.sql"), new string('x', 300));
            await File.WriteAllTextAsync(Path.Combine(root, "failed.sql"), "FAIL");
            var options = new WorkspaceOptions(200);
            var files = new WorkspaceFiles(options);
            await using var store = new DocumentStore(new FailingParser(), files, options);
            await store.InitializeAsync([new Uri(root)], CancellationToken.None);
            var summary = store.Coverage;
            Assert.Equal(4, summary.Discovered);
            Assert.Equal(2, summary.Indexed);
            Assert.Equal(1, summary.ParseErrorFiles);
            Assert.True(summary.ParseErrors > 0);
            Assert.Equal(1, summary.ExcludedSize);
            Assert.Equal(1, summary.Failed);
            Assert.Equal(0, summary.Pending);
            Assert.Equal(3, summary.ParseAttempts);
            Assert.True(summary.ReadBytes > 0);
            var entries = store.CoverageFiles();
            Assert.Equal("utf-16", entries.Single(f => f.Uri.LocalPath.EndsWith("good.sql")).Encoding);
            Assert.Equal("InvalidOperationException", entries.Single(f => f.Status == "Failed").FailureKind);
            Assert.Equal(2, store.CoverageFiles(1, 2).Length);
            var deleted = new Uri(Path.Combine(root, "good.sql"));
            File.Delete(deleted.LocalPath);
            await store.RefreshAsync(deleted, CancellationToken.None);
            Assert.Equal(1, store.Coverage.Deleted);
            Assert.Equal(1, store.Coverage.Indexed);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FailingParser : ISqlParser
    {
        private readonly SqlParser parser = new(new());
        public Task<SqlParseResult> ParseAsync(SourceText source, CancellationToken token) =>
            source.Text == "FAIL" ? throw new InvalidOperationException("secret source details") : parser.ParseAsync(source, token);
    }
}
