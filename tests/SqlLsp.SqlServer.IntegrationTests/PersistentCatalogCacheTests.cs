using System.Collections.Immutable;
using System.Text.Json;
using SqlLsp.Core;
using SqlLsp.Mcp.IntegrationTests;
using SqlLsp.Providers.SqlServer;

namespace SqlLsp.SqlServer.IntegrationTests;

public class PersistentCatalogContextTests
{
    [Fact]
    public async Task Catalog_templates_rebind_new_private_files_revisions_and_capture_times_and_include_comparison_rules()
    {
        var catalog = ProviderTests.Snapshot;
        using var oldFiles = new CatalogDocuments();
        using var newFiles = new CatalogDocuments();
        var first = await oldFiles.BuildAsync(catalog, new(), CancellationToken.None);
        var later = await newFiles.BuildAsync(catalog with { CapturedAt = catalog.CapturedAt.AddSeconds(10) }, new(), CancellationToken.None);
        SqlNavigationIndex Index(CatalogNavigationSnapshot snapshot, StringComparer? comparer = null) => new(snapshot.Documents, _ => "fixture",
            new(DefaultDatabase: catalog.Database, IdentifierComparer: comparer ?? new CatalogIdentifierComparer(1033, 1, catalog.Collation)), snapshot.Symbols, indexDeclarations: false);
        var oldIndex = Index(first); var newIndex = Index(later);
        var oldContext = oldIndex.CreateAnalysisCacheContext("config", CancellationToken.None);
        var newContext = newIndex.CreateAnalysisCacheContext("config", CancellationToken.None);
        Assert.Equal(oldContext.Fingerprint, newContext.Fingerprint);
        var oldSymbol = first.Symbols.Single(s => s.Identity.Name == "CurrentName");
        var newSymbol = later.Symbols.Single(s => s.Identity.Name == "CurrentName");
        Assert.Equal(oldContext.RequestKey(oldSymbol.Target.Uri, oldSymbol.Target.Range.Start, 2, 20), newContext.RequestKey(newSymbol.Target.Uri, newSymbol.Target.Range.Start, 2, 20));
        var restored = newContext.Restore(oldContext.Normalize(oldIndex.AnalyzeProcedure(oldSymbol.Target.Uri, oldSymbol.Target.Range.Start, CancellationToken.None)));
        var fresh = newIndex.AnalyzeProcedure(newSymbol.Target.Uri, newSymbol.Target.Range.Start, CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(fresh, SqlAnalysisCacheContext.JsonOptions), JsonSerializer.Serialize(restored, SqlAnalysisCacheContext.JsonOptions));
        Assert.DoesNotContain(oldFiles.Root.Replace('\\', '/'), JsonSerializer.Serialize(restored, SqlAnalysisCacheContext.JsonOptions).Replace('\\', '/'));
        Assert.NotEqual(oldContext.Fingerprint, Index(first, new CatalogIdentifierComparer(1033, 0, catalog.Collation)).CreateAnalysisCacheContext("config", CancellationToken.None).Fingerprint);
        using var hiddenFiles = new CatalogDocuments();
        var hidden = await hiddenFiles.BuildAsync(catalog with { Objects = catalog.Objects.Select(o => o with { Definition = null }).ToImmutableArray() }, new(), CancellationToken.None);
        Assert.NotEqual(oldContext.Fingerprint, Index(hidden).CreateAnalysisCacheContext("config", CancellationToken.None).Fingerprint);
    }
}

[Collection("Disposable SQL Server")]
public class LivePersistentCatalogCacheTests(SqlServerFixture fixture)
{
    [SqlServerFact]
    public async Task Real_mcp_process_restart_reuses_catalog_report_after_live_validation_and_rebases_all_anchors()
    {
        var root = Path.Combine(TestPaths.TempRoot, "catalog-cache 中文 " + Guid.NewGuid().ToString("N"));
        var config = JsonSerializer.Serialize(new
        {
            sql = new { mode = "online" },
            databases = new[] { new { id = "fixture", enabled = true, readOnly = true, environment = "owned-test", server = fixture.Endpoint,
                database = "SqlLspFixture", connectionStringEnv = fixture.Profile.ConnectionStringEnv } }
        });
        try
        {
            string? oldUri = null, oldRevision = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var client = await McpClient.Start("-- not scanned in online mode", config, Path.Combine(root, "saved"), root, true);
                var selected = McpClient.Data(await client.Tool("find_procedures", new { query = "dbo.ValueCaller" })).GetProperty("modules")[0];
                var uri = selected.GetProperty("target").GetProperty("uri").GetString();
                var result = McpClient.Data(await client.Tool("analyze_procedure", new
                {
                    documentUri = uri,
                    line = selected.GetProperty("selection").GetProperty("line").GetInt32(),
                    character = selected.GetProperty("selection").GetProperty("character").GetInt32(),
                    expectedRevision = selected.GetProperty("revision").GetString()
                }));
                Assert.Equal(attempt == 0 ? "Stored" : "Hit", result.GetProperty("cache").GetProperty("status").GetString());
                var module = result.GetProperty("report").GetProperty("modules")[0];
                Assert.Equal(uri, module.GetProperty("definition").GetProperty("uri").GetString());
                var call = Assert.Single(module.GetProperty("valueFlow").GetProperty("calls").EnumerateArray());
                var target = result.GetProperty("report").GetProperty("modules")[1];
                Assert.Equal(target.GetProperty("id").GetString(), call.GetProperty("targetModuleId").GetString());
                Assert.Equal(target.GetProperty("revision").GetString(), call.GetProperty("targetRevision").GetString());
                if (attempt == 1)
                {
                    Assert.NotEqual(oldUri, uri);
                    Assert.NotEqual(oldRevision, module.GetProperty("revision").GetString());
                    Assert.False(File.Exists(new Uri(oldUri!).LocalPath));
                }
                oldUri = uri; oldRevision = module.GetProperty("revision").GetString();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
