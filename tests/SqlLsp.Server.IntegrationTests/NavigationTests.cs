using System.Diagnostics;
using System.Text.Json;

namespace SqlLsp.Server.IntegrationTests;

public class NavigationTests
{
    [Fact]
    public async Task Cross_file_definition_references_workspace_search_and_unsaved_changes_work_over_stdio()
    {
        using var fixture = new NavigationWorkspace();
        var procedure = fixture.Write("procedure.sql", "CREATE PROCEDURE dbo.ProcessPayroll AS SELECT 1;");
        var table = fixture.Write("table.sql", "CREATE TABLE dbo.Employee (EmployeeId int);");
        fixture.Write("function.sql", "CREATE FUNCTION dbo.AdjustPay(@n int) RETURNS int AS BEGIN RETURN @n+1; END;");
        const string sql = "EXEC dbo.ProcessPayroll;\nSELECT e.EmployeeId FROM dbo.Employee e WHERE dbo.AdjustPay(e.EmployeeId)>0;";
        var use = fixture.Write("uses.sql", sql);
        await using var client = new LspClient();
        var initialized = await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
        var capabilities = initialized.GetProperty("result").GetProperty("capabilities");
        foreach (var name in new[] { "definitionProvider", "referencesProvider", "workspaceSymbolProvider" })
            Assert.True(capabilities.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.Object);
        await client.NotifyAsync("initialized", new { });
        var symbols = await SearchAsync(client, "");
        Assert.Equal(3, symbols.GetArrayLength());
        var definition = Assert.Single((await DefinitionAsync(client, use, sql, "dbo.ProcessPayroll")).EnumerateArray());
        AssertUri(procedure, definition.GetProperty("uri"));
        Assert.Equal(17, definition.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        AssertUri(table, (await DefinitionAsync(client, use, sql, "e.EmployeeId"))[0].GetProperty("uri"));
        var procedureUses = await ReferencesAsync(client, use, sql, "dbo.ProcessPayroll", false);
        Assert.Single(procedureUses.EnumerateArray());
        AssertUri(use, procedureUses[0].GetProperty("uri"));
        Assert.Equal(2, (await ReferencesAsync(client, use, sql, "dbo.ProcessPayroll", true)).GetArrayLength());
        var tableSql = await File.ReadAllTextAsync(table);
        var tableUses = await ReferencesAsync(client, table, tableSql, "dbo.Employee", false);
        Assert.Equal(4, tableUses.GetArrayLength());
        Assert.All(tableUses.EnumerateArray(), location => AssertUri(use, location.GetProperty("uri")));
        await client.NotifyAsync("textDocument/didOpen", new
        { textDocument = new { uri = new Uri(procedure).AbsoluteUri, languageId = "sql", version = 1, text = await File.ReadAllTextAsync(procedure) } });
        await client.DiagnosticsAsync(new Uri(procedure).AbsoluteUri, 1);
        await client.NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri = new Uri(procedure).AbsoluteUri, version = 2 },
            contentChanges = new[] { new { text = "CREATE PROCEDURE dbo.UnsavedPayroll AS SELECT 1;" } }
        });
        await client.DiagnosticsAsync(new Uri(procedure).AbsoluteUri, 2);
        Assert.Single((await SearchAsync(client, "UnsavedPayroll")).EnumerateArray());
        Assert.Empty((await DefinitionAsync(client, use, sql, "dbo.ProcessPayroll")).EnumerateArray());
        await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = new Uri(procedure).AbsoluteUri } });
        await client.DiagnosticsAsync(new Uri(procedure).AbsoluteUri);
        AssertUri(procedure, (await DefinitionAsync(client, use, sql, "dbo.ProcessPayroll"))[0].GetProperty("uri"));
        await client.StopAsync();
    }

    [Fact]
    public async Task Ambiguous_and_missing_targets_have_no_definition_or_references()
    {
        using var fixture = new NavigationWorkspace();
        fixture.Write("dbo.sql", "CREATE PROCEDURE dbo.ProcessPayroll AS SELECT 1;");
        fixture.Write("sales.sql", "CREATE PROCEDURE sales.ProcessPayroll AS SELECT 1;");
        const string sql = "EXEC ProcessPayroll; EXEC sales.ProcessPayroll; EXEC dbo.Missing; EXEC(N'EXEC dbo.ProcessPayroll');";
        var use = fixture.Write("use.sql", sql);
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
        await client.NotifyAsync("initialized", new { });
        Assert.Equal(2, (await SearchAsync(client, "ProcessPayroll")).GetArrayLength());
        Assert.Empty((await DefinitionAsync(client, use, sql, "ProcessPayroll")).EnumerateArray());
        Assert.Empty((await ReferencesAsync(client, use, sql, "ProcessPayroll", true)).EnumerateArray());
        Assert.Single((await DefinitionAsync(client, use, sql, "sales.ProcessPayroll")).EnumerateArray());
        Assert.Empty((await DefinitionAsync(client, use, sql, "dbo.Missing")).EnumerateArray());
        Assert.Empty((await DefinitionAsync(client, use, sql, "dbo.ProcessPayroll")).EnumerateArray());
        Assert.Single((await ReferencesAsync(client, use, sql, "sales.ProcessPayroll", false)).EnumerateArray());
        await client.StopAsync();
    }

    [Fact]
    public async Task Cte_local_definition_and_alias_binding_are_not_counted_as_global_table_references()
    {
        using var fixture = new NavigationWorkspace();
        var table = fixture.Write("table.sql", "CREATE TABLE dbo.Employee (id int);");
        const string sql = "WITH Employee AS (SELECT 1 AS id) SELECT e.id FROM Employee e;\nSELECT a.id FROM dbo.Employee a;";
        var use = fixture.Write("cte.sql", sql);
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
        await client.NotifyAsync("initialized", new { });
        var local = (await DefinitionAsync(client, use, sql, "e.id"))[0];
        AssertUri(use, local.GetProperty("uri"));
        Assert.Equal(5, local.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        AssertUri(table, (await DefinitionAsync(client, use, sql, "a.id"))[0].GetProperty("uri"));
        var refs = await ReferencesAsync(client, table, await File.ReadAllTextAsync(table), "dbo.Employee", false);
        Assert.Equal(3, refs.GetArrayLength());
        Assert.All(refs.EnumerateArray(), r => Assert.Equal(1, r.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()));
        await client.StopAsync();
    }

    [Fact]
    public async Task Explicit_schema_and_case_options_and_quoted_names_are_honored()
    {
        using var fixture = new NavigationWorkspace();
        var declaration = fixture.Write("table.sql", "CREATE TABLE [schema.dot].[a]]b] (id int); CREATE TABLE dbo.Employee (id int); CREATE TABLE sales.Employee (id int);");
        const string sql = "SELECT * FROM [schema.dot].[a]]b]; SELECT * FROM employee;";
        var use = fixture.Write("use.sql", sql);
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri,
            new { resolution = new { caseSensitive = false, defaultSchema = "dbo" } });
        await client.NotifyAsync("initialized", new { });
        AssertUri(declaration, (await DefinitionAsync(client, use, sql, "[schema.dot].[a]]b]"))[0].GetProperty("uri"));
        var employee = Assert.Single((await DefinitionAsync(client, use, sql, "employee")).EnumerateArray());
        Assert.Equal((await File.ReadAllTextAsync(declaration)).IndexOf("dbo.Employee", StringComparison.Ordinal),
            employee.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        await client.StopAsync();
    }

    [Fact]
    public async Task Native_file_watcher_handles_create_change_file_and_directory_rename_delete_and_exclusions()
    {
        using var fixture = new NavigationWorkspace();
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
        await client.NotifyAsync("initialized", new { });
        var oldPath = fixture.Write("sub dir/original 中文.sql", "CREATE TABLE dbo.Watched (id int);");
        await EventuallyAsync(client, "Watched", symbols => symbols.GetArrayLength() == 1);
        fixture.Write("sub dir/original 中文.sql", "CREATE TABLE dbo.Changed (id int);");
        await EventuallyAsync(client, "Changed", symbols => symbols.GetArrayLength() == 1);
        var nextPath = Path.Combine(fixture.Root, "sub dir", "renamed 中文.sql");
        File.Move(oldPath, nextPath);
        await EventuallyAsync(client, "Changed", symbols => symbols.GetArrayLength() == 1 && SymbolUri(symbols[0]) == new Uri(nextPath));
        var newFolder = Path.Combine(fixture.Root, "renamed folder");
        Directory.Move(Path.Combine(fixture.Root, "sub dir"), newFolder);
        var finalPath = Path.Combine(newFolder, "renamed 中文.sql");
        await EventuallyAsync(client, "Changed", symbols => symbols.GetArrayLength() == 1 && SymbolUri(symbols[0]) == new Uri(finalPath));
        fixture.Write("bin/excluded.sql", "CREATE TABLE dbo.Excluded (id int);");
        File.Delete(finalPath);
        await EventuallyAsync(client, "", symbols => symbols.GetArrayLength() == 0);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_file_notifications_update_index_and_disk_deletion_preserves_unsaved_buffer_until_close()
    {
        using var fixture = new NavigationWorkspace();
        await using var client = new LspClient();
        await client.InitializeAsync(new Uri(fixture.Root).AbsoluteUri);
        await client.NotifyAsync("initialized", new { });
        var path = fixture.Write("space name.sql", "CREATE TABLE dbo.Created (id int);");
        var uri = new Uri(path).AbsoluteUri;
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri, type = 1 } } });
        Assert.Single((await SearchAsync(client, "Created")).EnumerateArray());
        fixture.Write("space name.sql", "CREATE TABLE dbo.DiskChanged (id int);");
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri, type = 2 } } });
        Assert.Single((await SearchAsync(client, "DiskChanged")).EnumerateArray());
        await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sql", version = 1, text = "CREATE TABLE dbo.Buffer (id int);" } });
        await client.DiagnosticsAsync(uri, 1);
        File.Delete(path);
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri, type = 3 } } });
        Assert.Single((await SearchAsync(client, "Buffer")).EnumerateArray());
        await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri } });
        await client.DiagnosticsAsync(uri);
        Assert.Empty((await SearchAsync(client, "")).EnumerateArray());
        await client.StopAsync();
    }

    [Fact]
    public async Task Multiple_workspace_roots_do_not_create_false_schema_collisions()
    {
        using var first = new NavigationWorkspace();
        using var second = new NavigationWorkspace();
        var target = first.Write("table.sql", "CREATE TABLE dbo.t (id int);");
        second.Write("table.sql", "CREATE TABLE dbo.t (id int);");
        const string sql = "SELECT * FROM dbo.t;";
        var use = first.Write("use.sql", sql);
        await using var client = new LspClient();
        await client.InitializeAsync(workspaceFolderUris: [new Uri(first.Root).AbsoluteUri, new Uri(second.Root).AbsoluteUri]);
        await client.NotifyAsync("initialized", new { });
        Assert.Equal(2, (await SearchAsync(client, "dbo.t")).GetArrayLength());
        AssertUri(target, Assert.Single((await DefinitionAsync(client, use, sql, "dbo.t")).EnumerateArray()).GetProperty("uri"));
        await client.StopAsync();
    }

    private static async Task<JsonElement> SearchAsync(LspClient client, string query) =>
        (await client.RequestAsync("workspace/symbol", new { query })).GetProperty("result");
    private static async Task<JsonElement> DefinitionAsync(LspClient client, string path, string sql, string name)
    {
        var parameters = PositionParameters(path, sql, name);
        var response = await client.RequestAsync("textDocument/definition", new { parameters.textDocument, parameters.position });
        Assert.False(response.TryGetProperty("error", out var error), error.ToString());
        var result = response.GetProperty("result");
        // LSP definition permits a single Location as well as an array of Locations.
        return result.ValueKind == JsonValueKind.Object ? JsonSerializer.SerializeToElement(new[] { result }) : result;
    }
    private static async Task<JsonElement> ReferencesAsync(LspClient client, string path, string sql, string name, bool includeDeclaration)
    {
        var parameters = PositionParameters(path, sql, name);
        return (await client.RequestAsync("textDocument/references", new { parameters.textDocument, parameters.position, context = new { includeDeclaration } })).GetProperty("result");
    }
    private static (object textDocument, object position) PositionParameters(string path, string sql, string name)
    {
        var offset = sql.IndexOf(name, StringComparison.Ordinal);
        Assert.True(offset >= 0);
        var prefix = sql[..offset];
        return (new { uri = new Uri(path).AbsoluteUri }, new { line = prefix.Count(c => c == '\n'), character = prefix.Length - (prefix.LastIndexOf('\n') + 1) });
    }
    private static void AssertUri(string path, JsonElement uri) => Assert.Equal(new Uri(path), new Uri(uri.GetString()!));
    private static Uri SymbolUri(JsonElement symbol) => new(symbol.GetProperty("location").GetProperty("uri").GetString()!);
    private static async Task EventuallyAsync(LspClient client, string query, Func<JsonElement, bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition(await SearchAsync(client, query))) return;
            await Task.Delay(50);
        }
        Assert.Fail("Workspace watcher did not produce the expected snapshot within 10 seconds.");
    }
}

internal sealed class NavigationWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(TestPaths.TempRoot, "sql-lsp-m2-wire-" + Guid.NewGuid().ToString("N"));
    public NavigationWorkspace() => Directory.CreateDirectory(Root);
    public string Write(string name, string sql)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sql);
        return path;
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
