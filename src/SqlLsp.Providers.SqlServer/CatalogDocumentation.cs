using System.Collections.Immutable;
using SqlLsp.Core;

namespace SqlLsp.Providers.SqlServer;

internal static class CatalogDocumentation
{
    public static SqlSymbolDocumentation Describe(CatalogObject obj, CatalogSnapshot catalog)
    {
        var signature = SqlSymbolDisplay.Name(new(null, catalog.Database, obj.Schema, obj.Name));
        var parameterCoverageLimited = catalog.Coverage.Contains("parameter-limit");
        if (obj.Kind is SqlObjectKind.Procedure or SqlObjectKind.Function)
        {
            var parameters = obj.Parameters.Where(p => p.Ordinal > 0).ToArray();
            signature += " (" + string.Join(", ", parameters.Take(SqlSymbolDisplay.MaxMembers).Select(p => SqlSymbolDisplay.OneLine(p.Name) + " " +
                Type(p.TypeSchema, p.Type, p.MaxLength, p.Precision, p.Scale) + (p.Output ? " OUTPUT" : ""))) +
                (parameters.Length > SqlSymbolDisplay.MaxMembers || parameterCoverageLimited ? (parameters.Length > 0 ? ", …" : "unknown") : "") + ")";
            if (obj.Kind == SqlObjectKind.Function)
            {
                var returned = obj.Parameters.FirstOrDefault(p => p.Ordinal == 0);
                signature += returned != null ? " RETURNS " + Type(returned.TypeSchema, returned.Type, returned.MaxLength, returned.Precision, returned.Scale)
                    : obj.Columns.Length > 0 ? " RETURNS TABLE" : " RETURNS unknown";
            }
        }
        var details = ImmutableArray.CreateBuilder<string>();
        details.Add("Catalog coverage: " + catalog.Status + (catalog.Coverage.Length == 0 ? "" : "; " + string.Join(", ", catalog.Coverage.Take(16))));
        if (obj.Definition == null) details.Add("Original source unavailable; metadata is not original DDL.");
        if (parameterCoverageLimited) details.Add("Parameter metadata may be incomplete because the catalog reached a configured limit.");
        if (obj.DefinitionTruncated) details.Add("Original source exceeded the configured module limit.");
        if (obj.Columns.Length > 0)
        {
            details.Add("Visible catalog columns:");
            foreach (var column in obj.Columns.Take(SqlSymbolDisplay.MaxMembers))
                details.Add(SqlSymbolDisplay.Quote(column.Name) + " " + Type(column.TypeSchema, column.Type, column.MaxLength, column.Precision, column.Scale) +
                    (column.Nullable ? " NULL" : " NOT NULL"));
            if (obj.Columns.Length > SqlSymbolDisplay.MaxMembers) details.Add("Additional columns omitted from hover.");
        }
        details.Add("Defaults, READONLY modifiers and body expressions are not inferred from this catalog projection.");
        return new(signature, details.ToImmutable(), "Permitted SQL Server catalog metadata; visibility and configured limits apply");
    }

    private static string Type(string schema, string name, int length, byte precision, byte scale)
    {
        var type = SqlSymbolDisplay.Quote(schema) + "." + SqlSymbolDisplay.Quote(name);
        if (schema != "sys") return type; // Alias/CLR types must not acquire fabricated builtin type arguments.
        return name switch
        {
            "nvarchar" or "nchar" => type + "(" + (length == -1 ? "max" : (length / 2).ToString(System.Globalization.CultureInfo.InvariantCulture)) + ")",
            "varchar" or "char" or "binary" or "varbinary" => type + "(" + (length == -1 ? "max" : length.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ")",
            "decimal" or "numeric" => type + $"({precision},{scale})",
            "datetime2" or "datetimeoffset" or "time" => type + $"({scale})",
            _ => type
        };
    }
}
