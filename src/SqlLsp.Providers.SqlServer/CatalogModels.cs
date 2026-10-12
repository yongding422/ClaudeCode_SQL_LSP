using System.Collections.Immutable;
using SqlLsp.Core;

namespace SqlLsp.Providers.SqlServer;

// Credentials are deliberately absent from the profile and all snapshot/status models.
public sealed record SqlServerProfile(string Id, string ConnectionStringEnv, string Environment,
    string Server, string Database, bool Enabled = false, int ConnectTimeoutSeconds = 5,
    int CommandTimeoutSeconds = 5, int LoadTimeoutSeconds = 20, int MaxObjects = 1000,
    int MaxMembersPerObject = 1024, int MaxDependenciesPerObject = 1024,
    int MaxModuleCharacters = 1_048_576, int CacheTtlSeconds = 300,
    ImmutableArray<string> Schemas = default, bool? CaseSensitive = null)
{
    public void Validate()
    {
        if (ConnectTimeoutSeconds is < 1 or > 30 || CommandTimeoutSeconds is < 1 or > 30 ||
            LoadTimeoutSeconds is < 1 or > 60 || MaxObjects is < 1 or > 10000 ||
            MaxMembersPerObject is < 1 or > 4096 || MaxDependenciesPerObject is < 1 or > 4096 ||
            MaxModuleCharacters is < 1 or > 2097152 || CacheTtlSeconds is < 1 or > 3600)
            throw new CatalogReadException("invalid-profile-limits");
    }
}

public sealed record CatalogColumn(int Ordinal, string Name, string TypeSchema, string Type,
    int MaxLength, byte Precision, byte Scale, bool Nullable);
public sealed record CatalogParameter(int Ordinal, string Name, string TypeSchema, string Type,
    int MaxLength, byte Precision, byte Scale, bool Output);
public sealed record CatalogDependency(string? Server, string? Database, string? Schema, string? Name,
    bool CallerDependent, bool Ambiguous);
public sealed record CatalogObject(int ObjectId, string Schema, string Name, SqlObjectKind Kind,
    string? Definition, bool DefinitionTruncated, bool QuotedIdentifier,
    ImmutableArray<CatalogColumn> Columns, ImmutableArray<CatalogParameter> Parameters,
    ImmutableArray<CatalogDependency> Dependencies);
public enum CatalogStatus { Ready, Partial, Unavailable }
public sealed record CatalogSnapshot(string ProfileId, string Database, string Collation, string ServerVersion,
    DateTimeOffset CapturedAt, StringComparer Comparer, ImmutableArray<CatalogObject> Objects,
    CatalogStatus Status, ImmutableArray<string> Coverage, string? DefaultSchema = null);
public sealed class CatalogReadException(string code, bool transient = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
}
public interface ISqlCatalogReader
{
    Task<CatalogSnapshot> ReadAsync(SqlServerProfile profile, CancellationToken token);
}
