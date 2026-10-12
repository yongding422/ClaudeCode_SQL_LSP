using System.Collections.Immutable;

namespace SqlLsp.Core;

public enum SqlDriftStatus { Match, Different, Unavailable, Ambiguous }
public sealed record SqlDefinitionDrift(SqlNavigationTarget Local, ImmutableArray<SqlNavigationTarget> DatabaseCandidates,
    SqlDriftStatus Status, string? LocalHash, ImmutableArray<string?> DatabaseHashes, DateTimeOffset? CapturedAt,
    string Explanation);

public static class SqlDefinitionDriftAnalysis
{
    public static ImmutableArray<SqlDefinitionDrift> Compare(SqlNavigationIndex index, SqlHybridPolicy policy,
        ResolutionOptions fallback, CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<SqlDefinitionDrift>();
        foreach (var local in index.Symbols.Where(s => s.Target.SourceKind != SqlSourceKind.Database))
        {
            token.ThrowIfCancellationRequested();
            if (!policy.Mappings.Any(m => m.WorkspaceContextId == local.Identity.SourceContextId)) continue;
            var identity = local.Identity;
            var kind = identity.Kind switch
            {
                SqlObjectKind.Procedure => SqlReferenceKind.Procedure,
                SqlObjectKind.Function => SqlReferenceKind.Function,
                SqlObjectKind.Table => SqlReferenceKind.Table,
                _ => SqlReferenceKind.Relation
            };
            var resolved = policy.Resolve(index.Symbols, identity.SourceContextId,
                new(identity.Server, identity.Database, identity.Schema, identity.Name), null, kind, fallback);
            var deployed = index.Symbols.Where(s => s.Target.SourceKind == SqlSourceKind.Database &&
                resolved.Candidates.Any(c => c.Uri == s.Target.Uri && c.Range == s.Target.Range)).ToArray();
            var status = resolved.Status == ResolutionStatus.Ambiguous ? SqlDriftStatus.Ambiguous : deployed.Length != 1 ||
                local.DefinitionHash == null || deployed[0].DefinitionHash == null ? SqlDriftStatus.Unavailable :
                local.DefinitionHash == deployed[0].DefinitionHash ? SqlDriftStatus.Match : SqlDriftStatus.Different;
            result.Add(new(local.Target, deployed.Select(s => s.Target).ToImmutableArray(), status, local.DefinitionHash,
                deployed.Select(s => s.DefinitionHash).ToImmutableArray(), deployed.FirstOrDefault()?.CapturedAt,
                status switch
                {
                    SqlDriftStatus.Match => "Normalized module tokens match; this is not a proof of semantic equivalence.",
                    SqlDriftStatus.Different => "Normalized module tokens differ; a behavioral change is not proven.",
                    SqlDriftStatus.Ambiguous => "Competing identities or duplicate local declarations prevent comparison.",
                    _ => "No unique visible, successfully parsed module source is available for comparison."
                }));
        }
        return result.ToImmutable();
    }
}
