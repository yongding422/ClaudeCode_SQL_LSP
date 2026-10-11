using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using SqlLsp.Core;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace SqlLsp.Server;

public sealed class ServerState : IAsyncDisposable
{
    public DocumentStore? Documents { get; private set; }
    public string? ConfigurationError { get; private set; }

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
        var workspace = config.Workspace;
        workspace.Validate();
        Documents = new DocumentStore(new SqlParser(parser), new WorkspaceFiles(workspace), workspace);
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
        await Documents.InitializeAsync(roots, token);
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
        if (Documents != null) await Documents.DisposeAsync();
    }
}
