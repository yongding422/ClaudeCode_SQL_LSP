using System.Collections.Immutable;
using SqlLsp.Core;

namespace SqlLsp.Core.Tests;

public class CallHierarchyTests
{
    private static readonly Uri Uri = new("file:///project/modules.sql");
    private static async Task<DocumentSnapshot> Parse(string text, int? version = null, SqlSourceKind kind = SqlSourceKind.Workspace) =>
        new(Uri, version, kind, await new SqlParser(new()).ParseAsync(new SourceText(text), CancellationToken.None));
    private static SqlNavigationIndex Index(params DocumentSnapshot[] documents) => new(documents.ToImmutableArray(), _ => "project", new());
    private static SqlCallHierarchyNode Node(SqlNavigationIndex index, DocumentSnapshot document, string name) =>
        Assert.IsType<SqlCallHierarchyNode>(index.PrepareCallHierarchy(document.Uri,
            document.Parse.Source.PositionAt(document.Parse.Source.Text.IndexOf(name, StringComparison.Ordinal)), CancellationToken.None));

    [Fact]
    public async Task Static_calls_group_repeated_sites_preserve_recursion_and_distinguish_schemas()
    {
        var document = await Parse("CREATE PROC dbo.Caller AS BEGIN EXEC dbo.Callee; EXEC [dbo].[Callee]; EXEC audit.Callee; EXEC dbo.Caller; END;\nGO\n" +
            "CREATE PROC dbo.Callee AS SELECT 1;\nGO\nCREATE PROC audit.Callee AS SELECT 2;");
        var index = Index(document);
        var caller = Node(index, document, "dbo.Caller");
        var edges = index.OutgoingCalls(caller, CancellationToken.None);
        Assert.Equal(3, edges.Length);
        Assert.Equal(2, Assert.Single(edges.Where(e => e.Node.Symbol.Identity.Name == "Callee" && e.Node.Symbol.Identity.Schema == "dbo")).FromRanges.Length);
        Assert.Single(edges.Where(e => e.Node.Symbol.Identity.Name == "Caller"));
        var incoming = Assert.Single(index.IncomingCalls(Node(index, document, "dbo.Callee"), CancellationToken.None));
        Assert.Equal("Caller", incoming.Node.Symbol.Identity.Name);
        Assert.Equal(2, incoming.FromRanges.Length);
        Assert.Equal(4, caller.Coverage.Resolved);
    }

    [Fact]
    public async Task Function_aliases_are_navigation_references_but_not_additional_invocations()
    {
        var document = await Parse("CREATE PROC dbo.P AS SELECT t.id, t.id, dbo.F(1) FROM dbo.T(1) t WHERE t.id > 0;\nGO\n" +
            "CREATE FUNCTION dbo.F(@id int) RETURNS int AS BEGIN RETURN @id; END;\nGO\n" +
            "CREATE FUNCTION dbo.T(@id int) RETURNS TABLE AS RETURN (SELECT @id AS id);");
        var index = Index(document);
        var calls = index.OutgoingCalls(Node(index, document, "dbo.P"), CancellationToken.None);
        Assert.Equal(2, calls.Length);
        Assert.All(calls, c => Assert.Single(c.FromRanges));
        Assert.True(document.Parse.Semantics.References.Count(r => r.Kind == SqlReferenceKind.Function) > 2);
        Assert.Equal(2, document.Parse.Semantics.References.Count(r => r.IsInvocation));
    }

    [Fact]
    public async Task Comments_literals_dynamic_missing_remote_and_ambiguous_calls_are_not_graph_edges()
    {
        var document = await Parse("CREATE PROC dbo.P AS BEGIN\n-- EXEC dbo.Q;\nSELECT N'EXEC dbo.Q';\nEXEC(N'EXEC dbo.Q');\nEXEC dbo.Missing;\nEXEC Q;\nEXEC remote.Db.dbo.Q;\nEND;\nGO\n" +
            "CREATE PROC dbo.Q AS SELECT 1;\nGO\nCREATE PROC audit.Q AS SELECT 2;");
        var index = Index(document);
        var node = Node(index, document, "dbo.P");
        Assert.Empty(index.OutgoingCalls(node, CancellationToken.None));
        Assert.Equal(new SqlCallCoverage(true, 0, 1, 1, 2), node.Coverage);
        Assert.Empty(index.IncomingCalls(Node(index, document, "dbo.Q"), CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_declarations_and_non_callable_objects_do_not_prepare_arbitrary_nodes()
    {
        var document = await Parse("CREATE PROC dbo.P AS SELECT 1;\nGO\nCREATE PROC dbo.P AS SELECT 2;\nGO\nCREATE TABLE dbo.T(id int);");
        var index = Index(document);
        Assert.Null(index.PrepareCallHierarchy(Uri, new(0, 16), CancellationToken.None));
        Assert.Null(index.PrepareCallHierarchy(Uri, document.Parse.Declarations[2].SelectionRange.Start, CancellationToken.None));
        Assert.Null(index.PrepareCallHierarchy(Uri, new(-1, 0), CancellationToken.None));
    }

    [Fact]
    public async Task Stale_revision_or_identity_never_attaches_to_changed_source_and_cancellation_is_observed()
    {
        var original = await Parse("CREATE PROC dbo.P AS SELECT 1;", 1, SqlSourceKind.UnsavedBuffer);
        var oldIndex = Index(original);
        var node = Node(oldIndex, original, "dbo.P");
        var changed = await Parse("CREATE PROC dbo.P AS SELECT 2;", 2, SqlSourceKind.UnsavedBuffer);
        var index = Index(changed);
        Assert.Empty(index.OutgoingCalls(node, CancellationToken.None));
        Assert.Empty(index.IncomingCalls(node, CancellationToken.None));
        Assert.Null(index.ValidateCallHierarchy(Uri, node.Symbol.Target.Range, node.Symbol.Declaration.Range,
            node.Symbol.Identity with { SourceContextId = "other" }, Node(index, changed, "dbo.P").Revision, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => index.OutgoingCalls(node, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => index.IncomingCalls(node, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => index.PrepareCallHierarchy(Uri, new(0, 0), cancellation.Token));
    }

    [Fact]
    public async Task Top_level_calls_do_not_invent_callable_owners_and_roots_stay_isolated()
    {
        var first = await Parse("CREATE PROC dbo.P AS SELECT 1;\nGO\nEXEC dbo.P;");
        var second = await Parse("CREATE PROC dbo.Other AS EXEC dbo.P;");
        second = second with { Uri = new("file:///other/modules.sql") };
        var index = new SqlNavigationIndex([first, second], uri => uri == Uri ? "first" : "second", new());
        Assert.Empty(index.IncomingCalls(Node(index, first, "dbo.P"), CancellationToken.None));
        Assert.Empty(index.OutgoingCalls(Node(index, second, "dbo.Other"), CancellationToken.None));
        Assert.Equal("P", index.PrepareCallHierarchy(Uri, new(0, 28), CancellationToken.None)!.Symbol.Identity.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hybrid_edges_follow_selected_source_and_duplicate_local_targets_remain_ambiguous(bool preferWorkspace)
    {
        var rules = new ResolutionOptions(DefaultDatabase: "Fixture", DefaultSchema: "dbo");
        var local = await Parse("CREATE PROC dbo.P AS EXEC dbo.Q;\nGO\nCREATE PROC dbo.Q AS SELECT 1;");
        var database = await Parse("CREATE PROC dbo.Q AS SELECT 2;", kind: SqlSourceKind.Database);
        database = database with { Uri = new("file:///catalog/q.sql") };
        var declaration = Assert.Single(database.Parse.Declarations);
        var identity = new SqlObjectIdentity("dev", null, "Fixture", "dbo", "Q", SqlObjectKind.Procedure);
        var symbol = new IndexedSqlSymbol(identity, declaration, new(database.Uri, declaration.SelectionRange, identity, SqlSourceKind.Database, "Database module AST"));
        var policy = new SqlHybridPolicy([new("dev", "Fixture", rules)], [new("project", ["dev"], "dev")], preferWorkspace);
        var index = new SqlNavigationIndex([local, database], uri => uri == database.Uri ? "dev" : "project", rules, [symbol], hybrid: policy);
        var node = Node(index, local, "dbo.P");
        Assert.Equal(preferWorkspace ? Uri : database.Uri, Assert.Single(index.OutgoingCalls(node, CancellationToken.None)).Node.Symbol.Target.Uri);
        var duplicate = local with { Uri = new("file:///project/duplicate.sql") };
        index = new([local, duplicate, database], uri => uri == database.Uri ? "dev" : "project", rules, [symbol], hybrid: policy);
        Assert.Null(index.PrepareCallHierarchy(Uri, node.Symbol.Target.Range.Start, CancellationToken.None));
    }
}
