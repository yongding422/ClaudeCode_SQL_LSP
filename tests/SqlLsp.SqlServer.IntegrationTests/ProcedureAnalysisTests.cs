using System.Collections.Immutable;
using SqlLsp.Core;
using SqlLsp.Providers.SqlServer;

namespace SqlLsp.SqlServer.IntegrationTests;

public class CatalogProcedureAnalysisTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hybrid_operation_targets_follow_selected_source_and_preserve_workspace_module_provenance(bool preferWorkspace)
    {
        using var storage = new CatalogDocuments();
        var snapshot = await storage.BuildAsync(ProviderTests.Snapshot, new(), CancellationToken.None);
        var uri = new Uri("file:///workspace/modules.sql");
        var rules = new ResolutionOptions(CaseSensitive: false, DefaultDatabase: "Fixture", DefaultSchema: "dbo");
        var local = new DocumentSnapshot(uri, null, SqlSourceKind.Workspace,
            await new SqlParser(new(), rules).ParseAsync(new SourceText("CREATE PROC dbo.P AS SELECT * FROM dbo.Payroll;\nGO\nCREATE TABLE dbo.Payroll(id int);"), CancellationToken.None));
        var policy = new SqlHybridPolicy([new("fixture", "Fixture", rules)], [new("workspace", ["fixture"], "fixture")], preferWorkspace);
        var index = new SqlNavigationIndex(snapshot.Documents.Add(local), u => u == uri ? "workspace" : "fixture", rules, snapshot.Symbols, hybrid: policy);
        var report = index.AnalyzeProcedure(uri, new(0, 16), CancellationToken.None);
        var module = Assert.Single(report.Modules);
        Assert.Equal(SqlSourceKind.Workspace, module.SourceKind);
        var access = Assert.Single(module.Accesses);
        Assert.Equal(ResolutionStatus.Resolved, access.Status);
        Assert.Equal(preferWorkspace ? SqlSourceKind.Workspace : SqlSourceKind.Database, access.Target!.SourceKind);
        Assert.Equal("Payroll", access.Target.Identity!.Name);
    }

    [Fact]
    public async Task Authoritative_renamed_identity_and_hidden_source_remain_distinct_from_parsed_header()
    {
        var catalog = ProviderTests.Snapshot with
        {
            Objects = ProviderTests.Snapshot.Objects.Select(o => o.ObjectId == 1
                ? o with { Definition = "CREATE PROC dbo.OldName AS EXEC dbo.Hidden;" } : o).ToImmutableArray()
        };
        using var storage = new CatalogDocuments();
        var snapshot = await storage.BuildAsync(catalog, new(), CancellationToken.None);
        var index = new SqlNavigationIndex(snapshot.Documents, _ => "fixture", new(DefaultDatabase: "Fixture"), snapshot.Symbols, indexDeclarations: false);
        var caller = snapshot.Symbols.Single(s => s.Identity.Name == "CurrentName");
        var report = index.AnalyzeProcedure(caller.Target.Uri, caller.Target.Range.Start, CancellationToken.None);
        Assert.Equal(2, report.Modules.Length);
        Assert.Equal("CurrentName", report.Modules[0].Identity.Name);
        Assert.True(report.Modules[0].BodyAvailable);
        Assert.Equal(SqlSourceKind.Database, report.Modules[0].SourceKind);
        var hidden = report.Modules.Single(m => m.Identity.Name == "Hidden");
        Assert.False(hidden.BodyAvailable);
        Assert.Contains(hidden.Issues, i => i.Code == "body-unavailable");
        Assert.Empty(hidden.Accesses);
        Assert.NotNull(hidden.CapturedAt);
    }
}

[Collection("Disposable SQL Server")]
public class LiveProcedureAnalysisTests(SqlServerFixture fixture)
{
    [SqlServerFact]
    public async Task Real_catalog_report_contains_verified_reads_and_encrypted_body_unknowns()
    {
        var catalog = await new SqlServerCatalogReader(_ => fixture.CatalogConnection).ReadAsync(fixture.Profile, CancellationToken.None);
        using var storage = new CatalogDocuments();
        var snapshot = await storage.BuildAsync(catalog, new(), CancellationToken.None);
        var index = new SqlNavigationIndex(snapshot.Documents, _ => fixture.Profile.Id,
            new(DefaultDatabase: fixture.Profile.Database, DefaultSchema: "dbo", IdentifierComparer: catalog.Comparer), snapshot.Symbols, indexDeclarations: false);
        var proc = snapshot.Symbols.Single(s => s.Identity.Name == "ProcessPayroll");
        var report = index.AnalyzeProcedure(proc.Target.Uri, proc.Target.Range.Start, CancellationToken.None);
        Assert.Contains(report.Modules[0].Accesses, a => a.Operation == SqlAccessOperation.Read && a.Status == ResolutionStatus.Resolved);
        var encrypted = snapshot.Symbols.Single(s => s.Identity.Name == "EncryptedPayroll");
        var unavailable = index.AnalyzeProcedure(encrypted.Target.Uri, encrypted.Target.Range.Start, CancellationToken.None);
        Assert.Contains(unavailable.Modules[0].Issues, i => i.Code == "body-unavailable");
    }
}
