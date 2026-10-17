using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace SqlLsp.Core;

public sealed record SqlCallCoverage(bool BodyAvailable, int Resolved, int Ambiguous, int Unresolved, int Unsupported);
public sealed record SqlCallHierarchyNode(IndexedSqlSymbol Symbol, string Revision, SqlCallCoverage Coverage);
public sealed record SqlCallHierarchyEdge(SqlCallHierarchyNode Node, ImmutableArray<TextRange> FromRanges);

public sealed partial class SqlNavigationIndex
{
    public SqlCallHierarchyNode? PrepareCallHierarchy(Uri uri, TextPosition position, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var document = documents.FirstOrDefault(d => d.Uri == uri);
        if (document == null) return null;
        try { document.Parse.Source.OffsetAt(position); }
        catch (ArgumentException) { return null; }
        var reference = document.Parse.Semantics.References.Where(r => Contains(r.Range, position))
            .OrderBy(r => SpanLength(document, r.Range)).FirstOrDefault();
        if (reference != null)
        {
            var target = Resolve(document, reference).Target;
            var symbol = target == null ? null : Symbols.FirstOrDefault(s => SameAnchor(s.Target, target));
            return symbol == null ? null : CallNode(symbol, token);
        }
        if (document.Parse.Semantics.Locals.Any(l => Contains(l.Range, position))) return null;
        var owner = CallOwner(document, position);
        return owner == null ? null : CallNode(owner, token);
    }

    public SqlCallHierarchyNode? ValidateCallHierarchy(Uri uri, TextRange selectionRange, TextRange range,
        SqlObjectIdentity identity, string revision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var symbol = Symbols.FirstOrDefault(s => s.Target.Uri == uri && s.Target.Range == selectionRange &&
            s.Declaration.Range == range && s.Identity == identity);
        var node = symbol == null ? null : CallNode(symbol, token);
        return node?.Revision == revision ? node : null;
    }

    public ImmutableArray<SqlCallHierarchyEdge> OutgoingCalls(SqlCallHierarchyNode node, CancellationToken token)
    {
        var current = ValidateCallHierarchy(node.Symbol.Target.Uri, node.Symbol.Target.Range,
            node.Symbol.Declaration.Range, node.Symbol.Identity, node.Revision, token);
        if (current == null || !current.Coverage.BodyAvailable) return [];
        var document = documents.First(d => d.Uri == current.Symbol.Target.Uri);
        var calls = new List<(IndexedSqlSymbol Symbol, TextRange Range)>();
        foreach (var reference in OwnedInvocations(document, current.Symbol))
        {
            token.ThrowIfCancellationRequested();
            var target = Resolve(document, reference).Target;
            var symbol = target == null ? null : Symbols.FirstOrDefault(s => SameAnchor(s.Target, target));
            if (symbol != null && IsUniqueCallable(symbol)) calls.Add((symbol, reference.Range));
        }
        return CallEdges(calls, token);
    }

    public ImmutableArray<SqlCallHierarchyEdge> IncomingCalls(SqlCallHierarchyNode node, CancellationToken token)
    {
        var current = ValidateCallHierarchy(node.Symbol.Target.Uri, node.Symbol.Target.Range,
            node.Symbol.Declaration.Range, node.Symbol.Identity, node.Revision, token);
        if (current == null) return [];
        var calls = new List<(IndexedSqlSymbol Symbol, TextRange Range)>();
        foreach (var document in documents)
            foreach (var reference in document.Parse.Semantics.References.Where(r => r.IsInvocation))
            {
                token.ThrowIfCancellationRequested();
                var target = Resolve(document, reference).Target;
                if (target == null || !SameAnchor(target, current.Symbol.Target)) continue;
                var owner = CallOwner(document, reference.Range.Start);
                if (owner != null && IsUniqueCallable(owner)) calls.Add((owner, reference.Range));
            }
        return CallEdges(calls, token);
    }

    private ImmutableArray<SqlCallHierarchyEdge> CallEdges(List<(IndexedSqlSymbol Symbol, TextRange Range)> calls, CancellationToken token)
    {
        var edges = ImmutableArray.CreateBuilder<SqlCallHierarchyEdge>();
        foreach (var group in calls.GroupBy(c => (c.Symbol.Target.Uri, c.Symbol.Target.Range))
            .OrderBy(g => g.Key.Uri.AbsoluteUri, StringComparer.Ordinal).ThenBy(g => g.Key.Range.Start.Line).ThenBy(g => g.Key.Range.Start.Character))
        {
            token.ThrowIfCancellationRequested();
            var node = CallNode(group.First().Symbol, token);
            if (node != null) edges.Add(new(node, group.Select(c => c.Range).Distinct()
                .OrderBy(r => r.Start.Line).ThenBy(r => r.Start.Character).ToImmutableArray()));
        }
        return edges.ToImmutable();
    }

    private IndexedSqlSymbol? CallOwner(DocumentSnapshot document, TextPosition position) => Symbols
        .Where(s => s.Target.Uri == document.Uri && IsCallable(s) && Contains(s.Declaration.Range, position))
        .OrderBy(s => SpanLength(document, s.Declaration.Range)).FirstOrDefault();

    private IEnumerable<SqlStaticReference> OwnedInvocations(DocumentSnapshot document, IndexedSqlSymbol symbol) =>
        document.Parse.Semantics.References.Where(r => r.IsInvocation &&
            Contains(symbol.Declaration.Range, r.Range.Start) && CallOwner(document, r.Range.Start)?.Target == symbol.Target);

    private SqlCallHierarchyNode? CallNode(IndexedSqlSymbol symbol, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsUniqueCallable(symbol)) return null;
        var document = documents.FirstOrDefault(d => d.Uri == symbol.Target.Uri);
        if (document == null) return null;
        var available = document.Parse.Declarations.Any(d => d.Kind == symbol.Identity.Kind && d.Range == symbol.Declaration.Range && !d.IsExternalImplementation);
        var resolved = 0;
        var ambiguous = 0;
        var unresolved = 0;
        var unsupported = 0;
        foreach (var reference in OwnedInvocations(document, symbol))
        {
            token.ThrowIfCancellationRequested();
            switch (Resolve(document, reference).Status)
            {
                case ResolutionStatus.Resolved: resolved++; break;
                case ResolutionStatus.Ambiguous: ambiguous++; break;
                case ResolutionStatus.Unresolved: unresolved++; break;
                default: unsupported++; break;
            }
        }
        // A prepare item must not silently attach to a changed buffer, disk document or catalog generation.
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.Parse.Source.Text))) +
            ":" + document.Version + ":" + document.SourceKind;
        return new(symbol, revision, new(available, resolved, ambiguous, unresolved, unsupported));
    }

    private bool IsUniqueCallable(IndexedSqlSymbol symbol)
    {
        if (!IsCallable(symbol)) return false;
        var rules = Rules(symbol.Target.Uri);
        var identity = symbol.Identity;
        return Symbols.Where(s => s.Identity.Kind == identity.Kind && s.Identity.SourceContextId == identity.SourceContextId &&
            rules.Comparer.Equals(s.Identity.Server, identity.Server) && rules.Comparer.Equals(s.Identity.Database, identity.Database) &&
            rules.Comparer.Equals(s.Identity.Schema, identity.Schema) && rules.Comparer.Equals(s.Identity.Name, identity.Name))
            .Select(s => (s.Target.Uri, s.Target.Range)).Distinct().Take(2).Count() == 1;
    }

    private static bool IsCallable(IndexedSqlSymbol symbol) => symbol.Identity.Kind is SqlObjectKind.Procedure or SqlObjectKind.Function;
}
