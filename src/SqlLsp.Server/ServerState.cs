using System.Collections.Concurrent;
using System.Collections.Immutable;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using SqlLsp.Core;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace SqlLsp.Server;

public sealed class ServerState : IAsyncDisposable
{
    public DocumentStore? Documents { get; private set; }
    public string? ConfigurationError { get; private set; }
    public WorkspaceFiles? Files { get; private set; }
    public ResolutionOptions Resolution { get; private set; } = new();
    public ImmutableArray<string> SourceStatuses => sourceStatuses.Values.ToImmutableArray();
    private readonly ConcurrentDictionary<string, string> sourceStatuses = new(StringComparer.Ordinal);
    private WorkspaceWatcher? watcher;
    private ServerConfiguration? configuration;
    private readonly Dictionary<string, SqlDatabaseSourceSession> databases = new(StringComparer.Ordinal);
    private ImmutableArray<SqlWorkspaceMapping> mappings = [];
    private readonly SemaphoreSlim databaseGate = new(1);
    private ILanguageServerFacade? facade;
    private string? lastDriftStatus;
    private readonly Dictionary<Uri, (DocumentSnapshot Original, DocumentSnapshot Bound, ResolutionOptions Rules)> boundDocuments = new();
    private readonly CancellationTokenSource lifetime = new();
    public bool IsReadOnlySnapshot(Uri uri) => databases.Values.Any(d => d.Files?.Owns(uri) == true);

    // Called only for explicitly enabled report persistence, after NavigationAsync validates current sources.
    // No connection string values; include permissions/coverage and comparison metadata, omit capture time.
    public string AnalysisCacheCatalogStamp() => System.Text.Json.JsonSerializer.Serialize(databases.OrderBy(p => p.Key, StringComparer.Ordinal).Select(pair => new
    {
        pair.Key,
        catalog = pair.Value.Current == null ? null : new
        {
            pair.Value.Current.Catalog.Database,
            pair.Value.Current.Catalog.Collation,
            pair.Value.Current.Catalog.ServerVersion,
            pair.Value.Current.Catalog.DefaultSchema,
            pair.Value.Current.Catalog.Status,
            pair.Value.Current.Catalog.Coverage,
            objects = pair.Value.Current.Catalog.Objects.OrderBy(o => o.ObjectId).Select(o => new
            {
                o.ObjectId,
                o.Schema,
                o.Name,
                o.Kind,
                o.DefinitionTruncated,
                o.QuotedIdentifier,
                o.Columns,
                o.Parameters,
                o.Dependencies,
                bodyAvailable = o.Definition != null
            })
        }
    }));

    public void FailInitialization()
    {
        ConfigurationError = "Workspace initialization failed; details suppressed. Restart with an accessible workspace.";
        Console.Error.WriteLine(ConfigurationError);
    }

    public async Task InitializeAsync(ILanguageServerFacade server, InitializeParams request, CancellationToken token)
    {
        ServerConfiguration config;
        var roots = request.WorkspaceFolders?.Select(folder => new Uri(folder.Uri.ToString())).ToArray();
        roots ??= request.RootUri is null ? [] : [new Uri(request.RootUri.ToString())];
        try { config = ServerConfiguration.Parse(request.InitializationOptions); config.ValidateMappings(roots); }
        catch (RpcErrorException error)
        {
            ConfigurationError = error.Message + ". Restart the server with valid options.";
            Console.Error.WriteLine(ConfigurationError);
            return;
        }
        facade = server;
        await InitializeSourcesAsync(config, roots, token);
    }

    // Shared acquisition for LSP and the read-only analysis adapter. Each process owns its snapshots.
    public async Task InitializeSourcesAsync(ServerConfiguration config, IEnumerable<Uri> workspaceRoots, CancellationToken token)
    {
        if (Documents != null) throw new InvalidOperationException("Sources already initialized.");
        var roots = workspaceRoots.ToArray();
        config.ValidateMappings(roots);
        var parser = config.Parser;
        configuration = config;
        var workspace = config.Workspace;
        workspace.Validate();
        Resolution = config.Resolution;
        Files = new WorkspaceFiles(workspace);
        Documents = new DocumentStore(new SqlParser(parser, Resolution), Files, workspace);
        Documents.Fault += message => Console.Error.WriteLine(message);
        Documents.Diagnostics += publication => facade?.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
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
        var scan = config.Mode != "online" || config.ScanInOnline;
        await Documents.InitializeAsync(scan ? roots : [], token);
        if (scan) watcher = new WorkspaceWatcher(Files, Documents, message => Console.Error.WriteLine(message));
        if (config.Mode == "hybrid")
        {
            mappings = config.WorkspaceMappings.Select(m => m with
            {
                WorkspaceContextId = Files.Roots.Select(r =>
                ServerConfiguration.NormalizeRoot(new Uri(r))).First(r => new Uri(r).Equals(new Uri(m.WorkspaceContextId)))
            }).ToImmutableArray();
            foreach (var profile in config.Profiles.Where(p => mappings.Any(m => m.ProfileIds.Contains(p.Id))))
                databases.Add(profile.Id, new SqlDatabaseSourceSession(profile, Resolution));
        }
        else if (config.DatabaseProfile is { } profile) databases.Add(profile.Id, new SqlDatabaseSourceSession(profile, Resolution));
    }

    public async Task<SqlNavigationIndex> NavigationAsync(CancellationToken token)
    {
        if (ConfigurationError != null) return new([], uri => uri.AbsoluteUri, Resolution);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = request.Token;
        var documents = Documents == null ? [] : await Documents.GetIndexAsync(token);
        if (databases.Count == 0 || configuration == null)
            return new(documents, uri => Files?.ContextId(uri) ?? uri.AbsoluteUri, Resolution);
        await databaseGate.WaitAsync(token);
        try
        {
            using var refresh = CancellationTokenSource.CreateLinkedTokenSource(token);
            refresh.CancelAfter(TimeSpan.FromSeconds(30));
            using var concurrency = new SemaphoreSlim(2);
            // Consume fresh cache entries before a slow refresh can exhaust the shared budget.
            await Task.WhenAll(databases.Values.OrderByDescending(source => source.Current != null &&
                DateTimeOffset.UtcNow < source.Current.Catalog.CapturedAt.AddSeconds(source.Profile.CacheTtlSeconds)).Select(async source =>
            {
                await concurrency.WaitAsync(token);
                try { await source.LoadAsync(configuration.Parser, Resolution, refresh.Token, token, ReportStatus); }
                finally { concurrency.Release(); }
            }));
            var snapshots = databases.Values.Where(d => d.Current != null).Select(d => d.Current!).ToArray();
            var catalogDocuments = snapshots.SelectMany(s => s.Documents).ToImmutableArray();
            var symbols = snapshots.SelectMany(s => s.Symbols).ToImmutableArray();
            var databaseContexts = symbols.ToDictionary(s => s.Target.Uri, s => s.Identity.SourceContextId);
            var hybrid = configuration.Mode == "hybrid" ? new SqlHybridPolicy(databases.Values.Select(d =>
                new SqlProfileContext(d.Profile.Id, d.Profile.Database, d.Rules)).ToImmutableArray(), mappings, configuration.PreferWorkspaceDefinitions) : null;
            var online = configuration.Mode == "online" ? databases.Values.Single() : null;
            string Context(Uri uri) => databaseContexts.GetValueOrDefault(uri) ?? (online?.Profile.Id ?? WorkspaceContext(uri));
            // Buffers may have changed while metadata was loading. Bind the completed current snapshots.
            documents = Documents == null ? [] : await Documents.GetIndexAsync(token);
            foreach (var removed in boundDocuments.Keys.Except(documents.Select(d => d.Uri)).ToArray()) boundDocuments.Remove(removed);
            var bound = ImmutableArray.CreateBuilder<DocumentSnapshot>();
            foreach (var document in documents)
            {
                var rules = hybrid?.RulesFor(Context(document.Uri), Resolution) ?? online?.Rules ?? Resolution;
                if (!boundDocuments.TryGetValue(document.Uri, out var entry) || !ReferenceEquals(document, entry.Original) || entry.Rules != rules)
                {
                    entry = (document, document with { Parse = SqlParser.Rebind(document.Parse, rules, token) }, rules);
                    boundDocuments[document.Uri] = entry;
                }
                bound.Add(entry.Bound);
            }
            var index = new SqlNavigationIndex(bound.ToImmutable().AddRange(catalogDocuments), Context, online?.Rules ?? Resolution,
                symbols, configuration.Mode == "hybrid" || configuration.ScanInOnline, hybrid, configuration.ReportDefinitionDrift, token);
            if (hybrid != null && configuration.ReportDefinitionDrift)
            {
                var driftStatus = string.Join(';', index.Drifts.OrderBy(d => d.Local.Uri.AbsoluteUri, StringComparer.Ordinal)
                    .ThenBy(d => d.Local.Range.Start.Line).Take(100).Select(d => $"{d.Local.Identity!.Schema}.{d.Local.Identity.Name}: {d.Status}; captured {d.CapturedAt:O}")) +
                    $"; total compared={index.Drifts.Length}";
                if (lastDriftStatus != driftStatus)
                {
                    lastDriftStatus = driftStatus;
                    ReportStatus("Hybrid definition comparison: " + driftStatus.Replace('\r', ' ').Replace('\n', ' ') +
                        ". Token differences do not prove behavioral drift.");
                }
            }
            return index;
        }
        finally { databaseGate.Release(); }
    }

    public async Task<ImmutableArray<SqlDeclaration>> DeclarationsAsync(Uri uri, CancellationToken token)
    {
        if (ConfigurationError != null) return [];
        if (IsReadOnlySnapshot(uri))
            return (await NavigationAsync(token)).Symbols.Where(s => s.Target.Uri == uri).Select(s => s.Declaration).ToImmutableArray();
        var document = Documents == null ? null : await Documents.GetAsync(uri, token);
        return document?.Parse.Declarations ?? [];
    }

    private string WorkspaceContext(Uri uri)
    {
        var context = Files?.ContextId(uri) ?? uri.AbsoluteUri;
        return Uri.TryCreate(context, UriKind.Absolute, out var root) && root.IsFile ? ServerConfiguration.NormalizeRoot(root) : context;
    }

    private void ReportStatus(string status)
    {
        // Keep the latest status per profile, so recovery does not retain an old unavailable warning.
        var key = status.Split(':', 2)[0];
        sourceStatuses[key] = status;
        if (facade != null) facade.Window.LogMessage(new LogMessageParams { Type = MessageType.Info, Message = status });
        else Console.Error.WriteLine(status);
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
        foreach (var source in databases.Values) await source.DisposeAsync();
        databaseGate.Dispose();
        lifetime.Dispose();
    }
}
