using System.Collections.Immutable;

namespace SqlLsp.Core;

public sealed record SqlModuleMetadata(string? Revision, ResolutionStatus Status)
{
    public ImmutableArray<Uri> CandidateUris { get; init; } = [];
}

// Discovery depends on extracted facts, never an AST-shaped placeholder.
public interface ISqlDiscoveryIndex
{
    int DocumentCount { get; }
    string Generation { get; }
    ImmutableArray<IndexedSqlSymbol> Symbols { get; }
    ISqlDiscoveryIndex Within(Uri directory);
    ImmutableArray<IndexedSqlSymbol> Search(string query, CancellationToken token);
    SqlModuleMetadata ModuleMetadata(IndexedSqlSymbol symbol, CancellationToken token);
    int DocumentCountFor(string? scopeId, Uri? uri = null);
    (int Count, int Files) ParseIssueCounts(Uri? uri = null, string? scopeId = null);
    IEnumerable<SqlCoverageIssue> ParseIssuePage(Uri? uri, int offset, int limit, CancellationToken token, string? scopeId = null);
}
