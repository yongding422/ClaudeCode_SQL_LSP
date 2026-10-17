using System.Text.Json;
using Json.Schema;

namespace SqlLsp.Mcp;

// Local storage settings only. SQL source/database options remain explicitly selected with --config.
internal sealed record PluginSettings(string? MetricsDirectory = null, string? CacheDirectory = null)
{
    private const int MaxBytes = 65_536;
    private static readonly JsonSchema Schema = LoadSchema();

    public static PluginSettings Read(string workspace, string? explicitFile)
    {
        var file = explicitFile == null ? Path.Combine(workspace, ".sqlscriptdom.json") : Path.GetFullPath(explicitFile);
        try
        {
            using var stream = File.OpenRead(file);
            var bytes = new byte[MaxBytes + 1];
            var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (length > MaxBytes) throw new ArgumentException("Plugin settings exceed the supported size.");
            // Accept the BOM produced by Windows editors; keep JSON otherwise strict.
            var offset = length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(offset, length - offset), new() { MaxDepth = 8 });
            var root = document.RootElement;
            if (!Schema.Evaluate(root).IsValid || HasDuplicates(root)) throw new ArgumentException("Invalid plugin settings.");
            var directory = Path.GetDirectoryName(file)!;
            return new(Storage(root, "metrics", directory), Storage(root, "analysisCache", directory));
        }
        catch (FileNotFoundException) when (explicitFile == null) { return new(); }
        catch (JsonException) { throw new ArgumentException("Invalid plugin settings JSON."); }
    }

    private static string? Storage(JsonElement root, string section, string directory)
    {
        if (!root.TryGetProperty(section, out var settings) || !settings.TryGetProperty("enabled", out var enabled) || !enabled.GetBoolean()) return null;
        var location = settings.TryGetProperty("directory", out var value) ? value.GetString()! : ".sqlscriptdom/" + (section == "metrics" ? "metrics" : "cache");
        if (string.IsNullOrWhiteSpace(location)) throw new ArgumentException("Storage directory must not be blank.");
        return Path.GetFullPath(location, directory);
    }

    private static bool HasDuplicates(JsonElement element) => element.ValueKind == JsonValueKind.Object &&
        (element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != element.EnumerateObject().Count() ||
         element.EnumerateObject().Any(p => HasDuplicates(p.Value)));

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(PluginSettings).Assembly.GetManifestResourceStream("SqlLsp.plugin-settings.schema.json")!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
