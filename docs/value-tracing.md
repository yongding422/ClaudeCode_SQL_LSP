# Bounded parameter and value dependencies

Version 0.9.0 adds `valueFlow` to each module in the existing read-only `analyze_procedure` report. It connects verified parameter/local identities, source definitions, variable reads and EXEC formal arguments. It does **not** evaluate SQL, compute runtime values, type-check/coerce arguments, solve loops or provide full column lineage. No hooks, new dependencies or separate installation are added.

Version 0.10.0 adds an optional [compact presentation](compact-reports.md) with shared evidence, ready-to-cite one-based lines, module references and bounded lexical value focus. The original full DTO below remains the default; Core dependency semantics are unchanged.

Ask Claude:

> Trace @State through this procedure's local assignments and called procedures into conditions and filters. Show exact argument bindings, reassignment alternatives, selected defaults and source lines. Separate possible sources from known runtime values, and report output/loop/dynamic-SQL uncertainty.

On Apple Silicon, explicitly select `/sqlscriptdom-lsp-osx-arm64:explain-procedure dbo.RoutePayroll tracing State into Region` if automatic skill selection does not occur. Substitute your installed platform name as needed. Save source changes before analysis; MCP still has an independent saved/catalog snapshot rather than LSP's unsaved buffers.

## Report contract

| Field | Meaning |
| --- | --- |
| `variables` | Module-local IDs, original name/declaration range/type, parameter ordinal, OUTPUT flag and presence of a declared default. Local binding reuses the semantic extractor; labels on named EXEC arguments are not caller variable reads. |
| `facts` | Parameter entry/default observations, declarations, SET/SELECT definitions, condition/filter reads, EXEC inputs/copy-back/return status and RETURN observations. Each has source ranges, bounded expression preview, input dependencies, target variable where applicable, control-region IDs, status and explanation. |
| `inputs[].sourceFacts` | IDs of possible preceding definitions **within that module**. `sourcesComplete` means supported source tracking, never a known value or guaranteed execution. False or missing sources must remain uncertain. |
| `calls` | EXEC site, selected target identity/source, target module ID/revision, whole-call `bindingStatus`, ordered formal bindings and explanation. Function-call argument binding remains unsupported. |
| `bindings` | Formal ordinal/name/declaration/variable ID, caller `inputFactId`, Explicit/ExplicitDefault/OmittedDefault mode, OUTPUT request and optional caller output fact. Formal anchors can be available even when the target module was not expanded. |
| `issues`, `truncated`, `limitations` | Unsupported binding/expression/control behavior, unavailable source, budgets and assumptions. They are part of the result, not proof of complete coverage. |

IDs are local to the module and revision. Target formal IDs belong to the selected target's module/revision; do not join identically spelled variables across modules. A selected signature can be inspected without expanding its body facts. Missing/truncated target facts do not invalidate a verified source anchor, but they prevent claiming a complete value path.

For [RoutePayroll](../samples/value-tracing-evaluation/modules.sql), the entry State feeds Region's declaration. An IF can replace Region with CA. The call input retains both possible definitions and maps to ApplyRegion's Region formal; the callee WHERE reads that formal identity. The analyzer does not choose a branch, propagate a caller's concrete value into a shared callee report, or prove which rows match.

## Supported and conservative behavior

Simple scalar variable/literal declarations and `SET =` definitions replace the previous source along a supported sequential path. IF/ELSE merges retain alternatives, including the incoming source when ELSE is absent. Branch correlations, dominance and runtime reachability are not solved. Compound/complex/UDT/cursor assignments are recorded with uncertain provenance rather than evaluated. RETURN is observed without connecting it to an arbitrary caller result.

SELECT assignment can retain a previous value on zero rows and has unspecified row/evaluation ordering. Variables written by a SELECT therefore have uncertain sources throughout that query, including its filters, and after it. WHILE invalidates ordered certainty before body/predicate observations and after the loop; TRY/CATCH invalidates it at catch entry and after the construct. GOTO disables ordered source certainty across the whole module. FETCH, RECEIVE, opaque/dynamic calls and unsupported output writes also preserve explicit unknowns.

EXEC named/positional arguments bind only to a uniquely selected procedure with an available parsed signature. Named order, duplicates, excess arguments, required omissions, DEFAULT availability and OUTPUT formal/caller-variable requirements are checked. Invalid argument lists publish **no partial verified formal bindings**. Hidden/encrypted metadata display text is not parsed as a signature; hybrid selection and catalog-renamed identities remain authoritative.

Opaque EXEC argument facts retain direct variable dependencies but suppress literal payload/default previews. Dynamic SQL is not parsed or inferred from an exported argument string. Ordinary declaration/assignment/default source previews remain part of this explicitly requested analysis; they are not telemetry records.

All explicit call inputs observe the pre-call state. An OUTPUT formal is also an input. Copy-back and EXEC return status get separate unknown definitions; aliasing multiple OUTPUT arguments does not select the last textual argument as a known value. Callee exit definitions, successful execution, actual values and SQL conversions remain unproven. Omitted/explicit DEFAULT observations point to the callee's original default expression, which is separate from its symbolic entry input.

WHERE, HAVING, JOIN, IF/WHILE and CASE facts expose bound variable reads, not bound columns, predicate truth or runtime row contents. Global variables, function argument boundaries, temp/table-variable value contents and full column lineage remain outside this increment. Do not compose uncertain or unrelated branch edges into a proven end-to-end path.

## Bounds and verification

Each module allows at most 128 variables, 256 value facts, 32 call sites, 64 signature/explicit arguments per call, 16 variable occurrences per expression and eight possible source IDs per input. Value records/bindings share a 512-item value budget within the existing 1,000 module / 2,000 report budgets. Expression previews are at most 256 UTF-16 units, surrogate-safe, and retain full original ranges. Limits produce visible issues/truncation; IDs never point to omitted caller facts or variables.

Core golden tests cover source replacement/merging, scope/case/duplicates, argument/default/OUTPUT binding, SELECT retention, loops/errors/jumps, expression and source limits, Unicode ranges, cancellation and expansion boundaries. Independent MCP process tests check actual structured/text payloads; deterministic hybrid/catalog and owned disposable SQL Server tests verify selected/renamed/hidden source behavior. Publication and actual client evidence are recorded separately in [milestones](milestones.md).

[Native release evidence](evidence/value-tracing-ci.json) records all nine build/test jobs and all four actual marketplace installations passing, including both Mac architectures. [Windows model evidence](evidence/claude-value-tracing-windows.json) verifies real formal/filter links while retaining the earlier timeout and observed answer errors. Tool and installation success do not guarantee model explanations or citations.

The [six public evaluation tasks and source-reviewed assertions](evaluation/value-tracing-gold.md) expand the previous single smoke fixture. They still need independent review and representative paired runs before any superiority claim. Tool success, measured tokens/cost and model answer correctness are separate measurements. Official semantics: [EXECUTE arguments and OUTPUT](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/execute-transact-sql?view=sql-server-ver17), [SELECT variable assignment](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/select-local-variable-transact-sql?view=sql-server-ver17).
