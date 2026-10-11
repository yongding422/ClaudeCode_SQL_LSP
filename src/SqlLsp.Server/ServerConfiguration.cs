using System.Text.Json;
using Json.Schema;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.JsonRpc;
using SqlLsp.Core;

namespace SqlLsp.Server;

public sealed record ServerConfiguration(ParserOptions Parser, WorkspaceOptions Workspace, ResolutionOptions Resolution)
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
        return new ServerConfiguration(
            new ParserOptions(config?["sql"]?["parserVersion"]?.Value<string>() ?? "170",
                config?["sql"]?["quotedIdentifier"]?.Value<bool>() ?? true),
            new WorkspaceOptions(config?["workspace"]?["maxFileSizeBytes"]?.Value<int>() ?? 2_097_152),
            new ResolutionOptions(config?["resolution"]?["caseSensitive"]?.Value<bool>() ?? true,
                config?["resolution"]?["defaultSchema"]?.Value<string>(), config?["resolution"]?["defaultDatabase"]?.Value<string>()));
    }

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
