using System.Collections.Immutable;

namespace SqlLsp.Core.Tests;

public class HybridTests
{
    private static readonly Uri Local = new("file:///workspace/proc.sql"), Query = new("file:///workspace/query.sql"), Db = new("file:///catalog/proc.sql");
    private static readonly ResolutionOptions Rules = new(CaseSensitive: false, DefaultDatabase: "Payroll", DefaultSchema: "dbo", FallbackSchema: "dbo");
    private static async Task<DocumentSnapshot> Document(Uri uri, string text, SqlSourceKind kind = SqlSourceKind.Workspace) =>
        new(uri, kind == SqlSourceKind.UnsavedBuffer ? 1 : null, kind, await new SqlParser(new(), Rules).ParseAsync(new(text), CancellationToken.None));
    private static IndexedSqlSymbol Deployed(DocumentSnapshot document, string profile = "dev", string database = "Payroll")
    {
        var declaration = document.Parse.Declarations.Single();
        var name = declaration.QualifiedName!;
        var identity = new SqlObjectIdentity(profile, null, database, name.Schema, name.Name, declaration.Kind);
        return new(identity, declaration, new(document.Uri, declaration.SelectionRange, identity, SqlSourceKind.Database, "Database module AST"),
            SqlDefinitionFingerprint.Compute(document.Parse, declaration), DateTimeOffset.UtcNow);
    }
    private static SqlHybridPolicy Policy(bool preferred = true, string? defaultProfile = "dev") => new(
        [new("dev", "Payroll", Rules)], [new("workspace", ["dev"], defaultProfile)], preferred);
    private static SqlNavigationIndex Index(IEnumerable<DocumentSnapshot> documents, IEnumerable<IndexedSqlSymbol> catalog,
        SqlHybridPolicy? policy = null, Func<Uri, string>? context = null) => new(documents.ToImmutableArray(),
            context ?? (uri => uri.AbsolutePath.StartsWith("/catalog/", StringComparison.Ordinal) ? "dev" : "workspace"),
            new(), catalog.ToImmutableArray(), hybrid: policy ?? Policy());
    private static ResolutionResult Resolve(SqlNavigationIndex index, DocumentSnapshot query) => index.Resolve(query, query.Parse.Semantics.References[0]);

    [Theory]
    [InlineData(SqlSourceKind.Workspace, true)]
    [InlineData(SqlSourceKind.UnsavedBuffer, true)]
    [InlineData(SqlSourceKind.UnsavedBuffer, false)]
    public async Task Same_identity_retains_both_sources_and_selects_configured_preference(SqlSourceKind kind, bool preferred)
    {
        var local = await Document(Local, "ALTER PROCEDURE dbo.P AS SELECT 2;", kind);
        var deployed = await Document(Db, "CREATE PROCEDURE dbo.P AS SELECT 1;", SqlSourceKind.Database);
        var query = await Document(Query, "EXEC dbo.p;");
        var index = Index([local, deployed, query], [Deployed(deployed)], Policy(preferred));
        var resolved = Resolve(index, query);
        Assert.Equal(ResolutionStatus.Resolved, resolved.Status);
        Assert.Equal(2, resolved.Candidates.Length);
        Assert.Equal(preferred ? Local : Db, resolved.Target!.Uri);
        Assert.Equal(SqlDriftStatus.Different, Assert.Single(index.Drifts).Status);
    }

    [Fact]
    public async Task Missing_local_objects_resolve_from_catalog_and_unmapped_roots_do_not()
    {
        var deployed = await Document(Db, "CREATE TABLE dbo.Payroll(id int);", SqlSourceKind.Database);
        var query = await Document(Query, "SELECT * FROM dbo.Payroll;");
        Assert.Equal(Db, Resolve(Index([deployed, query], [Deployed(deployed)]), query).Target!.Uri);
        var unmapped = Index([deployed, query], [Deployed(deployed)], context: uri => uri == Db ? "dev" : "unmapped");
        Assert.Equal(ResolutionStatus.Unresolved, Resolve(unmapped, query).Status);
    }

    [Fact]
    public async Task Duplicate_local_definitions_remain_ambiguous_even_when_one_is_unsaved()
    {
        var local = await Document(Local, "CREATE PROCEDURE dbo.P AS SELECT 1;", SqlSourceKind.UnsavedBuffer);
        var duplicate = await Document(new("file:///workspace/duplicate.sql"), "ALTER PROCEDURE dbo.P AS SELECT 2;");
        var deployed = await Document(Db, "CREATE PROCEDURE dbo.P AS SELECT 1;", SqlSourceKind.Database);
        var query = await Document(Query, "EXEC dbo.P;");
        var index = Index([local, duplicate, deployed, query], [Deployed(deployed)]);
        var resolved = Resolve(index, query);
        Assert.Equal(ResolutionStatus.Ambiguous, resolved.Status);
        Assert.Null(resolved.Target);
        Assert.Equal(3, resolved.Candidates.Length);
        Assert.All(index.Drifts, d => Assert.Equal(SqlDriftStatus.Ambiguous, d.Status));
    }

    [Fact]
    public async Task Multiple_profiles_do_not_merge_by_short_name_and_explicit_default_selects_only_its_target()
    {
        var dev = await Document(Db, "CREATE PROCEDURE dbo.P AS SELECT 1;", SqlSourceKind.Database);
        var prod = await Document(new("file:///catalog/prod.sql"), "CREATE PROCEDURE dbo.P AS SELECT 2;", SqlSourceKind.Database);
        var query = await Document(Query, "EXEC dbo.P;");
        var policy = new SqlHybridPolicy([new("dev", "Payroll", Rules), new("prod", "Payroll", Rules)], [new("workspace", ["dev", "prod"])]);
        var catalog = new[] { Deployed(dev), Deployed(prod, "prod") };
        Assert.Equal(ResolutionStatus.Ambiguous, Resolve(Index([dev, prod, query], catalog, policy), query).Status);
        var mapped = policy with { Mappings = [new("workspace", ["dev", "prod"], "dev")] };
        Assert.Equal(Db, Resolve(Index([dev, prod, query], catalog, mapped), query).Target!.Uri);
    }

    [Fact]
    public async Task Qualified_database_uses_only_authorized_profile_and_schema_fallback_applies_across_sources()
    {
        var local = await Document(Local, "CREATE TABLE dbo.Payroll(id int);");
        var deployed = await Document(Db, "CREATE TABLE sales.Payroll(id int);", SqlSourceKind.Database);
        var query = await Document(Query, "SELECT * FROM Payroll; SELECT * FROM Other.sales.Payroll;");
        var policy = Policy() with { Profiles = [new("dev", "Payroll", Rules with { DefaultSchema = "sales" })] };
        var index = Index([local, deployed, query], [Deployed(deployed)], policy);
        Assert.Equal(Db, Resolve(index, query).Target!.Uri);
        Assert.Equal(ResolutionStatus.Unresolved, index.Resolve(query, query.Parse.Semantics.References[1]).Status);
    }

    [Fact]
    public async Task Hybrid_references_include_local_and_deployed_AST_uses_from_either_declaration_anchor()
    {
        var local = await Document(Local, "CREATE PROCEDURE dbo.P AS SELECT 1;");
        var deployed = await Document(Db, "CREATE PROCEDURE dbo.P AS SELECT 1;", SqlSourceKind.Database);
        var caller = await Document(new("file:///catalog/caller.sql"), "EXEC dbo.P;", SqlSourceKind.Database);
        var query = await Document(Query, "EXEC dbo.P;");
        var index = Index([local, deployed, caller, query], [Deployed(deployed)]);
        foreach (var declaration in new[] { local, deployed })
        {
            var refs = index.References(declaration.Uri, declaration.Parse.Declarations[0].SelectionRange.Start, false, CancellationToken.None);
            Assert.Equal(2, refs.Length);
            Assert.Contains(refs, r => r.SourceKind == SqlSourceKind.Database);
            Assert.Contains(refs, r => r.SourceKind == SqlSourceKind.Workspace);
        }
    }

    [Fact]
    public async Task Unavailable_database_keeps_qualified_local_navigation_and_drift_unknown()
    {
        var local = await Document(Local, "CREATE PROCEDURE dbo.P AS SELECT 1;");
        var query = await Document(Query, "EXEC dbo.P;");
        var index = Index([local, query], []);
        Assert.Equal(Local, Resolve(index, query).Target!.Uri);
        Assert.Equal(SqlDriftStatus.Unavailable, Assert.Single(index.Drifts).Status);
    }

    [Theory]
    [InlineData("-- comment\nALTER PROC [dbo].[P] AS\n SELECT 1; -- tail", true)]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.P AS select 1;", true)]
    [InlineData("CREATE PROCEDURE dbo.P AS SELECT 2;", false)]
    [InlineData("CREATE PROCEDURE dbo.P AS SELECT 'one';", false)]
    public async Task Fingerprints_ignore_comments_spacing_and_headers_but_preserve_body_values(string other, bool matches)
    {
        var original = await Document(Local, "CREATE PROCEDURE dbo.P AS SELECT 1;");
        var changed = await Document(Db, other);
        string? Hash(DocumentSnapshot d) => SqlDefinitionFingerprint.Compute(d.Parse, Assert.Single(d.Parse.Declarations));
        Assert.Equal(matches, Hash(original) == Hash(changed));
    }

    [Fact]
    public async Task Literal_case_and_identifier_boundaries_are_not_removed_by_normalization()
    {
        var original = await Document(Local, "CREATE PROC dbo.P AS SELECT 'Case';");
        var changed = await Document(Db, "CREATE PROC dbo.P AS SELECT 'case';");
        Assert.NotEqual(SqlDefinitionFingerprint.Compute(original.Parse, original.Parse.Declarations[0]),
            SqlDefinitionFingerprint.Compute(changed.Parse, changed.Parse.Declarations[0]));
    }
}
