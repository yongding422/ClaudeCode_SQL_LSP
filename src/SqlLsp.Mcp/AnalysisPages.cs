using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol;

namespace SqlLsp.Mcp;

// Bounded process-local evidence expansion. Stored JSON is data, never executable serialization.
internal sealed class AnalysisPages
{
    private sealed record Saved(string Id, string Generation, Uri Uri, JsonElement Data, int Bytes, DateTimeOffset At, Uri? Scope);
    private readonly Dictionary<string, Saved> reports = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public string Register(string generation, Uri uri, object report, Uri? scope = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ProcedureTools.JsonOptions);
        if (bytes.Length > 8 * 1024 * 1024) throw new McpException("Report exceeds the evidence-retention budget. Request one module/depth 0 or a focused compact report.");
        using var json = JsonDocument.Parse(bytes);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        lock (gate)
        {
            while (reports.Count >= 8 || reports.Values.Sum(r => r.Bytes) + bytes.Length > 8 * 1024 * 1024)
                reports.Remove(reports.Values.OrderBy(r => r.At).First().Id);
            reports[id] = new(id, generation, uri, json.RootElement.Clone(), bytes.Length, DateTimeOffset.UtcNow, scope);
        }
        return id;
    }

    public Uri Source(string reference) => Get(reference, null).Uri;
    public Uri? Scope(string reference) => Get(reference, null).Scope;
    private Saved Get(string reference, string? generation)
    {
        lock (gate)
        {
            if (reference.Length > 64 || !reports.TryGetValue(reference, out var saved) || DateTimeOffset.UtcNow - saved.At > TimeSpan.FromMinutes(10))
                throw new McpException("Report reference expired or is unavailable in this process. Request fresh scoped analysis.");
            if (generation != null && saved.Generation != generation) throw new McpException("Report reference is stale: source generation changed. Request fresh scoped analysis.");
            return saved;
        }
    }

    public object Overview(string reference, string generation, int moduleCount)
    {
        var saved = Get(reference, generation);
        var data = saved.Data;
        var modules = data.GetProperty("modules").EnumerateArray().ToArray();
        var summary = modules.Take(moduleCount).Select((module, i) => new
        {
            index = i,
            identity = Property(module, "identity"),
            revision = Property(module, "revision"),
            definition = Property(module, "definition"),
            bodyAvailable = Property(module, "bodyAvailable"),
            truncated = Property(module, "truncated"),
            calls = module.GetProperty("calls").GetArrayLength(),
            accesses = module.GetProperty("accesses").GetArrayLength(),
            issues = module.GetProperty("issues").GetArrayLength(),
            sections = new[] { $"/modules/{i}/calls", $"/modules/{i}/accesses", $"/modules/{i}/controlFlow", $"/modules/{i}/valueFlow", $"/modules/{i}/issues" }
        }).ToArray();
        return new
        {
            format = "paged-report-v1",
            reportRef = reference,
            generation,
            status = Property(data, "status"),
            explanation = "Report exceeds the wire budget. Counts are not empty-effect claims. Expand only relevant sections using reportRef, section and offset; every retained value remains available.",
            totalModules = modules.Length,
            omittedModuleSummaries = modules.Length - summary.Length,
            moduleSummaries = summary,
            sections = data.EnumerateObject().Select(p => "/" + Escape(p.Name)).ToArray(),
            limitations = "Static possible effects only. Triggers, temporal/implicit engine effects, CLR/dynamic bodies and runtime/ambient transactions are not fully modeled. Empty or omitted lists do not prove no effects. Keep exact source citations; expand /evidence, /sources and /notes for compact reports."
        };
    }

    public object Page(string reference, string generation, string section, int offset, int maxItems, Func<object, bool> fits)
    {
        var saved = Get(reference, generation);
        if (section.Length > 2048 || offset < 0 || maxItems is < 1 or > 100) throw new McpException("Invalid bounded report section/page.");
        var selected = Select(saved.Data, section);
        var items = new List<object>();
        if (selected.ValueKind == JsonValueKind.String)
        {
            var text = selected.GetString()!;
            if (offset > text.Length || offset > 0 && char.IsLowSurrogate(text[offset < text.Length ? offset : offset - 1])) throw new McpException("Invalid text offset.");
            var length = Math.Min(1024, text.Length - offset);
            if (length > 0 && char.IsHighSurrogate(text[offset + length - 1])) length--;
            return new
            {
                format = "report-text-page-v1",
                reportRef = reference,
                generation,
                section,
                offset,
                text = text.Substring(offset, length),
                totalLength = text.Length,
                nextOffset = offset + length < text.Length ? offset + length : (int?)null
            };
        }
        var children = selected.ValueKind == JsonValueKind.Array ? selected.EnumerateArray().Select((v, i) => (Path: section + "/" + i, Value: v)).ToArray() :
            selected.ValueKind == JsonValueKind.Object ? selected.EnumerateObject().Select(p => (Path: section + "/" + Escape(p.Name), Value: p.Value)).ToArray() :
            [(Path: section, Value: selected)];
        if (offset > children.Length) throw new McpException("Invalid report offset.");
        var consumed = 0;
        object Payload() => new
        {
            format = "report-page-v1",
            reportRef = reference,
            generation,
            section,
            offset,
            kind = selected.ValueKind.ToString(),
            totalItems = children.Length,
            returned = items.Count,
            items,
            hasMore = offset + consumed < children.Length,
            nextOffset = offset + consumed < children.Length ? offset + consumed : (int?)null,
            coverage = "Expanded values preserve original IDs/spans. Items with expand require another selected section; they are not absent facts."
        };
        foreach (var item in children.Skip(offset).Take(maxItems))
        {
            consumed++;
            items.Add(new { path = item.Path, value = item.Value });
            if (fits(Payload())) continue;
            items.RemoveAt(items.Count - 1);
            if (items.Count != 0) { consumed--; break; }
            items.Add(new
            {
                path = item.Path,
                expand = item.Path,
                kind = item.Value.ValueKind.ToString(),
                count = item.Value.ValueKind == JsonValueKind.Array ? item.Value.GetArrayLength() : item.Value.ValueKind == JsonValueKind.Object ? item.Value.EnumerateObject().Count() : 1
            });
            break;
        }
        return Payload();
    }

    private static JsonElement? Property(JsonElement data, string key) => data.TryGetProperty(key, out var value) ? value : null;
    private static string Escape(string value) => value.Replace("~", "~0").Replace("/", "~1");
    private static JsonElement Select(JsonElement data, string path)
    {
        if (path == "") return data;
        if (!path.StartsWith('/')) throw new McpException("section must be a JSON Pointer from the report.");
        foreach (var encoded in path[1..].Split('/'))
        {
            var key = encoded.Replace("~1", "/").Replace("~0", "~");
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var child)) data = child;
            else if (data.ValueKind == JsonValueKind.Array && int.TryParse(key, out var index) && index >= 0 && index < data.GetArrayLength()) data = data[index];
            else throw new McpException("Unknown report section; choose a returned section path.");
        }
        return data;
    }
}
