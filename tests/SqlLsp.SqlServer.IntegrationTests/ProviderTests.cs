using System.Collections.Immutable;
using SqlLsp.Core;
using SqlLsp.Providers.SqlServer;

namespace SqlLsp.SqlServer.IntegrationTests;

public class ProviderTests
{
    internal static SqlServerProfile Profile => new("fixture", "SQLLSP_FIXTURE", "test", "localhost", "Fixture", Enabled: true);
    internal static CatalogSnapshot Snapshot => new("fixture", "Fixture", "Latin1_General_100_CI_AS", "16.0",
        DateTimeOffset.UtcNow, StringComparer.OrdinalIgnoreCase,
        [new(1, "dbo", "CurrentName", SqlObjectKind.Procedure, "CREATE PROCEDURE dbo.OldName AS SELECT * FROM dbo.Payroll;", false, true, [], [], []),
         new(2, "dbo", "Payroll", SqlObjectKind.Table, null, false, true,
            [new(1, "PayrollId", "sys", "int", 4, 10, 0, false)], [], []),
         new(3, "dbo", "Hidden", SqlObjectKind.Procedure, null, false, true, [], [new(1,"@id","sys","int",4,10,0,false)], [])],
        CatalogStatus.Partial, ["module-source-unavailable"]);

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(0, false, false)]
    [InlineData(3, true, true)]
    public void Sql_collation_case_and_accent_flags_are_applied(int style, bool equalCase, bool equalAccent)
    {
        var comparer = new CatalogIdentifierComparer(1033, style, "Latin1_General_100");
        Assert.Equal(equalCase, comparer.Equals("payroll", "Payroll"));
        Assert.Equal(equalAccent, comparer.Equals("resume", "résumé"));
        if (equalCase) Assert.Equal(comparer.GetHashCode("payroll"), comparer.GetHashCode("Payroll"));
    }

    [Fact]
    public async Task Database_documents_resolve_in_the_shared_index_without_fabricating_hidden_module_ddl()
    {
        using var storage = new CatalogDocuments();
        var materialized = await storage.BuildAsync(Snapshot, new(), CancellationToken.None);
        var parser = new SqlParser(new());
        var query = new DocumentSnapshot(new Uri("file:///query.sql"), 1, SqlSourceKind.UnsavedBuffer,
            await parser.ParseAsync(new("EXEC dbo.currentname; SELECT * FROM dbo.Payroll;"), CancellationToken.None));
        var index = new SqlNavigationIndex(materialized.Documents.Add(query), _ => "fixture",
            new(DefaultDatabase: "Fixture", IdentifierComparer: Snapshot.Comparer), materialized.Symbols, indexDeclarations: false);
        var result = index.Definition(query.Uri, new(0, 12), CancellationToken.None);
        Assert.Equal(ResolutionStatus.Resolved, result.Status);
        Assert.Equal("CurrentName", result.Candidates[0].Identity!.Name);
        Assert.Equal(SqlSourceKind.Database, result.Candidates[0].SourceKind);
        var target = result.Candidates[0];
        var parsed = materialized.Documents.Single(d => d.Uri == target.Uri);
        Assert.Equal("dbo.OldName", parsed.Parse.Declarations.Single().Name);
        Assert.Single(index.References(target.Uri, target.Range.Start, false, CancellationToken.None));
        var hidden = materialized.Symbols.Single(s => s.Identity.Name == "Hidden");
        var text = await File.ReadAllTextAsync(hidden.Target.Uri.LocalPath);
        Assert.Contains("original DDL", text);
        Assert.DoesNotContain("CREATE PROCEDURE", text);
        Assert.Contains("@id", text);
        Assert.True(storage.Owns(hidden.Target.Uri));
        Assert.False(storage.Owns(new Uri(Path.Combine(storage.Root + "-outside", "file.sql"))));
        if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(hidden.Target.Uri.LocalPath).HasFlag(FileAttributes.ReadOnly));
        else Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(hidden.Target.Uri.LocalPath));
    }

    [Fact]
    public async Task Metadata_snapshots_are_private_and_removed_at_shutdown()
    {
        var storage = new CatalogDocuments();
        var root = storage.Root;
        var result = await storage.BuildAsync(Snapshot, new(), CancellationToken.None);
        Assert.All(result.Symbols, s => Assert.True(File.Exists(s.Target.Uri.LocalPath)));
        storage.Dispose();
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Cache_reuses_a_snapshot_and_retries_only_transient_catalog_reads()
    {
        var calls = 0;
        var reader = new FakeReader((_, _) => ++calls == 1 ? throw new CatalogReadException("timeout", transient: true) : Task.FromResult(Snapshot));
        await using var cache = new CatalogCache(Profile, reader);
        var first = await cache.GetAsync(CancellationToken.None);
        Assert.Same(first, await cache.GetAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Expired_cache_failure_returns_no_stale_snapshot()
    {
        var clock = new TestClock();
        var calls = 0;
        var reader = new FakeReader((_, _) => ++calls == 1 ? Task.FromResult(Snapshot) : throw new CatalogReadException("authentication-failed"));
        await using var cache = new CatalogCache(Profile with { CacheTtlSeconds = 1 }, reader, clock);
        await cache.GetAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(2);
        await Assert.ThrowsAsync<CatalogReadException>(() => cache.GetAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_overlap_acquisition_or_cancel_its_owner()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var cache = new CatalogCache(Profile, new FakeReader(async (_, token) =>
        { Interlocked.Increment(ref calls); started.SetResult(); await release.Task.WaitAsync(token); return Snapshot; }));
        var owner = cache.GetAsync(CancellationToken.None);
        await started.Task;
        using var cancelled = new CancellationTokenSource();
        var waiter = cache.GetAsync(cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.SetResult();
        Assert.Same(await owner, await cache.GetAsync(CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Load_deadline_cancels_provider_and_cancelled_results_are_never_cached()
    {
        var calls = 0;
        var reader = new FakeReader(async (_, token) =>
        {
            if (++calls == 1) await Task.Delay(Timeout.Infinite, token);
            return Snapshot;
        });
        await using var cache = new CatalogCache(Profile with { LoadTimeoutSeconds = 1 }, reader);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(CancellationToken.None));
        Assert.NotNull(await cache.GetAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cache_shutdown_cancels_and_awaits_its_acquisition()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new CatalogCache(Profile, new FakeReader(async (_, token) =>
        { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return Snapshot; }));
        var load = cache.GetAsync(CancellationToken.None);
        await started.Task;
        await cache.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Database_default_schema_falls_back_to_dbo_and_explicit_schema_does_not()
    {
        using var storage = new CatalogDocuments();
        var materialized = await storage.BuildAsync(Snapshot, new(), CancellationToken.None);
        var query = new DocumentSnapshot(new Uri("file:///query.sql"), 1, SqlSourceKind.UnsavedBuffer,
            await new SqlParser(new()).ParseAsync(new("EXEC CurrentName; EXEC sales.CurrentName;"), CancellationToken.None));
        var index = new SqlNavigationIndex(materialized.Documents.Add(query), _ => "fixture",
            new(DefaultDatabase: "Fixture", DefaultSchema: "sales", FallbackSchema: "dbo"), materialized.Symbols, indexDeclarations: false);
        Assert.Equal(ResolutionStatus.Resolved, index.Definition(query.Uri, new(0, 6), CancellationToken.None).Status);
        Assert.Equal(ResolutionStatus.Unresolved, index.Definition(query.Uri, new(0, 30), CancellationToken.None).Status);
    }

    [Theory]
    [InlineData(false, "unused", "profile-disabled")]
    [InlineData(true, null, "missing-secret")]
    [InlineData(true, "Password=SECRET;Bogus=SECRET", "invalid-connection-settings")]
    [InlineData(true, "Server=elsewhere;Database=Fixture;Password=SECRET", "profile-endpoint-mismatch")]
    [InlineData(true, "Server=localhost;Database=Fixture;Encrypt=False;Password=SECRET", "tls-required")]
    public async Task Unsafe_or_invalid_settings_fail_before_connect_and_redact_secrets(bool enabled, string? value, string code)
    {
        var reads = 0;
        var reader = new SqlServerCatalogReader(_ => { reads++; return value; });
        var error = await Assert.ThrowsAsync<CatalogReadException>(() => reader.ReadAsync(Profile with { Enabled = enabled }, CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.Equal(enabled ? 1 : 0, reads);
    }

    [Fact]
    public async Task Provider_enforces_query_limits_even_when_used_without_the_server_schema()
    {
        var reader = new SqlServerCatalogReader(_ => throw new InvalidOperationException("Secret must not be read."));
        var error = await Assert.ThrowsAsync<CatalogReadException>(() => reader.ReadAsync(Profile with { CommandTimeoutSeconds = 0 }, CancellationToken.None));
        Assert.Equal("invalid-profile-limits", error.Code);
    }

    private sealed class FakeReader(Func<SqlServerProfile, CancellationToken, Task<CatalogSnapshot>> read) : ISqlCatalogReader
    { public Task<CatalogSnapshot> ReadAsync(SqlServerProfile profile, CancellationToken token) => read(profile, token); }
    private sealed class TestClock : TimeProvider
    { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
