using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SqlLsp.Core;

public sealed record SqlAnalysisCacheSource(Uri LiveUri, Uri StoredUri, string Revision, DateTimeOffset? CapturedAt);
public interface ISqlComparerIdentity { string ComparisonIdentity { get; } }

// A report template is reusable only against the same complete indexed source/configuration fingerprint.
// ASTs are not serialized. Catalog URIs/revisions/timestamps are rebound to the validated current snapshot.
public sealed class SqlAnalysisCacheContext(string fingerprint, ImmutableArray<SqlAnalysisCacheSource> sources)
{
    public string Fingerprint { get; } = fingerprint;
    public ImmutableArray<SqlAnalysisCacheSource> Sources { get; } = sources;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new AbsoluteSourceUriConverter() }
    };

    // Uri's default JSON converter treats Unix OriginalString paths as relative.
    // Source anchors are absolute even when their original spelling is /folder/file.sql.
    private sealed class AbsoluteSourceUriConverter : JsonConverter<Uri>
    {
        public override Uri Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString() ?? throw new JsonException("Missing source URI."), UriKind.Absolute);
        public override void Write(Utf8JsonWriter writer, Uri value, JsonSerializerOptions options) => writer.WriteStringValue(value.OriginalString);
    }

    public string RequestKey(Uri uri, TextPosition position, int maxDepth, int maxModules) => Hash(JsonSerializer.Serialize(new
    {
        Fingerprint,
        source = Sources.SingleOrDefault(s => s.LiveUri == uri)?.StoredUri ?? uri,
        position,
        maxDepth,
        maxModules
    }, JsonOptions));

    public JsonObject Normalize(SqlProcedureReport report)
    {
        var node = JsonSerializer.SerializeToNode(report, JsonOptions)!.AsObject();
        Revisions(node, restore: false);
        RewriteUris(node, Sources.ToDictionary(s => s.LiveUri.AbsoluteUri, s => s.StoredUri, StringComparer.Ordinal));
        return node;
    }

    public SqlProcedureReport Restore(JsonObject template)
    {
        var node = template.DeepClone().AsObject();
        Revisions(node, restore: true);
        RewriteUris(node, Sources.ToDictionary(s => s.StoredUri.AbsoluteUri, s => s.LiveUri, StringComparer.Ordinal));
        return node.Deserialize<SqlProcedureReport>(JsonOptions) ?? throw new InvalidDataException("Invalid cached report.");
    }

    private void Revisions(JsonObject node, bool restore)
    {
        foreach (var module in node["modules"]!.AsArray().OfType<JsonObject>())
        {
            var uri = new Uri(module["definition"]!["uri"]!.GetValue<string>(), UriKind.Absolute);
            var source = Sources.Single(s => (restore ? s.StoredUri : s.LiveUri) == uri);
            module["revision"] = restore ? source.Revision : source.StoredUri.AbsoluteUri;
            module["capturedAt"] = restore ? JsonSerializer.SerializeToNode(source.CapturedAt, JsonOptions) : null;
            foreach (var call in module["valueFlow"]?["calls"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (call["targetRevision"] == null) continue;
                var targetUri = new Uri(call["target"]!["uri"]!.GetValue<string>(), UriKind.Absolute);
                var target = Sources.Single(s => (restore ? s.StoredUri : s.LiveUri) == targetUri);
                call["targetRevision"] = restore ? target.Revision : target.StoredUri.AbsoluteUri;
            }
        }
    }

    private static void RewriteUris(JsonNode? node, Dictionary<string, Uri> replacements)
    {
        if (node is JsonArray array) foreach (var item in array) RewriteUris(item, replacements);
        if (node is not JsonObject obj) return;
        foreach (var property in obj.ToArray())
        {
            if (property.Key is "uri" or "id" or "targetModuleId" && property.Value is JsonValue value && value.TryGetValue<string>(out var text))
            {
                var split = property.Key == "uri" ? -1 : text.IndexOf('#');
                var key = split < 0 ? text : text[..split];
                if (Uri.TryCreate(key, UriKind.Absolute, out var uri)) key = uri.AbsoluteUri;
                if (replacements.TryGetValue(key, out var replacement)) obj[property.Key] = property.Key == "uri"
                    ? replacement.OriginalString : replacement.AbsoluteUri + (split < 0 ? "" : text[split..]);
            }
            else RewriteUris(property.Value, replacements);
        }
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

public sealed partial class SqlNavigationIndex
{
    public SqlAnalysisCacheContext CreateAnalysisCacheContext(string configurationStamp, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (documents.Any(d => d.SourceKind == SqlSourceKind.UnsavedBuffer)) throw new InvalidOperationException("Only saved sources can be persisted.");
        var documentsByUri = documents.ToDictionary(d => d.Uri);
        var symbolsByUri = Symbols.GroupBy(s => s.Target.Uri).ToDictionary(g => g.Key, g => g.First());
        var sources = documents.Select(document =>
        {
            token.ThrowIfCancellationRequested();
            var symbol = symbolsByUri.GetValueOrDefault(document.Uri);
            var stored = document.SourceKind == SqlSourceKind.Database
                ? new Uri("sqlcache://catalog/" + SqlAnalysisCacheContext.Hash(JsonSerializer.Serialize(new { symbol?.Identity, file = Path.GetFileName(document.Uri.LocalPath) }, SqlAnalysisCacheContext.JsonOptions)))
                : document.Uri;
            return new SqlAnalysisCacheSource(document.Uri, stored, AnalysisRevision(document), symbol?.CapturedAt);
        }).ToImmutableArray();
        var sourcesByUri = sources.ToDictionary(s => s.LiveUri);
        if (sources.Select(s => s.StoredUri).Distinct().Count() != sources.Length) throw new InvalidOperationException("Ambiguous cache source identity.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(object? value)
        {
            token.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SqlAnalysisCacheContext.JsonOptions);
            // Length-delimited records avoid concatenation collisions.
            hash.AppendData(Encoding.UTF8.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            hash.AppendData(bytes);
        }
        string ComparerIdentity(StringComparer comparer)
        {
            if (comparer is ISqlComparerIdentity identified) return identified.ComparisonIdentity;
            if (comparer.Equals(StringComparer.Ordinal)) return "Ordinal";
            if (comparer.Equals(StringComparer.OrdinalIgnoreCase)) return "OrdinalIgnoreCase";
            throw new InvalidOperationException("Comparer has no stable persistence identity.");
        }
        object Rules(ResolutionOptions rules) => new
        {
            rules.CaseSensitive,
            rules.DefaultSchema,
            rules.DefaultDatabase,
            rules.FallbackSchema,
            comparer = ComparerIdentity(rules.Comparer)
        };
        Append(new { format = 1, core = typeof(SqlNavigationIndex).Module.ModuleVersionId, parser = typeof(Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragment).Module.ModuleVersionId, runtime = Environment.Version.ToString(), configurationStamp, rules = Rules(options) });
        if (hybrid != null) Append(new { hybrid.PreferWorkspaceDefinitions, hybrid.Mappings, profiles = hybrid.Profiles.Select(p => new { p.Id, p.Database, rules = Rules(p.Rules) }) });
        foreach (var source in sources.OrderBy(s => s.StoredUri.AbsoluteUri, StringComparer.Ordinal))
        {
            var document = documentsByUri[source.LiveUri];
            var text = document.Parse.Source.Text;
            if (document.SourceKind == SqlSourceKind.Database && source.CapturedAt is { } captured)
            {
                var end = text.IndexOf('\n');
                var timestamp = captured.ToString("O", CultureInfo.InvariantCulture);
                if (end >= timestamp.Length && text.StartsWith("-- Read-only catalog snapshot: ", StringComparison.Ordinal) && text[..end].EndsWith(timestamp, StringComparison.Ordinal))
                    text = text[..(end - timestamp.Length)] + new string('0', timestamp.Length) + text[end..];
            }
            Append(new { source.StoredUri, document.SourceKind, document.Version, context = context(document.Uri), text });
        }
        foreach (var symbol in Symbols.OrderBy(s => sourcesByUri[s.Target.Uri].StoredUri.AbsoluteUri, StringComparer.Ordinal).ThenBy(s => s.Target.Range.Start.Line).ThenBy(s => s.Target.Range.Start.Character))
            Append(new { symbol.Identity, symbol.Declaration, uri = sourcesByUri[symbol.Target.Uri].StoredUri, symbol.Target.Range, symbol.Target.SourceKind, symbol.Target.Provenance, symbol.DefinitionHash });
        return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), sources);
    }
}
