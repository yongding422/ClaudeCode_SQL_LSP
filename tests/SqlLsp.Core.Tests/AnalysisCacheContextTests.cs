using System.Text.Json;

namespace SqlLsp.Core.Tests;

public class AnalysisCacheContextTests
{
    private static async Task<SqlNavigationIndex> Index(string sql, ParserOptions? parser = null, ResolutionOptions? rules = null, SqlSourceKind kind = SqlSourceKind.Workspace, Uri? uri = null)
    {
        var document = new DocumentSnapshot(uri ?? new("file:///workspace/modules.sql"), null, kind,
            await new SqlParser(parser ?? new(), rules).ParseAsync(new(sql), CancellationToken.None));
        return new([document], _ => "workspace", rules ?? new());
    }

    [Fact]
    public async Task Report_templates_round_trip_full_control_and_value_evidence_without_serializing_ast()
    {
        foreach (var uri in new[] { new Uri("file:///workspace/modules.sql"), new Uri(Path.Combine(Path.GetTempPath(), "cache 中文", "modules.sql"), UriKind.Absolute) })
        {
            var index = await Index("CREATE PROC dbo.P @n int AS BEGIN DECLARE @v int=@n; IF @n>0 SET @v=2; EXEC dbo.Q @q=@v; END;\nGO\nCREATE PROC dbo.Q @q int,@m int=7 AS SELECT * FROM dbo.A WHERE id=@q;\nGO\nCREATE TABLE dbo.A(id int);", uri: uri);
            var symbol = index.Symbols.Single(s => s.Identity.Name == "P");
            var report = index.AnalyzeProcedure(symbol.Target.Uri, symbol.Target.Range.Start, CancellationToken.None);
            var context = index.CreateAnalysisCacheContext("parser=170", CancellationToken.None);
            var template = context.Normalize(report);
            var restored = context.Restore(template);
            Assert.Equal(JsonSerializer.Serialize(report, SqlAnalysisCacheContext.JsonOptions), JsonSerializer.Serialize(restored, SqlAnalysisCacheContext.JsonOptions));
            Assert.DoesNotContain("scriptTokenStream", template.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(restored.Modules[0].ValueFlow!.Calls[0].Bindings);
            Assert.All(restored.Candidates, c => Assert.True(c.Uri.IsAbsoluteUri));
            foreach (var module in restored.Modules)
            {
                Assert.True(module.Definition.Uri.IsAbsoluteUri);
                Assert.Equal(uri.AbsoluteUri, module.Definition.Uri.AbsoluteUri);
                Assert.All(module.Parameters, p => Assert.True(p.Location.Uri.IsAbsoluteUri));
                Assert.All(module.ValueFlow!.Facts, f => Assert.True(f.Location.Uri.IsAbsoluteUri));
            }
        }
    }

    [Fact]
    public async Task Fingerprint_and_request_keys_include_whole_source_set_configuration_and_expansion_limits()
    {
        const string original = "CREATE PROC dbo.P AS EXEC dbo.Q;\nGO\nCREATE PROC dbo.Q AS SELECT 1;";
        var index = await Index(original);
        var context = index.CreateAnalysisCacheContext("a", CancellationToken.None);
        Assert.Equal(context.Fingerprint, (await Index(original)).CreateAnalysisCacheContext("a", CancellationToken.None).Fingerprint);
        Assert.NotEqual(context.Fingerprint, (await Index(original.Replace("SELECT 1", "SELECT 2", StringComparison.Ordinal))).CreateAnalysisCacheContext("a", CancellationToken.None).Fingerprint);
        Assert.NotEqual(context.Fingerprint, (await Index(original + "\nGO\nCREATE PROC audit.Q AS SELECT 1;")).CreateAnalysisCacheContext("a", CancellationToken.None).Fingerprint);
        Assert.NotEqual(context.Fingerprint, index.CreateAnalysisCacheContext("changed-parser-config", CancellationToken.None).Fingerprint);
        Assert.NotEqual(context.Fingerprint, (await Index(original, rules: new(CaseSensitive: false))).CreateAnalysisCacheContext("a", CancellationToken.None).Fingerprint);
        var uri = new Uri("file:///workspace/modules.sql");
        Assert.NotEqual(context.RequestKey(uri, new(0, 16), 2, 20), context.RequestKey(uri, new(0, 16), 0, 20));
        Assert.NotEqual(context.RequestKey(uri, new(0, 16), 2, 20), context.RequestKey(uri, new(0, 16), 2, 1));
        Assert.NotEqual(context.RequestKey(uri, new(0, 16), 2, 20), context.RequestKey(uri, new(2, 16), 2, 20));
    }

    [Fact]
    public async Task Unsaved_sources_and_cancelled_fingerprinting_are_never_persistable()
    {
        var index = await Index("CREATE PROC dbo.P AS SELECT 1;", kind: SqlSourceKind.UnsavedBuffer);
        Assert.Throws<InvalidOperationException>(() => index.CreateAnalysisCacheContext("a", CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var saved = await Index("CREATE PROC dbo.P AS SELECT 1;");
        Assert.ThrowsAny<OperationCanceledException>(() => saved.CreateAnalysisCacheContext("a", cancellation.Token));
    }
}
