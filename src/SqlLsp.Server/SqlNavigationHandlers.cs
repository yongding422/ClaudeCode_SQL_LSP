using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using SqlLsp.Core;

namespace SqlLsp.Server;

public sealed class SqlDefinitionHandler(ServerState state) : IDefinitionHandler
{
    public async Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken token)
    {
        var navigation = await state.NavigationAsync(token);
        var result = navigation.Definition(new Uri(request.TextDocument.Uri.ToString()),
            new TextPosition(request.Position.Line, request.Position.Character), token);
        return new LocationOrLocationLinks(result.Status == ResolutionStatus.Resolved
            ? result.Candidates.Select(t => new LocationOrLocationLink(SqlLocations.From(t))) : []);
    }

    public DefinitionRegistrationOptions GetRegistrationOptions(DefinitionCapability capability, ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSelector.ForLanguage("sql") };
}

public sealed class SqlReferencesHandler(ServerState state) : IReferencesHandler
{
    public async Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken token)
    {
        var navigation = await state.NavigationAsync(token);
        return new LocationContainer(navigation.References(new Uri(request.TextDocument.Uri.ToString()),
            new TextPosition(request.Position.Line, request.Position.Character), request.Context.IncludeDeclaration, token).Select(SqlLocations.From));
    }

    public ReferenceRegistrationOptions GetRegistrationOptions(ReferenceCapability capability, ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSelector.ForLanguage("sql") };
}

public sealed class SqlWorkspaceSymbolsHandler(ServerState state) : IWorkspaceSymbolsHandler
{
    public async Task<Container<WorkspaceSymbol>?> Handle(WorkspaceSymbolParams request, CancellationToken token)
    {
        var navigation = await state.NavigationAsync(token);
        return new Container<WorkspaceSymbol>(navigation.Search(request.Query, token).Select(symbol => new WorkspaceSymbol
        {
            Name = symbol.Declaration.Name,
            Kind = ServerState.ToKind(symbol.Identity.Kind),
            ContainerName = symbol.Identity.SourceContextId + (symbol.Identity.Database == null ? "" : " | " + symbol.Identity.Database),
            Location = SqlLocations.From(symbol.Target)
        }));
    }

    public WorkspaceSymbolRegistrationOptions GetRegistrationOptions(WorkspaceSymbolCapability capability, ClientCapabilities clientCapabilities) => new();
}

internal static class SqlLocations
{
    public static Location From(SqlNavigationTarget target) => new()
    {
        Uri = target.Uri.AbsoluteUri,
        Range = ServerState.ToRange(target.Range)
    };
}
