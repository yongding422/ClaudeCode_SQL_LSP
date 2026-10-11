using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace SqlLsp.Server;

public sealed class SqlWatchedFilesHandler(ServerState state) : IDidChangeWatchedFilesHandler
{
    public async Task<Unit> Handle(DidChangeWatchedFilesParams notification, CancellationToken token)
    {
        if (state.Documents is not { } documents || state.Files is not { } files) return Unit.Value;
        foreach (var change in notification.Changes)
        {
            var uri = new Uri(change.Uri.ToString());
            if (!files.CanTraverse(uri)) continue;
            if (string.Equals(Path.GetExtension(uri.LocalPath), ".sql", StringComparison.OrdinalIgnoreCase)) await documents.RefreshAsync(uri, token);
            else await documents.RescanAsync(token);
        }
        return Unit.Value;
    }

    public DidChangeWatchedFilesRegistrationOptions GetRegistrationOptions(DidChangeWatchedFilesCapability capability,
        ClientCapabilities clientCapabilities) => new()
        {
            Watchers = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher>(new[]
            {
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                { GlobPattern = new GlobPattern("**/*.sql"), Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete }
            })
        };
}
