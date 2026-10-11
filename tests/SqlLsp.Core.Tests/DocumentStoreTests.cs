using System.Collections.Concurrent;

namespace SqlLsp.Core.Tests;

public class DocumentStoreTests
{
    private static readonly Uri Uri = new("file:///sql-lsp-tests/buffer.sql");

    [Fact]
    public async Task Older_work_finishing_last_cannot_replace_index_or_publish_stale_diagnostics()
    {
        var parser = new ControlledParser();
        var options = new WorkspaceOptions();
        await using var store = new DocumentStore(parser, new WorkspaceFiles(options), options);
        var publications = new ConcurrentQueue<DiagnosticPublication>();
        store.Diagnostics += publications.Enqueue;
        store.Open(Uri, 1, "CREATE PROC dbo.old AS SELECT 1;");
        var old = await parser.NextAsync();
        store.Change(Uri, 2, [new("CREATE PROC dbo.updated AS SELECT 2;")]);
        var current = await parser.NextAsync();
        await current.FinishAsync();
        var snapshot = await store.GetAsync(Uri, CancellationToken.None);
        await old.FinishAsync(); // Deliberately ignores cancellation: identity checks must still protect the index.
        await store.DisposeAsync(); // Drain both jobs before asserting the final index/publication history.
        Assert.Equal("dbo.updated", Assert.Single(snapshot!.Parse.Declarations).Name);
        Assert.Equal("dbo.updated", Assert.Single((await store.GetAsync(Uri, CancellationToken.None))!.Parse.Declarations).Name);
        Assert.False(store.Change(Uri, 1, [new("SELECT FROM;")]));
        Assert.All(publications, p => Assert.Equal(2, p.Version));
        Assert.Single(store.Index);
    }

    [Fact]
    public async Task Close_reopen_cannot_resurrect_previous_overlay()
    {
        var parser = new ControlledParser();
        var options = new WorkspaceOptions();
        await using var store = new DocumentStore(parser, new WorkspaceFiles(options), options);
        store.Open(Uri, 100, "CREATE TABLE dbo.old (id int);");
        var old = await parser.NextAsync();
        await store.CloseAsync(Uri, CancellationToken.None);
        store.Open(Uri, 1, "CREATE TABLE dbo.new (id int);");
        var next = await parser.NextAsync();
        await next.FinishAsync();
        await old.FinishAsync();
        await store.GetAsync(Uri, CancellationToken.None);
        await store.DisposeAsync();
        Assert.Equal("dbo.new", Assert.Single((await store.GetAsync(Uri, CancellationToken.None))!.Parse.Declarations).Name);
    }

    [Fact]
    public async Task Invalid_edit_is_rejected_without_changing_version_or_text()
    {
        var options = new WorkspaceOptions();
        await using var store = new DocumentStore(new SqlParser(new()), new WorkspaceFiles(options), options);
        store.Open(Uri, 1, "SELECT 1;");
        await store.GetAsync(Uri, CancellationToken.None);
        Assert.False(store.Change(Uri, 2, [new("x", new TextRange(new(10, 0), new(10, 1)))]));
        var snapshot = await store.GetAsync(Uri, CancellationToken.None);
        Assert.Equal(1, snapshot!.Version);
        Assert.Equal("SELECT 1;", snapshot.Parse.Source.Text);
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_wait_without_canceling_document_analysis()
    {
        var parser = new ControlledParser();
        var options = new WorkspaceOptions();
        await using var store = new DocumentStore(parser, new WorkspaceFiles(options), options);
        store.Open(Uri, 1, "SELECT 1;");
        var pending = await parser.NextAsync();
        using var cancellation = new CancellationTokenSource();
        var read = store.GetAsync(Uri, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await pending.FinishAsync();
        Assert.NotNull(await store.GetAsync(Uri, CancellationToken.None));
    }

    private sealed class ControlledParser : ISqlParser
    {
        private readonly System.Threading.Channels.Channel<PendingParse> pending = System.Threading.Channels.Channel.CreateUnbounded<PendingParse>();
        public async Task<SqlParseResult> ParseAsync(SourceText source, CancellationToken cancellationToken)
        {
            var item = new PendingParse(source);
            await pending.Writer.WriteAsync(item);
            return await item.Completion.Task;
        }
        public async Task<PendingParse> NextAsync() => await pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class PendingParse(SourceText source)
    {
        public TaskCompletionSource<SqlParseResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task FinishAsync() => Completion.SetResult(await new SqlParser(new()).ParseAsync(source, CancellationToken.None));
    }
}
