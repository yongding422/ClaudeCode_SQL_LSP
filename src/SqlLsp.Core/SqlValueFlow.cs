using System.Collections.Immutable;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

public enum SqlValueFactKind { ParameterInput, ParameterDefault, Declaration, Assignment, SelectAssignment, Predicate, CallArgument, CallOutput, CallReturn, Return }
public enum SqlArgumentMode { Explicit, ExplicitDefault, OmittedDefault }
public sealed record SqlValueVariable(string Id, string Name, SqlObjectKind Kind, SqlAnalysisLocation Definition, string? Type,
    int? Ordinal, bool Output, bool HasDefault);
public sealed record SqlValueExpression(string Kind, SqlAnalysisLocation Location, string Preview, bool Truncated);
public sealed record SqlValueInput(string Name, string? VariableId, SqlAnalysisLocation Location, ResolutionStatus Status,
    ImmutableArray<string> SourceFacts, bool SourcesComplete);
public sealed record SqlValueFact(string Id, SqlValueFactKind Kind, string Role, SqlAnalysisLocation Location,
    string? TargetVariableId, SqlValueExpression? Expression, ImmutableArray<SqlValueInput> Inputs,
    ImmutableArray<string> FlowContext, ResolutionStatus Status, string Detail);
public sealed record SqlArgumentBinding(int Ordinal, string ParameterName, string ParameterVariableId,
    SqlAnalysisLocation Parameter, string InputFactId, SqlArgumentMode Mode, bool Output, string? OutputFactId);
public sealed record SqlValueCall(string Id, SqlAnalysisLocation Location, SqlNavigationTarget? Target,
    string? TargetModuleId, string? TargetRevision, ResolutionStatus BindingStatus,
    ImmutableArray<SqlArgumentBinding> Bindings, string Detail);
public sealed record SqlValueFlowSummary(bool BodyAvailable, ImmutableArray<SqlValueVariable> Variables,
    ImmutableArray<SqlValueFact> Facts, ImmutableArray<SqlValueCall> Calls, bool Truncated,
    ImmutableArray<SqlCoverageIssue> Issues, ImmutableArray<string> Limitations)
{
    public int FactCount => Variables.Length + Facts.Length + Calls.Length + Calls.Sum(c => c.Bindings.Length);
}

internal sealed record SqlValueSignature(SqlNavigationTarget? Target, string? ModuleId, string? Revision,
    DocumentSnapshot? Document, ImmutableArray<ProcedureParameter> Parameters, StringComparer Comparer, string? Error, bool Limited = false);

// Bounded source definitions, not constant evaluation, a CFG, SQL type checking or column lineage.
internal sealed partial class SqlValueFlow(DocumentSnapshot document, TSqlFragment body, SqlControlFlowSummary flow,
    Func<ExecuteSpecification, SqlValueSignature> signature, int budget, CancellationToken token) : TSqlFragmentVisitor
{
    private sealed record Origins(ImmutableArray<string> Facts, bool Complete);
    private readonly List<SqlValueVariable> variables = [];
    private readonly List<SqlValueFact> facts = [];
    private readonly List<SqlValueCall> calls = [];
    private readonly List<SqlCoverageIssue> issues = [];
    private readonly Dictionary<TextRange, SqlStaticReference> referenceBindings = document.Parse.Semantics.References
        .Where(r => r.Kind == SqlReferenceKind.Variable).GroupBy(r => r.Range).ToDictionary(g => g.Key, g => g.First());
    private readonly Dictionary<TextRange, SqlLocalDefinition> localBindings = document.Parse.Semantics.Locals
        .GroupBy(l => l.Range).ToDictionary(g => g.Key, g => g.First());
    private Dictionary<string, Origins> state = new(StringComparer.Ordinal);
    private readonly Stack<List<(string Variable, string? Fact)>> selectWrites = new();
    private bool truncated;
    private bool unstructured;
    private int used;
    private static readonly ImmutableArray<string> Limitations =
    [
        "Source dependencies only, not observed values, SQL type/coercion checking, predicate evaluation, reachability or full column lineage. Resolved binding never proves execution or a particular value.",
        "SourceFacts are module-local possible definition IDs. SourcesComplete means supported source tracking, not a known runtime value. Branch alternatives are merged without path correlations; do not combine them into a proven path.",
        "Loops and TRY/CATCH invalidate ordered source certainty; GOTO disables it throughout the module. SELECT assignments retain unknown row/order behavior. Unsupported writes and dynamic calls invalidate affected source tracking.",
        "EXEC bindings require a uniquely selected procedure and available parsed signature. Defaults are separate from entry inputs. OUTPUT exposes a copy-back boundary with an unknown value, not a proven callee exit definition; return status is separate.",
        "Call inputs are captured before copy-back. Callee parameter inputs remain symbolic: do not substitute one caller's values into a shared callee report. Function-call arguments, globals, cursor/row contents, temp/table-variable value propagation and column lineage are not traced.",
        "Uses cover supported assignments, EXEC arguments, RETURN and IF/WHILE/CASE/WHERE/HAVING/JOIN conditions. Absence of a fact does not prove absence of value use. Revisions, source availability and report limits still apply."
    ];

    public static SqlValueFlowSummary Extract(DocumentSnapshot document, TSqlFragment? body, SqlControlFlowSummary flow,
        Func<ExecuteSpecification, SqlValueSignature> signature, int budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (body == null) return new(false, [], [], [], false, [], Limitations);
        var visitor = new SqlValueFlow(document, body, flow, signature, Math.Clamp(budget, 0, 512), token);
        var scanner = new JumpScanner(token); body.Accept(scanner); visitor.unstructured = scanner.Found;
        if (visitor.unstructured) visitor.Issue("value-unstructured-flow", "GOTO prevents ordered definition tracking throughout this module.", body);
        visitor.Seed();
        body.Accept(visitor);
        if (visitor.truncated) visitor.Issue("value-flow-limit", "Value report is incomplete: variable/fact/input/source/signature budget. Missing edges do not establish no dependencies.", body);
        return new(true, visitor.variables.ToImmutableArray(), visitor.facts.ToImmutableArray(), visitor.calls.ToImmutableArray(),
            visitor.truncated, visitor.issues.ToImmutableArray(), Limitations);
    }

    private void Seed()
    {
        var parameters = (body as ProcedureStatementBodyBase)?.Parameters ?? [];
        foreach (var local in document.Parse.Semantics.Locals.Where(l =>
            l.Kind is SqlObjectKind.Parameter or SqlObjectKind.Variable && SqlNavigationIndex.Contains(Range(body), l.Range.Start)))
        {
            token.ThrowIfCancellationRequested();
            if (variables.Count >= 128 || !Reserve()) { truncated = true; break; }
            var ordinal = parameters.ToList().FindIndex(p => Range(p.VariableName) == local.Range);
            var parameter = ordinal < 0 ? null : parameters[ordinal];
            var variable = new SqlValueVariable(VariableId(document, local.Range), local.Name, local.Kind, new(document.Uri, local.Range),
                local.DeclaredType, parameter == null ? null : ordinal + 1, parameter?.Modifier == ParameterModifier.Output, parameter?.Value != null);
            variables.Add(variable);
            if (local.Kind != SqlObjectKind.Parameter) continue;
            var input = Fact(SqlValueFactKind.ParameterInput, "Entry", local.Range, variable.Id, null, [], true,
                "Symbolic entry input; caller/default/runtime value is not substituted into this module.");
            Set(variable.Id, input, true);
            if (parameter?.Value != null) Observe(SqlValueFactKind.ParameterDefault, "Default", parameter, variable.Id, parameter.Value,
                "Declared default only; not applied to entry state unless selected by a verified call binding.");
        }
    }

    internal static string VariableId(DocumentSnapshot doc, TextRange range) => "variable-" + doc.Parse.Source.OffsetAt(range.Start);
    private TextRange Range(TSqlFragment node) => document.Parse.Source.Range(node.StartOffset, node.FragmentLength);
    private bool Reserve(int count = 1)
    {
        if (used + count > budget) { truncated = true; return false; }
        used += count; return true;
    }
    private void Issue(string code, string detail, TSqlFragment node)
    {
        if (issues.Count < 128 && !issues.Any(i => i.Code == code && i.Location?.Range == Range(node))) issues.Add(new(code, detail, new(document.Uri, Range(node))));
        else if (issues.Count >= 128) truncated = true;
    }
    public override void Visit(TSqlFragment node) => token.ThrowIfCancellationRequested();
    public override void ExplicitVisit(ProcedureParameter node) { } // Defaults are observations, not entry assignments.

    private SqlValueVariable? Variable(TextRange range, bool declaration = false)
    {
        var reference = declaration ? null : referenceBindings.GetValueOrDefault(range);
        var local = declaration ? localBindings.GetValueOrDefault(range) : reference?.LocalTarget;
        if (local == null || local.IsAmbiguous || reference?.UnsupportedReason != null) return null;
        return variables.FirstOrDefault(v => v.Definition.Range == local.Range);
    }

    private ImmutableArray<SqlValueInput> Inputs(TSqlFragment? expression, VariableReference? previous = null)
    {
        var references = expression == null ? [] : document.Parse.Semantics.References.Where(r => r.Kind == SqlReferenceKind.Variable &&
            SqlNavigationIndex.Contains(Range(expression), r.Range.Start)).ToArray();
        if (previous != null) references = references.Concat(document.Parse.Semantics.References.Where(r => r.Kind == SqlReferenceKind.Variable && r.Range == Range(previous))).ToArray();
        if (references.Length > 16) { truncated = true; Issue("value-input-limit", "Expression has more than 16 variable occurrences; input dependencies are incomplete.", expression ?? previous!); }
        return references.Take(16).Select(r =>
        {
            var variable = Variable(r.Range);
            var origin = variable != null && !unstructured && state.TryGetValue(variable.Id, out var found) ? found : new Origins([], false);
            return new SqlValueInput(r.RawName, variable?.Id, new(document.Uri, r.Range), variable == null ? ResolutionStatus.Unsupported : ResolutionStatus.Resolved,
                origin.Facts, origin.Complete && references.Length <= 16);
        }).ToImmutableArray();
    }

    private static SqlValueExpression? Expression(DocumentSnapshot doc, TSqlFragment? node)
    {
        if (node == null) return null;
        var length = Math.Min(node.FragmentLength, 256);
        if (length > 0 && char.IsHighSurrogate(doc.Parse.Source.Text[node.StartOffset + length - 1])) length--;
        var kind = node switch { VariableReference => "Variable", DefaultLiteral => "Default", Literal => "Literal", _ => "Expression" };
        return new(kind, new(doc.Uri, doc.Parse.Source.Range(node.StartOffset, node.FragmentLength)),
            SqlSymbolDisplay.OneLine(doc.Parse.Source.Text.Substring(node.StartOffset, length), 256), length < node.FragmentLength);
    }
    private string? Fact(SqlValueFactKind kind, string role, TextRange range, string? target, SqlValueExpression? expression,
        ImmutableArray<SqlValueInput> inputs, bool supported, string detail)
    {
        token.ThrowIfCancellationRequested();
        if (facts.Count >= 256 || !Reserve()) { truncated = true; return null; }
        var id = "value-" + facts.Count + "-" + document.Parse.Source.OffsetAt(range.Start);
        facts.Add(new(id, kind, role, new(document.Uri, range), target, expression, inputs, flow.ContextAt(range.Start),
            supported && inputs.All(i => i.Status == ResolutionStatus.Resolved) ? ResolutionStatus.Resolved : ResolutionStatus.Unsupported, detail));
        return id;
    }
    private string? Observe(SqlValueFactKind kind, string role, TSqlFragment node, string? target, TSqlFragment? expression,
        string detail, bool supported = true, VariableReference? previous = null)
    {
        token.ThrowIfCancellationRequested();
        if (facts.Count >= 256 || used >= budget) { truncated = true; return null; }
        return Fact(kind, role, Range(node), target, Expression(document, expression), Inputs(expression, previous), supported, detail);
    }
    private void Set(string? variable, string? fact, bool supported)
    {
        if (variable == null) return;
        var recorded = facts.LastOrDefault(f => f.Id == fact);
        state[variable] = new(fact == null ? [] : [fact], !unstructured && supported && recorded != null &&
            recorded.Status == ResolutionStatus.Resolved && recorded.Inputs.All(i => i.SourcesComplete));
    }
    private void Unknown() => state = state.ToDictionary(p => p.Key, _ => new Origins([], false), StringComparer.Ordinal);
    private void Merge(Dictionary<string, Origins> first, Dictionary<string, Origins> second)
    {
        state = new(StringComparer.Ordinal);
        foreach (var key in first.Keys.Concat(second.Keys).Distinct())
        {
            var a = first.GetValueOrDefault(key, new([], false)); var b = second.GetValueOrDefault(key, new([], false));
            var union = a.Facts.Concat(b.Facts).Distinct().ToArray();
            if (union.Length > 8) { truncated = true; Issue("value-source-limit", "More than eight possible definitions; source set is incomplete.", body); }
            state[key] = new(union.Take(8).ToImmutableArray(), a.Complete && b.Complete && union.Length <= 8 && !unstructured);
        }
    }

    public override void ExplicitVisit(DeclareVariableElement node)
    {
        var target = Variable(Range(node.VariableName), true);
        var supported = node.Value == null || node.Value is VariableReference or Literal;
        var fact = Observe(SqlValueFactKind.Declaration, "Declare", node, target?.Id, node.Value, "Declaration/initializer source; expressions are not evaluated.", supported && target != null);
        if (!supported || target == null) Issue("value-assignment-unsupported", "Declaration binding/expression is partial; no value is inferred.", node);
        node.Value?.Accept(this);
        Set(target?.Id, fact, supported);
    }
    public override void ExplicitVisit(SetVariableStatement node)
    {
        var target = Variable(Range(node.Variable));
        var supported = node.AssignmentKind == AssignmentKind.Equals && node.Expression is VariableReference or Literal && !node.FunctionCallExists;
        var fact = Observe(SqlValueFactKind.Assignment, "Set", node, target?.Id, node.Expression,
            "Assignment definition replaces the previous source on this supported sequential path; no runtime execution or value is proven.", supported && target != null,
            node.AssignmentKind != AssignmentKind.Equals ? node.Variable : null);
        if (!supported || target == null) Issue("value-assignment-unsupported", "Assignment is opaque, compound, cursor/UDT-based or unbound; target provenance is uncertain.", node);
        node.Expression?.Accept(this);
        Set(target?.Id, fact, supported);
    }
    public override void ExplicitVisit(QuerySpecification node)
    {
        var before = new Dictionary<string, Origins>(state, StringComparer.Ordinal);
        // SQL may read and assign these variables across multiple rows in unspecified order.
        // Neither projection order nor WHERE syntax establishes a pre-query definition for such reads.
        foreach (var assignment in node.SelectElements.OfType<SelectSetVariable>())
            if (Variable(Range(assignment.Variable)) is { } variable) state[variable.Id] = new([], false);
        selectWrites.Push([]);
        node.AcceptChildren(this);
        foreach (var (variable, fact) in selectWrites.Pop())
        {
            var old = before.GetValueOrDefault(variable, new([], false));
            var union = old.Facts.Concat(fact == null ? [] : new[] { fact }).Distinct().ToArray();
            if (union.Length > 8) { truncated = true; Issue("value-source-limit", "SELECT retained more than eight possible sources; source set is incomplete.", node); }
            state[variable] = new(union.Take(8).ToImmutableArray(), false);
        }
    }
    public override void ExplicitVisit(SelectSetVariable node)
    {
        var target = Variable(Range(node.Variable));
        var fact = Observe(SqlValueFactKind.SelectAssignment, "Select", node, target?.Id, node.Expression,
            "SELECT assignment depends on row count/order and may retain a prior value. No latest-row or evaluation-order inference.", false);
        if (target != null && selectWrites.TryPeek(out var pending)) pending.Add((target.Id, fact));
        else { Set(target?.Id, fact, false); }
        Issue("value-select-assignment", "SELECT assignment has unknown row/order and retention semantics.", node);
        node.Expression?.Accept(this);
    }
    private void Predicate(TSqlFragment expression, string role)
    {
        Observe(SqlValueFactKind.Predicate, role, expression, null, expression, "Bound local/parameter reads in this condition; columns, truth and row contents are not inferred.");
        expression.Accept(this);
    }
    public override void ExplicitVisit(WhereClause node) { if (node.SearchCondition != null) Predicate(node.SearchCondition, "Where"); }
    public override void ExplicitVisit(HavingClause node) => Predicate(node.SearchCondition, "Having");
    public override void ExplicitVisit(QualifiedJoin node)
    {
        node.FirstTableReference.Accept(this); node.SecondTableReference.Accept(this); Predicate(node.SearchCondition, "Join");
    }
    public override void ExplicitVisit(IfStatement node)
    {
        Predicate(node.Predicate, "If"); var before = new Dictionary<string, Origins>(state, StringComparer.Ordinal);
        node.ThenStatement.Accept(this); var then = state;
        state = new(before, StringComparer.Ordinal); node.ElseStatement?.Accept(this); Merge(then, state);
    }
    public override void ExplicitVisit(WhileStatement node)
    {
        Unknown(); Issue("value-loop", "Loop backedges/iterations are not solved; pre-loop definitions cannot be assumed for body reads.", node);
        Predicate(node.Predicate, "While"); node.Statement.Accept(this); Unknown();
    }
    public override void ExplicitVisit(TryCatchStatement node)
    {
        node.TryStatements.Accept(this); Unknown(); node.CatchStatements.Accept(this); Unknown();
        Issue("value-error-flow", "CATCH can be entered from multiple failure points; ordered source certainty is invalidated.", node);
    }
    public override void ExplicitVisit(SearchedWhenClause node) { Predicate(node.WhenExpression, "CaseWhen"); node.ThenExpression.Accept(this); }
    public override void ExplicitVisit(SimpleWhenClause node) { Predicate(node.WhenExpression, "CaseMatch"); node.ThenExpression.Accept(this); }
    public override void ExplicitVisit(SimpleCaseExpression node)
    {
        Predicate(node.InputExpression, "CaseInput"); foreach (var clause in node.WhenClauses) clause.Accept(this); node.ElseExpression?.Accept(this);
    }
    public override void ExplicitVisit(ReturnStatement node)
    {
        Observe(SqlValueFactKind.Return, "Return", node, null, node.Expression, "RETURN expression observation; not joined to caller status or used for reachability.");
        node.Expression?.Accept(this);
    }
    public override void ExplicitVisit(FetchCursorStatement node)
    {
        Unknown(); Issue("value-opaque-write", "FETCH/cursor values are not traced; local provenance is invalidated.", node);
    }
    public override void ExplicitVisit(ReceiveStatement node)
    {
        node.AcceptChildren(this); Unknown(); Issue("value-opaque-write", "RECEIVE values are not traced; local provenance is invalidated.", node);
    }
    private sealed class JumpScanner(CancellationToken token) : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }
        public override void Visit(TSqlFragment node) => token.ThrowIfCancellationRequested();
        public override void ExplicitVisit(GoToStatement node) => Found = true;
    }
}
