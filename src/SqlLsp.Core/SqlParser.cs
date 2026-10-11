using System.Collections.Immutable;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

public enum SqlObjectKind { Procedure, View, Function, Table, Cte, TempTable, Alias, Variable, Parameter }
public enum SqlSourceKind { Workspace, UnsavedBuffer }
public sealed record SqlDeclaration(string Name, SqlObjectKind Kind, TextRange Range, TextRange SelectionRange,
    QualifiedSqlName? QualifiedName = null, string? Database = null, bool IsDefinition = true);
public sealed record SqlParseDiagnostic(int Code, string Message, TextRange Range);
public sealed record SqlParseResult(SourceText Source, TSqlFragment Ast,
    ImmutableArray<TSqlParserToken> Tokens, ImmutableArray<SqlParseDiagnostic> Diagnostics,
    ImmutableArray<SqlDeclaration> Declarations, SqlSemanticDocument Semantics);

public sealed record ParserOptions(string Version = "170", bool QuotedIdentifier = true)
{
    public SqlVersion Dialect => Enum.TryParse<SqlVersion>("Sql" + Version, out var version) && Enum.IsDefined(version)
        ? version : throw new ArgumentException($"Unsupported ScriptDOM parserVersion '{Version}'.");
}

public interface ISqlParser
{
    Task<SqlParseResult> ParseAsync(SourceText source, CancellationToken cancellationToken);
}

public sealed class SqlParser(ParserOptions options, ResolutionOptions? resolution = null) : ISqlParser
{
    private readonly SqlVersion dialect = options.Dialect;

    public Task<SqlParseResult> ParseAsync(SourceText source, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parser = TSqlParser.CreateParser(dialect, options.QuotedIdentifier);
        using var reader = new CancellableReader(source.Text, cancellationToken);
        var tokens = parser.GetTokenStream(reader, out var lexicalErrors);
        var parsedAst = parser.Parse(tokens, out var parseErrors);
        var errors = lexicalErrors.Concat(parseErrors).ToArray();
        var ast = parsedAst ?? new TSqlScript();
        cancellationToken.ThrowIfCancellationRequested();
        var visitor = new DeclarationVisitor(source, errors, cancellationToken);
        ast.Accept(visitor);
        var diagnostics = errors.Select(error =>
        {
            var offset = Math.Clamp(error.Offset, 0, source.Text.Length);
            var token = tokens.FirstOrDefault(t => t.Offset == offset);
            var length = Math.Min(token?.Text?.Length ?? (offset < source.Text.Length ? 1 : 0), source.Text.Length - offset);
            return new SqlParseDiagnostic(error.Number, error.Message, source.Range(offset, length));
        }).ToImmutableArray();
        var semantics = SqlSemanticExtractor.Extract(ast, source, errors, resolution ?? new(), cancellationToken);
        return new SqlParseResult(source, ast, tokens.ToImmutableArray(), diagnostics,
            visitor.Declarations.ToImmutableArray(), semantics);
    }, cancellationToken);

    private sealed class DeclarationVisitor(SourceText source, IList<ParseError> errors, CancellationToken token) : TSqlFragmentVisitor
    {
        public List<SqlDeclaration> Declarations { get; } = new();
        private string? database;

        // Only batch-level declarations: a procedure's local CREATE TABLE is not a global definition.
        public override void ExplicitVisit(TSqlBatch node)
        {
            foreach (var statement in node.Statements)
            {
                token.ThrowIfCancellationRequested();
                switch (statement)
                {
                    case UseStatement use:
                        database = use.DatabaseName.Value; break;
                    case ProcedureStatementBody procedure:
                        Add(procedure, procedure.ProcedureReference.Name, SqlObjectKind.Procedure, procedure.ProcedureReference.Number == null); break;
                    case ViewStatementBody view:
                        Add(view, view.SchemaObjectName, SqlObjectKind.View); break;
                    case FunctionStatementBody function:
                        Add(function, function.Name, SqlObjectKind.Function); break;
                    case CreateTableStatement table:
                        Add(table, table.SchemaObjectName, SqlObjectKind.Table); break;
                    case AlterTableStatement table:
                        Add(table, table.SchemaObjectName, SqlObjectKind.Table, false); break;
                }
            }
        }

        private void Add(TSqlFragment node, SchemaObjectName? name, SqlObjectKind kind, bool isDefinition = true)
        {
            if (name == null || name.Identifiers.Count == 0 || node.FragmentLength <= 0) return;
            if (errors.Any(e => e.Offset >= node.StartOffset && e.Offset <= node.StartOffset + node.FragmentLength)) return;
            Declarations.Add(new SqlDeclaration(string.Join('.', name.Identifiers.Select(i => i.Value)), kind,
                source.Range(node.StartOffset, node.FragmentLength), source.Range(name.StartOffset, name.FragmentLength),
                QualifiedSqlName.From(name), database, isDefinition));
        }
    }

    private sealed class CancellableReader(string text, CancellationToken token) : StringReader(text)
    {
        public override int Peek() { token.ThrowIfCancellationRequested(); return base.Peek(); }
        public override int Read() { token.ThrowIfCancellationRequested(); return base.Read(); }
        public override int Read(char[] buffer, int index, int count)
        { token.ThrowIfCancellationRequested(); return base.Read(buffer, index, count); }
        public override int Read(Span<char> buffer)
        { token.ThrowIfCancellationRequested(); return base.Read(buffer); }
    }
}
