namespace SqlLsp.Core.Tests;

public class WorkspaceTests
{
    [Fact]
    public async Task Disk_index_preserves_distinct_files_overlay_and_restores_saved_file_on_close()
    {
        var root = Path.Combine(Path.GetTempPath(), "sql-lsp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "a.sql");
            await File.WriteAllTextAsync(path, "CREATE PROC dbo.disk AS SELECT 1;");
            await File.WriteAllTextAsync(Path.Combine(root, "b.sql"), "CREATE PROC sales.disk AS SELECT 1;");
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            await File.WriteAllTextAsync(Path.Combine(root, "bin", "excluded.sql"), "SELECT FROM;");
            await File.WriteAllTextAsync(Path.Combine(root, "large.sql"), new string('x', 300));
            var options = new WorkspaceOptions(200);
            var files = new WorkspaceFiles(options);
            await using var store = new DocumentStore(new SqlParser(new()), files, options);
            await store.InitializeAsync([new Uri(root)], CancellationToken.None);
            Assert.Equal(2, store.Index.Length);
            Assert.False(files.CanRead(new Uri(Path.Combine(root, "..", "outside.sql"))));
            Assert.False(files.CanRead(new Uri(Path.Combine(root, "bin", "excluded.sql"))));
            var uri = new Uri(path);
            store.Open(uri, 1, "CREATE PROC dbo.overlay AS SELECT 1;");
            Assert.Equal(SqlSourceKind.UnsavedBuffer, (await store.GetAsync(uri, CancellationToken.None))!.SourceKind);
            Assert.Equal(2, store.Index.Length);
            await File.WriteAllTextAsync(path, "CREATE PROC dbo.saved AS SELECT 1;");
            await store.CloseAsync(uri, CancellationToken.None);
            var restored = await store.GetAsync(uri, CancellationToken.None);
            Assert.Equal(SqlSourceKind.Workspace, restored!.SourceKind);
            Assert.Equal("dbo.saved", Assert.Single(restored.Parse.Declarations).Name);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
