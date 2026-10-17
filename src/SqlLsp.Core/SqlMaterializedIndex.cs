using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace SqlLsp.Core;

public sealed record SqlMaterializedRules(bool CaseSensitive, string? DefaultSchema, string? DefaultDatabase, string? FallbackSchema = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ResolutionOptions Options => new(CaseSensitive, DefaultSchema, DefaultDatabase, FallbackSchema: FallbackSchema);
    public static SqlMaterializedRules From(ResolutionOptions rules)
    {
        if (rules.IdentifierComparer != null) throw new ArgumentException("Custom catalog comparison rules cannot be persisted through the offline index.");
        return new(rules.CaseSensitive, rules.DefaultSchema, rules.DefaultDatabase, rules.FallbackSchema);
    }
}

// Plain data only: no SourceText, TSqlFragment, runtime comparer or executable type metadata.
public sealed record SqlMaterializedFile(Uri Uri, string RawHash, string Revision, long Bytes, string Encoding,
    string ContextId, SqlMaterializedRules Rules, ImmutableArray<SqlDeclaration> Declarations,
    SqlSemanticDocument Semantics, ImmutableArray<SqlParseDiagnostic> Diagnostics);

public sealed class SqlMaterializedIndex : ISqlDiscoveryIndex
{
    public ImmutableArray<SqlMaterializedFile> Files { get; }
    public ImmutableArray<IndexedSqlSymbol> Symbols { get; }
    public int DocumentCount => Files.Length;
    public string Generation { get; }
    private readonly Dictionary<Uri, SqlMaterializedFile> files;
    private readonly HashSet<(string From, string To)> references;
    public ISqlDiscoveryIndex Within(Uri directory) => new SqlMaterializedIndex(Files.Where(f => directory.IsBaseOf(f.Uri)).ToImmutableArray(), references);

    public SqlMaterializedIndex(ImmutableArray<SqlMaterializedFile> sourceFiles, HashSet<(string From, string To)>? references = null)
    {
        Files = sourceFiles.OrderBy(f => f.Uri.AbsoluteUri, StringComparer.Ordinal).ToImmutableArray();
        files = Files.ToDictionary(f => f.Uri);
        this.references = references ?? [];
        Symbols = Files.SelectMany(file => file.Declarations.Where(d => d.IsDefinition && d.QualifiedName != null && !d.QualifiedName.Name.StartsWith('#')).Select(d =>
        {
            var name = d.QualifiedName!;
            var identity = new SqlObjectIdentity(file.ContextId, name.Server, name.Database ?? d.Database ?? file.Rules.DefaultDatabase,
                name.Schema ?? file.Rules.DefaultSchema, name.Name, d.Kind);
            return new IndexedSqlSymbol(identity, d, new(file.Uri, d.SelectionRange, identity, SqlSourceKind.Workspace, "Workspace AST"));
        })).ToImmutableArray();
        Generation = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Files.Select(f => new
        { uri = f.Uri.AbsoluteUri, f.RawHash, f.Revision, f.ContextId, f.Rules }))));
    }

    public ImmutableArray<IndexedSqlSymbol> Search(string query, CancellationToken token) => Symbols.Where(s =>
    {
        token.ThrowIfCancellationRequested();
        return s.Declaration.Name.Contains(query, files[s.Target.Uri].Rules.Options.Comparison);
    }).ToImmutableArray();

    public SqlModuleMetadata ModuleMetadata(IndexedSqlSymbol symbol, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!files.TryGetValue(symbol.Target.Uri, out var file)) return new(null, ResolutionStatus.Unresolved);
        var identity = symbol.Identity;
        var comparer = file.Rules.Options.Comparer;
        var duplicates = Symbols.Count(s => s.Identity.SourceContextId == identity.SourceContextId && s.Identity.Kind == identity.Kind &&
            comparer.Equals(s.Identity.Server, identity.Server) && comparer.Equals(s.Identity.Database, identity.Database) &&
            comparer.Equals(s.Identity.Schema, identity.Schema) && comparer.Equals(s.Identity.Name, identity.Name));
        return duplicates == 1 ? new(file.Revision, ResolutionStatus.Resolved) : new(null, ResolutionStatus.Ambiguous);
    }

    public ResolutionResult Resolve(SqlMaterializedFile file, SqlStaticReference reference)
    {
        if (reference.UnsupportedReason != null) return new(ResolutionStatus.Unsupported, [], reference.UnsupportedReason);
        if (reference.LocalTarget is { } local) return new(ResolutionStatus.Resolved, [new(file.Uri, local.Range, null, SqlSourceKind.Workspace, "Lexical scope")], "Verified local binding.");
        if (reference.Name is not { } name || name.Server != null) return new(ResolutionStatus.Unsupported, [], "No mapped static target.");
        var rules = file.Rules.Options;
        IEnumerable<SqlNavigationTarget> Match(string? schema) => Symbols.Where(s =>
            (s.Identity.SourceContextId == file.ContextId || name.Database != null && references.Contains((file.ContextId, s.Identity.SourceContextId))) &&
            rules.Comparer.Equals(s.Identity.Server, name.Server) && rules.Comparer.Equals(s.Identity.Database, name.Database ?? reference.Database ?? rules.DefaultDatabase) &&
            rules.Comparer.Equals(s.Identity.Name, name.Name) && (schema == null || rules.Comparer.Equals(s.Identity.Schema, schema)) &&
            SqlNavigationIndex.KindMatches(s.Identity.Kind, reference.Kind)).Select(s => s.Target);
        var candidates = Match(name.Schema ?? rules.DefaultSchema).Distinct().ToImmutableArray();
        if (candidates.Length == 0 && name.Schema == null && rules.FallbackSchema is { } fallback) candidates = Match(fallback).Distinct().ToImmutableArray();
        return new(candidates.Length == 1 ? ResolutionStatus.Resolved : candidates.Length > 1 ? ResolutionStatus.Ambiguous : ResolutionStatus.Unresolved,
            candidates, candidates.Length == 1 ? "Unique qualified source target." : "No unique declaration in the verified source context/database.");
    }

    public int DocumentCountFor(string? scopeId, Uri? uri = null) => Selected(scopeId, uri).Count();
    public (int Count, int Files) ParseIssueCounts(Uri? uri = null, string? scopeId = null) =>
        (Selected(scopeId, uri).Sum(f => f.Diagnostics.Length), Selected(scopeId, uri).Count(f => f.Diagnostics.Length > 0));
    public IEnumerable<SqlCoverageIssue> ParseIssuePage(Uri? uri, int offset, int limit, CancellationToken token, string? scopeId = null)
    {
        foreach (var issue in Selected(scopeId, uri).SelectMany(f => f.Diagnostics.Select(d => new SqlCoverageIssue("parse-error", d.Message, new(f.Uri, d.Range)))).Skip(offset).Take(limit))
        {
            token.ThrowIfCancellationRequested();
            yield return issue;
        }
    }
    private IEnumerable<SqlMaterializedFile> Selected(string? scopeId, Uri? uri) => Files.Where(f => (scopeId == null || f.ContextId == scopeId) && (uri == null || f.Uri == uri));
}
