using SqlLsp.Core;

namespace SqlLsp.Mcp;

// A bounded projection of existing facts. No execution paths or runtime values are inferred.
internal sealed class ProcedureBrief(SqlProcedureReport report, string workspace, string intent, CancellationToken token, string[]? focus = null)
{
    private sealed record Fact(int Priority, int Module, string Kind, string? Name, string Detail,
        SqlAnalysisLocation Location, string Section, object? Data = null);

    public object Create(int limit)
    {
        var facts = new List<Fact>();
        for (var i = 0; i < report.Modules.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var module = report.Modules[i];
            for (var j = 0; j < module.Issues.Length; j++)
            {
                var issue = module.Issues[j];
                if (issue.Location != null && issue.Code == "return-after-throw")
                    facts.Add(new(-1, i, issue.Code, null, issue.Message, issue.Location, $"/modules/{i}/issues/{j}"));
            }
            for (var j = 0; j < module.Observations.Length; j++)
            {
                var observation = module.Observations[j];
                var priority = observation.Kind == "TransactionContract" ? 0 :
                    observation.Kind is "SequenceAllocation" or "SelectAssignment" or "LocalTableTransfer" or "LocalTableRead" or "DynamicSqlConstruction" or "DeclaredExecutionOptions" ? 1 :
                    observation.Kind == "AtomicOption" && intent == "transactions" ? 1 : 3;
                facts.Add(new(priority, i, observation.Kind, observation.Name, observation.Detail, observation.Location,
                    $"/modules/{i}/observations/{j}", ObservationData(observation)));
            }
            for (var j = 0; j < module.Accesses.Length; j++)
            {
                var access = module.Accesses[j];
                facts.Add(new(intent == "dependencies" ? 1 : access.Operation == SqlAccessOperation.Read ? 5 : 3,
                    i, "RelationAccess", access.Name, access.Explanation, access.Location, $"/modules/{i}/accesses/{j}",
                    new { access.Operation, access.Status, local = access.Local != null, target = access.Target?.Identity, access.FlowContext }));
            }
            for (var j = 0; j < module.Calls.Length; j++)
            {
                var call = module.Calls[j];
                facts.Add(new(intent == "dependencies" ? 1 : 4, i, "Call", call.Name, call.Explanation, call.Location,
                    $"/modules/{i}/calls/{j}", new { call.Status, target = call.Target?.Identity, call.FlowContext }));
            }
            var nodes = module.ControlFlow?.Nodes ?? [];
            for (var j = 0; j < nodes.Length; j++)
            {
                var node = nodes[j];
                if (node.Kind is not (SqlFlowKind.Throw or SqlFlowKind.RaiseError or SqlFlowKind.BeginTransaction or
                    SqlFlowKind.CommitTransaction or SqlFlowKind.RollbackTransaction or SqlFlowKind.SaveTransaction)) continue;
                facts.Add(new(intent == "transactions" ? node.Kind == SqlFlowKind.Throw ? 1 : 2 : 6, i, node.Kind.ToString(), null, node.Detail, node.Location,
                    $"/modules/{i}/controlFlow/nodes/{j}", new
                    {
                        node.Id,
                        node.ParentId,
                        subject = Expression(node.Subject),
                        @throw = node.Throw == null ? null : new
                        {
                            node.Throw.Kind,
                            errorNumber = Expression(node.Throw.ErrorNumber),
                            message = Expression(node.Throw.Message),
                            state = Expression(node.Throw.State)
                        }
                    }));
            }
        }
        // Repeated SELECT assignments must not crowd all other finding kinds out of a brief.
        var ordered = facts.GroupBy(f => (f.Module, f.Kind, Name: f.Kind is "RelationAccess" or "Call" ? f.Name : null))
            .SelectMany(group => group.Take(2))
            .OrderBy(f => f.Priority).ThenBy(f => f.Module).ThenBy(f => f.Location.Range.Start.Line)
            .ThenBy(f => f.Location.Range.Start.Character).ThenBy(f => f.Section, StringComparer.Ordinal).Take(limit).ToArray();
        var sources = ordered.Select(f => f.Location.Uri).Distinct().Select((uri, i) => (uri, id: "b" + i)).ToDictionary(p => p.uri, p => p.id);
        string PathFor(Uri uri)
        {
            if (!uri.IsFile) return uri.AbsoluteUri;
            var relative = Path.GetRelativePath(workspace, uri.LocalPath);
            return (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? relative : uri.LocalPath).Replace('\\', '/');
        }
        return new
        {
            format = "procedure-brief-v1",
            intent,
            report.Status,
            report.Explanation,
            selectionRecovery = report.Status == ResolutionStatus.Ambiguous ?
                "Duplicate identities remain ambiguous. Use a discovery scopeChoice.moduleRef only when its physical directory matches the requested source, or repeat discovery with explicit scopeUri. Selecting a file alone does not establish a separate database context." : null,
            facts = ordered.Select(f => new
            {
                moduleIndex = f.Module,
                f.Kind,
                f.Name,
                f.Detail,
                source = sources[f.Location.Uri],
                line = f.Location.Range.Start.Line + 1,
                column = f.Location.Range.Start.Character + 1,
                endLine = f.Location.Range.End.Line + 1,
                endColumn = f.Location.Range.End.Character + 1,
                citation = PathFor(f.Location.Uri) + ":" + (f.Location.Range.Start.Line + 1),
                section = f.Section,
                sourceOrder = SourceOrder(f, PathFor),
                data = f.Data
            }).ToArray(),
            modules = ordered.Select(f => f.Module).Distinct().Select(i => new
            {
                index = i,
                report.Modules[i].Identity,
                report.Modules[i].Revision,
                report.Modules[i].SourceKind,
                report.Modules[i].CapturedAt,
                report.Modules[i].BodyAvailable,
                report.Modules[i].ImplementationKind
            }).ToArray(),
            sources = sources.Select(s => new { id = s.Value, uri = s.Key.AbsoluteUri, path = PathFor(s.Key) }).ToArray(),
            selection = "Counts describe eligible source observations, return-after-throw issues, relation accesses, calls and transaction/error nodes only. Other retained control/value/parameter facts remain in report. Repeated observation kinds may be omitted even below the item limit.",
            totalFacts = facts.Count,
            omittedFacts = facts.Count - ordered.Length,
            truncated = facts.Count > ordered.Length || report.Modules.Any(m => m.Truncated) || report.ExpansionIssues.Length > 0,
            coverage = new
            {
                modules = report.Modules.Length,
                issues = report.Modules.Sum(m => m.Issues.Length),
                expansionIssues = report.ExpansionIssues.Length,
                report.MaxDepth,
                report.MaxModules
            },
            limitations = "Selected static facts only; not a complete footprint or execution trace. Facts from other modules are transitive possibilities. Missing/omitted facts do not prove no effects. Source lines are one-based UTF-16, ends exclusive; section paths select retained report evidence when reportRef is supplied. Runtime values, branch truth, loop counts and ambient transaction outcomes are not inferred."
        };
    }

    private bool IncludeExpression(SqlSourceObservation observation) => focus is not { Length: > 0 } || observation.Kind == "AtomicOption" ||
        focus.Any(term => observation.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) == true ||
            observation.Expression?.Preview.Contains(term, StringComparison.OrdinalIgnoreCase) == true);

    private object ObservationData(SqlSourceObservation observation)
    {
        var data = new Dictionary<string, object>();
        void Add(string name, object? value) { if (value != null) data[name] = value; }
        Add("beforeFirstExplicitTransaction", observation.BeforeFirstExplicitTransaction);
        Add("jsonPath", observation.JsonPath); Add("asJson", observation.AsJson); Add("operation", observation.Operation);
        Add("expression", IncludeExpression(observation) ? Expression(observation.Expression) : null);
        if (observation.Expression != null && !IncludeExpression(observation)) data["expressionOmitted"] = true;
        if (!observation.Inputs.IsEmpty) data["inputs"] = observation.Inputs;
        Add("inputsComplete", observation.InputsComplete); Add("transactions", observation.Transactions);
        if (!observation.Options.IsEmpty) data["options"] = observation.Options;
        return data;
    }

    private object? SourceOrder(Fact fact, Func<Uri, string> path)
    {
        if (fact.Kind is not ("SequenceAllocation" or "Throw" or "return-after-throw" or "LocalTableTransfer" or "LocalTableRead" or "DynamicExecution")) return null;
        var flow = report.Modules[fact.Module].ControlFlow;
        if (flow == null) return null;
        var contexts = flow.Nodes.Where(n => n.Kind is SqlFlowKind.Try or SqlFlowKind.Catch or SqlFlowKind.Then or SqlFlowKind.Else or SqlFlowKind.LoopBody &&
            SqlNavigationIndex.Contains(n.Location.Range, fact.Location.Range.Start)).ToArray();
        var first = flow.Nodes.Where(n => n.Kind == SqlFlowKind.BeginTransaction).OrderBy(n => n.Location.Range.Start.Line)
            .ThenBy(n => n.Location.Range.Start.Character).FirstOrDefault();
        var position = fact.Location.Range.Start;
        return new
        {
            syntaxOnly = true,
            enclosureComplete = !flow.Truncated,
            beforeFirstExplicitBegin = first == null || flow.Truncated ? (bool?)null :
                position.Line < first.Location.Range.Start.Line || position.Line == first.Location.Range.Start.Line && position.Character < first.Location.Range.Start.Character,
            regions = contexts.Take(4).Select(n => new
            {
                n.Kind,
                predicate = Expression(n.Predicate ?? flow.Nodes.FirstOrDefault(p => p.Id == n.ParentId && p.Kind is SqlFlowKind.If or SqlFlowKind.While)?.Predicate),
                citation = path(n.Location.Uri) + ":" + (n.Location.Range.Start.Line + 1)
            }).ToArray(),
            omittedRegions = Math.Max(0, contexts.Length - 4)
        };
    }

    private static object? Expression(SqlFlowExpression? expression) => expression == null ? null : new { expression.Preview, expression.Truncated };
}
