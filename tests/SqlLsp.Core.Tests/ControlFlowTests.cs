using SqlLsp.Core;

namespace SqlLsp.Core.Tests;

public class ControlFlowTests
{
    private static async Task<(DocumentSnapshot Document, SqlProcedureReport Report)> Analyze(string body, string extra = "")
    {
        var sql = "-- 😀 中文\r\nCREATE PROC [dbo].[P] @state varchar(2), @n int AS BEGIN\r\n" + body +
            "\r\nEND;\r\nGO\r\nCREATE TABLE dbo.A(id int);\r\nGO\r\nCREATE PROC dbo.Q AS SELECT 1;" + extra;
        var document = new DocumentSnapshot(new("file:///project/flow.sql"), null, SqlSourceKind.Workspace,
            await new SqlParser(new()).ParseAsync(new SourceText(sql), CancellationToken.None));
        Assert.Empty(document.Parse.Diagnostics);
        var index = new SqlNavigationIndex([document], _ => "project", new());
        return (document, index.AnalyzeProcedure(document.Uri, new(1, 20), CancellationToken.None));
    }

    private static SqlControlFlowSummary Flow(SqlModuleAnalysis module) => Assert.IsType<SqlControlFlowSummary>(module.ControlFlow);
    private static SqlFlowKind[] Kinds(SqlModuleAnalysis module, IEnumerable<string> ids) =>
        ids.Select(id => Flow(module).Nodes.Single(n => n.Id == id).Kind).ToArray();
    private static string Text(DocumentSnapshot document, TextRange range) =>
        document.Parse.Source.Text[document.Parse.Source.OffsetAt(range.Start)..document.Parse.Source.OffsetAt(range.End)];

    [Fact]
    public async Task Branches_keep_exact_predicates_utf16_ranges_and_module_local_contexts()
    {
        var (document, report) = await Analyze("IF @state = 'OH' BEGIN EXEC dbo.Q; UPDATE dbo.A SET id=1; END ELSE DELETE dbo.A;");
        var module = report.Modules[0];
        var flow = Flow(module);
        Assert.True(flow.BodyAvailable);
        Assert.False(flow.Truncated);
        Assert.Empty(flow.Issues);
        var branch = Assert.Single(flow.Nodes.Where(n => n.Kind == SqlFlowKind.If));
        Assert.Equal("@state = 'OH'", branch.Predicate!.Preview);
        Assert.Equal(branch.Predicate.Preview, Text(document, branch.Predicate.Location.Range));
        Assert.Equal(new[] { SqlFlowKind.If, SqlFlowKind.Then }, Kinds(module, Assert.Single(module.Calls).FlowContext));
        Assert.Equal(new[] { SqlFlowKind.If, SqlFlowKind.Then }, Kinds(module, module.Accesses.Single(a => a.Operation == SqlAccessOperation.Update).FlowContext));
        Assert.Equal(new[] { SqlFlowKind.If, SqlFlowKind.Else }, Kinds(module, module.Accesses.Single(a => a.Operation == SqlAccessOperation.Delete).FlowContext));
        Assert.Contains("FALSE or UNKNOWN", flow.Nodes.Single(n => n.Kind == SqlFlowKind.Else).Detail);
        Assert.Empty(Flow(report.Modules[1]).Nodes); // Do not propagate the caller's OH condition into Q.
        Assert.All(flow.Nodes, node =>
        {
            Assert.Equal(document.Uri, node.Location.Uri);
            Assert.True(node.Location.Range.Start.Line >= 2);
            Assert.NotEmpty(Text(document, node.Location.Range));
            if (node.ParentId != null) Assert.Contains(flow.Nodes.TakeWhile(n => n.Id != node.Id), n => n.Id == node.ParentId);
        });
    }

    [Fact]
    public async Task Predicate_calls_are_outside_branch_bodies_and_case_results_do_not_guard_the_whole_write()
    {
        var (_, report) = await Analyze("IF dbo.F(@n)>0 EXEC dbo.Q;\r\n" +
            "UPDATE dbo.A SET id=CASE WHEN dbo.F(@n)>1 THEN dbo.F(2) ELSE dbo.F(3) END;\r\n" +
            "SELECT CASE @n WHEN 1 THEN dbo.F(4) ELSE dbo.F(5) END;",
            "\r\nGO\r\nCREATE FUNCTION dbo.F(@x int) RETURNS int AS BEGIN RETURN @x; END;");
        var module = report.Modules[0];
        var functions = module.Calls.Where(c => c.Target?.Identity?.Name == "F").ToArray();
        Assert.Equal(6, functions.Length);
        Assert.Equal(new[] { SqlFlowKind.If }, Kinds(module, functions[0].FlowContext));
        Assert.Equal(new[] { SqlFlowKind.SearchedCase, SqlFlowKind.CaseWhen }, Kinds(module, functions[1].FlowContext));
        Assert.Equal(new[] { SqlFlowKind.SearchedCase, SqlFlowKind.CaseWhen, SqlFlowKind.CaseResult }, Kinds(module, functions[2].FlowContext));
        Assert.Equal(new[] { SqlFlowKind.SearchedCase, SqlFlowKind.CaseElse }, Kinds(module, functions[3].FlowContext));
        Assert.Equal(new[] { SqlFlowKind.SimpleCase, SqlFlowKind.CaseWhen, SqlFlowKind.CaseResult }, Kinds(module, functions[4].FlowContext));
        Assert.Empty(Assert.Single(module.Accesses).FlowContext);
        Assert.Equal("@n", Flow(module).Nodes.Single(n => n.Kind == SqlFlowKind.SimpleCase).Subject!.Preview);
        Assert.Contains(Flow(module).Limitations, l => l.Contains("short-circuit"));
    }

    [Fact]
    public async Task Loops_and_early_exits_are_observations_without_invented_reachability()
    {
        var (_, report) = await Analyze("WHILE @n>0 BEGIN IF @n=1 BREAK; ELSE CONTINUE; END; RETURN; UPDATE dbo.A SET id=9;");
        var module = report.Modules[0];
        var flow = Flow(module);
        var stop = flow.Nodes.Single(n => n.Kind == SqlFlowKind.Break);
        Assert.Equal(new[] { SqlFlowKind.While, SqlFlowKind.LoopBody, SqlFlowKind.If, SqlFlowKind.Then, SqlFlowKind.Break },
            Kinds(module, flow.ContextAt(stop.Location.Range.Start)));
        Assert.Contains(flow.Nodes, n => n.Kind == SqlFlowKind.Continue);
        Assert.Contains(flow.Nodes, n => n.Kind == SqlFlowKind.Return);
        Assert.Empty(Assert.Single(module.Accesses).FlowContext); // Still retained after RETURN, with no invented guard.
        Assert.Contains(flow.Limitations, l => l.Contains("Empty context does not mean unconditional"));
        Assert.Contains(flow.Limitations, l => l.Contains("earlier exits"));
    }

    [Fact]
    public async Task Try_catch_rethrow_and_transaction_names_retain_regions_without_claiming_outcomes()
    {
        var (document, report) = await Analyze("BEGIN TRY BEGIN TRANSACTION Payroll; SAVE TRANSACTION beforeWrite;\r\n" +
            "IF @n>0 UPDATE dbo.A SET id=1; COMMIT TRANSACTION Payroll; END TRY\r\n" +
            "BEGIN CATCH ROLLBACK TRANSACTION beforeWrite; RAISERROR(N'PRIVATE_SQL_CANARY',10,1); THROW; END CATCH;");
        var module = report.Modules[0];
        var flow = Flow(module);
        Assert.Equal(new[] { SqlFlowKind.TryCatch, SqlFlowKind.Try, SqlFlowKind.If, SqlFlowKind.Then }, Kinds(module, Assert.Single(module.Accesses).FlowContext));
        var rethrow = flow.Nodes.Single(n => n.Kind == SqlFlowKind.Throw);
        Assert.Equal(new[] { SqlFlowKind.TryCatch, SqlFlowKind.Catch, SqlFlowKind.Throw }, Kinds(module, flow.ContextAt(rethrow.Location.Range.Start)));
        Assert.Contains("Bare THROW", rethrow.Detail);
        Assert.Equal("Payroll", flow.Nodes.Single(n => n.Kind == SqlFlowKind.BeginTransaction).Subject!.Preview);
        foreach (var kind in new[] { SqlFlowKind.SaveTransaction, SqlFlowKind.RollbackTransaction })
        {
            var subject = flow.Nodes.Single(n => n.Kind == kind).Subject!;
            Assert.Equal("beforeWrite", subject.Preview);
            Assert.Equal(subject.Preview, Text(document, subject.Location.Range));
        }
        Assert.Contains(flow.Nodes, n => n.Kind == SqlFlowKind.CommitTransaction);
        Assert.Contains("severity", flow.Nodes.Single(n => n.Kind == SqlFlowKind.RaiseError).Detail);
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", System.Text.Json.JsonSerializer.Serialize(flow));
        Assert.Contains(flow.Limitations, l => l.Contains("actual commit/rollback outcomes are not inferred"));
    }

    [Fact]
    public async Task Unstructured_jumps_are_explicit_without_fabricated_edges()
    {
        var (_, report) = await Analyze("GOTO finished; UPDATE dbo.A SET id=2; finished: RETURN;");
        var module = report.Modules[0];
        var flow = Flow(module);
        Assert.Contains(flow.Nodes, n => n.Kind == SqlFlowKind.Goto && n.Subject!.Preview == "finished");
        Assert.Contains(flow.Nodes, n => n.Kind == SqlFlowKind.Label);
        Assert.Contains(module.Issues, i => i.Code == "unstructured-control-flow");
        Assert.Empty(Assert.Single(module.Accesses).FlowContext);
        Assert.All(flow.Nodes, n => { if (n.ParentId != null) Assert.Contains(flow.Nodes, parent => parent.Id == n.ParentId); });
    }

    [Fact]
    public async Task Preview_and_node_budgets_preserve_full_evidence_ranges_and_visible_truncation()
    {
        var literal = new string('a', 246) + "😀" + new string('b', 100);
        var (document, report) = await Analyze("IF @state=N'" + literal + "' EXEC dbo.Q;\r\n" + string.Concat(Enumerable.Repeat("IF @n=0 RETURN;\r\n", 100)));
        var module = report.Modules[0];
        var flow = Flow(module);
        Assert.Equal(256, flow.Nodes.Length);
        Assert.True(flow.Truncated);
        Assert.True(module.Truncated);
        Assert.Contains(module.Issues, i => i.Code == "control-flow-limit");
        var predicate = flow.Nodes[0].Predicate!;
        Assert.True(predicate.Truncated);
        Assert.True(predicate.Preview.Length <= 256);
        Assert.Equal(255, predicate.Preview.Length); // The high surrogate occupies unit 256 of the original expression.
        Assert.False(char.IsHighSurrogate(predicate.Preview[^1]));
        Assert.Contains(literal, Text(document, predicate.Location.Range));
        Assert.All(module.Calls.SelectMany(c => c.FlowContext).Concat(module.Accesses.SelectMany(a => a.FlowContext)), id => Assert.Contains(flow.Nodes, n => n.Id == id));
    }
}
