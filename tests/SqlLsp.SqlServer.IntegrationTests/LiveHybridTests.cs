using System.Text.Json;
using SqlLsp.Providers.SqlServer;
using SqlLsp.Server.IntegrationTests;

namespace SqlLsp.SqlServer.IntegrationTests;

[Collection("Disposable SQL Server")]
public class LiveHybridTests(SqlServerFixture fixture)
{
    private object Options(string root, bool preferLocal = true, int ttl = 300) => new
    {
        sql = new { mode = "hybrid" },
        databases = new[] { new { id = fixture.Profile.Id, enabled = true, environment = "disposable-test",
            connectionStringEnv = fixture.Profile.ConnectionStringEnv, server = fixture.Endpoint, database = "SqlLspFixture",
            readOnly = true, cacheTtlSeconds = ttl, connectTimeoutSeconds = ttl == 1 ? 1 : 5,
            commandTimeoutSeconds = ttl == 1 ? 1 : 5, loadTimeoutSeconds = ttl == 1 ? 3 : 10 } },
        resolution = new
        {
            preferWorkspaceDefinitions = preferLocal,
            workspaceMappings = new[] {
            new { workspaceRoot = root, databaseProfiles = new[] { fixture.Profile.Id }, defaultDatabaseProfile = fixture.Profile.Id } }
        }
    };
    private static async Task Open(LspClient client, string uri, string text, int version = 1)
    {
        await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sql", version, text } });
        await client.DiagnosticsAsync(uri, version);
    }
    private static async Task<JsonElement> Definition(LspClient client, string uri, int character = 12)
    {
        var response = await client.RequestAsync("textDocument/definition", new { textDocument = new { uri }, position = new { line = 0, character } });
        var result = response.GetProperty("result");
        return result.ValueKind == JsonValueKind.Array && result.GetArrayLength() == 1 ? result[0] : result;
    }

    [SqlServerFact]
    public async Task Local_and_unsaved_definitions_win_catalog_fallback_and_drift_references_survive_close()
    {
        var root = Path.Combine(TestPaths.TempRoot, "hybrid-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = await new SqlServerCatalogReader(_ => fixture.CatalogConnection).ReadAsync(fixture.Profile, CancellationToken.None);
            var original = snapshot.Objects.Single(o => o.Name == "ProcessPayroll").Definition!;
            var localSql = "-- local formatting\n" + original.Replace("CREATE PROCEDURE", "CREATE OR ALTER PROCEDURE");
            var procPath = Path.Combine(root, "proc.sql");
            await File.WriteAllTextAsync(procPath, localSql);
            await File.WriteAllTextAsync(Path.Combine(root, "encrypted.sql"), "CREATE PROCEDURE dbo.EncryptedPayroll AS SELECT 123;");
            var rootUri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
            var procUri = new Uri(procPath).AbsoluteUri;
            var queryUri = new Uri(Path.Combine(root, "query.sql")).AbsoluteUri;
            await using var client = new LspClient();
            await client.InitializeAsync(rootUri, Options(rootUri));
            await client.NotifyAsync("initialized", new { });
            await Open(client, queryUri, "EXEC dbo.ProcessPayroll;");
            Assert.Equal(new Uri(procUri), new Uri((await Definition(client, queryUri)).GetProperty("uri").GetString()!));
            var localHover = await Hover(client, queryUri);
            Assert.Contains("Source: Workspace", localHover);
            Assert.Contains("Definition comparison: Match", localHover);
            Assert.Contains("Comparison catalog captured:", localHover);
            var status = await client.NotificationAsync("window/logMessage");
            Assert.Contains("captured", status.GetProperty("message").GetString());
            var searched = await client.RequestAsync("workspace/symbol", new { query = "ProcessPayroll" });
            var candidates = searched.GetProperty("result").EnumerateArray().ToArray();
            Assert.Equal(2, candidates.Length);
            var local = Assert.Single(candidates, c => c.GetProperty("data").GetProperty("source").GetString() == "Workspace");
            Assert.Equal("Match", local.GetProperty("data").GetProperty("drift").GetString());
            var deployed = Assert.Single(candidates, c => c.GetProperty("data").GetProperty("source").GetString() == "Database");
            Assert.True(File.Exists(new Uri(deployed.GetProperty("location").GetProperty("uri").GetString()!).LocalPath));
            var hidden = await client.RequestAsync("workspace/symbol", new { query = "EncryptedPayroll" });
            Assert.Equal("Unavailable", hidden.GetProperty("result").EnumerateArray().Single(c =>
                c.GetProperty("data").GetProperty("source").GetString() == "Workspace").GetProperty("data").GetProperty("drift").GetString());

            await Open(client, procUri, localSql.Replace("SET @Result=1", "SET @Result=2"));
            var changed = await client.RequestAsync("workspace/symbol", new { query = "ProcessPayroll" });
            var buffer = Assert.Single(changed.GetProperty("result").EnumerateArray(), c => c.GetProperty("data").GetProperty("source").GetString() == "UnsavedBuffer");
            Assert.Equal("Different", buffer.GetProperty("data").GetProperty("drift").GetString());
            Assert.Equal(new Uri(procUri), new Uri((await Definition(client, queryUri)).GetProperty("uri").GetString()!));
            var bufferHover = await Hover(client, queryUri);
            Assert.Contains("Source: UnsavedBuffer", bufferHover);
            Assert.Contains("Definition comparison: Different", bufferHover);
            var references = await client.RequestAsync("textDocument/references", new
            {
                textDocument = new { uri = procUri },
                position = buffer.GetProperty("location").GetProperty("range").GetProperty("start"),
                context = new { includeDeclaration = false }
            });
            Assert.Equal(2, references.GetProperty("result").GetArrayLength());
            Assert.Contains(references.GetProperty("result").EnumerateArray(), r => new Uri(r.GetProperty("uri").GetString()!).LocalPath.Contains("sqlscriptdom-lsp-", StringComparison.Ordinal));
            await Open(client, queryUri, "SELECT * FROM dbo.Payroll;", 2);
            var table = await Definition(client, queryUri, 20);
            Assert.Contains("Column 1: PayrollId", await File.ReadAllTextAsync(new Uri(table.GetProperty("uri").GetString()!).LocalPath));
            await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = procUri } });
            await client.DiagnosticsAsync(procUri);
            var closed = await client.RequestAsync("workspace/symbol", new { query = "ProcessPayroll" });
            Assert.Equal("Match", closed.GetProperty("result").EnumerateArray().Single(c =>
                c.GetProperty("data").GetProperty("source").GetString() == "Workspace").GetProperty("data").GetProperty("drift").GetString());
            await client.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [SqlServerFact]
    public async Task Configured_database_preference_selects_deployed_source_and_local_duplicates_still_prevent_a_jump()
    {
        var root = Path.Combine(TestPaths.TempRoot, "hybrid-preference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "proc.sql"), "CREATE PROCEDURE dbo.ProcessPayroll AS SELECT 1;");
            var rootUri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
            var queryUri = new Uri(Path.Combine(root, "query.sql")).AbsoluteUri;
            await using var client = new LspClient();
            await client.InitializeAsync(rootUri, Options(rootUri, preferLocal: false));
            await client.NotifyAsync("initialized", new { });
            await Open(client, queryUri, "EXEC dbo.ProcessPayroll;");
            var deployed = await Definition(client, queryUri);
            Assert.Contains("sqlscriptdom-lsp-", new Uri(deployed.GetProperty("uri").GetString()!).LocalPath);
            var deployedHover = await Hover(client, queryUri);
            Assert.Contains("Source: Database", deployedHover);
            Assert.Contains("@PayrollId [sys].[int]", deployedHover);
            Assert.Contains("Definition comparison: Different", deployedHover);
            var duplicate = Path.Combine(root, "duplicate.sql");
            await File.WriteAllTextAsync(duplicate, "ALTER PROCEDURE dbo.ProcessPayroll AS SELECT 2;");
            await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = new Uri(duplicate).AbsoluteUri, type = 1 } } });
            Assert.Empty((await Definition(client, queryUri)).EnumerateArray());
            Assert.Equal(JsonValueKind.Null, (await client.RequestAsync("textDocument/hover", new
            {
                textDocument = new { uri = queryUri },
                position = new { line = 0, character = 12 }
            })).GetProperty("result").ValueKind);
            await client.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [SqlServerFact]
    public async Task Multiple_roots_keep_profile_contexts_isolated_and_one_failed_profile_does_not_remove_other_metadata()
    {
        var parent = Path.Combine(TestPaths.TempRoot, "hybrid-multi-" + Guid.NewGuid().ToString("N"));
        var good = Path.Combine(parent, "good"); var failed = Path.Combine(parent, "failed");
        Directory.CreateDirectory(good); Directory.CreateDirectory(failed);
        try
        {
            var local = Path.Combine(failed, "proc.sql");
            await File.WriteAllTextAsync(local, "CREATE PROCEDURE dbo.ProcessPayroll AS SELECT 1;");
            var roots = new[] { new Uri(good + Path.DirectorySeparatorChar).AbsoluteUri, new Uri(failed + Path.DirectorySeparatorChar).AbsoluteUri };
            await using var client = new LspClient();
            await client.InitializeAsync(workspaceFolderUris: roots, initializationOptions: new
            {
                sql = new { mode = "hybrid" },
                databases = new[] {
                    new { id = fixture.Profile.Id, enabled = true, environment = "disposable-test", connectionStringEnv = fixture.Profile.ConnectionStringEnv,
                        server = fixture.Endpoint, database = "SqlLspFixture", readOnly = true },
                    new { id = "failed", enabled = true, environment = "disposable-test", connectionStringEnv = "SQLLSP_HYBRID_INTENTIONALLY_ABSENT",
                        server = "127.0.0.1,1", database = "DifferentDatabase", readOnly = true }
                },
                resolution = new
                {
                    workspaceMappings = new[] {
                    new { workspaceRoot = roots[0], databaseProfiles = new[] { fixture.Profile.Id } },
                    new { workspaceRoot = roots[1], databaseProfiles = new[] { "failed" } }
                }
                }
            });
            await client.NotifyAsync("initialized", new { });
            var goodQuery = new Uri(Path.Combine(good, "query.sql")).AbsoluteUri;
            var failedQuery = new Uri(Path.Combine(failed, "query.sql")).AbsoluteUri;
            await Open(client, goodQuery, "EXEC dbo.ProcessPayroll;");
            Assert.Contains("sqlscriptdom-lsp-", new Uri((await Definition(client, goodQuery)).GetProperty("uri").GetString()!).LocalPath);
            await Open(client, failedQuery, "EXEC dbo.ProcessPayroll;");
            Assert.Equal(new Uri(local), new Uri((await Definition(client, failedQuery)).GetProperty("uri").GetString()!));
            await Open(client, failedQuery, "SELECT * FROM dbo.Payroll;", 2);
            Assert.Empty((await Definition(client, failedQuery, 20)).EnumerateArray());
            var searched = await client.RequestAsync("workspace/symbol", new { query = "ProcessPayroll" });
            Assert.Equal(2, searched.GetProperty("result").GetArrayLength());
            await client.StopAsync();
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [SqlServerFact]
    public async Task Real_database_outage_after_cache_expiry_preserves_local_navigation_and_removes_deployed_targets()
    {
        var root = Path.Combine(TestPaths.TempRoot, "hybrid-outage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stopped = false;
        try
        {
            var procPath = Path.Combine(root, "proc.sql");
            await File.WriteAllTextAsync(procPath, "CREATE PROCEDURE dbo.ProcessPayroll AS SELECT 1;");
            var rootUri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
            var queryUri = new Uri(Path.Combine(root, "query.sql")).AbsoluteUri;
            await using var client = new LspClient();
            await client.InitializeAsync(rootUri, Options(rootUri, ttl: 1));
            await client.NotifyAsync("initialized", new { });
            var before = await client.RequestAsync("workspace/symbol", new { query = "ProcessPayroll" });
            Assert.Equal(2, before.GetProperty("result").GetArrayLength());
            await fixture.StopServerAsync(); stopped = true;
            await Task.Delay(1100);
            await Open(client, queryUri, "EXEC dbo.ProcessPayroll;");
            Assert.Equal(new Uri(procPath), new Uri((await Definition(client, queryUri)).GetProperty("uri").GetString()!));
            var fallbackHover = await Hover(client, queryUri);
            Assert.Contains("Source: Workspace", fallbackHover);
            Assert.DoesNotContain("Catalog captured:", fallbackHover);
            var after = await client.RequestAsync("workspace/symbol", new { query = "ProcessPayroll" });
            Assert.Equal("Unavailable", Assert.Single(after.GetProperty("result").EnumerateArray()).GetProperty("data").GetProperty("drift").GetString());
            await Open(client, queryUri, "SELECT * FROM dbo.Payroll;", 2);
            Assert.Empty((await Definition(client, queryUri, 20)).EnumerateArray());
            Assert.Equal(JsonValueKind.Null, (await client.RequestAsync("textDocument/hover", new
            {
                textDocument = new { uri = queryUri },
                position = new { line = 0, character = 20 }
            })).GetProperty("result").ValueKind);
            await client.StopAsync();
        }
        finally
        {
            if (stopped) await fixture.StartServerAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> Hover(LspClient client, string uri) =>
        (await client.RequestAsync("textDocument/hover", new { textDocument = new { uri }, position = new { line = 0, character = 12 } }))
            .GetProperty("result").GetProperty("contents").GetProperty("value").GetString()!;
}
