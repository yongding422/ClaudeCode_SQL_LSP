using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

public enum SqlAccessOperation { Read, Insert, Update, Delete, Create, Drop, Truncate }
public sealed record SqlAnalysisLocation(Uri Uri, TextRange Range);
public sealed record SqlCoverageIssue(string Code, string Message, SqlAnalysisLocation? Location);
public sealed record SqlRelationAccess(SqlAccessOperation Operation, string Name, SqlAnalysisLocation Location,
    ResolutionStatus Status, SqlNavigationTarget? Target, SqlLocalDefinition? Local, string Explanation)
{
    public ImmutableArray<string> FlowContext { get; init; } = [];
}
public sealed record SqlAnalyzedCall(string Name, SqlAnalysisLocation Location, ResolutionStatus Status,
    SqlNavigationTarget? Target, string Explanation)
{
    public ImmutableArray<string> FlowContext { get; init; } = [];
}
public sealed record SqlAnalysisParameter(string Name, string? Type, SqlAnalysisLocation Location);
public sealed record SqlModuleAnalysis(string Id, SqlObjectIdentity Identity, SqlSourceKind SourceKind, string Revision,
    DateTimeOffset? CapturedAt, SqlAnalysisLocation Definition, string? Signature, bool BodyAvailable,
    ImmutableArray<SqlAnalysisParameter> Parameters, ImmutableArray<SqlRelationAccess> Accesses,
    ImmutableArray<SqlAnalyzedCall> Calls, ImmutableArray<SqlCoverageIssue> Issues, bool Truncated)
{
    public SqlControlFlowSummary? ControlFlow { get; init; }
    public SqlValueFlowSummary? ValueFlow { get; init; }
}
public sealed record SqlProcedureReport(ResolutionStatus Status, string Explanation,
    ImmutableArray<SqlNavigationTarget> Candidates, ImmutableArray<SqlModuleAnalysis> Modules,
    ImmutableArray<SqlCoverageIssue> ExpansionIssues, ImmutableArray<string> Limitations,
    int MaxDepth, int MaxModules);

public sealed partial class SqlNavigationIndex
{
    private const int MaxAnalysisFacts = 1000;
    private static readonly ImmutableArray<string> AnalysisLimitations =
    [
        "Static possible effects, not observed execution. Direct effects belong to each module; expanded module effects are transitive possibilities.",
        "Dynamic SQL, triggers, synonyms, remote rowsets, runtime data and implicit engine effects are not fully analyzed.",
        "Control scopes and bounded value dependencies are source observations, not runtime values or complete execution conditions. Sequential reachability, transaction correctness, SQL conversions, function-argument binding and full column lineage are not modeled.",
        "CTE reads retain local identity and underlying query reads. Writes through CTEs/views require further binding and are explicitly partial.",
        "Results cover indexed sources only. File exclusions, size limits and metadata permissions can hide dependencies. Empty results do not prove no effects."
    ];

    public SqlProcedureReport AnalyzeProcedure(Uri uri, TextPosition position, CancellationToken token,
        int maxDepth = 2, int maxModules = 20, string? expectedRevision = null)
    {
        token.ThrowIfCancellationRequested();
        if (maxDepth is < 0 or > 4 || maxModules is < 1 or > 40) throw new ArgumentException("Analysis limits: depth 0..4, modules 1..40.");
        SqlProcedureReport Empty(ResolutionStatus status, string message, ImmutableArray<SqlNavigationTarget> candidates = default) =>
            new(status, message, candidates.IsDefault ? [] : candidates, [], SourceParseIssues(token).Where(i => i.Location?.Uri == uri).ToImmutableArray(),
                AnalysisLimitations, maxDepth, maxModules);
        var document = documents.FirstOrDefault(d => d.Uri == uri);
        if (document == null) return Empty(ResolutionStatus.Unresolved, "Document is not indexed or is excluded/unavailable.");
        try { document.Parse.Source.OffsetAt(position); }
        catch (ArgumentException) { return Empty(ResolutionStatus.Unsupported, "Invalid UTF-16 source position."); }
        var owner = Symbols.Where(s => s.Target.Uri == uri && IsAnalyzable(s) && Contains(s.Declaration.Range, position))
            .OrderBy(s => SpanLength(document, s.Declaration.Range)).FirstOrDefault();
        if (owner == null)
        {
            var resolution = Definition(uri, position, token);
            if (resolution.Status != ResolutionStatus.Resolved) return Empty(resolution.Status, resolution.Explanation, resolution.Candidates);
            owner = Symbols.FirstOrDefault(s => SameAnchor(s.Target, resolution.Target!) && IsAnalyzable(s));
        }
        if (owner == null) return Empty(ResolutionStatus.Unsupported, "Select a procedure, function or view definition/body.");
        var rules = Rules(owner.Target.Uri);
        var duplicates = Symbols.Where(s => s.Target.SourceKind == owner.Target.SourceKind &&
            s.Identity.SourceContextId == owner.Identity.SourceContextId && s.Identity.Kind == owner.Identity.Kind &&
            rules.Comparer.Equals(s.Identity.Server, owner.Identity.Server) && rules.Comparer.Equals(s.Identity.Database, owner.Identity.Database) &&
            rules.Comparer.Equals(s.Identity.Schema, owner.Identity.Schema) && rules.Comparer.Equals(s.Identity.Name, owner.Identity.Name)).ToArray();
        if (duplicates.Length != 1) return Empty(ResolutionStatus.Ambiguous, "Duplicate module identity; no module selected.", duplicates.Select(s => s.Target).ToImmutableArray());
        var revision = AnalysisRevision(documents.First(d => d.Uri == owner.Target.Uri));
        if (expectedRevision != null && expectedRevision != revision)
            return Empty(ResolutionStatus.Unresolved, "Source revision changed; locate and analyze the current source again.");

        var modules = ImmutableArray.CreateBuilder<SqlModuleAnalysis>();
        var expansion = ImmutableArray.CreateBuilder<SqlCoverageIssue>();
        var queue = new Queue<(IndexedSqlSymbol Symbol, int Depth)>();
        var seen = new HashSet<(Uri, TextRange)> { (owner.Target.Uri, owner.Target.Range) };
        var remainingFacts = 2000;
        queue.Enqueue((owner, 0));
        while (queue.TryDequeue(out var entry))
        {
            token.ThrowIfCancellationRequested();
            if (remainingFacts <= 0)
            {
                expansion.Add(new("report-limit", "Report fact budget exhausted; additional modules were not expanded.", new(entry.Symbol.Target.Uri, entry.Symbol.Target.Range)));
                break;
            }
            var analysis = AnalyzeModule(entry.Symbol, token, Math.Min(MaxAnalysisFacts, remainingFacts));
            modules.Add(analysis);
            remainingFacts -= analysis.Accesses.Length + analysis.Calls.Length + analysis.Parameters.Length + (analysis.ControlFlow?.Nodes.Length ?? 0) + (analysis.ValueFlow?.FactCount ?? 0);
            var dependencies = analysis.Calls.Where(c => c.Status == ResolutionStatus.Resolved && c.Target != null).Select(c => (c.Target!, c.Location))
                .Concat(analysis.Accesses.Where(a => a.Operation == SqlAccessOperation.Read && a.Status == ResolutionStatus.Resolved && a.Target?.Identity?.Kind == SqlObjectKind.View)
                    .Select(a => (a.Target!, a.Location)));
            foreach (var (target, location) in dependencies)
            {
                token.ThrowIfCancellationRequested();
                if (seen.Contains((target.Uri, target.Range))) continue;
                var symbol = Symbols.FirstOrDefault(s => SameAnchor(s.Target, target) && IsAnalyzable(s));
                if (symbol == null) continue;
                if (entry.Depth >= maxDepth || seen.Count >= maxModules)
                {
                    if (expansion.Count < MaxAnalysisFacts) expansion.Add(new("expansion-limit", "Dependency was not expanded: depth/module limit.", location));
                    continue;
                }
                seen.Add((target.Uri, target.Range));
                queue.Enqueue((symbol, entry.Depth + 1));
            }
        }
        return new(ResolutionStatus.Resolved, "Verified selected source; inspect issues and limitations before interpreting coverage.",
            [owner.Target], modules.ToImmutable(), expansion.ToImmutable(), AnalysisLimitations, maxDepth, maxModules);
    }

    private static bool IsAnalyzable(IndexedSqlSymbol symbol) => symbol.Identity.Kind is SqlObjectKind.Procedure or SqlObjectKind.Function or SqlObjectKind.View;
    public static string AnalysisRevision(DocumentSnapshot document) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.Parse.Source.Text))) + ":" + document.Version + ":" + document.SourceKind;

    public ImmutableArray<SqlCoverageIssue> SourceParseIssues(CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<SqlCoverageIssue>();
        foreach (var document in documents)
            foreach (var diagnostic in document.Parse.Diagnostics)
            {
                token.ThrowIfCancellationRequested();
                if (result.Count >= MaxAnalysisFacts) return result.ToImmutable();
                result.Add(new("parse-error", diagnostic.Message, new(document.Uri, diagnostic.Range)));
            }
        return result.ToImmutable();
    }

    private SqlModuleAnalysis AnalyzeModule(IndexedSqlSymbol symbol, CancellationToken token, int maxFacts)
    {
        var document = documents.First(d => d.Uri == symbol.Target.Uri);
        var finder = new AnalysisBodyFinder(document.Parse.Source, symbol.Declaration.Range);
        document.Parse.Ast.Accept(finder);
        var issues = new List<SqlCoverageIssue>();
        var accesses = new List<SqlRelationAccess>();
        var calls = new List<SqlAnalyzedCall>();
        var truncated = false;
        void Issue(string code, string message, TextRange? range = null)
        {
            if (issues.Count < MaxAnalysisFacts) issues.Add(new(code, message, new(document.Uri, range ?? symbol.Declaration.Range)));
            else truncated = true;
        }
        if (finder.Body == null) Issue("body-unavailable", "Original module body is unavailable or unparseable; metadata is not a substitute for source.");
        foreach (var diagnostic in document.Parse.Diagnostics) Issue("parse-error", diagnostic.Message, diagnostic.Range);
        if (finder.Body != null)
        {
            var visitor = new SqlAccessExtractor(document, symbol, token);
            finder.Body.Accept(visitor);
            foreach (var item in visitor.Accesses.Take(maxFacts))
            {
                token.ThrowIfCancellationRequested();
                var reference = item.Reference;
                var resolution = Resolve(document, reference);
                var local = reference.LocalTarget;
                if (item.Operation != SqlAccessOperation.Read && local?.Kind == SqlObjectKind.Cte)
                    resolution = new(ResolutionStatus.Unsupported, [], "Write through a CTE requires additional target binding.");
                // Creating a local temp table/table variable has an explicit local anchor, not a catalog identity.
                var access = new SqlRelationAccess(item.Operation, reference.Name == null ? reference.RawName : SqlSymbolDisplay.Name(reference.Name),
                    new(document.Uri, reference.Range), resolution.Status, resolution.Target, local, resolution.Explanation);
                accesses.Add(access);
                if (resolution.Status != ResolutionStatus.Resolved) Issue("relation-" + resolution.Status.ToString().ToLowerInvariant(), resolution.Explanation, reference.Range);
                if (item.Operation != SqlAccessOperation.Read && resolution.Target?.Identity?.Kind == SqlObjectKind.View)
                    Issue("view-write", "View target is verified; underlying write effects and triggers are not analyzed.", reference.Range);
            }
            foreach (var issue in visitor.Issues.Take(MaxAnalysisFacts)) Issue(issue.Code, issue.Message, issue.Location!.Range);
            if (visitor.Accesses.Count > maxFacts || visitor.Issues.Count > MaxAnalysisFacts) truncated = true;
            foreach (var reference in document.Parse.Semantics.References.Where(r => r.IsInvocation && Contains(symbol.Declaration.Range, r.Range.Start)))
            {
                token.ThrowIfCancellationRequested();
                if (calls.Count + accesses.Count >= maxFacts) { truncated = true; break; }
                var resolution = Resolve(document, reference);
                calls.Add(new(reference.Name == null ? "Unsupported invocation" : SqlSymbolDisplay.Name(reference.Name),
                    new(document.Uri, reference.Range), resolution.Status, resolution.Target, resolution.Explanation));
                if (resolution.Status != ResolutionStatus.Resolved) Issue("call-" + resolution.Status.ToString().ToLowerInvariant(), resolution.Explanation, reference.Range);
                if ((reference.Name?.Schema == null || string.Equals(reference.Name.Schema, "sys", StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(reference.Name?.Name, "sp_executesql", StringComparison.OrdinalIgnoreCase))
                    Issue("dynamic-sql", "sp_executesql body and its effects are not analyzed.", reference.Range);
            }
        }
        var allParameters = document.Parse.Semantics.Locals.Where(l => l.Kind == SqlObjectKind.Parameter && Contains(symbol.Declaration.Range, l.Range.Start)).ToArray();
        var parameters = allParameters.Take(Math.Max(0, maxFacts - calls.Count - accesses.Count))
            .Select(l => new SqlAnalysisParameter(l.Name, l.DeclaredType, new(document.Uri, l.Range))).ToImmutableArray();
        truncated |= allParameters.Length > parameters.Length;
        var flow = SqlControlFlow.Extract(finder.Body, document, Math.Max(0, maxFacts - calls.Count - accesses.Count - parameters.Length), token);
        foreach (var issue in flow.Issues) Issue(issue.Code, issue.Message, issue.Location?.Range);
        truncated |= flow.Truncated;
        var values = SqlValueFlow.Extract(document, finder.Body, flow, execute => ValueSignature(document, execute, token),
            Math.Max(0, maxFacts - calls.Count - accesses.Count - parameters.Length - flow.Nodes.Length), token);
        foreach (var issue in values.Issues) Issue(issue.Code, issue.Message, issue.Location?.Range);
        truncated |= values.Truncated;
        if (truncated) issues.Add(new("fact-limit", "Module report reached its fact limit; omitted facts may include effects.", new(document.Uri, symbol.Declaration.Range)));
        return new(ModuleAnalysisId(symbol),
            symbol.Identity, document.SourceKind, AnalysisRevision(document), symbol.CapturedAt,
            new(document.Uri, symbol.Declaration.Range), symbol.Declaration.Documentation?.Signature,
            finder.Body != null, parameters,
            accesses.Distinct().Select(a => a with { FlowContext = flow.ContextAt(a.Location.Range.Start) }).ToImmutableArray(),
            calls.Select(c => c with { FlowContext = flow.ContextAt(c.Location.Range.Start) }).ToImmutableArray(),
            issues.Distinct().ToImmutableArray(), truncated)
        { ControlFlow = flow, ValueFlow = values };
    }

    private sealed class AnalysisBodyFinder(SourceText source, TextRange range) : TSqlFragmentVisitor
    {
        public TSqlFragment? Body { get; private set; }
        public override void Visit(TSqlFragment node)
        {
            if (node is ProcedureStatementBodyBase or ViewStatementBody && source.Range(node.StartOffset, node.FragmentLength) == range) Body = node;
        }
    }
}

// Statement context classifies effects; the existing semantic extractor remains the sole scope resolver.
internal sealed class SqlAccessExtractor(DocumentSnapshot document, IndexedSqlSymbol symbol, CancellationToken token) : TSqlFragmentVisitor
{
    public List<(SqlAccessOperation Operation, SqlStaticReference Reference)> Accesses { get; } = new();
    public List<SqlCoverageIssue> Issues { get; } = new();
    private readonly Dictionary<int, ImmutableArray<SqlAccessOperation>> targets = new();
    private SourceText Source => document.Parse.Source;
    private TextRange Range(TSqlFragment node) => Source.Range(node.StartOffset, node.FragmentLength);
    private void Mark(TableReference? target, params SqlAccessOperation[] operations)
    {
        if (target != null) targets[target.StartOffset] = operations.ToImmutableArray();
    }
    public override void ExplicitVisit(InsertSpecification node) { Mark(node.Target, SqlAccessOperation.Insert); base.ExplicitVisit(node); }
    public override void ExplicitVisit(UpdateSpecification node) { Mark(node.Target, SqlAccessOperation.Update); base.ExplicitVisit(node); }
    public override void ExplicitVisit(DeleteSpecification node) { Mark(node.Target, SqlAccessOperation.Delete); base.ExplicitVisit(node); }
    public override void ExplicitVisit(MergeSpecification node)
    {
        var operations = node.ActionClauses.Select(c => c.Action switch
        {
            InsertMergeAction => SqlAccessOperation.Insert,
            UpdateMergeAction => SqlAccessOperation.Update,
            DeleteMergeAction => SqlAccessOperation.Delete,
            _ => throw new InvalidOperationException("Unrecognized ScriptDOM MERGE action.")
        }).Distinct().ToArray();
        Mark(node.Target, operations);
        base.ExplicitVisit(node);
    }
    public override void ExplicitVisit(OutputIntoClause node) { Mark(node.IntoTable, SqlAccessOperation.Insert); base.ExplicitVisit(node); }
    public override void ExplicitVisit(NamedTableReference node)
    {
        var reference = document.Parse.Semantics.References.FirstOrDefault(r => r.Range == Range(node.SchemaObject));
        reference ??= Reference(node.SchemaObject);
        foreach (var operation in targets.GetValueOrDefault(node.StartOffset, [SqlAccessOperation.Read])) Accesses.Add((operation, reference));
        base.ExplicitVisit(node);
    }
    public override void ExplicitVisit(VariableTableReference node)
    {
        var reference = document.Parse.Semantics.References.FirstOrDefault(r => r.Range == Range(node.Variable));
        reference ??= new(null, node.Variable.Name, SqlReferenceKind.Variable, Range(node.Variable), symbol.Identity.Database,
            UnsupportedReason: "Table variable binding unavailable.");
        foreach (var operation in targets.GetValueOrDefault(node.StartOffset, [SqlAccessOperation.Read])) Accesses.Add((operation, reference));
        base.ExplicitVisit(node);
    }
    public override void ExplicitVisit(SelectStatement node)
    {
        if (node.Into != null)
        {
            var reference = Reference(node.Into, creatingLocal: true);
            Accesses.Add((SqlAccessOperation.Create, reference));
            Accesses.Add((SqlAccessOperation.Insert, reference));
        }
        base.ExplicitVisit(node);
    }
    public override void ExplicitVisit(CreateTableStatement node)
    {
        Accesses.Add((SqlAccessOperation.Create, Reference(node.SchemaObjectName, creatingLocal: true)));
        base.ExplicitVisit(node);
    }
    public override void ExplicitVisit(DeclareTableVariableStatement node)
    {
        var range = Range(node.Body.VariableName);
        var local = document.Parse.Semantics.Locals.FirstOrDefault(l => l.Range == range);
        Accesses.Add((SqlAccessOperation.Create, new(null, node.Body.VariableName.Value, SqlReferenceKind.Variable,
            range, symbol.Identity.Database, local, local?.IsAmbiguous == true ? "Duplicate local declaration is unsupported." :
                local == null ? "Table variable binding unavailable." : null)));
        base.ExplicitVisit(node);
    }
    public override void ExplicitVisit(DropTableStatement node)
    {
        foreach (var name in node.Objects) Accesses.Add((SqlAccessOperation.Drop,
            document.Parse.Semantics.References.FirstOrDefault(r => r.Range == Range(name)) ?? Reference(name)));
        base.ExplicitVisit(node);
    }
    public override void ExplicitVisit(TruncateTableStatement node) { Accesses.Add((SqlAccessOperation.Truncate, Reference(node.TableName))); base.ExplicitVisit(node); }
    public override void Visit(TSqlFragment node)
    {
        token.ThrowIfCancellationRequested();
        if (node is TableReference && node is not (NamedTableReference or VariableTableReference or SchemaObjectFunctionTableReference or
            QueryDerivedTable or InlineDerivedTable or JoinTableReference or UnqualifiedJoin or JoinParenthesisTableReference or
            PivotedTableReference or UnpivotedTableReference or OpenJsonTableReference or OpenXmlTableReference))
            Issues.Add(new("opaque-relation", "Relation source/target " + node.GetType().Name + " is not fully analyzed.", new(document.Uri, Range(node))));
        if (node is AlterTableStatement or ExecuteAsStatement)
            Issues.Add(new("unsupported-statement", node.GetType().Name + " requires additional effect analysis.", new(document.Uri, Range(node))));
    }
    private SqlStaticReference Reference(SchemaObjectName node, bool creatingLocal = false)
    {
        var existing = document.Parse.Semantics.References.FirstOrDefault(r => r.Range == Range(node));
        if (existing != null) return existing;
        var name = QualifiedSqlName.From(node);
        var range = Range(node);
        var unqualifiedTemp = name.Name.StartsWith('#') && name.Server == null && name.Database == null && name.Schema == null;
        var local = unqualifiedTemp && creatingLocal ? document.Parse.Semantics.Locals.FirstOrDefault(l => l.Range == range && l.Kind == SqlObjectKind.TempTable) : null;
        if (local == null && creatingLocal && unqualifiedTemp) local = new(name.Name, SqlObjectKind.TempTable, range);
        return new(name, Source.Text.Substring(node.StartOffset, node.FragmentLength), SqlReferenceKind.Relation, range,
            symbol.Identity.Database, local, local?.IsAmbiguous == true ? "Duplicate local declaration is unsupported." :
                name.Name.StartsWith('#') && local == null ? "Temporary table scope is unknown." : null);
    }
}
