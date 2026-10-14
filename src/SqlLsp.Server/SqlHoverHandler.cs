using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SqlLsp.Core;

namespace SqlLsp.Server;

public sealed class SqlHoverHandler(ServerState state) : IHoverHandler
{
    private MarkupKind format = MarkupKind.PlainText;

    public async Task<Hover?> Handle(HoverParams request, CancellationToken token)
    {
        var navigation = await state.NavigationAsync(token);
        var hover = navigation.Hover(new Uri(request.TextDocument.Uri.ToString()), new(request.Position.Line, request.Position.Character), token);
        if (hover == null) return null;
        return new Hover
        {
            Range = ServerState.ToRange(hover.Range),
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = format,
                Value = format == MarkupKind.Markdown ? MarkdownText(hover.Text) : hover.Text
            })
        };
    }

    private static string MarkdownText(string value)
    {
        var maximum = 0;
        var run = 0;
        foreach (var character in value)
        {
            run = character == '`' ? run + 1 : 0;
            maximum = Math.Max(maximum, run);
        }
        var fence = new string('`', Math.Max(3, maximum + 1));
        return fence + "text\n" + value + "\n" + fence;
    }

    public HoverRegistrationOptions GetRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        format = MarkupKind.PlainText;
        if (capability.ContentFormat is not null)
            foreach (var kind in capability.ContentFormat)
                if (kind == MarkupKind.PlainText || kind == MarkupKind.Markdown) { format = kind; break; }
        return new() { DocumentSelector = TextDocumentSelector.ForLanguage("sql") };
    }
}
