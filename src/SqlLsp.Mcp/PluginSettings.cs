using System.Text.Json;
using Json.Schema;

namespace SqlLsp.Mcp;

// Local storage settings only. SQL source/database options remain explicitly selected with --config.
internal sealed record PluginSettings(string? MetricsDirectory = null, string? CacheDirectory = null, int DiscoveryMaxBytes = 16384, string? IndexCacheDirectory = null, int AnalysisMaxBytes = 32768)
{
    private const string Starter = """
        {
          "version": 1,
          "metrics": { "enabled": false, "directory": ".sqlscriptdom/metrics" },
          "analysisCache": { "enabled": false, "directory": ".sqlscriptdom/cache" }
        }
        """;
    private const int MaxBytes = 65_536;
    private static readonly JsonSchema Schema = LoadSchema();

    public static void EnsureStarter(string workspace)
    {
        var target = Path.Combine(workspace, ".sqlscriptdom.json");
        if (File.Exists(target)) return;
        var temporary = Path.Combine(workspace, ".sqlscriptdom-" + Guid.NewGuid().ToString("N") + ".tmp");
        var owned = false;
        try
        {
            // Publish only a complete file. Concurrent MCP starts must never read partial JSON.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                stream.Write(System.Text.Encoding.UTF8.GetBytes(Starter + "\n"));
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (File.Exists(target)) { /* Another process or user created it first. */ }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Read-only workspaces still support analysis. Keep protocol stdout clean and values private.
            Console.Error.WriteLine("SQL plugin could not create starter settings; continuing with current options.");
        }
        finally
        {
            try { if (owned) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

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
            return new(Storage(root, "metrics", directory), Storage(root, "analysisCache", directory),
                root.TryGetProperty("responses", out var responses) && responses.TryGetProperty("discoveryMaxBytes", out var budget) ? budget.GetInt32() : 16384,
                Storage(root, "indexCache", directory),
                root.TryGetProperty("responses", out responses) && responses.TryGetProperty("analysisMaxBytes", out budget) ? budget.GetInt32() : 32768);
        }
        catch (FileNotFoundException) when (explicitFile == null) { return new(); }
        catch (JsonException) { throw new ArgumentException("Invalid plugin settings JSON."); }
    }

    private static string? Storage(JsonElement root, string section, string directory)
    {
        if (!root.TryGetProperty(section, out var settings) || !settings.TryGetProperty("enabled", out var enabled) || !enabled.GetBoolean()) return null;
        var location = settings.TryGetProperty("directory", out var value) ? value.GetString()! : ".sqlscriptdom/" +
            (section == "metrics" ? "metrics" : section == "indexCache" ? "index" : "cache");
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
