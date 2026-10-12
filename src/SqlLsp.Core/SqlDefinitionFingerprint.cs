using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

// A token comparison reports definition differences, never proves a behavioral/semantic change.
public static class SqlDefinitionFingerprint
{
    public static string? Compute(SqlParseResult parse, SqlDeclaration declaration, CancellationToken cancellationToken = default)
    {
        if (declaration.Kind is not (SqlObjectKind.Procedure or SqlObjectKind.View or SqlObjectKind.Function)) return null;
        var start = parse.Source.OffsetAt(declaration.Range.Start);
        var end = parse.Source.OffsetAt(declaration.Range.End);
        var nameStart = parse.Source.OffsetAt(declaration.SelectionRange.Start);
        var nameEnd = parse.Source.OffsetAt(declaration.SelectionRange.End);
        if (parse.Diagnostics.Any(d => d.Offset >= start && d.Offset <= end)) return null;
        var result = new StringBuilder(declaration.Kind.ToString());
        foreach (var token in parse.Tokens.Where(t => t.Offset >= start && t.Offset < end))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // CREATE / ALTER / CREATE OR ALTER and the object's own name are acquisition/header differences.
            if (token.Offset < nameEnd) continue;
            var type = token.TokenType.ToString();
            if (type is "WhiteSpace" or "SingleLineComment" or "MultilineComment" or "EndOfFile") continue;
            var value = type is "Identifier" or "QuotedIdentifier"
                ? "Identifier:" + Identifier.DecodeIdentifier(token.Text, out _)
                : type.Contains("Literal", StringComparison.Ordinal) || type == "Variable" ? type + ":" + token.Text : type + ":" + token.Text.ToUpperInvariant();
            result.Append('|').Append(value.Length).Append(':').Append(value);
        }
        return nameStart < nameEnd ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.ToString()))) : null;
    }
}
