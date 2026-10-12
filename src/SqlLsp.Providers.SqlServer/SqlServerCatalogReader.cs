using System.Collections.Immutable;
using System.Data;
using System.Data.SqlTypes;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlLsp.Core;

namespace SqlLsp.Providers.SqlServer;

public sealed class SqlServerCatalogReader(Func<string, string?>? secrets = null) : ISqlCatalogReader
{
    private readonly Func<string, string?> secrets = secrets ?? System.Environment.GetEnvironmentVariable;

    public async Task<CatalogSnapshot> ReadAsync(SqlServerProfile profile, CancellationToken token)
    {
        if (!profile.Enabled) throw new CatalogReadException("profile-disabled");
        profile.Validate();
        try
        {
            var secret = secrets(profile.ConnectionStringEnv);
            if (string.IsNullOrWhiteSpace(secret)) throw new CatalogReadException("missing-secret");
            var settings = new SqlConnectionStringBuilder(secret);
            // Metadata discovery must not attach files or silently route to an unapproved endpoint.
            if (!string.IsNullOrEmpty(settings.AttachDBFilename) || settings.UserInstance ||
                !string.IsNullOrEmpty(settings.FailoverPartner)) throw new CatalogReadException("unsafe-connection-option");
            if (!StringComparer.OrdinalIgnoreCase.Equals(settings.DataSource, profile.Server) ||
                !StringComparer.Ordinal.Equals(settings.InitialCatalog, profile.Database))
                throw new CatalogReadException("profile-endpoint-mismatch");
            if (settings.Encrypt == SqlConnectionEncryptOption.Optional) throw new CatalogReadException("tls-required");
            // ReadOnly is routing intent, never an authorization claim. Permissions must enforce least privilege.
            settings.ApplicationIntent = ApplicationIntent.ReadOnly;
            settings.ConnectTimeout = profile.ConnectTimeoutSeconds;
            settings.ConnectRetryCount = 0;
            settings.PersistSecurityInfo = false;
            settings.ApplicationName = "SqlScriptDOM LSP metadata";
            await using var connection = new SqlConnection(settings.ConnectionString);
            await connection.OpenAsync(token);
            var coverage = ImmutableArray.CreateBuilder<string>();
            string database, collation, version;
            string? defaultSchema;
            int lcid, style;
            await using (var command = Command(connection, profile, """
                SELECT DB_NAME(), CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')),
                       CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                       CONVERT(int, COLLATIONPROPERTY(CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), 'LCID')),
                       CONVERT(int, COLLATIONPROPERTY(CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), 'ComparisonStyle')),
                       SCHEMA_NAME();
                """))
            await using (var row = await command.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token)) throw new CatalogReadException("database-metadata-unavailable");
                database = row.GetString(0); collation = row.GetString(1); version = row.GetString(2);
                lcid = row.GetInt32(3); style = row.GetInt32(4);
                defaultSchema = Text(row, 5);
            }
            StringComparer comparer = profile.CaseSensitive is { } sensitive
                ? sensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase
                : new CatalogIdentifierComparer(lcid, style, collation);
            var objects = new List<(int Id, string Schema, string Name, SqlObjectKind Kind)>();
            await using (var command = Command(connection, profile, """
                SELECT TOP (@limit) o.object_id, s.name, o.name, o.type
                FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
                WHERE o.is_ms_shipped=0 AND o.type IN ('U','V','P','FN','IF','TF')
                  AND (@schemas IS NULL OR s.name IN (SELECT value FROM OPENJSON(@schemas)))
                ORDER BY o.object_id;
                """))
            {
                command.Parameters.Add("@limit", SqlDbType.Int).Value = profile.MaxObjects + 1;
                command.Parameters.Add("@schemas", SqlDbType.NVarChar, -1).Value = profile.Schemas.IsDefaultOrEmpty
                    ? DBNull.Value : JsonSerializer.Serialize(profile.Schemas);
                await using var rows = await command.ExecuteReaderAsync(token);
                while (await rows.ReadAsync(token))
                {
                    if (objects.Count == profile.MaxObjects) { coverage.Add("object-limit"); break; }
                    var kind = rows.GetString(3).Trim() switch
                    {
                        "U" => SqlObjectKind.Table,
                        "V" => SqlObjectKind.View,
                        "P" => SqlObjectKind.Procedure,
                        _ => SqlObjectKind.Function
                    };
                    objects.Add((rows.GetInt32(0), rows.GetString(1), rows.GetString(2), kind));
                }
            }
            var result = ImmutableArray.CreateBuilder<CatalogObject>();
            var moduleBudget = 8_388_608; // Bound aggregate retained module text to 16 MiB UTF-16.
            var memberBudget = 65_536;
            var dependencyBudget = 65_536;
            foreach (var obj in objects)
            {
                token.ThrowIfCancellationRequested();
                var columns = ImmutableArray.CreateBuilder<CatalogColumn>();
                var parameters = ImmutableArray.CreateBuilder<CatalogParameter>();
                var dependencies = ImmutableArray.CreateBuilder<CatalogDependency>();
                string? definition = null;
                bool truncated = false, quoted = true;
                await using (var command = ObjectCommand(connection, profile, obj.Id, """
                    SELECT TOP (@limit) c.column_id,c.name,SCHEMA_NAME(t.schema_id),t.name,c.max_length,c.precision,c.scale,c.is_nullable
                    FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
                    WHERE c.object_id=@id ORDER BY c.column_id;
                    """, Math.Min(profile.MaxMembersPerObject, memberBudget)))
                await using (var rows = await command.ExecuteReaderAsync(token))
                    while (await rows.ReadAsync(token))
                    {
                        if (columns.Count == profile.MaxMembersPerObject || memberBudget == 0) { coverage.Add("column-limit"); break; }
                        columns.Add(new(rows.GetInt32(0), rows.GetString(1), rows.GetString(2), rows.GetString(3),
                            rows.GetInt16(4), rows.GetByte(5), rows.GetByte(6), rows.GetBoolean(7)));
                        memberBudget--;
                    }
                await using (var command = ObjectCommand(connection, profile, obj.Id, """
                    SELECT TOP (@limit) p.parameter_id,p.name,SCHEMA_NAME(t.schema_id),t.name,p.max_length,p.precision,p.scale,p.is_output
                    FROM sys.parameters p JOIN sys.types t ON t.user_type_id=p.user_type_id
                    WHERE p.object_id=@id ORDER BY p.parameter_id;
                    """, Math.Min(profile.MaxMembersPerObject, memberBudget)))
                await using (var rows = await command.ExecuteReaderAsync(token))
                    while (await rows.ReadAsync(token))
                    {
                        if (parameters.Count == profile.MaxMembersPerObject || memberBudget == 0) { coverage.Add("parameter-limit"); break; }
                        parameters.Add(new(rows.GetInt32(0), rows.GetString(1), rows.GetString(2), rows.GetString(3),
                            rows.GetInt16(4), rows.GetByte(5), rows.GetByte(6), rows.GetBoolean(7)));
                        memberBudget--;
                    }
                await using (var command = ObjectCommand(connection, profile, obj.Id, """
                    SELECT CASE WHEN DATALENGTH(definition)<=CONVERT(bigint,@limit)*2 THEN definition END,
                           CONVERT(bit,CASE WHEN DATALENGTH(definition)>CONVERT(bigint,@limit)*2 THEN 1 ELSE 0 END),uses_quoted_identifier
                    FROM sys.sql_modules WHERE object_id=@id;
                    """, Math.Min(profile.MaxModuleCharacters, moduleBudget), addOne: false))
                await using (var row = await command.ExecuteReaderAsync(token))
                    if (await row.ReadAsync(token))
                    {
                        definition = Text(row, 0); truncated = row.GetBoolean(1); quoted = row.GetBoolean(2);
                        moduleBudget -= definition?.Length ?? 0;
                        if (truncated) coverage.Add("module-limit");
                    }
                if (definition == null && obj.Kind != SqlObjectKind.Table) coverage.Add("module-source-unavailable");
                try
                {
                    await using var command = ObjectCommand(connection, profile, obj.Id, """
                        SELECT DISTINCT TOP (@limit) referenced_server_name,referenced_database_name,referenced_schema_name,
                               referenced_entity_name,is_caller_dependent,is_ambiguous
                        FROM sys.sql_expression_dependencies WHERE referencing_id=@id
                        ORDER BY referenced_server_name,referenced_database_name,referenced_schema_name,referenced_entity_name,is_caller_dependent,is_ambiguous;
                        """, Math.Min(profile.MaxDependenciesPerObject, dependencyBudget));
                    await using var rows = await command.ExecuteReaderAsync(token);
                    while (await rows.ReadAsync(token))
                    {
                        if (dependencies.Count == profile.MaxDependenciesPerObject || dependencyBudget == 0) { coverage.Add("dependency-limit"); break; }
                        dependencies.Add(new(Text(rows, 0), Text(rows, 1), Text(rows, 2), Text(rows, 3), rows.GetBoolean(4), rows.GetBoolean(5)));
                        dependencyBudget--;
                    }
                }
                catch (SqlException error) when (error.Number == 229) { coverage.Add("dependency-permission-denied"); }
                result.Add(new(obj.Id, obj.Schema, obj.Name, obj.Kind, definition, truncated, quoted,
                    columns.ToImmutable(), parameters.ToImmutable(), dependencies.ToImmutable()));
            }
            return new(profile.Id, database, collation, version, DateTimeOffset.UtcNow, comparer, result.ToImmutable(),
                coverage.Count == 0 ? CatalogStatus.Ready : CatalogStatus.Partial, coverage.Distinct().ToImmutableArray(), defaultSchema);
        }
        catch (SqlException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (SqlException error)
        {
            var code = error.Number switch { 18456 => "authentication-failed", 229 => "metadata-permission-denied", -2 => "timeout", _ => "connection-or-catalog-failed" };
            throw new CatalogReadException(code, error.Number is -2 or 1205 or 40197 or 40501 or 40613 or 10928 or 10929);
        }
        catch (ArgumentException) { throw new CatalogReadException("invalid-connection-settings"); }
        catch (InvalidOperationException) { throw new CatalogReadException("invalid-connection-settings"); }
    }

    private static string? Text(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static SqlCommand Command(SqlConnection connection, SqlServerProfile profile, string sql) => new(sql, connection)
    { CommandTimeout = profile.CommandTimeoutSeconds };
    private static SqlCommand ObjectCommand(SqlConnection connection, SqlServerProfile profile, int id, string sql, int limit, bool addOne = true)
    {
        var command = Command(connection, profile, sql);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit + (addOne ? 1 : 0);
        return command;
    }
}

// SqlString honors SQL LCID, case/accent/kana/width and binary flags without culture-global state.
public sealed class CatalogIdentifierComparer(int lcid, int style, string collation) : StringComparer
{
    private readonly SqlCompareOptions options = collation.Contains("_BIN2", StringComparison.OrdinalIgnoreCase)
        ? SqlCompareOptions.BinarySort2 : collation.Contains("_BIN", StringComparison.OrdinalIgnoreCase)
        ? SqlCompareOptions.BinarySort :
        ((style & 1) != 0 ? SqlCompareOptions.IgnoreCase : SqlCompareOptions.None) |
        ((style & 2) != 0 ? SqlCompareOptions.IgnoreNonSpace : SqlCompareOptions.None) |
        ((style & 65536) != 0 ? SqlCompareOptions.IgnoreKanaType : SqlCompareOptions.None) |
        ((style & 131072) != 0 ? SqlCompareOptions.IgnoreWidth : SqlCompareOptions.None);
    public override int Compare(string? x, string? y) => x == null ? y == null ? 0 : -1 : y == null ? 1
        : new SqlString(x, lcid, options).CompareTo(new SqlString(y, lcid, options));
    public override bool Equals(string? x, string? y) => Compare(x, y) == 0;
    public override int GetHashCode(string obj) => new SqlString(obj, lcid, options).GetHashCode();
}
