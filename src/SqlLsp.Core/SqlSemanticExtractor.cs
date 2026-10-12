using System.Collections.Immutable;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

internal sealed class SqlSemanticExtractor(SourceText source, ResolutionOptions options, CancellationToken token) : TSqlFragmentVisitor
{
    private readonly List<SqlStaticReference> references = new();
    private readonly List<SqlLocalDefinition> locals = new();
    private readonly Stack<Dictionary<string, SqlLocalDefinition>> lexical = new();
    private readonly Stack<Dictionary<string, SqlLocalDefinition>> ctes = new();
    private readonly Stack<Dictionary<string, List<Binding>>> queries = new();
    private readonly Dictionary<int, List<Binding>> dmlTargets = new();
    private string? database;

    public static SqlSemanticDocument Extract(TSqlFragment ast, SourceText source, IEnumerable<int> errors,
        ResolutionOptions options, CancellationToken token)
    {
        var visitor = new SqlSemanticExtractor(source, options, token);
        if (ast is TSqlScript script)
            foreach (var batch in script.Batches)
            {
                visitor.lexical.Push(new(options.Comparer));
                foreach (var statement in batch.Statements)
                {
                    token.ThrowIfCancellationRequested();
                    if (errors.Any(offset => offset >= statement.StartOffset && offset <= statement.StartOffset + statement.FragmentLength)) continue;
                    if (statement is UseStatement use) visitor.database = use.DatabaseName.Value;
                    else
                    {
                        if (statement is AlterTableStatement alter)
                            visitor.Add(QualifiedSqlName.From(alter.SchemaObjectName), alter.SchemaObjectName, SqlReferenceKind.Table);
                        statement.Accept(visitor);
                    }
                }
                visitor.lexical.Pop();
            }
        return new(visitor.references.ToImmutableArray(), visitor.locals.ToImmutableArray());
    }

    public override void ExplicitVisit(CreateProcedureStatement node) => Routine(node, node.Parameters);
    public override void ExplicitVisit(AlterProcedureStatement node) => Routine(node, node.Parameters);
    public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => Routine(node, node.Parameters);
    public override void ExplicitVisit(CreateFunctionStatement node) => Routine(node, node.Parameters);
    public override void ExplicitVisit(AlterFunctionStatement node) => Routine(node, node.Parameters);
    public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => Routine(node, node.Parameters);
    public override void ExplicitVisit(CreateViewStatement node) => Routine(node, []);
    public override void ExplicitVisit(AlterViewStatement node) => Routine(node, []);
    public override void ExplicitVisit(CreateOrAlterViewStatement node) => Routine(node, []);

    private void Routine(TSqlFragment node, IList<ProcedureParameter> parameters)
    {
        var outer = lexical.ToArray();
        lexical.Clear(); // Stored modules cannot capture variables declared in their creation batch.
        lexical.Push(new(options.Comparer));
        foreach (var parameter in parameters) Local(parameter.VariableName.Value, SqlObjectKind.Parameter, parameter.VariableName);
        node.AcceptChildren(this);
        lexical.Pop();
        foreach (var scope in outer.Reverse()) lexical.Push(scope);
    }

    public override void ExplicitVisit(DeclareVariableElement node)
    {
        Local(node.VariableName.Value, SqlObjectKind.Variable, node.VariableName);
        node.Value?.Accept(this);
    }

    public override void ExplicitVisit(DeclareTableVariableStatement node)
    {
        Local(node.Body.VariableName.Value, SqlObjectKind.Variable, node.Body.VariableName);
        node.Body.Definition.Accept(this);
    }

    public override void ExplicitVisit(DropTableStatement node)
    {
        foreach (var nameNode in node.Objects)
        {
            var name = QualifiedSqlName.From(nameNode);
            var local = RelationLocal(name);
            Add(name, nameNode, SqlReferenceKind.Table, local,
                name.Name.StartsWith('#') && local == null ? "Temporary table scope is unknown." : null);
            if (name.Name.StartsWith('#')) lexical.Peek().Remove(name.Name);
        }
    }

    public override void ExplicitVisit(VariableReference node)
    {
        var local = FindLocal(lexical, node.Name);
        Add(null, node, SqlReferenceKind.Variable, local, local == null ? "Variable scope is unknown." : null);
    }

    public override void ExplicitVisit(CreateTableStatement node)
    {
        var name = QualifiedSqlName.From(node.SchemaObjectName);
        if (name.Name.StartsWith('#')) Local(name.Name, SqlObjectKind.TempTable, node.SchemaObjectName);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(SelectStatement node)
    {
        WithCtes(node);
    }

    // WITH can also scope CTEs used by INSERT/UPDATE/DELETE/MERGE.
    public override void ExplicitVisit(InsertStatement node) => WithCtes(node);
    public override void ExplicitVisit(UpdateStatement node) => WithCtes(node);
    public override void ExplicitVisit(DeleteStatement node) => WithCtes(node);
    public override void ExplicitVisit(MergeStatement node) => WithCtes(node);

    private void WithCtes(StatementWithCtesAndXmlNamespaces node)
    {
        var scope = new Dictionary<string, SqlLocalDefinition>(options.Comparer);
        foreach (var cte in node.WithCtesAndXmlNamespaces?.CommonTableExpressions ?? [])
        {
            var definition = new SqlLocalDefinition(cte.ExpressionName.Value, SqlObjectKind.Cte,
                source.Range(cte.ExpressionName.StartOffset, cte.ExpressionName.FragmentLength));
            // Duplicate local names are unsafe: retain no binding for them.
            if (!scope.TryAdd(definition.Name, definition)) scope[definition.Name] = definition with { IsAmbiguous = true };
            locals.Add(definition);
        }
        ctes.Push(scope);
        var dml = node switch
        {
            UpdateStatement update => (update.UpdateSpecification.Target as NamedTableReference, update.UpdateSpecification.FromClause),
            DeleteStatement delete => (delete.DeleteSpecification.Target as NamedTableReference, delete.DeleteSpecification.FromClause),
            _ => (null, null)
        };
        if (dml.Item1 != null && dml.Item2 != null)
        {
            var collector = new RelationCollector(source, options);
            dml.Item2.Accept(collector);
            var target = QualifiedSqlName.From(dml.Item1.SchemaObject);
            if (target.Schema == null && target.Database == null && collector.Bindings.TryGetValue(target.Name, out var bindings))
                dmlTargets[dml.Item1.StartOffset] = bindings;
            queries.Push(collector.Bindings);
            node.AcceptChildren(this);
            queries.Pop();
        }
        else node.AcceptChildren(this);
        ctes.Pop();
    }

    public override void ExplicitVisit(QuerySpecification node)
    {
        var collector = new RelationCollector(source, options);
        node.FromClause?.Accept(collector);
        queries.Push(collector.Bindings);
        node.AcceptChildren(this);
        queries.Pop();
    }

    public override void ExplicitVisit(NamedTableReference node)
    {
        var name = QualifiedSqlName.From(node.SchemaObject);
        if (dmlTargets.TryGetValue(node.StartOffset, out var aliases))
        {
            if (aliases.Count != 1) { Add(null, node.SchemaObject, SqlReferenceKind.Alias, null, "Ambiguous DML target alias."); return; }
            var alias = aliases[0];
            var target = BindingLocal(alias);
            Add(alias.Name, node.SchemaObject, alias.Kind, target,
                alias.Name == null && target == null ? "Unsupported DML target alias." : null);
            base.ExplicitVisit(node);
            return;
        }
        var local = RelationLocal(name);
        Add(name, node.SchemaObject, SqlReferenceKind.Relation, local,
            name.Name.StartsWith('#') && local == null ? "Temporary table scope is unknown." : null);
        if (node.Alias != null) Add(name, node.Alias, SqlReferenceKind.Relation, local,
            name.Name.StartsWith('#') && local == null ? "Temporary table scope is unknown." : null);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(ColumnReferenceExpression node)
    {
        var parts = node.MultiPartIdentifier?.Identifiers;
        if (parts?.Count != 2) return; // Column binding beyond a verified local qualifier is deferred.
        var qualifier = parts[0];
        foreach (var scope in queries)
        {
            if (!scope.TryGetValue(qualifier.Value, out var bindings)) continue;
            if (bindings.Count != 1) { Add(null, qualifier, SqlReferenceKind.Alias, null, "Ambiguous query alias."); return; }
            var binding = bindings[0];
            var local = BindingLocal(binding);
            Add(binding.Name, qualifier, binding.Kind, local,
                local == null && (binding.VariableName != null || binding.Name?.Name.StartsWith('#') == true) ? "Temporary/table variable scope is unknown." : null);
            return;
        }
        Add(null, qualifier, SqlReferenceKind.Alias, null, "Unknown query qualifier.");
    }

    public override void ExplicitVisit(ExecuteSpecification node)
    {
        if (node.ExecutableEntity is ExecutableProcedureReference procedure)
        {
            var name = procedure.ProcedureReference?.ProcedureReference?.Name;
            if (name != null && procedure.ProcedureReference?.ProcedureReference?.Number == null && procedure.AdHocDataSource == null && node.LinkedServer == null)
                Add(QualifiedSqlName.From(name), name, SqlReferenceKind.Procedure);
            else Add(null, procedure, SqlReferenceKind.Dynamic, null, "Variable, numbered, remote or ad-hoc EXEC target is unsupported.");
        }
        else Add(null, node.ExecutableEntity, SqlReferenceKind.Dynamic, null, "Dynamic SQL target is unsupported.");
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(ExecuteParameter node)
    {
        // The optional named argument label is the callee's parameter, not a caller variable use.
        node.ParameterValue?.Accept(this);
    }

    public override void ExplicitVisit(FunctionCall node)
    {
        if (node.CallTarget is MultiPartIdentifierCallTarget target)
        {
            var parts = target.MultiPartIdentifier.Identifiers.Select(i => i.Value).Append(node.FunctionName.Value);
            if (target.MultiPartIdentifier.Identifiers.Count <= 3)
            {
                var start = target.StartOffset;
                Add(QualifiedSqlName.FromParts(parts), source.Range(start, node.FunctionName.StartOffset + node.FunctionName.FragmentLength - start), SqlReferenceKind.Function);
            }
        }
        // Unqualified functions may be built-ins; do not fabricate a user-function binding.
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
    {
        Add(QualifiedSqlName.From(node.SchemaObject), node.SchemaObject, SqlReferenceKind.Function);
        if (node.Alias != null) Add(QualifiedSqlName.From(node.SchemaObject), node.Alias, SqlReferenceKind.Function);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(QueryDerivedTable node)
    {
        if (node.Alias != null) locals.Add(new(node.Alias.Value, SqlObjectKind.Alias, source.Range(node.Alias.StartOffset, node.Alias.FragmentLength)));
        base.ExplicitVisit(node);
    }

    private SqlLocalDefinition? RelationLocal(QualifiedSqlName name)
    {
        if (name.Server != null || name.Database != null || name.Schema != null) return null;
        return FindLocal(ctes, name.Name) ?? (name.Name.StartsWith('#') ? FindLocal(lexical, name.Name) : null);
    }

    private SqlLocalDefinition? BindingLocal(Binding binding) => binding.VariableName != null
        ? FindLocal(lexical, binding.VariableName) : binding.Local ?? (binding.Name == null ? null : RelationLocal(binding.Name));

    private static SqlLocalDefinition? FindLocal(Stack<Dictionary<string, SqlLocalDefinition>> scopes, string name)
    {
        foreach (var scope in scopes) if (scope.TryGetValue(name, out var local)) return local;
        return null;
    }

    private void Local(string name, SqlObjectKind kind, TSqlFragment node)
    {
        var definition = new SqlLocalDefinition(name, kind, source.Range(node.StartOffset, node.FragmentLength));
        if (lexical.Peek().ContainsKey(name)) definition = definition with { IsAmbiguous = true };
        lexical.Peek()[name] = definition;
        locals.Add(definition);
    }

    private void Add(QualifiedSqlName? name, TSqlFragment? node, SqlReferenceKind kind, SqlLocalDefinition? local = null, string? unsupported = null)
    {
        if (node != null) Add(name, source.Range(node.StartOffset, node.FragmentLength), kind, local, unsupported);
    }

    private void Add(QualifiedSqlName? name, TextRange range, SqlReferenceKind kind, SqlLocalDefinition? local = null, string? unsupported = null)
    {
        token.ThrowIfCancellationRequested();
        if (local?.IsAmbiguous == true || (local?.Kind == SqlObjectKind.Cte && source.OffsetAt(local.Range.Start) > source.OffsetAt(range.Start)))
        {
            unsupported = "Duplicate or forward local binding is unsupported.";
            local = null;
        }
        var start = source.OffsetAt(range.Start);
        var end = source.OffsetAt(range.End);
        references.Add(new(name, source.Text[start..end], kind, range, database, local, unsupported));
    }

    private sealed record Binding(QualifiedSqlName? Name, SqlReferenceKind Kind, SqlLocalDefinition? Local = null, string? VariableName = null);

    private sealed class RelationCollector(SourceText source, ResolutionOptions options) : TSqlFragmentVisitor
    {
        public Dictionary<string, List<Binding>> Bindings { get; } = new(options.Comparer);
        // Subqueries in JOIN predicates have their own namespace, just like derived tables.
        public override void ExplicitVisit(QuerySpecification node) { }
        public override void ExplicitVisit(ScalarSubquery node) { }
        public override void ExplicitVisit(NamedTableReference node) =>
            Bind(node.Alias?.Value ?? node.SchemaObject.BaseIdentifier.Value, new(QualifiedSqlName.From(node.SchemaObject), SqlReferenceKind.Relation));
        public override void ExplicitVisit(SchemaObjectFunctionTableReference node) =>
            Bind(node.Alias?.Value ?? node.SchemaObject.BaseIdentifier.Value, new(QualifiedSqlName.From(node.SchemaObject), SqlReferenceKind.Function));
        public override void ExplicitVisit(QueryDerivedTable node)
        {
            if (node.Alias != null) Bind(node.Alias.Value, new(null, SqlReferenceKind.Alias,
                new(node.Alias.Value, SqlObjectKind.Alias, source.Range(node.Alias.StartOffset, node.Alias.FragmentLength))));
        }
        public override void ExplicitVisit(VariableTableReference node) =>
            Bind(node.Alias?.Value ?? node.Variable.Name, new(null, SqlReferenceKind.Variable, VariableName: node.Variable.Name));
        private void Bind(string alias, Binding binding)
        {
            if (!Bindings.TryGetValue(alias, out var list)) Bindings[alias] = list = new();
            list.Add(binding);
        }
    }
}
