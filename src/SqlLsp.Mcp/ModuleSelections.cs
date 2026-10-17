using System.Security.Cryptography;
using ModelContextProtocol;
using SqlLsp.Core;

namespace SqlLsp.Mcp;

internal sealed record ModuleSelection(string Generation, Uri Uri, TextPosition Position, string? Revision, Uri? Scope = null);
internal sealed class ModuleSelections
{
    private readonly object gate = new();
    private readonly Dictionary<string, ModuleSelection> selections = new(StringComparer.Ordinal);
    public string Register(ModuleSelection selection)
    {
        lock (gate)
        {
            var existing = selections.FirstOrDefault(p => p.Value == selection);
            if (existing.Key != null) return existing.Key;
            while (selections.Count >= 512) selections.Remove(selections.Keys.First());
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            selections[id] = selection;
            return id;
        }
    }
    public ModuleSelection Get(string reference)
    {
        lock (gate) return reference.Length <= 32 && selections.TryGetValue(reference, out var selection) ? selection :
            throw new McpException("Module reference is unavailable in this process; use a file/declaration selector or fresh discovery.");
    }
}
