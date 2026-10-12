using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace SqlLsp.Server;

public sealed class SqlDocumentSymbolHandler(ServerState state) : IDocumentSymbolHandler
{
    private bool hierarchical;

    public async Task<SymbolInformationOrDocumentSymbolContainer?> Handle(DocumentSymbolParams request, CancellationToken token)
    {
        var declarations = await state.DeclarationsAsync(new Uri(request.TextDocument.Uri.ToString()), token);
        var symbols = new List<SymbolInformationOrDocumentSymbol>();
        foreach (var declaration in declarations)
        {
            token.ThrowIfCancellationRequested();
            if (hierarchical)
                symbols.Add(new DocumentSymbol
                {
                    Name = declaration.Name,
                    Detail = declaration.Kind.ToString(),
                    Kind = ServerState.ToKind(declaration.Kind),
                    Range = ServerState.ToRange(declaration.Range),
                    SelectionRange = ServerState.ToRange(declaration.SelectionRange)
                });
            else
                symbols.Add(new SymbolInformation
                {
                    Name = declaration.Name,
                    Kind = ServerState.ToKind(declaration.Kind),
                    Location = new Location { Uri = request.TextDocument.Uri, Range = ServerState.ToRange(declaration.Range) }
                });
        }
        return new SymbolInformationOrDocumentSymbolContainer(symbols);
    }

    public DocumentSymbolRegistrationOptions GetRegistrationOptions(DocumentSymbolCapability capability, ClientCapabilities clientCapabilities)
    {
        hierarchical = capability.HierarchicalDocumentSymbolSupport == true;
        return new DocumentSymbolRegistrationOptions { DocumentSelector = TextDocumentSelector.ForLanguage("sql") };
    }
}
