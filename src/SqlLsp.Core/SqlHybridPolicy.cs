using System.Collections.Immutable;

namespace SqlLsp.Core;

public sealed record SqlProfileContext(string Id, string Database, ResolutionOptions Rules);
public sealed record SqlWorkspaceMapping(string WorkspaceContextId, ImmutableArray<string> ProfileIds, string? DefaultProfileId = null);
public sealed record SqlHybridPolicy(ImmutableArray<SqlProfileContext> Profiles, ImmutableArray<SqlWorkspaceMapping> Mappings,
    bool PreferWorkspaceDefinitions = true)
{
    public SqlProfileContext? DefaultProfile(string context)
    {
        var own = Profiles.FirstOrDefault(p => p.Id == context);
        if (own != null) return own;
        var mapping = Mappings.FirstOrDefault(m => m.WorkspaceContextId == context);
        var id = mapping?.DefaultProfileId ?? (mapping?.ProfileIds.Length == 1 ? mapping.ProfileIds[0] : null);
        return Profiles.FirstOrDefault(p => p.Id == id);
    }

    public ResolutionOptions RulesFor(string context, ResolutionOptions fallback) => DefaultProfile(context)?.Rules ?? fallback;

    internal ResolutionResult Resolve(ImmutableArray<IndexedSqlSymbol> symbols, string context, QualifiedSqlName name,
        string? useDatabase, SqlReferenceKind kind, ResolutionOptions fallback)
    {
        var own = Profiles.FirstOrDefault(p => p.Id == context);
        var mapping = Mappings.FirstOrDefault(m => m.WorkspaceContextId == context);
        var authorized = own != null ? new[] { own } : Profiles.Where(p => mapping?.ProfileIds.Contains(p.Id) == true).ToArray();
        var defaultProfile = DefaultProfile(context);
        var explicitDatabase = name.Database ?? useDatabase;
        if (defaultProfile != null && (explicitDatabase == null || defaultProfile.Rules.Comparer.Equals(defaultProfile.Database, explicitDatabase)))
            authorized = [defaultProfile];
        else if (explicitDatabase != null)
            authorized = authorized.Where(p => p.Rules.Comparer.Equals(p.Database, explicitDatabase)).ToArray();

        var rules = RulesFor(context, fallback);
        if (authorized.Length == 1 && explicitDatabase != null) rules = authorized[0].Rules;
        var database = explicitDatabase ?? rules.DefaultDatabase;
        bool Match(IndexedSqlSymbol s, ResolutionOptions comparison, string? db, string? schema) =>
            comparison.Comparer.Equals(s.Identity.Server, name.Server) && comparison.Comparer.Equals(s.Identity.Database, db) &&
            comparison.Comparer.Equals(s.Identity.Name, name.Name) && (schema == null || comparison.Comparer.Equals(s.Identity.Schema, schema)) &&
            SqlNavigationIndex.KindMatches(s.Identity.Kind, kind);
        IndexedSqlSymbol[] InSchema(IEnumerable<IndexedSqlSymbol> source, ResolutionOptions comparison, string? db, bool fallbackAllowed = true)
        {
            var items = source.ToArray();
            var result = items.Where(s => Match(s, comparison, db, name.Schema ?? comparison.DefaultSchema)).ToArray();
            if (fallbackAllowed && result.Length == 0 && name.Schema == null && comparison.FallbackSchema != null)
                result = items.Where(s => Match(s, comparison, db, comparison.FallbackSchema)).ToArray();
            return result;
        }
        var localSource = symbols.Where(s => s.Target.SourceKind != SqlSourceKind.Database && s.Identity.SourceContextId == context).ToArray();
        var sharedSchema = own == null && authorized.Length == 1 && name.Schema == null && rules.DefaultSchema != null;
        var local = own == null
            ? InSchema(localSource, rules, database, !sharedSchema)
            : [];
        var deployed = authorized.SelectMany(p => InSchema(symbols.Where(s => s.Target.SourceKind == SqlSourceKind.Database &&
            s.Identity.SourceContextId == p.Id), p.Rules, p.Database, !sharedSchema)).ToArray();
        if (sharedSchema && local.Length == 0 && deployed.Length == 0)
        {
            local = InSchema(localSource, rules, database);
            deployed = authorized.SelectMany(p => InSchema(symbols.Where(s => s.Target.SourceKind == SqlSourceKind.Database &&
                s.Identity.SourceContextId == p.Id), p.Rules, p.Database)).ToArray();
        }
        var all = local.Concat(deployed).Select(s => s.Target).Distinct().ToImmutableArray();
        if (all.Length == 0) return new(ResolutionStatus.Unresolved, [], "No verified target in the explicitly mapped sources.");
        // Precedence cannot repair duplicate declarations or competing profile/schema/kind identities.
        if (local.Length > 1 || deployed.Length > 1 || local.Length == 1 && deployed.Length == 1 &&
            !SameObject(local[0].Identity, deployed[0].Identity, Profiles.Single(p => p.Id == deployed[0].Identity.SourceContextId).Rules.Comparer))
            return new(ResolutionStatus.Ambiguous, all, "Multiple object identities or duplicate declarations match; source preference does not select one.");
        var selected = PreferWorkspaceDefinitions && local.Length == 1 ? local[0].Target : deployed.FirstOrDefault()?.Target ?? local[0].Target;
        return new(ResolutionStatus.Resolved, all, "One mapped object identity; all source candidates retained with a selected navigation target.", selected);
    }

    public static bool SameObject(SqlObjectIdentity a, SqlObjectIdentity b, StringComparer comparer) =>
        comparer.Equals(a.Server, b.Server) && comparer.Equals(a.Database, b.Database) && comparer.Equals(a.Schema, b.Schema) &&
        comparer.Equals(a.Name, b.Name) && a.Kind == b.Kind;
}
