using System.Text;

namespace SqlLsp.Core;

public sealed record SqlHover(TextRange Range, string Text);

public static class SqlHoverFormatting
{
    public const int MaxCharacters = 16_384;

    public static string Describe(SqlNavigationTarget target, SqlSymbolDocumentation? documentation,
        SqlLocalDefinition? local, DateTimeOffset? capturedAt, int? version, SqlDefinitionDrift? drift)
    {
        var text = new StringBuilder();
        text.AppendLine("Kind: " + (target.Identity?.Kind ?? local?.Kind)?.ToString());
        text.AppendLine("Signature: " + (documentation?.Signature ?? SqlSymbolDisplay.OneLine(local?.Name ?? target.Identity?.Name ?? "unknown")));
        if (local?.DeclaredType is { } type) text.AppendLine("Declared type: " + type);
        text.AppendLine("Source: " + target.SourceKind);
        text.AppendLine("Provenance: " + SqlSymbolDisplay.OneLine(target.Provenance));
        text.AppendLine("Definition file: " + SqlSymbolDisplay.OneLine(target.Uri.AbsoluteUri, 2048));
        if (target.Identity is { } identity)
        {
            text.AppendLine("Context: " + SqlSymbolDisplay.OneLine(identity.SourceContextId, 2048));
            if (identity.Database != null) text.AppendLine("Database: " + SqlSymbolDisplay.OneLine(identity.Database));
        }
        if (version != null) text.AppendLine("Buffer version: " + version);
        if (capturedAt != null) text.AppendLine($"Catalog captured: {capturedAt:O}");
        if (documentation != null)
        {
            text.AppendLine("Basis: " + documentation.Basis);
            foreach (var detail in documentation.Details.Take(24)) text.AppendLine(SqlSymbolDisplay.OneLine(detail, 1024));
        }
        if (drift != null)
        {
            text.AppendLine($"Definition comparison: {drift.Status}. {drift.Explanation}");
            if (drift.CapturedAt != null) text.AppendLine($"Comparison catalog captured: {drift.CapturedAt:O}");
        }
        if (text.Length <= MaxCharacters) return text.ToString().TrimEnd();
        var value = text.ToString();
        var length = MaxCharacters - 32;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + "\nAdditional hover text omitted.";
    }
}
