using System.Collections.Immutable;
using System.Text.Json;

namespace SqlLsp.Core.Tests;

public class MaterializedIndexTests
{
    [Theory]
    [InlineData("file:///workspace/p.sql")]
    [InlineData("/workspace/p.sql")]
    public void Persisted_URI_converter_preserves_absolute_file_identity_for_native_path_spelling(string spelling)
    {
        var uri = new Uri(spelling.StartsWith('/') ? Path.GetFullPath(spelling) : spelling, UriKind.Absolute);
        var json = JsonSerializer.Serialize(uri, SqlAnalysisCacheContext.JsonOptions);
        var restored = JsonSerializer.Deserialize<Uri>(json, SqlAnalysisCacheContext.JsonOptions)!;
        Assert.True(restored.IsAbsoluteUri);
        Assert.True(restored.IsFile);
        Assert.Equal(uri.AbsoluteUri, restored.AbsoluteUri);
        Assert.Equal(json, JsonSerializer.Serialize(restored, SqlAnalysisCacheContext.JsonOptions));
    }
    [Fact]
    public async Task Real_extracted_facts_roundtrip_without_asts_and_preserve_discovery_and_resolution()
    {
        var uri = new Uri("file:///workspace/p.sql");
        var source = new SourceText("CREATE PROC dbo.P AS EXEC dbo.Q;\nGO\nCREATE PROC dbo.Q AS SELECT 1;");
        var parsed = await new SqlParser(new()).ParseAsync(source, CancellationToken.None);
        var document = new DocumentSnapshot(uri, null, SqlSourceKind.Workspace, parsed);
        var facts = new SqlMaterializedFile(uri, new string('A', 64), SqlNavigationIndex.AnalysisRevision(document), source.Text.Length,
            "utf-8", "project", SqlMaterializedRules.From(new()), parsed.Declarations, parsed.Semantics, parsed.Diagnostics);
        var json = JsonSerializer.Serialize(facts);
        Assert.DoesNotContain("TSql", json);
        Assert.DoesNotContain("sourceText", json, StringComparison.OrdinalIgnoreCase);
        var restored = JsonSerializer.Deserialize<SqlMaterializedFile>(json)!;
        var index = new SqlMaterializedIndex([restored]);
        var fresh = new SqlNavigationIndex([document], _ => "project", new());
        Assert.Equal(JsonSerializer.Serialize(fresh.Symbols), JsonSerializer.Serialize(index.Symbols));
        Assert.Equal(fresh.ModuleMetadata(fresh.Symbols[0], CancellationToken.None), index.ModuleMetadata(index.Symbols[0], CancellationToken.None));
        var call = Assert.Single(restored.Semantics.References.Where(r => r.IsInvocation));
        Assert.Equal(JsonSerializer.Serialize(fresh.Resolve(document, call)), JsonSerializer.Serialize(index.Resolve(restored, call)));
        Assert.Equal(JsonSerializer.Serialize(fresh.Search("P", CancellationToken.None)), JsonSerializer.Serialize(index.Search("P", CancellationToken.None)));
    }
}
