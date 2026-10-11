using System.Threading.Channels;

namespace SqlLsp.Core.Tests;

public class FileRefreshTests
{
    [Fact]
    public void Untitled_buffers_have_explicit_independent_contexts()
    {
        var files = new WorkspaceFiles(new());
        Assert.Equal("untitled:one.sql", files.ContextId(new Uri("untitled:one.sql")));
        Assert.NotEqual(files.ContextId(new Uri("untitled:one.sql")), files.ContextId(new Uri("untitled:two.sql")));
    }
    [Fact]
    public async Task Older_disk_parse_cannot_overwrite_newer_refresh_or_an_open_overlay()
    {
        using var fixture = new SqlWorkspaceFixture();
        var path = fixture.Write("renamable file.sql", "CREATE TABLE dbo.old (id int);");
        var files = new WorkspaceFiles(new());
        files.Configure([new Uri(fixture.Root)]);
        var parser = new DelayedParser();
        await using var store = new DocumentStore(parser, files, new());
        var uri = new Uri(path);
        var firstJob = store.RefreshAsync(uri, CancellationToken.None);
        var first = await parser.NextAsync();
        fixture.Write("renamable file.sql", "CREATE TABLE dbo.newer (id int);");
        var secondJob = store.RefreshAsync(uri, CancellationToken.None);
        var second = await parser.NextAsync();
        await second.FinishAsync();
        await secondJob;
        await first.FinishAsync();
        await firstJob;
        Assert.Equal("dbo.newer", Assert.Single((await store.GetAsync(uri, CancellationToken.None))!.Parse.Declarations).Name);
        store.Open(uri, 1, "CREATE TABLE dbo.overlay (id int);");
        var overlay = await parser.NextAsync();
        await overlay.FinishAsync();
        await store.GetAsync(uri, CancellationToken.None);
        File.Delete(path);
        await store.RefreshAsync(uri, CancellationToken.None);
        Assert.Equal("dbo.overlay", Assert.Single((await store.GetIndexAsync(CancellationToken.None))[0].Parse.Declarations).Name);
        await store.CloseAsync(uri, CancellationToken.None);
        Assert.Empty(await store.GetIndexAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Refresh_create_change_delete_and_rename_preserve_URI_anchors()
    {
        using var fixture = new SqlWorkspaceFixture();
        var options = new WorkspaceOptions();
        var files = new WorkspaceFiles(options);
        await using var store = new DocumentStore(new SqlParser(new()), files, options);
        await store.InitializeAsync([new Uri(fixture.Root)], CancellationToken.None);
        var oldPath = fixture.Write("old name 中文.sql", "CREATE TABLE dbo.created (id int);");
        var oldUri = new Uri(oldPath);
        await store.RefreshAsync(oldUri, CancellationToken.None);
        Assert.Single(await store.GetIndexAsync(CancellationToken.None));
        fixture.Write("old name 中文.sql", "CREATE TABLE dbo.changed (id int);");
        await store.RefreshAsync(oldUri, CancellationToken.None);
        Assert.Equal("dbo.changed", store.Index[0].Parse.Declarations[0].Name);
        var nextPath = Path.Combine(fixture.Root, "new name 中文.sql");
        File.Move(oldPath, nextPath);
        await store.RescanAsync(CancellationToken.None);
        Assert.Null(await store.GetAsync(oldUri, CancellationToken.None));
        Assert.Equal(new Uri(nextPath), Assert.Single(store.Index).Uri);
        File.Delete(nextPath);
        await store.RefreshAsync(new Uri(nextPath), CancellationToken.None);
        Assert.Empty(store.Index);
    }

    [Fact]
    public async Task Multi_root_contexts_are_separate_even_for_identical_object_names()
    {
        using var first = new SqlWorkspaceFixture();
        using var second = new SqlWorkspaceFixture();
        first.Write("declaration.sql", "CREATE TABLE dbo.t (id int);");
        var usePath = first.Write("use.sql", "SELECT * FROM dbo.t;");
        second.Write("declaration.sql", "CREATE TABLE dbo.t (id int);");
        var files = new WorkspaceFiles(new());
        await using var store = new DocumentStore(new SqlParser(new()), files, new());
        await store.InitializeAsync([new Uri(first.Root), new Uri(second.Root)], CancellationToken.None);
        var index = new SqlNavigationIndex(await store.GetIndexAsync(CancellationToken.None), files.ContextId, new());
        var used = (await store.GetAsync(new Uri(usePath), CancellationToken.None))!;
        var target = index.Resolve(used, Assert.Single(used.Parse.Semantics.References));
        Assert.Equal(ResolutionStatus.Resolved, target.Status);
        Assert.Equal(new Uri(first.Root).AbsoluteUri, target.Candidates[0].Identity!.SourceContextId);
    }

    private sealed class DelayedParser : ISqlParser
    {
        private readonly Channel<Pending> pending = Channel.CreateUnbounded<Pending>();
        public async Task<SqlParseResult> ParseAsync(SourceText source, CancellationToken cancellationToken)
        {
            var parse = new Pending(source);
            await pending.Writer.WriteAsync(parse);
            return await parse.Completion.Task; // Ignore cancellation to verify generation protection.
        }
        public async Task<Pending> NextAsync() => await pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private sealed class Pending(SourceText source)
    {
        public TaskCompletionSource<SqlParseResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task FinishAsync() => Completion.SetResult(await new SqlParser(new()).ParseAsync(source, CancellationToken.None));
    }
}

internal sealed class SqlWorkspaceFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sql-lsp-m2-" + Guid.NewGuid().ToString("N"));
    public SqlWorkspaceFixture() => Directory.CreateDirectory(Root);
    public string Write(string name, string sql)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sql);
        return path;
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
