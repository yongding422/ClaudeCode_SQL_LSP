using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

public sealed record SqlSymbolDocumentation(string Signature, ImmutableArray<string> Details, string Basis);

// Display text never participates in identity comparison or resolution.
public static class SqlSymbolDisplay
{
    public const int MaxMembers = 16;
    public static string OneLine(string value, int limit = 512)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (result.Length + rune.Utf16SequenceLength > limit) { result.Append('…'); break; }
            result.Append(Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format ? " " : rune.ToString());
        }
        return result.ToString();
    }

    public static string Quote(string identifier) => "[" + OneLine(identifier).Replace("]", "]]") + "]";
    public static string Name(QualifiedSqlName name) =>
        (name.Server == null ? "" : Quote(name.Server) + ".") +
        (name.Database == null ? "" : Quote(name.Database) + ".") +
        (name.Schema == null ? name.Database == null ? "" : "." : Quote(name.Schema) + ".") + Quote(name.Name);

    public static string TypeText(TSqlFragment? fragment, SourceText source)
    {
        if (fragment == null || fragment.StartOffset < 0 || fragment.FragmentLength <= 0 ||
            fragment.StartOffset + fragment.FragmentLength > source.Text.Length) return "unknown";
        // Type spans exclude parameter defaults, variable initializers and module bodies.
        var length = Math.Min(fragment.FragmentLength, 512);
        if (length > 0 && char.IsHighSurrogate(source.Text[fragment.StartOffset + length - 1])) length--;
        return OneLine(source.Text.Substring(fragment.StartOffset, length)).Trim() + (length < fragment.FragmentLength ? "…" : "");
    }
}

internal static class SqlSyntaxDocumentation
{
    public static SqlSymbolDocumentation Describe(TSqlFragment node, QualifiedSqlName name, SourceText source)
    {
        var signature = SqlSymbolDisplay.Name(name);
        var details = ImmutableArray.CreateBuilder<string>();
        if (node is ProcedureStatementBodyBase routine)
        {
            signature += " (" + string.Join(", ", routine.Parameters.Take(SqlSymbolDisplay.MaxMembers).Select(p =>
                SqlSymbolDisplay.OneLine(p.VariableName.Value) + " " + SqlSymbolDisplay.TypeText(p.DataType, source) + (p.IsVarying ? " VARYING" : "") +
                (p.Modifier == ParameterModifier.Output ? " OUTPUT" : p.Modifier == ParameterModifier.ReadOnly ? " READONLY" : ""))) +
                (routine.Parameters.Count > SqlSymbolDisplay.MaxMembers ? ", …" : "") + ")";
            if (node is FunctionStatementBody function)
                signature += function.ReturnType switch
                {
                    ScalarFunctionReturnType scalar => " RETURNS " + SqlSymbolDisplay.TypeText(scalar.DataType, source),
                    TableValuedFunctionReturnType or SelectFunctionReturnType => " RETURNS TABLE",
                    _ => " RETURNS unknown"
                };
            details.Add("Parameter defaults and body expressions are omitted.");
        }
        if (node is CreateTableStatement table)
        {
            details.Add("Columns declared in this statement; migrations and database nullability are not inferred:");
            foreach (var column in table.Definition.ColumnDefinitions.Take(SqlSymbolDisplay.MaxMembers))
            {
                var nullable = column.Constraints.OfType<NullableConstraintDefinition>().FirstOrDefault();
                details.Add(SqlSymbolDisplay.Quote(column.ColumnIdentifier.Value) + " " +
                    (column.ComputedColumnExpression != null ? "computed (type not inferred)" : SqlSymbolDisplay.TypeText(column.DataType, source)) +
                    (nullable == null ? " (nullability unspecified)" : nullable.Nullable ? " NULL" : " NOT NULL"));
            }
            if (table.Definition.ColumnDefinitions.Count > SqlSymbolDisplay.MaxMembers) details.Add("Additional columns omitted from hover.");
        }
        if (node is ViewStatementBody view)
        {
            if (view.Columns.Count > 0)
                details.Add("Declared column names: " + string.Join(", ", view.Columns.Take(SqlSymbolDisplay.MaxMembers).Select(c => SqlSymbolDisplay.Quote(c.Value))) +
                    (view.Columns.Count > SqlSymbolDisplay.MaxMembers ? ", …" : ""));
            details.Add("Projected column types are not inferred.");
        }
        return new(signature, details.ToImmutable(), "Declared syntax; not verified database metadata");
    }
}
