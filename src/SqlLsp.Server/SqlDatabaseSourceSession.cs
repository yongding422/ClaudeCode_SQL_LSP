using SqlLsp.Core;
using SqlLsp.Providers.SqlServer;

namespace SqlLsp.Server;

internal sealed class SqlDatabaseSourceSession : IAsyncDisposable
{
    public SqlServerProfile Profile { get; }
    public CatalogDocuments? Files { get; private set; }
    public CatalogNavigationSnapshot? Current { get; private set; }
    public ResolutionOptions Rules { get; private set; }
    private readonly CatalogCache cache;
    private string? lastStatus;

    public SqlDatabaseSourceSession(SqlServerProfile profile, ResolutionOptions defaults)
    {
        Profile = profile;
        Rules = defaults with { DefaultDatabase = profile.Database, CaseSensitive = profile.CaseSensitive ?? defaults.CaseSensitive };
        cache = new CatalogCache(profile, new SqlServerCatalogReader());
    }

    public async Task LoadAsync(ParserOptions parser, ResolutionOptions defaults, CancellationToken loadToken,
        CancellationToken requestToken, Action<string> report)
    {
        string status;
        try
        {
            var snapshot = await cache.GetAsync(loadToken);
            if (!ReferenceEquals(snapshot, Current?.Catalog))
                Current = await (Files ??= new CatalogDocuments()).BuildAsync(snapshot, parser, loadToken);
            Rules = defaults with
            {
                DefaultDatabase = snapshot.Database,
                IdentifierComparer = snapshot.Comparer,
                DefaultSchema = defaults.DefaultSchema ?? snapshot.DefaultSchema,
                FallbackSchema = "dbo"
            };
            status = $"Database profile {Profile.Id}: {snapshot.Status}; captured {snapshot.CapturedAt:O}; visible objects={snapshot.Objects.Length}; " +
                $"collation={snapshot.Collation}; server version={snapshot.ServerVersion}; coverage={string.Join(',', snapshot.Coverage)}. Catalog dependencies are incomplete; reference locations use parsed ASTs.";
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is CatalogReadException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            Current = null;
            var code = error is CatalogReadException known ? known.Code : error is OperationCanceledException ? "timeout" : "snapshot-storage-unavailable";
            status = $"Database profile {Profile.Id}: Unavailable ({code}); stale catalog results are disabled; local sources remain available.";
        }
        if (lastStatus != status) { lastStatus = status; report(status); }
    }

    public async ValueTask DisposeAsync()
    {
        await cache.DisposeAsync();
        try { Files?.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Private catalog snapshot cleanup could not complete."); }
    }
}
