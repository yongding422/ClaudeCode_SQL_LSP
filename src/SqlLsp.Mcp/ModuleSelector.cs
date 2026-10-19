using Microsoft.SqlServer.TransactSql.ScriptDom;
using ModelContextProtocol;
using SqlLsp.Core;

namespace SqlLsp.Mcp;

// Parse identifier tokens, not a dot-separated display string: [a.b] is one identifier.
internal sealed record ModuleSelector(string? Schema, string Name)
{
    public static ModuleSelector Parse(string value)
    {
        using var reader = new StringReader(value);
        var parser = TSqlParser.CreateParser(SqlVersion.Sql180, true);
        var tokens = parser.GetTokenStream(reader, out var errors)
            .Where(t => t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile)).ToArray();
        if (errors.Count != 0 || tokens.Length is not (1 or 3) || tokens.Length == 3 && tokens[1].TokenType != TSqlTokenType.Dot)
            throw Invalid();
        string Part(TSqlParserToken token)
        {
            if (token.TokenType is not (TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier) &&
                !(token.TokenType == TSqlTokenType.AsciiStringOrQuotedIdentifier && token.Text.StartsWith('"'))) throw Invalid();
            var result = Identifier.DecodeIdentifier(token.Text, out _);
            if (string.IsNullOrEmpty(result)) throw Invalid();
            return result;
        }
        return tokens.Length == 1 ? new(null, Part(tokens[0])) : new(Part(tokens[0]), Part(tokens[2]));
    }

    public bool Matches(SqlObjectIdentity identity, StringComparer comparer) =>
        comparer.Equals(Name, identity.Name) && (Schema == null || comparer.Equals(Schema, identity.Schema));

    private static McpException Invalid() => new("declaration must be one identifier or schema.name; bracketed/double-quoted identifiers are supported. No statements, comments or database qualifiers.");
}
