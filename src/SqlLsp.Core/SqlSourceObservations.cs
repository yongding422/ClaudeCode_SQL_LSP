using System.Collections.Immutable;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

public sealed record SqlSourceObservation(string Kind, string? Name, SqlAnalysisLocation Location, string Detail)
{
    public bool? BeforeFirstExplicitTransaction { get; init; }
    public string? JsonPath { get; init; }
    public bool? AsJson { get; init; }
    public string? JsonKeyComparison { get; init; }
    public string? Operation { get; init; }
    public SqlFlowExpression? Expression { get; init; }
    public ImmutableArray<SqlObservationInput> Inputs { get; init; } = [];
    public bool? InputsComplete { get; init; }
    public SqlTransactionSyntax? Transactions { get; init; }
    public ImmutableArray<SqlDeclaredSourceOption> Options { get; init; } = [];
    public SqlUpdateFromSyntax? UpdateFrom { get; init; }

    public IEnumerable<SqlFlowExpression> Expressions()
    {
        if (Expression != null) yield return Expression;
        if (UpdateFrom == null) yield break;
        yield return UpdateFrom.Target;
        yield return UpdateFrom.From;
        if (UpdateFrom.Where != null) yield return UpdateFrom.Where;
        foreach (var assignment in UpdateFrom.Assignments) yield return assignment;
    }
}

public sealed record SqlObservationInput(string Name, string Kind, SqlAnalysisLocation Location, bool VerifiedLocal);
public sealed record SqlTransactionSyntax(int ExplicitBegins, int Commits, int Rollbacks, int AtomicBlocks, bool SyntaxComplete);
public sealed record SqlDeclaredSourceOption(string Name, SqlFlowExpression Source);
public sealed record SqlUpdateFromSyntax(SqlFlowExpression Target, SqlFlowExpression From, SqlFlowExpression? Where,
    ImmutableArray<SqlFlowExpression> Assignments, bool AssignmentsComplete, bool SyntaxComplete);

internal sealed class SqlSourceObservations(DocumentSnapshot document, int budget, int? firstTransactionOffset, CancellationToken token) : TSqlFragmentVisitor
{
    private readonly List<SqlSourceObservation> facts = [];
    private bool truncated;
    public static (ImmutableArray<SqlSourceObservation> Facts, bool Truncated) Extract(TSqlFragment? body, DocumentSnapshot document,
        int budget, SqlControlFlowSummary flow, CancellationToken token)
    {
        if (body == null) return ([], false);
        var first = flow.Nodes.Where(n => n.Kind == SqlFlowKind.BeginTransaction).Select(n => (int?)document.Parse.Source.OffsetAt(n.Location.Range.Start)).Min();
        var limit = Math.Clamp(budget, 0, 64);
        var execution = SqlExecutionEvidence.Extract(body, document, limit / 2, token);
        var visitor = new SqlSourceObservations(document, limit - execution.Facts.Length, first, token);
        body.Accept(visitor);
        return (execution.Facts.AddRange(visitor.facts), visitor.truncated || execution.Truncated);
    }
    public override void Visit(TSqlFragment node) => token.ThrowIfCancellationRequested();
    private void Add(TSqlFragment node, string kind, string? name, string detail, bool? before = null,
        string? jsonPath = null, bool? asJson = null, string? jsonKeyComparison = null)
    {
        token.ThrowIfCancellationRequested();
        if (facts.Count >= budget) { truncated = true; return; }
        if (node.StartOffset < 0 || node.FragmentLength <= 0 || node.StartOffset + node.FragmentLength > document.Parse.Source.Text.Length) { truncated = true; return; }
        facts.Add(new(kind, name, new(document.Uri, document.Parse.Source.Range(node.StartOffset, node.FragmentLength)), detail)
        { BeforeFirstExplicitTransaction = before, JsonPath = jsonPath, AsJson = asJson, JsonKeyComparison = jsonKeyComparison });
    }
    public override void ExplicitVisit(StringLiteral node)
    {
        // Classify literal text only, without evaluating variables or installing/executing nested SQL.
        var prefix = node.Value.AsSpan(0, Math.Min(node.Value.Length, 256)).TrimStart();
        if (!ConsumeKeyword(ref prefix, "CREATE")) return;
        if (ConsumeKeyword(ref prefix, "OR") && !ConsumeKeyword(ref prefix, "ALTER")) return;
        var length = 0;
        while (length < prefix.Length && IsWordCharacter(prefix[length])) length++;
        var kind = prefix[..length];
        if (kind.Equals("PROC", StringComparison.OrdinalIgnoreCase) || kind.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase) ||
            kind.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) || kind.Equals("VIEW", StringComparison.OrdinalIgnoreCase))
            Add(node, "EmbeddedModuleDefinitionText", kind.ToString().ToUpperInvariant(),
            "String literal begins module-definition DDL. Installing a routine is separate from invoking its body. Runtime execution of this text is not proven; embedded references are not direct call edges of the containing routine.");
    }

    private static bool ConsumeKeyword(ref ReadOnlySpan<char> source, string keyword)
    {
        if (!source.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) || source.Length == keyword.Length || !char.IsWhiteSpace(source[keyword.Length])) return false;
        source = source[keyword.Length..].TrimStart();
        return true;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) ||
        char.GetUnicodeCategory(value) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.ConnectorPunctuation;
    public override void ExplicitVisit(NextValueForExpression node)
    {
        Add(node, "SequenceAllocation", SqlSymbolDisplay.Name(QualifiedSqlName.From(node.SequenceName)),
            "NEXT VALUE FOR allocates sequence state independently of transaction rollback. Source order relative to BEGIN TRAN is syntax only: ambient transactions, predicate truth, row count, values and gaps are not determined.",
            firstTransactionOffset == null ? null : node.StartOffset < firstTransactionOffset);
        node.AcceptChildren(this);
    }
    public override void ExplicitVisit(SelectSetVariable node)
    {
        Add(node, "SelectAssignment", node.Variable.Name,
            "Zero-row SELECT assignment retains the previous value. Check the declaration and earlier writes: a fresh scalar with no initializer starts NULL, not a previous invocation's ID. A scalar subquery's no-value NULL case differs. Row count/order and distinct loop progress are not proved.");
        node.AcceptChildren(this);
    }
    public override void ExplicitVisit(BeginEndAtomicBlockStatement node)
    {
        Add(node, "AtomicTransaction", null,
            "BEGIN ATOMIC is declared here; read its stated isolation option, do not assume SNAPSHOT is the only supported option. An existing session transaction can remain active; block rollback/commit and caught versus uncaught errors do not prove the entire ambient transaction ends.");
        node.AcceptChildren(this);
    }
    public override void ExplicitVisit(MethodSpecifier node)
    {
        Add(node, "ExternalImplementation", string.Join('.', node.AssemblyName.Value, node.ClassName.Value, node.MethodName.Value),
            "External implementation is not a known-empty T-SQL body. Empty extracted SQL calls/accesses cannot establish HTTP headers, network activity or table effects of the external method.");
    }
    public override void ExplicitVisit(OpenJsonTableReference node)
    {
        foreach (var item in node.SchemaDeclarationItems)
            Add(item, "OpenJsonColumn", item.ColumnDefinition.ColumnIdentifier.Value,
                "OPENJSON WITH column/path evidence. JSON key/path matching is case-sensitive BIN2 independently of SQL identifier collation. Missing lax properties can yield NULL; strict paths can fail. AS JSON retains fragments. Input validity, row cardinality, column writes and runtime values are not evaluated.",
                jsonPath: (item.Mapping as StringLiteral)?.Value, asJson: item.AsJson, jsonKeyComparison: "BIN2-case-sensitive");
        node.AcceptChildren(this);
    }
}
