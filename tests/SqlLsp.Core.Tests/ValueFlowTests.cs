using SqlLsp.Core;

namespace SqlLsp.Core.Tests;

public class ValueFlowTests
{
    private static async Task<(DocumentSnapshot Document, SqlNavigationIndex Index, SqlProcedureReport Report)> Analyze(
        string body, string extra = "", string parameters = "@State varchar(2), @n int", ResolutionOptions? options = null, int depth = 2)
    {
        var source = new SourceText("-- 😀 中文\r\nCREATE PROC [dbo].[P] " + parameters + " AS BEGIN\r\n" + body +
            "\r\nEND;\r\nGO\r\nCREATE TABLE dbo.A(StateCode varchar(2));" + extra);
        var document = new DocumentSnapshot(new("file:///project/values.sql"), null, SqlSourceKind.Workspace,
            await new SqlParser(new(), options).ParseAsync(source, CancellationToken.None));
        Assert.Empty(document.Parse.Diagnostics);
        var index = new SqlNavigationIndex([document], _ => "project", options ?? new());
        return (document, index, index.AnalyzeProcedure(document.Uri, source.PositionAt(source.Text.IndexOf("[P]", StringComparison.Ordinal)), CancellationToken.None, depth));
    }
    private static SqlValueFlowSummary Values(SqlModuleAnalysis module) => Assert.IsType<SqlValueFlowSummary>(module.ValueFlow);
    private static SqlValueFact Fact(SqlValueFlowSummary flow, string id) => flow.Facts.Single(f => f.Id == id);
    private static string Text(DocumentSnapshot doc, SqlAnalysisLocation location) => doc.Parse.Source.Text[
        doc.Parse.Source.OffsetAt(location.Range.Start)..doc.Parse.Source.OffsetAt(location.Range.End)];
    private static void CheckReferences(SqlValueFlowSummary flow)
    {
        Assert.All(flow.Facts, f =>
        {
            if (f.TargetVariableId != null) Assert.Contains(flow.Variables, v => v.Id == f.TargetVariableId);
            Assert.All(f.Inputs, i =>
            {
                if (i.VariableId != null) Assert.Contains(flow.Variables, v => v.Id == i.VariableId);
                Assert.All(i.SourceFacts, id => Assert.Contains(flow.Facts, source => source.Id == id));
            });
        });
        Assert.All(flow.Calls.SelectMany(c => c.Bindings), b =>
        {
            Assert.Contains(flow.Facts, f => f.Id == b.InputFactId);
            if (b.OutputFactId != null) Assert.Contains(flow.Facts, f => f.Id == b.OutputFactId);
        });
    }

    [Fact]
    public async Task Named_argument_renaming_links_entry_to_local_to_callee_filter_with_exact_ranges()
    {
        var (doc, _, report) = await Analyze("DECLARE @region varchar(2)=@State; IF @region='OH' EXEC dbo.Q @Region=@region;",
            "\r\nGO\r\nCREATE PROC dbo.Q @Region varchar(2) AS SELECT * FROM dbo.A WHERE StateCode=@Region;");
        var root = Values(report.Modules[0]); var callee = Values(report.Modules[1]);
        var call = Assert.Single(root.Calls); var binding = Assert.Single(call.Bindings);
        Assert.Equal(ResolutionStatus.Resolved, call.BindingStatus);
        Assert.Equal(report.Modules[1].Id, call.TargetModuleId);
        Assert.Equal(report.Modules[1].Revision, call.TargetRevision);
        Assert.Equal("@Region", binding.ParameterName);
        Assert.Equal("@Region", Text(doc, binding.Parameter));
        var input = Assert.Single(Fact(root, binding.InputFactId).Inputs);
        var declaration = Fact(root, Assert.Single(input.SourceFacts));
        Assert.Equal(SqlValueFactKind.Declaration, declaration.Kind);
        Assert.Equal("@State", Assert.Single(declaration.Inputs).Name);
        Assert.Equal(SqlValueFactKind.ParameterInput, Fact(root, Assert.Single(declaration.Inputs[0].SourceFacts)).Kind);
        Assert.Contains(root.Facts.Single(f => f.Id == binding.InputFactId).FlowContext, id => report.Modules[0].ControlFlow!.Nodes.Any(n => n.Id == id && n.Kind == SqlFlowKind.Then));
        var where = Assert.Single(callee.Facts, f => f.Kind == SqlValueFactKind.Predicate && f.Role == "Where");
        Assert.Equal(binding.ParameterVariableId, Assert.Single(where.Inputs).VariableId);
        Assert.Equal(SqlValueFactKind.ParameterInput, Fact(callee, Assert.Single(where.Inputs[0].SourceFacts)).Kind);
        Assert.All(root.Facts, f => Assert.Equal(doc.Uri, f.Location.Uri));
        Assert.False(root.Truncated); CheckReferences(root); CheckReferences(callee);
    }

    [Fact]
    public async Task Reassignment_replaces_old_definitions_without_substituting_values_into_shared_callees()
    {
        var (_, _, report) = await Analyze("DECLARE @x varchar(2)=@State; SET @x='CA'; EXEC dbo.Q @x; EXEC dbo.Q @State;",
            "\nGO\nCREATE PROC dbo.Q @Region varchar(2)='OH' AS SELECT * FROM dbo.A WHERE StateCode=@Region;");
        var flow = Values(report.Modules[0]);
        var first = Assert.Single(Fact(flow, flow.Calls[0].Bindings[0].InputFactId).Inputs);
        Assert.True(first.SourcesComplete);
        var assignment = Fact(flow, Assert.Single(first.SourceFacts));
        Assert.Equal(SqlValueFactKind.Assignment, assignment.Kind);
        Assert.Equal("'CA'", assignment.Expression!.Preview);
        Assert.Empty(assignment.Inputs);
        var second = Assert.Single(Fact(flow, flow.Calls[1].Bindings[0].InputFactId).Inputs);
        Assert.Equal(SqlValueFactKind.ParameterInput, Fact(flow, Assert.Single(second.SourceFacts)).Kind);
        var callee = Values(report.Modules[1]);
        var entry = Assert.Single(callee.Facts, f => f.Kind == SqlValueFactKind.ParameterInput);
        var defaultValue = Assert.Single(callee.Facts, f => f.Kind == SqlValueFactKind.ParameterDefault);
        Assert.DoesNotContain(defaultValue.Id, callee.Facts.Single(f => f.Role == "Where").Inputs[0].SourceFacts);
        Assert.Contains(entry.Id, callee.Facts.Single(f => f.Role == "Where").Inputs[0].SourceFacts);
    }

    [Fact]
    public async Task Branch_merges_preserve_both_definitions_and_missing_else_keeps_entry_alternative()
    {
        var (_, _, report) = await Analyze("IF @n=0 SET @State='OH'; ELSE SET @State='CA'; SELECT * FROM dbo.A WHERE StateCode=@State;");
        var flow = Values(report.Modules[0]);
        var use = Assert.Single(flow.Facts.Single(f => f.Role == "Where").Inputs);
        Assert.True(use.SourcesComplete); Assert.Equal(2, use.SourceFacts.Length);
        Assert.Equal(new[] { "'CA'", "'OH'" }, use.SourceFacts.Select(id => Fact(flow, id).Expression!.Preview).Order().ToArray());
        var (_, _, withoutElse) = await Analyze("IF @n=0 SET @State='OH'; SELECT * FROM dbo.A WHERE StateCode=@State;");
        flow = Values(withoutElse.Modules[0]);
        use = Assert.Single(flow.Facts.Single(f => f.Role == "Where").Inputs);
        Assert.Equal(2, use.SourceFacts.Length);
        Assert.Contains(use.SourceFacts, id => Fact(flow, id).Kind == SqlValueFactKind.ParameterInput);
    }

    [Theory]
    [InlineData("EXEC dbo.Q @State, DEFAULT;", SqlArgumentMode.ExplicitDefault)]
    [InlineData("EXEC dbo.Q @First=@State;", SqlArgumentMode.OmittedDefault)]
    [InlineData("EXEC dbo.Q @Second=DEFAULT,@First=@State;", SqlArgumentMode.ExplicitDefault)]
    public async Task Defaults_and_named_reordering_bind_only_to_the_verified_formal(string body, SqlArgumentMode mode)
    {
        var (doc, _, report) = await Analyze(body, "\nGO\nCREATE PROC dbo.Q @First varchar(2), @Second int=7 AS SELECT 1;");
        var flow = Values(report.Modules[0]); var call = Assert.Single(flow.Calls);
        Assert.Equal(ResolutionStatus.Resolved, call.BindingStatus);
        Assert.Equal(2, call.Bindings.Length);
        Assert.Equal("@First", call.Bindings[0].ParameterName);
        Assert.Equal(mode, call.Bindings[1].Mode);
        var selected = Fact(flow, call.Bindings[1].InputFactId);
        Assert.Equal("7", selected.Expression!.Preview);
        Assert.Equal("7", Text(doc, selected.Expression.Location));
        Assert.Empty(selected.Inputs); CheckReferences(flow);
    }

    [Theory]
    [InlineData("EXEC dbo.Q @Wrong=@State;")]
    [InlineData("EXEC dbo.Q @First=@State,@First=@State;")]
    [InlineData("EXEC dbo.Q;")]
    [InlineData("EXEC dbo.Q DEFAULT;")]
    [InlineData("EXEC dbo.Q @State,1,2;")]
    [InlineData("EXEC dbo.Q @State OUTPUT;")]
    public async Task Invalid_argument_lists_never_publish_a_partial_verified_binding(string body)
    {
        var (_, _, report) = await Analyze(body, "\nGO\nCREATE PROC dbo.Q @First varchar(2), @Second int=7 AS SELECT 1;");
        var flow = Values(report.Modules[0]); var call = Assert.Single(flow.Calls);
        Assert.Equal(ResolutionStatus.Unsupported, call.BindingStatus); Assert.Empty(call.Bindings);
        Assert.Contains(flow.Issues, i => i.Code == "value-call-binding"); CheckReferences(flow);
    }

    [Fact]
    public async Task Output_inputs_use_precall_state_and_copyback_and_return_status_remain_unknown()
    {
        var (_, _, report) = await Analyze("DECLARE @status int; EXEC @status=dbo.Q @First=@n OUTPUT,@Second=@n; SELECT * FROM dbo.A WHERE StateCode=@n;",
            "\nGO\nCREATE PROC dbo.Q @First int OUTPUT,@Second int AS BEGIN SET @First=9; RETURN 42; END;");
        var flow = Values(report.Modules[0]); var call = Assert.Single(flow.Calls);
        Assert.Equal(ResolutionStatus.Resolved, call.BindingStatus);
        Assert.True(call.Bindings[0].Output); Assert.NotNull(call.Bindings[0].OutputFactId);
        Assert.Equal(SqlValueFactKind.CallOutput, Fact(flow, call.Bindings[0].OutputFactId!).Kind);
        foreach (var binding in call.Bindings)
            Assert.Equal(SqlValueFactKind.ParameterInput, Fact(flow, Assert.Single(Fact(flow, binding.InputFactId).Inputs[0].SourceFacts)).Kind);
        Assert.False(flow.Facts.Single(f => f.Role == "Where").Inputs[0].SourcesComplete);
        Assert.Equal(ResolutionStatus.Unsupported, Assert.Single(flow.Facts, f => f.Kind == SqlValueFactKind.CallReturn).Status);
        Assert.DoesNotContain(flow.Facts, f => f.Expression?.Preview == "42"); CheckReferences(flow);
    }

    [Fact]
    public async Task Select_assignment_does_not_assume_projection_or_row_order_for_expression_and_filter_reads()
    {
        var (_, _, report) = await Analyze("DECLARE @x int=@n; SELECT @x=@n,@n=@x FROM dbo.A WHERE StateCode=@x; SELECT * FROM dbo.A WHERE StateCode=@x;");
        var flow = Values(report.Modules[0]);
        var assignment = flow.Facts.Single(f => f.Kind == SqlValueFactKind.SelectAssignment && f.Inputs[0].Name == "@x");
        Assert.False(assignment.Inputs[0].SourcesComplete); Assert.Empty(assignment.Inputs[0].SourceFacts);
        var uses = flow.Facts.Where(f => f.Role == "Where").ToArray();
        Assert.False(uses[0].Inputs[0].SourcesComplete); Assert.Empty(uses[0].Inputs[0].SourceFacts);
        Assert.False(uses[1].Inputs[0].SourcesComplete);
        Assert.Contains(flow.Issues, i => i.Code == "value-select-assignment");
    }

    [Fact]
    public async Task Loops_try_catch_goto_and_dynamic_calls_do_not_fabricate_a_latest_definition()
    {
        var (_, _, report) = await Analyze("SET @n=1; WHILE @n>0 BEGIN IF @n=2 BREAK; SET @n=3; END; " +
            "BEGIN TRY SET @n=4; END TRY BEGIN CATCH IF @n=5 RETURN; END CATCH; EXEC(N'SELECT 1'); SELECT * FROM dbo.A WHERE StateCode=@n;");
        var flow = Values(report.Modules[0]);
        Assert.False(flow.Facts.Single(f => f.Role == "While").Inputs[0].SourcesComplete);
        var caught = flow.Facts.Single(f => f.Role == "If" && f.Expression!.Preview == "@n=5");
        Assert.False(caught.Inputs[0].SourcesComplete);
        Assert.False(flow.Facts.Single(f => f.Role == "Where").Inputs[0].SourcesComplete);
        Assert.Contains(flow.Issues, i => i.Code == "value-loop");
        Assert.Contains(flow.Issues, i => i.Code == "value-error-flow");
        Assert.Contains(flow.Issues, i => i.Code == "value-call-opaque");
        var (_, _, jumped) = await Analyze("finished: IF @n=1 RETURN; SET @n=2; GOTO finished;");
        flow = Values(jumped.Modules[0]);
        Assert.False(flow.Facts.Single(f => f.Role == "If").Inputs[0].SourcesComplete);
        Assert.Contains(flow.Issues, i => i.Code == "value-unstructured-flow");
    }

    [Fact]
    public async Task Scoped_forward_duplicate_and_complex_assignments_stay_partial_and_rhs_subquery_reads_old_state()
    {
        var (_, _, report) = await Analyze("SET @later=1; DECLARE @later int; DECLARE @x int=@n; " +
            "SET @x=(SELECT COUNT(*) FROM dbo.A WHERE StateCode=@x); DECLARE @x int; SET @x=2; SELECT * FROM dbo.A WHERE StateCode=@x;");
        var flow = Values(report.Modules[0]);
        Assert.Null(flow.Facts.First(f => f.Kind == SqlValueFactKind.Assignment).TargetVariableId);
        var uses = flow.Facts.Where(f => f.Role == "Where").ToArray();
        Assert.Equal(SqlValueFactKind.Declaration, Fact(flow, Assert.Single(uses[0].Inputs[0].SourceFacts)).Kind);
        Assert.Equal(ResolutionStatus.Unsupported, uses[1].Inputs[0].Status);
        Assert.Null(uses[1].Inputs[0].VariableId);
        Assert.Contains(flow.Issues, i => i.Code == "value-assignment-unsupported"); CheckReferences(flow);
    }

    [Fact]
    public async Task Limits_are_visible_ids_are_valid_and_preview_cuts_are_surrogate_safe()
    {
        var literal = new string('a', 253) + "😀" + new string('b', 80);
        var (_, _, report) = await Analyze("DECLARE @x nvarchar(400)=N'" + literal + "'; " + string.Concat(Enumerable.Repeat("SET @n=1; ", 300)));
        var flow = Values(report.Modules[0]);
        Assert.True(flow.Truncated); Assert.True(report.Modules[0].Truncated);
        Assert.True(flow.Facts.Length <= 256); Assert.True(flow.FactCount <= 512);
        Assert.Contains(flow.Issues, i => i.Code == "value-flow-limit");
        var preview = flow.Facts.Single(f => f.Kind == SqlValueFactKind.Declaration).Expression!;
        Assert.True(preview.Truncated); Assert.True(preview.Preview.Length <= 256);
        Assert.Equal(255, preview.Preview.Length);
        Assert.False(char.IsHighSurrogate(preview.Preview[^1])); CheckReferences(flow);
    }

    [Fact]
    public async Task Callee_case_rules_depth_limits_and_cancelled_requests_preserve_the_binding_contract()
    {
        var (doc, index, report) = await Analyze("EXEC dbo.Q @region=@State;", "\nGO\nCREATE PROC dbo.Q @Region varchar(2) AS SELECT 1;",
            options: new(CaseSensitive: false), depth: 0);
        Assert.Single(report.Modules); Assert.NotEmpty(report.ExpansionIssues);
        Assert.Equal(ResolutionStatus.Resolved, Values(report.Modules[0]).Calls[0].BindingStatus);
        Assert.NotNull(Values(report.Modules[0]).Calls[0].TargetRevision);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => index.AnalyzeProcedure(doc.Uri, new(1, 20), cancelled.Token));
        var (_, _, sensitive) = await Analyze("EXEC dbo.Q @region=@State;", "\nGO\nCREATE PROC dbo.Q @Region varchar(2) AS SELECT 1;");
        Assert.Equal(ResolutionStatus.Unsupported, Values(sensitive.Modules[0]).Calls[0].BindingStatus);
    }

    [Fact]
    public async Task Ambiguous_targets_and_duplicate_formals_never_guess_a_binding()
    {
        var (_, _, duplicateTarget) = await Analyze("EXEC dbo.Q @State;", "\nGO\nCREATE PROC dbo.Q @Region varchar(2) AS SELECT 1;\nGO\nCREATE PROC dbo.Q @Region varchar(2) AS SELECT 2;");
        var call = Values(duplicateTarget.Modules[0]).Calls[0];
        Assert.Equal(ResolutionStatus.Unsupported, call.BindingStatus); Assert.Empty(call.Bindings);
        var (_, _, duplicateFormal) = await Analyze("EXEC dbo.Q @State,@State;", "\nGO\nCREATE PROC dbo.Q @Region varchar(2),@Region varchar(2) AS SELECT 1;");
        call = Values(duplicateFormal.Modules[0]).Calls[0];
        Assert.Equal(ResolutionStatus.Unsupported, call.BindingStatus); Assert.Empty(call.Bindings);
    }

    [Fact]
    public async Task Aliased_output_arguments_keep_all_unknown_copyback_sources()
    {
        var (_, _, report) = await Analyze("EXEC dbo.Q @n OUTPUT,@n OUTPUT; IF @n=1 RETURN;", "\nGO\nCREATE PROC dbo.Q @A int OUTPUT,@B int OUTPUT AS SELECT 1;");
        var flow = Values(report.Modules[0]); var call = Assert.Single(flow.Calls);
        Assert.Equal(ResolutionStatus.Resolved, call.BindingStatus);
        var use = flow.Facts.Single(f => f.Role == "If").Inputs[0];
        Assert.False(use.SourcesComplete);
        Assert.Equal(2, use.SourceFacts.Length);
        Assert.All(use.SourceFacts, id => Assert.Equal(SqlValueFactKind.CallOutput, Fact(flow, id).Kind));
    }

    [Fact]
    public async Task Input_source_signature_and_call_limits_are_visible_without_dangling_ids()
    {
        var manyInputs = string.Join("+", Enumerable.Repeat("@n", 17));
        var (_, _, inputReport) = await Analyze("DECLARE @x int; SET @x=" + manyInputs + "; IF @x=1 RETURN;");
        var flow = Values(inputReport.Modules[0]);
        Assert.True(flow.Truncated); Assert.Contains(flow.Issues, i => i.Code == "value-input-limit");
        Assert.False(flow.Facts.Single(f => f.Role == "If").Inputs[0].SourcesComplete); CheckReferences(flow);
        var (_, _, sourceReport) = await Analyze(string.Concat(Enumerable.Range(1, 10).Select(i => "IF @n=" + i + " SET @State='OH'; ")) + "IF @State='CA' RETURN;");
        flow = Values(sourceReport.Modules[0]);
        Assert.True(flow.Truncated); Assert.Contains(flow.Issues, i => i.Code == "value-source-limit"); CheckReferences(flow);
        var parameters = string.Join(",", Enumerable.Range(1, 65).Select(i => "@p" + i + " int=0"));
        var (_, _, signatureReport) = await Analyze("EXEC dbo.Q;", "\nGO\nCREATE PROC dbo.Q " + parameters + " AS SELECT 1;");
        flow = Values(signatureReport.Modules[0]);
        Assert.True(flow.Truncated); Assert.Empty(flow.Calls[0].Bindings);
        var (_, _, callsReport) = await Analyze(string.Concat(Enumerable.Repeat("EXEC dbo.Q; ", 35)), "\nGO\nCREATE PROC dbo.Q AS SELECT 1;");
        flow = Values(callsReport.Modules[0]);
        Assert.True(flow.Truncated); Assert.Equal(32, flow.Calls.Length); CheckReferences(flow);
    }

    [Fact]
    public async Task Case_join_and_having_conditions_retain_parameter_definition_reads()
    {
        var (_, _, report) = await Analyze("SELECT CASE @State WHEN 'OH' THEN CASE WHEN @n=1 THEN 1 ELSE 2 END ELSE 3 END " +
            "FROM dbo.A a JOIN dbo.A b ON a.StateCode=@State GROUP BY a.StateCode HAVING COUNT(*)>@n;");
        var flow = Values(report.Modules[0]);
        foreach (var role in new[] { "CaseInput", "CaseWhen", "Join", "Having" })
        {
            var use = Assert.Single(flow.Facts, f => f.Role == role);
            Assert.NotEmpty(use.Inputs);
            Assert.All(use.Inputs, i => Assert.Equal(SqlValueFactKind.ParameterInput, Fact(flow, Assert.Single(i.SourceFacts)).Kind));
        }
        Assert.Contains(flow.Facts, f => f.Role == "CaseMatch"); CheckReferences(flow);
    }

    [Fact]
    public async Task Variable_budget_omits_untracked_locals_explicitly_instead_of_fabricating_ids()
    {
        var (_, _, report) = await Analyze(string.Concat(Enumerable.Range(1, 150).Select(i => "DECLARE @v" + i + " int=@n; ")) + "IF @v150=1 RETURN;");
        var flow = Values(report.Modules[0]);
        Assert.True(flow.Truncated); Assert.Equal(128, flow.Variables.Length);
        Assert.Equal(ResolutionStatus.Unsupported, flow.Facts.Single(f => f.Role == "If").Inputs[0].Status);
        CheckReferences(flow);
    }

    [Fact]
    public async Task Public_evaluation_fixture_has_parseable_modules_and_source_supported_gold_boundaries()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "value-tracing.sql"));
        var document = new DocumentSnapshot(new("file:///evaluation/modules.sql"), null, SqlSourceKind.Workspace,
            await new SqlParser(new()).ParseAsync(new SourceText(text), CancellationToken.None));
        Assert.Empty(document.Parse.Diagnostics);
        var index = new SqlNavigationIndex([document], _ => "evaluation", new());
        SqlProcedureReport Report(string name) => index.AnalyzeProcedure(document.Uri,
            index.Symbols.Single(s => s.Identity.Name == name).Target.Range.Start, CancellationToken.None);
        var route = Report("RoutePayroll");
        var flow = Values(route.Modules[0]);
        var binding = Assert.Single(flow.Calls).Bindings[0];
        var input = Assert.Single(Fact(flow, binding.InputFactId).Inputs);
        Assert.Equal(2, input.SourceFacts.Length); Assert.True(input.SourcesComplete);
        Assert.Contains(input.SourceFacts, id => Fact(flow, id).Kind == SqlValueFactKind.Declaration);
        Assert.Contains(input.SourceFacts, id => Fact(flow, id).Kind == SqlValueFactKind.Assignment);
        var filter = Values(route.Modules[1]).Facts.Single(f => f.Role == "Where");
        Assert.Contains(filter.Inputs, i => i.VariableId == binding.ParameterVariableId);
        var defaults = Values(Report("DefaultRoute").Modules[0]).Calls;
        Assert.Equal(SqlArgumentMode.ExplicitDefault, defaults[0].Bindings[1].Mode);
        Assert.Equal(SqlArgumentMode.OmittedDefault, defaults[1].Bindings[1].Mode);
        Assert.Equal(ResolutionStatus.Unsupported, Values(Report("InvalidRoute").Modules[0]).Calls[0].BindingStatus);
        Assert.False(Values(Report("SelectRetention").Modules[0]).Facts.Single(f => f.Role == "If").Inputs[0].SourcesComplete);
        Assert.False(Values(Report("LoopRoute").Modules[0]).Facts.Single(f => f.Role == "While").Inputs[0].SourcesComplete);
        Assert.Contains(Values(Report("OutputRoute").Modules[0]).Facts, f => f.Kind == SqlValueFactKind.CallReturn && f.Status == ResolutionStatus.Unsupported);
    }

    [Fact]
    public async Task Opaque_exec_suppresses_payload_previews_but_preserves_explicit_variable_dependencies()
    {
        var (_, _, report) = await Analyze("EXEC sys.sp_executesql N'PRIVATE_SQL_CANARY',N'@p varchar(2)',@p=@State; IF @State='OH' RETURN;");
        var flow = Values(report.Modules[0]);
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", System.Text.Json.JsonSerializer.Serialize(flow));
        Assert.Empty(flow.Calls[0].Bindings);
        var input = Assert.Single(flow.Facts, f => f.Kind == SqlValueFactKind.CallArgument && f.Expression?.Kind == "Variable");
        Assert.Equal("@State", Assert.Single(input.Inputs).Name);
        Assert.Equal(ResolutionStatus.Unsupported, input.Status);
        Assert.False(flow.Facts.Single(f => f.Role == "If").Inputs[0].SourcesComplete);
        CheckReferences(flow);
    }
}
