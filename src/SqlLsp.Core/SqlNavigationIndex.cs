using System.Collections.Immutable;

namespace SqlLsp.Core;

// A request reads one immutable workspace snapshot; resolution never consults changing dictionaries.
public sealed class SqlNavigationIndex
{
    private readonly ImmutableArray<DocumentSnapshot> documents;
    private readonly Func<Uri, string> context;
    private readonly ResolutionOptions options;
    public ImmutableArray<IndexedSqlSymbol> Symbols { get; }

    public SqlNavigationIndex(ImmutableArray<DocumentSnapshot> documents, Func<Uri, string> context, ResolutionOptions options)
    {
        this.documents = documents;
        this.context = context;
        this.options = options;
        Symbols = documents.SelectMany(document => document.Parse.Declarations
            .Where(d => d.IsDefinition && d.QualifiedName != null && !d.QualifiedName.Name.StartsWith('#'))
            .Select(d =>
            {
                var name = d.QualifiedName!;
                var identity = new SqlObjectIdentity(context(document.Uri), name.Server,
                    name.Database ?? d.Database ?? options.DefaultDatabase, name.Schema ?? options.DefaultSchema, name.Name, d.Kind);
                var target = new SqlNavigationTarget(document.Uri, d.SelectionRange, identity, document.SourceKind,
                    document.SourceKind == SqlSourceKind.UnsavedBuffer ? "Unsaved buffer" : "Workspace AST");
                return new IndexedSqlSymbol(identity, d, target);
            })).ToImmutableArray();
    }

    public ResolutionResult Resolve(DocumentSnapshot document, SqlStaticReference reference)
    {
        if (reference.UnsupportedReason != null) return new(ResolutionStatus.Unsupported, [], reference.UnsupportedReason);
        if (reference.LocalTarget is { } local)
            return new(ResolutionStatus.Resolved, [new(document.Uri, local.Range, null, document.SourceKind, "Lexical scope")], "Verified local binding.");
        if (reference.Name is not { } name) return new(ResolutionStatus.Unsupported, [], "This syntax has no supported static object target.");
        if (name.Server != null) return new(ResolutionStatus.Unsupported, [], "Cross-server targets require explicit source mapping.");
        var database = name.Database ?? reference.Database ?? options.DefaultDatabase;
        var schema = name.Schema ?? options.DefaultSchema;
        var candidates = Symbols.Where(symbol =>
            symbol.Identity.SourceContextId == context(document.Uri) &&
            options.Comparer.Equals(symbol.Identity.Server, name.Server) &&
            options.Comparer.Equals(symbol.Identity.Database, database) &&
            options.Comparer.Equals(symbol.Identity.Name, name.Name) &&
            (schema == null || options.Comparer.Equals(symbol.Identity.Schema, schema)) &&
            KindMatches(symbol.Identity.Kind, reference.Kind)).Select(symbol => symbol.Target).Distinct().ToImmutableArray();
        return candidates.Length switch
        {
            1 => new(ResolutionStatus.Resolved, candidates, "Unique qualified workspace target."),
            > 1 => new(ResolutionStatus.Ambiguous, candidates, "Multiple declarations match; no target was selected."),
            _ => new(ResolutionStatus.Unresolved, [], "No verified declaration in this source context/database.")
        };
    }

    public ResolutionResult Definition(Uri uri, TextPosition position, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var document = documents.FirstOrDefault(d => d.Uri == uri);
        if (document == null) return new(ResolutionStatus.Unresolved, [], "Document is not indexed.");
        foreach (var reference in document.Parse.Semantics.References.OrderBy(r => SpanLength(document, r.Range)))
        {
            token.ThrowIfCancellationRequested();
            if (Contains(reference.Range, position)) return Resolve(document, reference);
        }
        var local = document.Parse.Semantics.Locals.FirstOrDefault(d => Contains(d.Range, position));
        if (local != null) return new(ResolutionStatus.Resolved, [new(uri, local.Range, null, document.SourceKind, "Lexical scope")], "Local declaration.");
        var declaration = Symbols.FirstOrDefault(s => s.Target.Uri == uri && Contains(s.Target.Range, position));
        return declaration == null ? new(ResolutionStatus.Unresolved, [], "Position is not a supported SQL object use or declaration.")
            : new(ResolutionStatus.Resolved, [declaration.Target], "Declaration anchor.");
    }

    public ImmutableArray<SqlNavigationTarget> References(Uri uri, TextPosition position, bool includeDeclaration, CancellationToken token)
    {
        var selected = Definition(uri, position, token);
        if (selected.Status != ResolutionStatus.Resolved) return [];
        var target = selected.Candidates[0];
        var results = new List<SqlNavigationTarget>();
        if (includeDeclaration) results.Add(target);
        foreach (var document in documents)
            foreach (var reference in document.Parse.Semantics.References)
            {
                token.ThrowIfCancellationRequested();
                var resolution = Resolve(document, reference);
                if (resolution.Status == ResolutionStatus.Resolved && SameAnchor(resolution.Candidates[0], target))
                    results.Add(new(document.Uri, reference.Range, target.Identity, document.SourceKind, "Static AST reference"));
            }
        return results.DistinctBy(r => (r.Uri, r.Range)).OrderBy(r => r.Uri.AbsoluteUri, StringComparer.Ordinal)
            .ThenBy(r => r.Range.Start.Line).ThenBy(r => r.Range.Start.Character).ToImmutableArray();
    }

    public ImmutableArray<IndexedSqlSymbol> Search(string query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = ImmutableArray.CreateBuilder<IndexedSqlSymbol>();
        foreach (var symbol in Symbols)
        {
            token.ThrowIfCancellationRequested();
            if (symbol.Declaration.Name.Contains(query, options.Comparison)) result.Add(symbol);
        }
        return result.ToImmutable();
    }

    private static bool SameAnchor(SqlNavigationTarget a, SqlNavigationTarget b) => a.Uri == b.Uri && a.Range == b.Range;
    private static int SpanLength(DocumentSnapshot d, TextRange range) => d.Parse.Source.OffsetAt(range.End) - d.Parse.Source.OffsetAt(range.Start);
    private static bool KindMatches(SqlObjectKind kind, SqlReferenceKind reference) => reference switch
    {
        SqlReferenceKind.Procedure => kind == SqlObjectKind.Procedure,
        SqlReferenceKind.Function => kind == SqlObjectKind.Function,
        SqlReferenceKind.Relation => kind is SqlObjectKind.Table or SqlObjectKind.View,
        SqlReferenceKind.Table => kind == SqlObjectKind.Table,
        _ => false
    };
    public static bool Contains(TextRange range, TextPosition position) => Compare(range.Start, position) <= 0 && Compare(position, range.End) < 0;
    private static int Compare(TextPosition a, TextPosition b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Character.CompareTo(b.Character);
}
