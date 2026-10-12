namespace SqlLsp.Providers.SqlServer;

// One bounded acquisition per cache; waiters honor cancellation independently. No stale fallback.
public sealed class CatalogCache(SqlServerProfile profile, ISqlCatalogReader reader, TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private CatalogSnapshot? snapshot;
    private DateTimeOffset expires;
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;

    public async Task<CatalogSnapshot> GetAsync(CancellationToken token)
    {
        profile.Validate();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await gate.WaitAsync(request.Token);
        try
        {
            if (snapshot != null && clock.GetUtcNow() < expires) return snapshot;
            snapshot = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(profile.LoadTimeoutSeconds));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var loaded = await reader.ReadAsync(profile, deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    snapshot = loaded;
                    expires = clock.GetUtcNow().AddSeconds(profile.CacheTtlSeconds);
                    return snapshot;
                }
                catch (CatalogReadException error) when (error.Transient && attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token);
                }
            }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync();
        await gate.WaitAsync();
        gate.Dispose();
        lifetime.Dispose();
    }
}
