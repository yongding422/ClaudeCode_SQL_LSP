using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SqlLsp.Core;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace SqlLsp.Server;

public sealed class SqlCallHierarchyHandler(ServerState state) : CallHierarchyHandlerBase
{
    public override async Task<Container<CallHierarchyItem>?> Handle(CallHierarchyPrepareParams request, CancellationToken token)
    {
        var navigation = await state.NavigationAsync(token);
        var node = navigation.PrepareCallHierarchy(new Uri(request.TextDocument.Uri.ToString()),
            new(request.Position.Line, request.Position.Character), token);
        return new Container<CallHierarchyItem>(node == null ? [] : new[] { Item(node) });
    }

    public override async Task<Container<CallHierarchyOutgoingCall>?> Handle(CallHierarchyOutgoingCallsParams request, CancellationToken token)
    {
        var navigation = await state.NavigationAsync(token);
        var node = Validate(navigation, request.Item, token);
        return new Container<CallHierarchyOutgoingCall>(node == null ? [] : navigation.OutgoingCalls(node, token)
            .Select(edge => new CallHierarchyOutgoingCall { To = Item(edge.Node), FromRanges = new Container<Range>(edge.FromRanges.Select(ServerState.ToRange)) }));
    }

    public override async Task<Container<CallHierarchyIncomingCall>?> Handle(CallHierarchyIncomingCallsParams request, CancellationToken token)
    {
        var navigation = await state.NavigationAsync(token);
        var node = Validate(navigation, request.Item, token);
        return new Container<CallHierarchyIncomingCall>(node == null ? [] : navigation.IncomingCalls(node, token)
            .Select(edge => new CallHierarchyIncomingCall { From = Item(edge.Node), FromRanges = new Container<Range>(edge.FromRanges.Select(ServerState.ToRange)) }));
    }

    protected override CallHierarchyRegistrationOptions CreateRegistrationOptions(CallHierarchyCapability capability, ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSelector.ForLanguage("sql") };

    private static CallHierarchyItem Item(SqlCallHierarchyNode node) => new()
    {
        Name = SqlSymbolDisplay.OneLine(node.Symbol.Declaration.Name),
        Kind = SymbolKind.Function,
        Uri = node.Symbol.Target.Uri.AbsoluteUri,
        Range = ServerState.ToRange(node.Symbol.Declaration.Range),
        SelectionRange = ServerState.ToRange(node.Symbol.Target.Range),
        Detail = node.Symbol.Target.SourceKind + "; partial static calls only; " +
            (node.Coverage.BodyAvailable ? $"resolved {node.Coverage.Resolved}, ambiguous {node.Coverage.Ambiguous}, missing {node.Coverage.Unresolved}, unsupported {node.Coverage.Unsupported}" : "original module body unavailable"),
        Data = JObject.FromObject(new { revision = node.Revision, identity = node.Symbol.Identity, coverage = node.Coverage })
    };

    private static SqlCallHierarchyNode? Validate(SqlNavigationIndex navigation, CallHierarchyItem? item, CancellationToken token)
    {
        if (item?.Data is not JObject data || data["revision"]?.Type != JTokenType.String ||
            data["identity"] is not JObject identity || item.Kind != SymbolKind.Function ||
            item.Uri is null || item.Range?.Start == null || item.Range.End == null ||
            item.SelectionRange?.Start == null || item.SelectionRange.End == null) return null;
        try
        {
            var id = identity.ToObject<SqlObjectIdentity>();
            return id == null ? null : navigation.ValidateCallHierarchy(new Uri(item.Uri.ToString()), CoreRange(item.SelectionRange),
                CoreRange(item.Range), id, data["revision"]!.Value<string>()!, token);
        }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
        catch (UriFormatException) { return null; }
    }

    private static TextRange CoreRange(Range range) => new(new(range.Start.Line, range.Start.Character), new(range.End.Line, range.End.Character));
}
