using SqlLsp.Core;

namespace SqlLsp.Mcp;

// Presentation over the verified report. Focus is lexical selection, never additional SQL binding.
internal sealed class CompactProcedureReport(SqlProcedureReport report, string workspace, string[] focus, CancellationToken token)
{
    private readonly Dictionary<string, object> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> evidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> notes = new(StringComparer.Ordinal);
    private readonly Dictionary<Uri, string> sourceIds = [];
    private readonly Dictionary<SqlAnalysisLocation, string> evidenceIds = [];
    private readonly Dictionary<string, string> noteIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> moduleIds = report.Modules.Select((m, i) => (m.Id, Short: "m" + i)).ToDictionary(x => x.Id, x => x.Short, StringComparer.Ordinal);

    public object Create()
    {
        var selected = SelectFacts();
        var modules = report.Modules.Select(m => Module(m, selected)).ToArray();
        var candidates = report.Candidates.Select(Target).ToArray();
        var issues = report.ExpansionIssues.Select(Issue).ToArray();
        var limitations = report.Limitations.Select(Note).ToArray();
        return Row(("format", "compact-v1"), ("status", report.Status), ("explanation", report.Explanation),
            ("focus", focus), ("focusMatched", focus.Length == 0 ? null : selected.Matched),
            ("selection", "Focus selects connected value details by case-insensitive literal substring, not SQL name resolution. All modules, calls, table effects, coverage issues and limitations remain. Omitted details are not absent behavior."),
            ("coordinates", "Evidence lines/columns are one-based UTF-16; ends are exclusive. Use evidence.text directly for citations. Input selections remain zero-based. IDs are report/module-local bookkeeping."),
            ("candidates", candidates), ("modules", modules), ("expansionIssues", issues),
            ("limitations", limitations), ("maxDepth", report.MaxDepth), ("maxModules", report.MaxModules),
            ("sources", sources), ("evidence", evidence), ("notes", notes));
    }

    private object Module(SqlModuleAnalysis module, Selection selection)
    {
        token.ThrowIfCancellationRequested();
        var values = module.ValueFlow;
        var selected = selection.Facts.GetValueOrDefault(module.Id) ?? [];
        // Boundary input/output facts are always retained. Add their source definitions without expanding unrelated uses.
        var byId = values?.Facts.ToDictionary(f => f.Id, StringComparer.Ordinal) ?? [];
        var boundaries = values?.Calls.SelectMany(c => c.Bindings.SelectMany(b =>
            b.OutputFactId == null ? new[] { b.InputFactId } : new[] { b.InputFactId, b.OutputFactId })) ?? [];
        var unknowns = values?.Facts.Where(f => f.Status != ResolutionStatus.Resolved || f.Inputs.Any(i => !i.SourcesComplete)).Select(f => f.Id) ?? [];
        var pending = new Queue<string>(boundaries.Concat(unknowns));
        while (pending.TryDequeue(out var id))
        {
            token.ThrowIfCancellationRequested();
            if (!selected.Add(id) || !byId.TryGetValue(id, out var fact)) continue;
            foreach (var source in fact.Inputs.SelectMany(i => i.SourceFacts)) pending.Enqueue(source);
        }
        var facts = values?.Facts.Where(f => selected.Contains(f.Id)).ToArray() ?? [];
        var variableIds = facts.SelectMany(f => f.Inputs.Select(i => i.VariableId).Append(f.TargetVariableId)).Where(id => id != null).ToHashSet(StringComparer.Ordinal);
        // Keep formal anchors for every retained call boundary, even when its body was not selected by focus.
        foreach (var binding in report.Modules.SelectMany(m => m.ValueFlow?.Calls ?? [])
            .Where(c => c.TargetModuleId == module.Id && c.TargetRevision == module.Revision).SelectMany(c => c.Bindings))
            variableIds.Add(binding.ParameterVariableId);
        if (focus.Length == 0) foreach (var v in values?.Variables ?? []) variableIds.Add(v.Id);
        var variables = values?.Variables.Where(v => variableIds.Contains(v.Id)).ToArray() ?? [];
        return Row(("id", moduleIds[module.Id]), ("identity", module.Identity), ("sourceKind", module.SourceKind),
            ("revision", module.Revision), ("capturedAt", module.CapturedAt), ("definition", Evidence(module.Definition)),
            ("signature", module.Signature), ("bodyAvailable", module.BodyAvailable), ("implementationKind", module.ImplementationKind), ("truncated", module.Truncated),
            ("parameters", module.Parameters.Select(p => Row(("name", p.Name), ("type", p.Type), ("at", Evidence(p.Location)))).ToArray()),
            ("accesses", module.Accesses.Select(a => Row(("operation", a.Operation), ("name", a.Name), ("at", Evidence(a.Location)),
                ("status", a.Status), ("target", Target(a.Target)), ("local", a.Local == null ? null : Row(("name", a.Local.Name),
                    ("kind", a.Local.Kind), ("type", a.Local.DeclaredType), ("ambiguous", a.Local.IsAmbiguous),
                    ("at", Evidence(new(a.Location.Uri, a.Local.Range))))), ("context", a.FlowContext), ("note", Note(a.Explanation)))).ToArray()),
            ("calls", module.Calls.Select(c => Row(("name", c.Name), ("at", Evidence(c.Location)), ("status", c.Status),
                ("target", Target(c.Target)), ("context", c.FlowContext), ("note", Note(c.Explanation)))).ToArray()),
            ("issues", module.Issues.Select(Issue).ToArray()),
            ("observations", module.Observations.Select(o => Row(("kind", o.Kind), ("name", o.Name), ("at", Evidence(o.Location)),
                ("beforeFirstExplicitTransaction", o.BeforeFirstExplicitTransaction), ("jsonPath", o.JsonPath), ("asJson", o.AsJson),
                ("jsonKeyComparison", o.JsonKeyComparison), ("operation", o.Operation),
                ("expression", IncludeObservationExpression(o) ? Expression(o.Expression) : null),
                ("expressionOmitted", o.Expression != null && !IncludeObservationExpression(o) ? true : null),
                ("inputs", o.Inputs.IsEmpty ? null : o.Inputs.Select(input => Row(("name", input.Name), ("kind", input.Kind),
                    ("at", Evidence(input.Location)), ("verifiedLocal", input.VerifiedLocal))).ToArray()),
                ("inputsComplete", o.InputsComplete), ("transactions", o.Transactions),
                ("options", o.Options.IsEmpty ? null : o.Options.Select(option => Row(("name", option.Name), ("source", Expression(option.Source)))).ToArray()),
                ("note", Note(o.Detail)))).ToArray()),
            ("controlFlow", module.ControlFlow == null ? null : Row(("bodyAvailable", module.ControlFlow.BodyAvailable),
                ("truncated", module.ControlFlow.Truncated), ("limitations", module.ControlFlow.Limitations.Select(Note).ToArray()),
                ("issues", module.ControlFlow.Issues.Select(Issue).ToArray()),
                ("nodes", module.ControlFlow.Nodes.Select(n => Row(("id", n.Id), ("kind", n.Kind), ("parentId", n.ParentId),
                    ("at", Evidence(n.Location)), ("predicate", Expression(n.Predicate)), ("subject", Expression(n.Subject)),
                    ("throw", n.Throw == null ? null : Row(("kind", n.Throw.Kind), ("errorNumber", Expression(n.Throw.ErrorNumber)),
                        ("message", Expression(n.Throw.Message)), ("state", Expression(n.Throw.State)))), ("note", Note(n.Detail)))).ToArray()))),
            ("valueFlow", values == null ? null : Row(("bodyAvailable", values.BodyAvailable), ("truncated", values.Truncated),
                ("omittedFacts", values.Facts.Length - facts.Length), ("omittedVariables", values.Variables.Length - variables.Length),
                ("limitations", values.Limitations.Select(Note).ToArray()), ("issues", values.Issues.Select(Issue).ToArray()),
                ("variables", variables.Select(v => Row(("id", v.Id), ("name", v.Name), ("kind", v.Kind), ("at", Evidence(v.Definition)),
                    ("type", v.Type), ("ordinal", v.Ordinal), ("output", v.Output), ("hasDefault", v.HasDefault))).ToArray()),
                ("facts", facts.Select(f => Row(("id", f.Id), ("kind", f.Kind), ("role", f.Role), ("at", Evidence(f.Location)),
                    ("targetVariableId", f.TargetVariableId), ("expression", f.Expression == null ? null : Row(("kind", f.Expression.Kind),
                        ("at", Evidence(f.Expression.Location)), ("preview", f.Expression.Preview), ("truncated", f.Expression.Truncated))),
                    ("inputs", f.Inputs.Select(i => Row(("name", i.Name), ("variableId", i.VariableId), ("at", Evidence(i.Location)),
                        ("status", i.Status), ("sourceFacts", i.SourceFacts), ("sourcesComplete", i.SourcesComplete))).ToArray()),
                    ("context", f.FlowContext), ("status", f.Status), ("note", Note(f.Detail)))).ToArray()),
                ("calls", values.Calls.Select(c => Row(("id", c.Id), ("at", Evidence(c.Location)), ("target", Target(c.Target)),
                    ("targetModule", c.TargetModuleId != null ? moduleIds.GetValueOrDefault(c.TargetModuleId) : null),
                    ("targetRevision", c.TargetRevision), ("bindingStatus", c.BindingStatus), ("note", Note(c.Detail)),
                    ("bindings", c.Bindings.Select(b => Row(("ordinal", b.Ordinal), ("parameterName", b.ParameterName),
                        ("parameterVariableId", b.ParameterVariableId), ("parameter", Evidence(b.Parameter)), ("inputFactId", b.InputFactId),
                        ("mode", b.Mode), ("output", b.Output), ("outputFactId", b.OutputFactId))).ToArray()))).ToArray()))));
    }

    private sealed record Selection(Dictionary<string, HashSet<string>> Facts, bool Matched);

    private bool IncludeObservationExpression(SqlSourceObservation observation) => focus.Length == 0 || observation.Kind == "AtomicOption" ||
        focus.Any(term => observation.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) == true ||
            observation.Expression?.Preview.Contains(term, StringComparison.OrdinalIgnoreCase) == true);

    private Selection SelectFacts()
    {
        var graph = new Dictionary<(string Module, string Id), HashSet<(string Module, string Id)>>();
        var seeds = new HashSet<(string Module, string Id)>();
        void Add((string Module, string Id) a, (string Module, string Id) b)
        {
            if (!graph.TryGetValue(a, out var aa)) graph[a] = aa = [];
            if (!graph.TryGetValue(b, out var bb)) graph[b] = bb = [];
            aa.Add(b); bb.Add(a);
        }
        bool Match(string? text) => text != null && focus.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        foreach (var module in report.Modules)
        {
            token.ThrowIfCancellationRequested();
            foreach (var v in module.ValueFlow?.Variables ?? []) if (Match(v.Name)) seeds.Add((module.Id, v.Id));
            foreach (var f in module.ValueFlow?.Facts ?? [])
            {
                var key = (module.Id, f.Id);
                if (focus.Length == 0 || Match(f.Expression?.Preview) || f.Inputs.Any(i => Match(i.Name))) seeds.Add(key);
                if (f.TargetVariableId != null) Add(key, (module.Id, f.TargetVariableId));
                foreach (var input in f.Inputs)
                {
                    if (input.VariableId != null) Add(key, (module.Id, input.VariableId));
                    foreach (var source in input.SourceFacts) Add(key, (module.Id, source));
                }
            }
        }
        foreach (var module in report.Modules)
            foreach (var call in module.ValueFlow?.Calls ?? [])
                if (report.Modules.Any(m => m.Id == call.TargetModuleId && m.Revision == call.TargetRevision))
                    foreach (var binding in call.Bindings)
                        Add((module.Id, binding.InputFactId), (call.TargetModuleId!, binding.ParameterVariableId));
        var matched = seeds.Count > 0;
        var pending = new Queue<(string Module, string Id)>(seeds);
        while (pending.TryDequeue(out var key))
        {
            token.ThrowIfCancellationRequested();
            foreach (var neighbor in graph.GetValueOrDefault(key) ?? []) if (seeds.Add(neighbor)) pending.Enqueue(neighbor);
        }
        return new(report.Modules.ToDictionary(m => m.Id, m => seeds.Where(s => s.Module == m.Id).Select(s => s.Id).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal), matched);
    }

    private object? Target(SqlNavigationTarget? target)
    {
        if (target == null) return null;
        var original = target.Uri.AbsoluteUri + "#" + target.Range.Start.Line + ":" + target.Range.Start.Character;
        var module = report.Modules.FirstOrDefault(m => m.Id == original && m.Identity == target.Identity && m.SourceKind == target.SourceKind);
        return module != null ? Row(("module", moduleIds[module.Id])) : Row(("identity", target.Identity),
            ("sourceKind", target.SourceKind), ("at", Evidence(new(target.Uri, target.Range))), ("provenance", target.Provenance));
    }

    private object Issue(SqlCoverageIssue issue) => Row(("code", issue.Code), ("note", Note(issue.Message)), ("at", issue.Location == null ? null : Evidence(issue.Location)));
    private object? Expression(SqlFlowExpression? expression) => expression == null ? null : Row(("at", Evidence(expression.Location)), ("preview", expression.Preview), ("truncated", expression.Truncated));
    private string Note(string text)
    {
        if (noteIds.TryGetValue(text, out var id)) return id;
        id = "n" + notes.Count; noteIds[text] = id; notes[id] = text; return id;
    }
    private string Evidence(SqlAnalysisLocation location)
    {
        if (evidenceIds.TryGetValue(location, out var id)) return id;
        if (!sourceIds.TryGetValue(location.Uri, out var source))
        {
            source = "s" + sources.Count;
            sourceIds[location.Uri] = source;
            sources[source] = Row(("uri", location.Uri.AbsoluteUri), ("path", PathFor(location.Uri)));
        }
        id = "e" + evidence.Count;
        evidenceIds[location] = id;
        var range = location.Range;
        evidence[id] = Row(("source", source), ("line", range.Start.Line + 1), ("column", range.Start.Character + 1),
            ("endLine", range.End.Line + 1), ("endColumn", range.End.Character + 1),
            ("text", PathFor(location.Uri) + ":" + (range.Start.Line + 1)));
        return id;
    }
    private string PathFor(Uri uri)
    {
        if (!uri.IsFile) return uri.AbsoluteUri;
        var relative = Path.GetRelativePath(workspace, uri.LocalPath);
        return (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? relative : uri.LocalPath).Replace('\\', '/');
    }
    private static Dictionary<string, object> Row(params (string Key, object? Value)[] fields) => fields
        .Where(f => f.Value != null).ToDictionary(f => f.Key, f => f.Value!, StringComparer.Ordinal);
}
