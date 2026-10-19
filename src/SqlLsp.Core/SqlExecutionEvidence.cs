using System.Collections.Immutable;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

// Source observations only: no execution, dynamic SQL evaluation or reaching-table-content claims.
internal sealed class SqlExecutionEvidence(DocumentSnapshot document, int budget, CancellationToken token) : TSqlFragmentVisitor
{
    private readonly List<SqlSourceObservation> facts = [];
    private readonly HashSet<TextRange> dynamicVariables = [];
    private int begins, commits, rollbacks, atomic;
    private bool truncated;
    private SourceText Source => document.Parse.Source;
    private DocumentSnapshot Document => document;
    private void CheckToken() => token.ThrowIfCancellationRequested();

    public static (ImmutableArray<SqlSourceObservation> Facts, bool Truncated) Extract(TSqlFragment body,
        DocumentSnapshot document, int budget, CancellationToken token)
    {
        if (body is ProcedureStatementBodyBase { MethodSpecifier: not null }) return ([], false);
        var visitor = new SqlExecutionEvidence(document, budget, token);
        body.Accept(new DynamicVariables(visitor));
        if (body is ProcedureStatementBody { Options.Count: > 0 } procedure)
            visitor.Add(new("DeclaredExecutionOptions", "Procedure", visitor.Location(procedure.Options[0]),
                "Declared procedure options; each source preview is syntax, not proof of caller authorization or effective runtime privileges.")
            {
                Options = procedure.Options.Take(16).Select(o => new SqlDeclaredSourceOption(o.OptionKind.ToString(), visitor.OptionPreview(o))).ToImmutableArray(),
                InputsComplete = procedure.Options.Count <= 16
            });
        body.Accept(visitor);
        // Absence is asserted only for a completely parsed body, never after parse recovery.
        var complete = document.Parse.Diagnostics.IsEmpty;
        var style = visitor.atomic > 0 ? "AtomicBlock" : visitor.begins > 0 ? "ExplicitStatements" : complete ? "CallerDependent" : "Unknown";
        var detail = style switch
        {
            "AtomicBlock" => "Declared atomic block. With an existing transaction it uses a savepoint and need not commit the caller transaction. Under SNAPSHOT a transaction sees its own writes; later no-row SELECT assignments can still retain old variables. Runtime success/conflicts are unknown.",
            "ExplicitStatements" => "Explicit transaction statements observed. Caller transaction and session mode are unknown: nested COMMIT need not end the caller transaction, and a full ROLLBACK can affect it. Counts/source order do not establish balanced or successful transactions.",
            "CallerDependent" => "No explicit BEGIN TRAN or atomic block in this parsed body. Caller transaction/session mode are unknown. Separate statement commits require autocommit with no enclosing transaction; do not assert that mode or a partial-commit outcome from this body alone.",
            _ => "Incomplete parsed source prevents proving transaction-boundary absence. Caller transaction/session mode and outcomes remain unknown."
        };
        var contract = new SqlSourceObservation("TransactionContract", style,
            new(document.Uri, document.Parse.Source.Range(body.StartOffset, body.FragmentLength)), detail)
        { Transactions = new(visitor.begins, visitor.commits, visitor.rollbacks, visitor.atomic, complete) };
        if (budget > 0)
        {
            if (visitor.facts.Count >= budget) { visitor.facts.RemoveAt(visitor.facts.Count - 1); visitor.truncated = true; }
            visitor.facts.Insert(0, contract);
        }
        else visitor.truncated = true;
        return (visitor.facts.ToImmutableArray(), visitor.truncated);
    }

    public override void Visit(TSqlFragment node) => token.ThrowIfCancellationRequested();
    public override void ExplicitVisit(BeginTransactionStatement node) { begins++; node.AcceptChildren(this); }
    public override void ExplicitVisit(CommitTransactionStatement node) { commits++; node.AcceptChildren(this); }
    public override void ExplicitVisit(RollbackTransactionStatement node) { rollbacks++; node.AcceptChildren(this); }

    public override void ExplicitVisit(BeginEndAtomicBlockStatement node)
    {
        atomic++;
        foreach (var option in node.Options)
            Add(new("AtomicOption", option.OptionKind.ToString(), Location(option),
                "Declared atomic-block option. No runtime isolation, row count or transaction outcome is inferred.")
            { Expression = Preview(option) });
        node.AcceptChildren(this);
    }

    public override void ExplicitVisit(InsertSpecification node)
    {
        if (Table(node.Target) is { VerifiedLocal: true } target)
        {
            var inputs = Relations(node.InsertSource);
            Add(new("LocalTableTransfer", target.Name, Location(node),
                "Syntactic insert into a verified local table. Inputs identify source relations only; rows, columns, paths, EXEC result shape and reaching table contents are not inferred.")
            {
                Operation = node.InsertSource is ExecuteInsertSource ? "InsertExecute" : "Insert",
                Inputs = inputs.Values,
                InputsComplete = inputs.Complete,
                Expression = Preview(node.InsertSource)
            });
        }
        node.AcceptChildren(this);
    }

    public override void ExplicitVisit(SelectStatement node)
    {
        if (node.Into != null && TableName(node.Into) is { VerifiedLocal: true } target)
        {
            var inputs = Relations(node.QueryExpression);
            Add(new("LocalTableTransfer", target.Name, Location(node),
                "SELECT INTO creates/populates this local table from the cited query sources. No row count, column lineage or definite execution path is inferred.")
            { Operation = "SelectInto", Inputs = inputs.Values, InputsComplete = inputs.Complete, Expression = Preview(node.QueryExpression) });
        }
        node.AcceptChildren(this);
    }

    public override void ExplicitVisit(SetVariableStatement node)
    {
        if (node.Expression != null)
        {
            LocalRead(node.Variable.Name, node.Expression);
            if (Local(node.Variable) is { } local && dynamicVariables.Contains(local.Range))
                Add(new("DynamicSqlConstruction", node.Variable.Name, Location(node),
                    "Expression assigned to a variable used as EXEC text in this module. Preview is unevaluated string construction, not parsed dynamic SQL or proof of its relations/effects; reassignment, branches and actual execution are not solved.")
                { Expression = Preview(node.Expression) });
        }
        node.AcceptChildren(this);
    }

    public override void ExplicitVisit(QuerySpecification node)
    {
        foreach (var assigned in node.SelectElements.OfType<SelectSetVariable>())
            LocalRead(assigned.Variable.Name, node, assigned.Expression);
        node.AcceptChildren(this);
    }

    public override void ExplicitVisit(ExecutableStringList node)
    {
        Add(new("DynamicExecution", null, Location(node),
            "EXEC string syntax. Literal text is omitted here; variable-only text expressions may be shown. Dynamic relations/effects are not evaluated or executed, and visible local tables do not prove its read/write targets.")
        {
            Operation = node.Strings.All(s => s is VariableReference) ? "VariableText" : "TextOmitted",
            Expression = node.Strings.All(s => s is VariableReference) ? Preview(node) : null
        });
        node.AcceptChildren(this);
    }

    private void LocalRead(string target, TSqlFragment query, TSqlFragment? expression = null)
    {
        var inputs = Relations(query);
        if (!inputs.Values.Any(i => i.VerifiedLocal)) return;
        Add(new("LocalTableRead", target, Location(query),
            "Variable assignment reads the cited local table(s). This links source syntax, not table contents, one row, a reaching insert or a concrete value. SELECT no-row retention differs from a scalar subquery's no-value NULL case.")
        { Operation = "ReadToVariable", Inputs = inputs.Values, InputsComplete = inputs.Complete, Expression = Preview(expression ?? query) });
    }

    private void Add(SqlSourceObservation observation)
    {
        token.ThrowIfCancellationRequested();
        if (facts.Count >= budget) { truncated = true; return; }
        facts.Add(observation);
        if (observation.InputsComplete == false) truncated = true;
    }
    private SqlAnalysisLocation Location(TSqlFragment fragment) => new(document.Uri, Source.Range(fragment.StartOffset, fragment.FragmentLength));
    private SqlFlowExpression Preview(TSqlFragment fragment)
        => Preview(fragment.StartOffset, fragment.FragmentLength);
    private SqlFlowExpression OptionPreview(ProcedureOption option)
    {
        if (option is not ExecuteAsProcedureOption) return Preview(option);
        // ScriptDOM 180 gives this option/clause only the EXECUTE token span. The grammar's
        // three significant tokens provide the source extent; never reconstruct a principal.
        var tokens = option.ScriptTokenStream?.Skip(Math.Max(0, option.FirstTokenIndex)).Take(32)
            .Where(t => t.TokenType != TSqlTokenType.WhiteSpace && !t.TokenType.ToString().EndsWith("Comment", StringComparison.Ordinal)).Take(3).ToArray();
        if (tokens is { Length: 3 } && tokens[0].Text.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase) &&
            tokens[1].Text.Equals("AS", StringComparison.OrdinalIgnoreCase))
            return Preview(option.StartOffset, tokens[2].Offset + tokens[2].Text.Length - option.StartOffset);
        return Preview(option) with { Truncated = true };
    }
    private SqlFlowExpression Preview(int offset, int size)
    {
        var length = Math.Min(256, size);
        if (length > 0 && char.IsHighSurrogate(Source.Text[offset + length - 1])) length--;
        return new(new(document.Uri, Source.Range(offset, size)), SqlSymbolDisplay.OneLine(Source.Text.Substring(offset, length), 256), length < size);
    }
    private SqlLocalDefinition? Local(TSqlFragment fragment) => document.Parse.Semantics.References
        .FirstOrDefault(r => r.Range == Location(fragment).Range)?.LocalTarget;
    private SqlObservationInput? Table(TableReference table) => table switch
    {
        NamedTableReference named => TableName(named.SchemaObject),
        VariableTableReference variable => Input(variable.Variable, variable.Variable.Name),
        _ => null
    };
    private SqlObservationInput TableName(SchemaObjectName name) => Input(name, SqlSymbolDisplay.Name(QualifiedSqlName.From(name)));
    private SqlObservationInput Input(TSqlFragment node, string syntax)
    {
        var local = Local(node);
        var verified = local is { IsAmbiguous: false } && (local.Kind == SqlObjectKind.TempTable ||
            local.Kind == SqlObjectKind.Variable && local.DeclaredType?.StartsWith("TABLE", StringComparison.Ordinal) == true);
        return new(local?.Name ?? syntax, verified ? local!.Kind == SqlObjectKind.TempTable ? "TempTable" : "TableVariable" : "RelationSyntax", Location(node), verified);
    }
    private (ImmutableArray<SqlObservationInput> Values, bool Complete) Relations(TSqlFragment node)
    {
        var visitor = new RelationsVisitor(this);
        node.Accept(visitor);
        return (visitor.Inputs.Take(16).ToImmutableArray(), visitor.Inputs.Count <= 16);
    }
    private sealed class RelationsVisitor(SqlExecutionEvidence owner) : TSqlFragmentVisitor
    {
        public List<SqlObservationInput> Inputs { get; } = [];
        public override void Visit(TSqlFragment node) => owner.CheckToken();
        public override void ExplicitVisit(NamedTableReference node) { Inputs.Add(owner.TableName(node.SchemaObject)); node.AcceptChildren(this); }
        public override void ExplicitVisit(VariableTableReference node) { Inputs.Add(owner.Input(node.Variable, node.Variable.Name)); node.AcceptChildren(this); }
    }
    private sealed class DynamicVariables(SqlExecutionEvidence owner) : TSqlFragmentVisitor
    {
        public override void Visit(TSqlFragment node) => owner.CheckToken();
        public override void ExplicitVisit(ExecutableStringList node)
        {
            var range = owner.Location(node).Range;
            foreach (var reference in owner.Document.Parse.Semantics.References.Where(r =>
                SqlNavigationIndex.Contains(range, r.Range.Start) && r.LocalTarget is { Kind: SqlObjectKind.Variable }))
                owner.dynamicVariables.Add(reference.LocalTarget!.Range);
        }
    }
}
