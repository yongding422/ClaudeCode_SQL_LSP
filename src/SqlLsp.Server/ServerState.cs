using System.Collections.Immutable;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using SqlLsp.Core;
using SqlLsp.Providers.SqlServer;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace SqlLsp.Server;

public sealed class ServerState : IAsyncDisposable
{
    public DocumentStore? Documents { get; private set; }
    public string? ConfigurationError { get; private set; }
    public WorkspaceFiles? Files { get; private set; }
    public ResolutionOptions Resolution { get; private set; } = new();
    private WorkspaceWatcher? watcher;
    private ServerConfiguration? configuration;
    private CatalogCache? catalog;
    private CatalogDocuments? catalogFiles;
    private CatalogNavigationSnapshot? database;
    private readonly SemaphoreSlim databaseGate = new(1);
    private ILanguageServerFacade? facade;
    private string? lastStatus;
    private readonly Dictionary<Uri, (DocumentSnapshot Original, DocumentSnapshot Bound)> boundDocuments = new();
    private readonly CancellationTokenSource lifetime = new();
    public bool IsReadOnlySnapshot(Uri uri) => catalogFiles?.Owns(uri) == true;

    public async Task InitializeAsync(ILanguageServerFacade server, InitializeParams request, CancellationToken token)
    {
        ServerConfiguration config;
        try { config = ServerConfiguration.Parse(request.InitializationOptions); }
        catch (RpcErrorException error)
        {
            ConfigurationError = error.Message + ". Restart the server with valid options.";
            Console.Error.WriteLine(ConfigurationError);
            return;
        }
        var parser = config.Parser;
        configuration = config;
        facade = server;
        var workspace = config.Workspace;
        workspace.Validate();
        Resolution = config.Resolution;
        Files = new WorkspaceFiles(workspace);
        Documents = new DocumentStore(new SqlParser(parser, Resolution), Files, workspace);
        Documents.Fault += message => Console.Error.WriteLine(message);
        Documents.Diagnostics += publication => server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = publication.Uri.AbsoluteUri,
            Version = publication.Version,
            Diagnostics = new Container<Diagnostic>(publication.Diagnostics.Select(diagnostic => new Diagnostic
            {
                Code = diagnostic.Code,
                Source = "SqlScriptDOM",
                Severity = DiagnosticSeverity.Error,
                Message = diagnostic.Message,
                Range = ToRange(diagnostic.Range)
            }))
        });
        var roots = request.WorkspaceFolders?.Select(folder => new Uri(folder.Uri.ToString())).ToArray();
        roots ??= request.RootUri is null ? [] : [new Uri(request.RootUri.ToString())];
        var scan = config.Mode == "offline" || config.ScanInOnline;
        await Documents.InitializeAsync(scan ? roots : [], token);
        if (scan) watcher = new WorkspaceWatcher(Files, Documents, message => Console.Error.WriteLine(message));
        if (config.DatabaseProfile is { } profile)
        {
            catalog = new CatalogCache(profile, new SqlServerCatalogReader());
            catalogFiles = new CatalogDocuments();
        }
    }

    public async Task<SqlNavigationIndex> NavigationAsync(CancellationToken token)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = request.Token;
        var documents = Documents == null ? [] : await Documents.GetIndexAsync(token);
        if (catalog == null || configuration?.DatabaseProfile is not { } profile)
            return new(documents, uri => Files?.ContextId(uri) ?? uri.AbsoluteUri, Resolution);
        await databaseGate.WaitAsync(token);
        try
        {
            try
            {
                var snapshot = await catalog.GetAsync(token);
                if (!ReferenceEquals(snapshot, database?.Catalog))
                {
                    database = await catalogFiles!.BuildAsync(snapshot, configuration.Parser, token);
                    boundDocuments.Clear();
                }
                var status = $"Database profile {profile.Id}: {snapshot.Status}; captured {snapshot.CapturedAt:O}; visible objects={snapshot.Objects.Length}; " +
                    $"collation={snapshot.Collation}; server version={snapshot.ServerVersion}; coverage={string.Join(',', snapshot.Coverage)}. Catalog dependencies are incomplete; reference locations use parsed ASTs.";
                ReportStatus(status);
                var resolution = Resolution with
                {
                    DefaultDatabase = snapshot.Database,
                    IdentifierComparer = snapshot.Comparer,
                    DefaultSchema = Resolution.DefaultSchema ?? snapshot.DefaultSchema,
                    FallbackSchema = "dbo"
                };
                // Buffers may have changed while metadata was loading. Bind the completed current snapshots.
                documents = Documents == null ? [] : await Documents.GetIndexAsync(token);
                foreach (var removed in boundDocuments.Keys.Except(documents.Select(d => d.Uri)).ToArray()) boundDocuments.Remove(removed);
                var bound = ImmutableArray.CreateBuilder<DocumentSnapshot>();
                foreach (var document in documents)
                {
                    if (!boundDocuments.TryGetValue(document.Uri, out var entry) || !ReferenceEquals(document, entry.Original))
                    {
                        entry = (document, document with { Parse = SqlParser.Rebind(document.Parse, resolution, token) });
                        boundDocuments[document.Uri] = entry;
                    }
                    bound.Add(entry.Bound);
                }
                documents = bound.ToImmutable();
                return new(documents.AddRange(database.Documents), _ => profile.Id, resolution,
                    database.Symbols, configuration.ScanInOnline);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is CatalogReadException or OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                // Never log provider exception text; arbitrary connection errors may contain credentials.
                database = null;
                boundDocuments.Clear();
                var code = error is CatalogReadException known ? known.Code : error is OperationCanceledException ? "timeout" : "snapshot-storage-unavailable";
                ReportStatus($"Database profile {profile.Id}: Unavailable ({code}); stale catalog results are disabled.");
                return new(documents, _ => profile.Id, Resolution, indexDeclarations: configuration.ScanInOnline);
            }
        }
        finally { databaseGate.Release(); }
    }

    public async Task<ImmutableArray<SqlDeclaration>> DeclarationsAsync(Uri uri, CancellationToken token)
    {
        if (IsReadOnlySnapshot(uri))
            return (await NavigationAsync(token)).Symbols.Where(s => s.Target.Uri == uri).Select(s => s.Declaration).ToImmutableArray();
        var document = Documents == null ? null : await Documents.GetAsync(uri, token);
        return document?.Parse.Declarations ?? [];
    }

    private void ReportStatus(string status)
    {
        if (lastStatus == status) return;
        lastStatus = status;
        facade?.Window.LogMessage(new LogMessageParams { Type = MessageType.Info, Message = status });
    }

    public static Range ToRange(TextRange range) => new(
        new Position(range.Start.Line, range.Start.Character), new Position(range.End.Line, range.End.Character));

    public static SymbolKind ToKind(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.Procedure or SqlObjectKind.Function => SymbolKind.Function,
        SqlObjectKind.Table => SymbolKind.Class,
        SqlObjectKind.View => SymbolKind.Interface,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (watcher != null) { await watcher.DisposeAsync(); watcher = null; }
        if (Documents != null) await Documents.DisposeAsync();
        await databaseGate.WaitAsync();
        if (catalog != null) await catalog.DisposeAsync();
        try { catalogFiles?.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Private catalog snapshot cleanup could not complete."); }
        databaseGate.Dispose();
        lifetime.Dispose();
    }
}
