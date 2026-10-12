using SqlLsp.Core;
using SqlLsp.Providers.SqlServer;
using SqlLsp.Server.IntegrationTests;

namespace SqlLsp.SqlServer.IntegrationTests;

[Collection("Disposable SQL Server")]
public class LiveCatalogTests(SqlServerFixture fixture)
{
    [SqlServerFact]
    public async Task Least_privilege_catalog_reader_discovers_metadata_definitions_and_dependencies()
    {
        var result = await new SqlServerCatalogReader(_ => fixture.CatalogConnection).ReadAsync(fixture.Profile, CancellationToken.None);
        Assert.Equal("SqlLspFixture", result.Database);
        Assert.Equal("Latin1_General_100_CI_AS", result.Collation);
        Assert.Equal("dbo", result.DefaultSchema);
        Assert.StartsWith("16.", result.ServerVersion);
        Assert.True(result.Comparer.Equals("payroll", "Payroll"));
        Assert.True(DateTimeOffset.UtcNow - result.CapturedAt < TimeSpan.FromMinutes(1));
        var payroll = result.Objects.Single(o => o.Schema == "dbo" && o.Name == "Payroll");
        Assert.Equal(3, payroll.Columns.Length);
        Assert.Equal("decimal", payroll.Columns[2].Type);
        Assert.Equal(12, payroll.Columns[2].Precision);
        Assert.Equal(2, payroll.Columns[2].Scale);
        Assert.True(payroll.Columns[1].Nullable);
        var proc = result.Objects.Single(o => o.Name == "ProcessPayroll");
        Assert.Equal(3, proc.Parameters.Length);
        Assert.True(proc.Parameters.Single(p => p.Name == "@Result").Output);
        Assert.Equal(80, proc.Parameters.Single(p => p.Name == "@Note").MaxLength);
        Assert.Contains("SELECT * FROM dbo.Payroll", proc.Definition);
        Assert.Contains(proc.Dependencies, d => d.Schema == "dbo" && d.Name == "Payroll" && !d.Ambiguous);
        Assert.Null(result.Objects.Single(o => o.Name == "EncryptedPayroll").Definition);
        Assert.Contains("OriginalName", result.Objects.Single(o => o.Name == "RenamedPayroll").Definition);
        Assert.Equal(SqlObjectKind.View, result.Objects.Single(o => o.Name == "PayrollView").Kind);
        var function = result.Objects.Single(o => o.Name == "PayrollTotal");
        Assert.Equal(SqlObjectKind.Function, function.Kind);
        Assert.Contains(function.Parameters, p => p.Name == "@id");
    }

    [SqlServerFact]
    public async Task Restricted_account_returns_only_visible_objects_and_safe_permission_status()
    {
        var result = await new SqlServerCatalogReader(_ => fixture.LimitedConnection).ReadAsync(fixture.Profile, CancellationToken.None);
        var proc = Assert.Single(result.Objects);
        Assert.Equal("ProcessPayroll", proc.Name);
        Assert.Null(proc.Definition);
        Assert.Empty(proc.Dependencies);
        Assert.Equal(CatalogStatus.Partial, result.Status);
        Assert.Contains("dependency-permission-denied", result.Coverage);
        Assert.Contains("module-source-unavailable", result.Coverage);
    }

    [SqlServerFact]
    public async Task Object_schema_member_and_module_limits_are_reported_without_partial_sql_source()
    {
        var result = await new SqlServerCatalogReader(_ => fixture.CatalogConnection).ReadAsync(fixture.Profile with
        { Schemas = ["dbo"], MaxObjects = 2, MaxMembersPerObject = 1, MaxModuleCharacters = 1 }, CancellationToken.None);
        Assert.Equal(2, result.Objects.Length);
        Assert.All(result.Objects, o => Assert.Equal("dbo", o.Schema));
        Assert.Contains("object-limit", result.Coverage);
        Assert.Contains("column-limit", result.Coverage);
        var proc = Assert.Single(result.Objects.Where(o => o.Kind == SqlObjectKind.Procedure));
        Assert.True(proc.DefinitionTruncated);
        Assert.Null(proc.Definition);
        Assert.Contains("module-limit", result.Coverage);
        Assert.Contains("parameter-limit", result.Coverage);
    }

    [SqlServerFact]
    public async Task Schema_filters_are_parameter_values_and_cancelled_real_reads_do_not_publish_results()
    {
        var reader = new SqlServerCatalogReader(_ => fixture.CatalogConnection);
        var result = await reader.ReadAsync(fixture.Profile with { Schemas = ["dbo'); DROP TABLE dbo.Payroll; --"] }, CancellationToken.None);
        Assert.Empty(result.Objects);
        var actual = await reader.ReadAsync(fixture.Profile, CancellationToken.None);
        Assert.Contains(actual.Objects, o => o.Schema == "dbo" && o.Name == "Payroll");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(fixture.Profile, cancelled.Token));
    }

    [SqlServerFact]
    public async Task Online_wire_navigation_opens_readonly_files_resolves_modules_and_does_not_scan_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), "lsp-online-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? snapshotPath = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "local.sql"), "CREATE PROCEDURE dbo.LocalOnly AS SELECT 1;");
            await using (var client = new LspClient())
            {
                var options = new
                {
                    sql = new { mode = "online" },
                    databases = new[] { new { id = fixture.Profile.Id, enabled = true, connectionStringEnv = fixture.Profile.ConnectionStringEnv,
                        environment = "disposable-test", server = fixture.Endpoint, database = "SqlLspFixture", readOnly = true } }
                };
                await client.InitializeAsync(new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri, options);
                await client.NotifyAsync("initialized", new { });
                var search = await client.RequestAsync("workspace/symbol", new { query = "processpayroll" });
                var symbol = Assert.Single(search.GetProperty("result").EnumerateArray());
                var uri = symbol.GetProperty("location").GetProperty("uri").GetString()!;
                snapshotPath = new Uri(uri).LocalPath;
                Assert.True(File.Exists(snapshotPath));
                Assert.Contains("CREATE PROCEDURE", await File.ReadAllTextAsync(snapshotPath));
                var local = await client.RequestAsync("workspace/symbol", new { query = "LocalOnly" });
                Assert.Empty(local.GetProperty("result").EnumerateArray());
                var queryUri = new Uri(Path.Combine(root, "query.sql")).AbsoluteUri;
                await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri = queryUri, languageId = "sql", version = 1, text = "EXEC dbo.processpayroll;" } });
                await client.DiagnosticsAsync(queryUri, 1);
                var definition = await client.RequestAsync("textDocument/definition", new { textDocument = new { uri = queryUri }, position = new { line = 0, character = 12 } });
                var location = definition.GetProperty("result");
                if (location.ValueKind == System.Text.Json.JsonValueKind.Array) location = Assert.Single(location.EnumerateArray());
                Assert.Equal(new Uri(uri), new Uri(location.GetProperty("uri").GetString()!));
                var aliasUri = new Uri(Path.Combine(root, "alias-query.sql")).AbsoluteUri;
                await client.NotifyAsync("textDocument/didOpen", new
                {
                    textDocument = new
                    {
                        uri = aliasUri,
                        languageId = "sql",
                        version = 1,
                        text = "SELECT p.PayrollId FROM dbo.Payroll AS P;"
                    }
                });
                await client.DiagnosticsAsync(aliasUri, 1);
                var aliasDefinition = await client.RequestAsync("textDocument/definition", new { textDocument = new { uri = aliasUri }, position = new { line = 0, character = 7 } });
                var aliasTarget = aliasDefinition.GetProperty("result");
                if (aliasTarget.ValueKind == System.Text.Json.JsonValueKind.Array) aliasTarget = Assert.Single(aliasTarget.EnumerateArray());
                var payrollFile = new Uri(aliasTarget.GetProperty("uri").GetString()!).LocalPath;
                Assert.Contains("Column 1: PayrollId", await File.ReadAllTextAsync(payrollFile));
                var range = symbol.GetProperty("location").GetProperty("range");
                var references = await client.RequestAsync("textDocument/references", new { textDocument = new { uri }, position = range.GetProperty("start"), context = new { includeDeclaration = false } });
                Assert.Equal(2, references.GetProperty("result").GetArrayLength()); // opened caller and deployed RunPayroll
                await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sql", version = 1, text = "CREATE PROCEDURE dbo.Forged AS SELECT 1;" } });
                var documentSymbols = await client.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri } });
                Assert.Equal("dbo.ProcessPayroll", Assert.Single(documentSymbols.GetProperty("result").EnumerateArray()).GetProperty("name").GetString());
                await client.StopAsync();
            }
            Assert.False(File.Exists(snapshotPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
