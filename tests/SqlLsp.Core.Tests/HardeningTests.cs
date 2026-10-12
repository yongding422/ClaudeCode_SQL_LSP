namespace SqlLsp.Core.Tests;

public class HardeningTests
{
    [Fact]
    public async Task Links_and_roots_below_linked_ancestors_never_read_outside_files()
    {
        using var fixture = new SqlWorkspaceFixture();
        using var outside = new SqlWorkspaceFixture();
        outside.Write("nested/outside.sql", "CREATE TABLE dbo.OutsideSecret (id int);");
        var link = Path.Combine(fixture.Root, "link");
        if (OperatingSystem.IsWindows())
        {
            // Directory junctions need no symlink privilege on Windows.
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new System.Diagnostics.ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + outside.Root.Replace("'", "''") + "' -ErrorAction Stop | Out-Null" }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await errors);
        }
        else Directory.CreateSymbolicLink(link, outside.Root);
        try
        {
            var files = new WorkspaceFiles(new());
            Assert.Empty(files.Configure([new Uri(fixture.Root)]));
            Assert.False(files.CanRead(new Uri(Path.Combine(link, "nested", "outside.sql"))));
            Assert.Null(await files.ReadAsync(new Uri(Path.Combine(link, "nested", "outside.sql")), CancellationToken.None));
            Assert.Empty(files.Configure([new Uri(Path.Combine(link, "nested"))]));
            Assert.Empty(files.Roots);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task File_churn_releases_completed_jobs_and_deleted_snapshots()
    {
        using var fixture = new SqlWorkspaceFixture();
        var files = new WorkspaceFiles(new());
        await using var store = new DocumentStore(new SqlParser(new()), files, new());
        await store.InitializeAsync([new Uri(fixture.Root)], CancellationToken.None);
        for (var i = 0; i < 200; i++)
        {
            var path = fixture.Write($"temporary{i}.sql", $"CREATE TABLE dbo.T{i} (id int);");
            var uri = new Uri(path);
            await store.RefreshAsync(uri, CancellationToken.None);
            Assert.NotNull(await store.GetAsync(uri, CancellationToken.None));
            File.Delete(path);
            await store.RefreshAsync(uri, CancellationToken.None);
            Assert.Null(await store.GetAsync(uri, CancellationToken.None));
        }
        await store.RescanAsync(CancellationToken.None);
        Assert.Empty(await store.GetIndexAsync(CancellationToken.None));
        Assert.Equal(0, store.PendingDiskReadCount);
    }

    [Fact]
    public async Task Bounded_disk_reader_preserves_utf8_bom_unicode_and_rejects_oversized_files()
    {
        using var fixture = new SqlWorkspaceFixture();
        const string sql = "CREATE TABLE [dbo].[员工😀] (id int);";
        var path = fixture.Write("unicode.sql", sql);
        await File.WriteAllTextAsync(path, sql, new System.Text.UTF8Encoding(true));
        var files = new WorkspaceFiles(new(128));
        files.Configure([new Uri(fixture.Root)]);
        Assert.Equal(sql, await files.ReadAsync(new Uri(path), CancellationToken.None));
        await File.WriteAllTextAsync(path, new string('x', 129));
        Assert.Null(await files.ReadAsync(new Uri(path), CancellationToken.None));
    }
}
