using System.Collections.Immutable;
using System.Text.Json;
using Json.Schema;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.JsonRpc;
using SqlLsp.Core;
using SqlLsp.Providers.SqlServer;

namespace SqlLsp.Server;

public sealed record ServerConfiguration(ParserOptions Parser, WorkspaceOptions Workspace, ResolutionOptions Resolution,
    string Mode, bool ScanInOnline, SqlServerProfile? DatabaseProfile)
{
    private static readonly JsonSchema Schema = LoadSchema();

    public static ServerConfiguration Parse(object? initializationOptions)
    {
        var options = initializationOptions as JToken ?? (initializationOptions is null ? null : JToken.FromObject(initializationOptions));
        using var instance = JsonDocument.Parse(options is null || options.Type == JTokenType.Null ? "{}" : options.ToString());
        var result = Schema.Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!result.IsValid)
        {
            var failures = Flatten(result).Where(r => r.Errors?.Count > 0)
                .Select(r => $"{r.InstanceLocation} ({string.Join(", ", r.Errors!.Keys)})").Distinct();
            throw new RpcErrorException(-32602, new { retry = false }, "Invalid initializationOptions: " + string.Join("; ", failures));
        }
        var config = options as JObject;
        var mode = config?["sql"]?["mode"]?.Value<string>() ?? "offline";
        var profiles = (config?["databases"] as JArray ?? []).OfType<JObject>().Select(p => new SqlServerProfile(
            p["id"]!.Value<string>()!, p["connectionStringEnv"]!.Value<string>()!, p["environment"]!.Value<string>()!,
            p["server"]!.Value<string>()!, p["database"]!.Value<string>()!, p["enabled"]?.Value<bool>() ?? false,
            p["connectTimeoutSeconds"]?.Value<int>() ?? 5, p["commandTimeoutSeconds"]?.Value<int>() ?? 5,
            p["loadTimeoutSeconds"]?.Value<int>() ?? 20, p["maxObjects"]?.Value<int>() ?? 1000,
            p["maxMembersPerObject"]?.Value<int>() ?? 1024, p["maxDependenciesPerObject"]?.Value<int>() ?? 1024,
            p["maxModuleCharacters"]?.Value<int>() ?? 1_048_576, p["cacheTtlSeconds"]?.Value<int>() ?? 300,
            (p["schemas"] as JArray ?? []).Select(s => s.Value<string>()!).ToImmutableArray(),
            p["caseSensitive"]?.Value<bool>() ?? config?["resolution"]?["caseSensitive"]?.Value<bool>())).ToArray();
        if (profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Length)
            throw Invalid("duplicate database profile ids");
        SqlServerProfile? selected = null;
        if (mode == "online")
        {
            var enabled = profiles.Where(p => p.Enabled).ToArray();
            var mapped = config?["resolution"]?["defaultDatabaseProfile"]?.Value<string>();
            selected = mapped != null ? enabled.SingleOrDefault(p => p.Id == mapped) : enabled.Length == 1 ? enabled[0] : null;
            if (selected == null) throw Invalid("online mode requires one enabled profile or an explicit defaultDatabaseProfile selecting an enabled profile");
        }
        return new ServerConfiguration(
            new ParserOptions(config?["sql"]?["parserVersion"]?.Value<string>() ?? "170",
                config?["sql"]?["quotedIdentifier"]?.Value<bool>() ?? true),
            new WorkspaceOptions(config?["workspace"]?["maxFileSizeBytes"]?.Value<int>() ?? 2_097_152),
            new ResolutionOptions(config?["resolution"]?["caseSensitive"]?.Value<bool>() ?? true,
                config?["resolution"]?["defaultSchema"]?.Value<string>(), config?["resolution"]?["defaultDatabase"]?.Value<string>()),
            mode, config?["workspace"]?["scanInOnline"]?.Value<bool>() ?? false, selected);
    }

    private static RpcErrorException Invalid(string reason) => new(-32602, new { retry = false }, "Invalid initializationOptions: " + reason);

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(ServerConfiguration).Assembly.GetManifestResourceStream("SqlLsp.configuration.schema.json")
            ?? throw new InvalidOperationException("Embedded configuration schema is missing.");
        using var json = JsonDocument.Parse(stream);
        return JsonSchema.Build(json.RootElement.Clone());
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults result)
    {
        yield return result;
        foreach (var child in result.Details ?? [])
            foreach (var descendant in Flatten(child)) yield return descendant;
    }
}
