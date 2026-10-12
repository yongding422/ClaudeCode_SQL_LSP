using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using SqlLsp.Core;

namespace SqlLsp.Server;

public sealed class SqlTextSyncHandler(ServerState state) : TextDocumentSyncHandlerBase
{
    public override Task<Unit> Handle(DidOpenTextDocumentParams notification, CancellationToken token)
    {
        var document = notification.TextDocument;
        if (state.IsReadOnlySnapshot(new Uri(document.Uri.ToString()))) return Unit.Task;
        state.Documents?.Open(new Uri(document.Uri.ToString()), document.Version ?? throw new ArgumentException("didOpen requires version."), document.Text);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams notification, CancellationToken token)
    {
        if (state.IsReadOnlySnapshot(new Uri(notification.TextDocument.Uri.ToString()))) return Unit.Task;
        var edits = notification.ContentChanges.Select(change => new SqlLsp.Core.TextEdit(change.Text,
            change.Range == null ? null : new TextRange(
                new TextPosition(change.Range.Start.Line, change.Range.Start.Character),
                new TextPosition(change.Range.End.Line, change.Range.End.Character))));
        state.Documents?.Change(new Uri(notification.TextDocument.Uri.ToString()), notification.TextDocument.Version ?? throw new ArgumentException("didChange requires version."), edits);
        return Unit.Task;
    }

    public override async Task<Unit> Handle(DidCloseTextDocumentParams notification, CancellationToken token)
    {
        if (state.IsReadOnlySnapshot(new Uri(notification.TextDocument.Uri.ToString()))) return Unit.Value;
        if (state.Documents is { } documents) await documents.CloseAsync(new Uri(notification.TextDocument.Uri.ToString()), token);
        return Unit.Value;
    }

    // The open buffer remains authoritative; close re-reads the saved file.
    public override Task<Unit> Handle(DidSaveTextDocumentParams notification, CancellationToken token) => Unit.Task;

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri) => new(uri, "sql");

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities) => new()
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("sql"),
            Change = TextDocumentSyncKind.Incremental,
            Save = new SaveOptions { IncludeText = false }
        };
}
