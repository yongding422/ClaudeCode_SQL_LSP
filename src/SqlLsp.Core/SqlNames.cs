using System.Collections.Immutable;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

public sealed record QualifiedSqlName(string? Server, string? Database, string? Schema, string Name)
{
    public static QualifiedSqlName From(MultiPartIdentifier identifier) => FromParts(identifier.Identifiers.Select(i => i.Value));

    public static QualifiedSqlName FromParts(IEnumerable<string> identifiers)
    {
        var parts = identifiers.ToArray();
        if (parts.Length is < 1 or > 4) throw new ArgumentException("SQL object names need one to four parts.");
        string? Part(int fromEnd) => parts.Length >= fromEnd && parts[^fromEnd].Length > 0 ? parts[^fromEnd] : null;
        return new(Part(4), Part(3), Part(2), Part(1) ?? "");
    }
}

public sealed record ResolutionOptions(bool CaseSensitive = true, string? DefaultSchema = null, string? DefaultDatabase = null,
    StringComparer? IdentifierComparer = null, string? FallbackSchema = null)
{
    public StringComparer Comparer => IdentifierComparer ?? (CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    public StringComparison Comparison => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

public enum SqlReferenceKind { Procedure, Relation, Table, Function, Variable, Alias, Dynamic }
public enum ResolutionStatus { Resolved, Ambiguous, Unresolved, Unsupported }
public sealed record SqlLocalDefinition(string Name, SqlObjectKind Kind, TextRange Range, bool IsAmbiguous = false);
public sealed record SqlStaticReference(QualifiedSqlName? Name, string RawName, SqlReferenceKind Kind,
    TextRange Range, string? Database, SqlLocalDefinition? LocalTarget = null, string? UnsupportedReason = null);
public sealed record SqlSemanticDocument(ImmutableArray<SqlStaticReference> References, ImmutableArray<SqlLocalDefinition> Locals);
public sealed record SqlObjectIdentity(string SourceContextId, string? Server, string? Database, string? Schema,
    string Name, SqlObjectKind Kind);
public sealed record SqlNavigationTarget(Uri Uri, TextRange Range, SqlObjectIdentity? Identity, SqlSourceKind SourceKind,
    string Provenance);
public sealed record IndexedSqlSymbol(SqlObjectIdentity Identity, SqlDeclaration Declaration, SqlNavigationTarget Target,
    string? DefinitionHash = null, DateTimeOffset? CapturedAt = null);
public sealed record ResolutionResult(ResolutionStatus Status, ImmutableArray<SqlNavigationTarget> Candidates, string Explanation,
    SqlNavigationTarget? Selected = null)
{
    public SqlNavigationTarget? Target => Status == ResolutionStatus.Resolved ? Selected ?? Candidates.FirstOrDefault() : null;
}
