using System.Collections.Immutable;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLsp.Core;

internal sealed partial class SqlValueFlow
{
    public override void ExplicitVisit(ExecuteSpecification node)
    {
        if (calls.Count >= 32 || used >= budget) { truncated = true; Unknown(); return; }
        var plan = signature(node);
        truncated |= plan.Limited;
        var procedure = node.ExecutableEntity as ExecutableProcedureReference;
        var arguments = procedure?.Parameters.ToArray() ?? [];
        var error = plan.Error;
        var bound = new Dictionary<int, ExecuteParameter>();
        if (arguments.Length > 64) { error = "Argument/signature limit (64)."; truncated = true; }
        if (error == null)
        {
            var named = false;
            for (var index = 0; index < arguments.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var argument = arguments[index];
                var ordinal = index;
                if (argument.Variable != null)
                {
                    named = true;
                    ordinal = Array.FindIndex(plan.Parameters.ToArray(), p => plan.Comparer.Equals(p.VariableName.Value, argument.Variable.Name));
                }
                else if (named) { error = "Positional argument follows a named argument."; break; }
                if (ordinal < 0 || ordinal >= plan.Parameters.Length || !bound.TryAdd(ordinal, argument))
                { error = "Unknown, duplicate or excess argument; no partial binding is published."; break; }
                var parameter = plan.Parameters[ordinal];
                if (argument.IsOutput && (parameter.Modifier != ParameterModifier.Output || argument.ParameterValue is not VariableReference output || Variable(Range(output)) == null))
                { error = "OUTPUT requires an OUTPUT formal and a verified caller variable."; break; }
                if (argument.ParameterValue is DefaultLiteral && parameter.Value == null)
                { error = "DEFAULT supplied for a parameter without an available declared default."; break; }
            }
            if (error == null && plan.Parameters.Where((_, index) => !bound.ContainsKey(index)).Any(p => p.Value == null))
                error = "Required parameter omitted; no partial binding is published.";
        }
        // All explicit argument inputs observe pre-call state, including aliased OUTPUT arguments.
        var inputFacts = new Dictionary<ExecuteParameter, string?>();
        foreach (var argument in arguments.Take(64))
            inputFacts[argument] = Observe(SqlValueFactKind.CallArgument, "ExecInput", argument, null,
                argument.ParameterValue is DefaultLiteral || (plan.Error != null && argument.ParameterValue is not VariableReference) ? null : argument.ParameterValue,
                plan.Error == null ? "Explicit EXEC input before any copy-back; binding and runtime value are separate." :
                    "Opaque EXEC input: variable dependencies only; literal payload/default text is not exported.", plan.Error == null);
        var outputs = new Dictionary<ExecuteParameter, string?>();
        var copied = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var argument in arguments.Where(a => a.IsOutput).Take(64))
        {
            var target = argument.ParameterValue is VariableReference variable ? Variable(Range(variable)) : null;
            var output = Observe(SqlValueFactKind.CallOutput, "CopyBack", argument, target?.Id, null,
                "Possible OUTPUT copy-back; actual value, callee exit definition, success and copy-back order are unknown.", false);
            outputs[argument] = output;
            if (target != null)
            {
                if (!copied.TryGetValue(target.Id, out var definitions)) copied[target.Id] = definitions = [];
                if (output != null) definitions.Add(output);
            }
        }
        foreach (var (variable, definitions) in copied)
        {
            if (definitions.Count > 8) { truncated = true; Issue("value-source-limit", "Aliased OUTPUT arguments exceed the eight-source limit.", node); }
            state[variable] = new(definitions.Take(8).ToImmutableArray(), false);
        }
        if (arguments.Length > 64 || procedure == null || plan.Error != null)
        {
            Unknown(); Issue("value-call-opaque", "Dynamic/remote/unresolved/opaque EXEC or oversized argument list; ordered local provenance is invalidated.", node);
        }
        if (node.Variable != null)
        {
            var target = Variable(Range(node.Variable));
            var result = Observe(SqlValueFactKind.CallReturn, "ReturnStatus", node.Variable, target?.Id, null,
                "EXEC return status is an unknown call result, separate from OUTPUT and not linked to a particular RETURN expression.", false);
            Set(target?.Id, result, false);
        }
        var bindings = ImmutableArray.CreateBuilder<SqlArgumentBinding>();
        if (error == null)
        {
            // Reserve the whole binding set plus its call record, never a misleading prefix of a signature.
            var defaults = Enumerable.Range(0, plan.Parameters.Length).Count(i => !bound.TryGetValue(i, out var a) || a.ParameterValue is DefaultLiteral);
            if (facts.Count + defaults > 256 || used + defaults + plan.Parameters.Length + 1 > budget || calls.Count >= 32)
            { truncated = true; error = "Value budget exhausted; no partial argument binding is published."; }
            else
            {
                for (var ordinal = 0; ordinal < plan.Parameters.Length; ordinal++)
                {
                    var parameter = plan.Parameters[ordinal];
                    bound.TryGetValue(ordinal, out var argument);
                    var mode = argument == null ? SqlArgumentMode.OmittedDefault : argument.ParameterValue is DefaultLiteral ? SqlArgumentMode.ExplicitDefault : SqlArgumentMode.Explicit;
                    var input = mode == SqlArgumentMode.Explicit ? inputFacts.GetValueOrDefault(argument!) :
                        Fact(SqlValueFactKind.CallArgument, "SelectedDefault", argument == null ? Range(node) : Range(argument), null,
                            Expression(plan.Document!, parameter.Value), [], true, "Declared callee default selected by this call syntax; no value evaluation or coercion is performed.");
                    if (input == null || (argument?.IsOutput == true && outputs.GetValueOrDefault(argument) == null))
                    { error = "Input/output fact omitted by a value limit; no partial binding is published."; break; }
                    bindings.Add(new(ordinal + 1, parameter.VariableName.Value,
                        VariableId(plan.Document!, plan.Document!.Parse.Source.Range(parameter.VariableName.StartOffset, parameter.VariableName.FragmentLength)),
                        new(plan.Document.Uri, plan.Document.Parse.Source.Range(parameter.VariableName.StartOffset, parameter.VariableName.FragmentLength)),
                        input, mode, argument?.IsOutput == true, argument == null ? null : outputs.GetValueOrDefault(argument)));
                }
            }
        }
        if (error != null)
        {
            bindings.Clear(); Issue("value-call-binding", error, node);
        }
        if (calls.Count < 32 && Reserve(1 + bindings.Count))
            calls.Add(new("value-call-" + node.StartOffset, new(document.Uri, Range(node)), plan.Target, plan.ModuleId, plan.Revision,
                error == null ? ResolutionStatus.Resolved : ResolutionStatus.Unsupported, bindings.ToImmutable(),
                error ?? "Verified source-signature argument mapping only; runtime values, SQL conversions, successful execution and callee exit states are not proven."));
        else truncated = true;
    }
}

public sealed partial class SqlNavigationIndex
{
    private SqlValueSignature ValueSignature(DocumentSnapshot caller, ExecuteSpecification execute, CancellationToken token)
    {
        var rules = Rules(caller.Uri).Comparer;
        SqlValueSignature Fail(string error, SqlNavigationTarget? target = null) => new(target, null, null, null, [], rules, error);
        if (execute.ExecutableEntity is not ExecutableProcedureReference procedure || procedure.ProcedureReference?.ProcedureReference?.Name is not { } name ||
            execute.LinkedServer != null || procedure.AdHocDataSource != null || procedure.ProcedureReference.ProcedureReference.Number != null)
            return Fail("Dynamic, variable, numbered or remote EXEC has no supported source signature.");
        var qualified = QualifiedSqlName.From(name);
        if ((qualified.Schema == null || string.Equals(qualified.Schema, "sys", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(qualified.Name, "sp_executesql", StringComparison.OrdinalIgnoreCase))
            return Fail("sp_executesql arguments describe a runtime signature; dynamic values are not bound.");
        var range = caller.Parse.Source.Range(name.StartOffset, name.FragmentLength);
        var reference = caller.Parse.Semantics.References.FirstOrDefault(r => r.IsInvocation && r.Kind == SqlReferenceKind.Procedure && r.Range == range);
        if (reference == null) return Fail("Invocation has no verified semantic reference.");
        var resolution = Resolve(caller, reference);
        if (resolution.Status != ResolutionStatus.Resolved || resolution.Target is not { } target) return Fail("Invocation target is missing, ambiguous or unsupported.");
        var selected = Symbols.Where(s => SameAnchor(s.Target, target)).ToArray();
        if (selected.Length != 1 || selected[0].Identity.Kind != SqlObjectKind.Procedure) return Fail("Only a unique procedure source signature is supported.", target);
        var symbol = selected[0];
        var source = documents.First(d => d.Uri == target.Uri);
        var finder = new AnalysisBodyFinder(source.Parse.Source, symbol.Declaration.Range); source.Parse.Ast.Accept(finder);
        if (finder.Body is not ProcedureStatementBodyBase routine || routine is FunctionStatementBody || source.Parse.Diagnostics.Length != 0)
            return Fail("Original parsed procedure signature is unavailable or has parse errors; metadata display text is not used as a signature.", target);
        var comparer = Rules(symbol.Target.Uri).Comparer;
        if (routine.Parameters.Count > 64) return Fail("Signature exceeds the 64-parameter binding limit.", target) with { Limited = true };
        if (routine.Parameters.Select(p => p.VariableName.Value).Distinct(comparer).Count() != routine.Parameters.Count)
            return Fail("Duplicate formal parameter names prevent a unique binding.", target);
        token.ThrowIfCancellationRequested();
        return new(target, ModuleAnalysisId(symbol), AnalysisRevision(source), source, routine.Parameters.ToImmutableArray(), comparer, null);
    }

    private static string ModuleAnalysisId(IndexedSqlSymbol symbol) => symbol.Target.Uri.AbsoluteUri + "#" + symbol.Target.Range.Start.Line + ":" + symbol.Target.Range.Start.Character;
}
