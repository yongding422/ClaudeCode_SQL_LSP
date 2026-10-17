using System.Collections.Immutable;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

public enum SqlFlowKind
{
    If, Then, Else, While, LoopBody, TryCatch, Try, Catch,
    SearchedCase, SimpleCase, CaseWhen, CaseResult, CaseElse,
    Return, Break, Continue, Goto, Label, Throw, RaiseError,
    BeginTransaction, CommitTransaction, RollbackTransaction, SaveTransaction
}

public sealed record SqlFlowExpression(SqlAnalysisLocation Location, string Preview, bool Truncated);
public sealed record SqlThrowObservation(string Kind, SqlFlowExpression? ErrorNumber, SqlFlowExpression? Message, SqlFlowExpression? State);
public sealed record SqlFlowNode(string Id, SqlFlowKind Kind, string? ParentId, SqlAnalysisLocation Location,
    SqlFlowExpression? Predicate, SqlFlowExpression? Subject, string Detail)
{
    public SqlThrowObservation? Throw { get; init; }
}
public sealed record SqlControlFlowSummary(bool BodyAvailable, ImmutableArray<SqlFlowNode> Nodes, bool Truncated,
    ImmutableArray<SqlCoverageIssue> Issues, ImmutableArray<string> Limitations)
{
    // Membership describes syntax enclosure, never guaranteed execution or a complete path predicate.
    public ImmutableArray<string> ContextAt(TextPosition position) => Nodes
        .Where(n => SqlNavigationIndex.Contains(n.Location.Range, position)).Select(n => n.Id).ToImmutableArray();
}

internal sealed class SqlControlFlow(DocumentSnapshot document, CancellationToken token, int maxNodes) : TSqlFragmentVisitor
{
    private readonly List<SqlFlowNode> nodes = [];
    private readonly List<SqlCoverageIssue> issues = [];
    private readonly Stack<string?> parents = new();
    private bool truncated;
    private static readonly ImmutableArray<string> Limitations =
    [
        "Structural source map only. FlowContext IDs identify enclosing constructs, not complete execution conditions. Empty context does not mean unconditional execution.",
        "Sequential reachability, dominance, earlier exits, GOTO targets, predicate truth and loop iteration counts are not computed. Statements after RETURN/THROW remain syntax observations.",
        "THEN requires an IF predicate to be TRUE; ELSE covers FALSE or UNKNOWN. No predicate is rewritten or evaluated.",
        "CASE arms describe expression structure, not guaranteed short-circuit evaluation. A CASE inside an UPDATE value does not guard the whole UPDATE.",
        "TRY/CATCH regions do not prove an error is caught. RAISERROR behavior depends on severity/options/runtime context; THROW observations do not prove a subsequent path.",
        "Transaction/savepoint statements and names are observations only. State, balance, isolation, implicit transactions, XACT_ABORT and actual commit/rollback outcomes are not inferred.",
        "Contexts are local to each source module. Caller predicates are not propagated into callees; row filters, argument/value propagation and column lineage require separate analysis."
    ];

    public static SqlControlFlowSummary Extract(TSqlFragment? body, DocumentSnapshot document, int maxNodes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (body == null) return new(false, [], false, [], Limitations);
        var visitor = new SqlControlFlow(document, token, Math.Clamp(maxNodes, 0, 256));
        body.Accept(visitor);
        if (visitor.truncated) visitor.issues.Add(new("control-flow-limit", "Control map is incomplete: node budget or source-range limit. Missing contexts must not be interpreted as unconditional execution.", null));
        return new(true, visitor.nodes.ToImmutableArray(), visitor.truncated, visitor.issues.ToImmutableArray(), Limitations);
    }

    public override void Visit(TSqlFragment node) => token.ThrowIfCancellationRequested();

    public override void ExplicitVisit(StatementList node)
    {
        for (var i = 0; i < node.Statements.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var statement = node.Statements[i];
            if (i > 0 && node.Statements[i - 1] is ThrowStatement && statement is ReturnStatement && issues.Count < 256)
                issues.Add(new("return-after-throw", "RETURN immediately follows THROW in the same statement list. Ordinary flow from that THROW does not deliver this return value; alternative entry/GOTO and runtime error routing are not solved.", Location(statement)));
            statement.Accept(this);
        }
    }

    public override void ExplicitVisit(IfStatement node) => Within(node, SqlFlowKind.If,
        "IF construct; predicate evaluation and branches are separate source regions.", () =>
        {
            node.Predicate.Accept(this);
            Within(node.ThenStatement, SqlFlowKind.Then, "Branch selected when the IF predicate is TRUE.", () => node.ThenStatement.Accept(this));
            if (node.ElseStatement != null) Within(node.ElseStatement, SqlFlowKind.Else,
                "Branch selected when the IF predicate is FALSE or UNKNOWN.", () => node.ElseStatement.Accept(this));
        }, node.Predicate);

    public override void ExplicitVisit(WhileStatement node) => Within(node, SqlFlowKind.While,
        "Loop construct; predicate evaluation is separate from the loop body.", () =>
        {
            node.Predicate.Accept(this);
            Within(node.Statement, SqlFlowKind.LoopBody, "Body for an iteration whose WHILE predicate is TRUE; count/reachability unknown.", () => node.Statement.Accept(this));
        }, node.Predicate);

    public override void ExplicitVisit(TryCatchStatement node) => Within(node, SqlFlowKind.TryCatch,
        "Associated TRY/CATCH regions; no exhaustive exception routing is inferred.", () =>
        {
            Within(node.TryStatements, SqlFlowKind.Try, "TRY region; statements can fail or transfer control.", () => node.TryStatements.Accept(this));
            Within(node.CatchStatements, SqlFlowKind.Catch, "CATCH region entered for qualifying errors; not all errors transfer here.", () => node.CatchStatements.Accept(this));
        });

    public override void ExplicitVisit(SearchedCaseExpression node) => Within(node, SqlFlowKind.SearchedCase,
        "Searched CASE expression; value arms are not statement execution guards.", () =>
        {
            foreach (var clause in node.WhenClauses) CaseArm(clause, clause.WhenExpression, clause.ThenExpression);
            CaseElse(node.ElseExpression);
        });

    public override void ExplicitVisit(SimpleCaseExpression node) => Within(node, SqlFlowKind.SimpleCase,
        "Simple CASE expression; compare WHEN values with this input using SQL semantics.", () =>
        {
            node.InputExpression.Accept(this);
            foreach (var clause in node.WhenClauses) CaseArm(clause, clause.WhenExpression, clause.ThenExpression);
            CaseElse(node.ElseExpression);
        }, subject: node.InputExpression);

    private void CaseArm(TSqlFragment clause, TSqlFragment predicate, ScalarExpression result) => Within(clause, SqlFlowKind.CaseWhen,
        "WHEN arm; predicate/match expression and result occupy separate source regions. Evaluation order is not proven.", () =>
        {
            predicate.Accept(this);
            Within(result, SqlFlowKind.CaseResult, "THEN value expression for this CASE arm; lexical membership is not a short-circuit guarantee.", () => result.Accept(this));
        }, predicate);

    private void CaseElse(ScalarExpression? expression)
    {
        if (expression != null) Within(expression, SqlFlowKind.CaseElse,
            "ELSE value expression; the CASE has no selected WHEN match. Runtime evaluation is not proven.", () => expression.Accept(this));
    }

    public override void ExplicitVisit(ReturnStatement node) => Within(node, SqlFlowKind.Return,
        "RETURN observed; sequential reachability and returned values are not inferred.", () => node.Expression?.Accept(this));
    public override void ExplicitVisit(BreakStatement node) => Event(node, SqlFlowKind.Break, "BREAK observed; loop-exit target/reachability is not computed.");
    public override void ExplicitVisit(ContinueStatement node) => Event(node, SqlFlowKind.Continue, "CONTINUE observed; loop continuation/reachability is not computed.");
    public override void ExplicitVisit(GoToStatement node)
    {
        Event(node, SqlFlowKind.Goto, "GOTO observed; label binding and unstructured path analysis are not computed.", node.LabelName);
        if (issues.Count < 256) issues.Add(new("unstructured-control-flow", "GOTO can bypass lexical regions; do not infer execution guards from enclosure alone.", Location(node)));
    }
    public override void ExplicitVisit(LabelStatement node) => Event(node, SqlFlowKind.Label, "Label observed; incoming jumps are not resolved.", node);
    public override void ExplicitVisit(ThrowStatement node) => Within(node, SqlFlowKind.Throw,
        node.ErrorNumber == null ? "Bare THROW rethrows the caught exception; its severity/state are inherited, not inferred here."
            : "New THROW: named errorNumber/message/state. The third argument is state, never severity; a new THROW has severity 16. Runtime argument validity/error routing are not evaluated.",
        () => node.AcceptChildren(this), thrown: new(node.ErrorNumber == null ? "Rethrow" : "NewError", Expression(node.ErrorNumber), Expression(node.Message), Expression(node.State)));
    public override void ExplicitVisit(RaiseErrorStatement node) => Within(node, SqlFlowKind.RaiseError,
        "RAISERROR observed; severity, options and runtime context govern whether control transfers. Message text is not exported.",
        () => node.AcceptChildren(this));
    public override void ExplicitVisit(BeginTransactionStatement node) => Event(node, SqlFlowKind.BeginTransaction,
        node.Distributed ? "BEGIN DISTRIBUTED TRANSACTION observed; state/outcome unknown." : "BEGIN TRANSACTION observed; state/outcome unknown.", node.Name);
    public override void ExplicitVisit(CommitTransactionStatement node) => Event(node, SqlFlowKind.CommitTransaction,
        "COMMIT observed; successful commit, nesting balance and durability are not proven.", node.Name);
    public override void ExplicitVisit(RollbackTransactionStatement node) => Event(node, SqlFlowKind.RollbackTransaction,
        "ROLLBACK observed; a name can denote a transaction/savepoint or a runtime variable. Outcome is unknown.", node.Name);
    public override void ExplicitVisit(SaveTransactionStatement node) => Event(node, SqlFlowKind.SaveTransaction,
        "SAVE TRANSACTION observed; savepoint validity and later use are not inferred.", node.Name);

    private void Event(TSqlFragment node, SqlFlowKind kind, string detail, TSqlFragment? subject = null) =>
        Within(node, kind, detail, () => node.AcceptChildren(this), subject: subject);

    private void Within(TSqlFragment node, SqlFlowKind kind, string detail, Action children,
        TSqlFragment? predicate = null, TSqlFragment? subject = null, SqlThrowObservation? thrown = null)
    {
        token.ThrowIfCancellationRequested();
        string? id = null;
        if (nodes.Count < maxNodes && Location(node) is { } location)
        {
            id = "flow-" + node.StartOffset + "-" + kind;
            nodes.Add(new(id, kind, parents.TryPeek(out var parent) ? parent : null, location, Expression(predicate), Expression(subject), detail) { Throw = thrown });
        }
        else truncated = true;
        parents.Push(id);
        try { children(); }
        finally { parents.Pop(); }
    }

    private SqlAnalysisLocation? Location(TSqlFragment node) => node.StartOffset >= 0 && node.FragmentLength > 0 &&
        node.StartOffset + node.FragmentLength <= document.Parse.Source.Text.Length
            ? new(document.Uri, document.Parse.Source.Range(node.StartOffset, node.FragmentLength)) : null;

    private SqlFlowExpression? Expression(TSqlFragment? node)
    {
        if (node == null || Location(node) is not { } location) return null;
        var length = Math.Min(node.FragmentLength, 256);
        if (char.IsHighSurrogate(document.Parse.Source.Text[node.StartOffset + length - 1])) length--;
        return new(location, SqlSymbolDisplay.OneLine(document.Parse.Source.Text.Substring(node.StartOffset, length), 256), length < node.FragmentLength);
    }
}
