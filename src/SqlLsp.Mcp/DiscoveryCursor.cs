using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;

namespace SqlLsp.Mcp;

// Process-owned MAC keeps client cursors opaque and prevents invented offsets/query substitution.
internal sealed class DiscoveryCursor
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private sealed record Page(string Generation, string QueryHash, int Offset);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal string Encode(string generation, string query, int offset)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Page(generation, Hash(query), offset));
        return Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(key, bytes));
    }

    internal int Decode(string? cursor, string generation, string query)
    {
        if (cursor == null) return 0;
        try
        {
            if (cursor.Length > 1024) throw new FormatException();
            var parts = cursor.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var bytes = Convert.FromBase64String(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, bytes), Convert.FromBase64String(parts[1]))) throw new FormatException();
            var page = JsonSerializer.Deserialize<Page>(bytes)!;
            if (page.Generation != generation) throw new McpException("Discovery cursor is stale: source generation changed. Start a new scoped query.");
            if (page.QueryHash != Hash(query) || page.Offset < 0) throw new FormatException();
            return page.Offset;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        { throw new McpException("Invalid discovery cursor or changed query/options. Start a new query."); }
    }
}
