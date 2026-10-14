# Structural control-flow map

Version 0.8.0 adds a ScriptDOM source map to the existing `analyze_procedure` MCP report. It helps answer which branch encloses a call/write and where exits, loops, error handlers and transaction statements appear. It is a structural map, not a complete control-flow graph or an execution trace. No hooks or additional installation are required.

Each module has `controlFlow` containing `bodyAvailable`, `nodes`, `truncated`, `issues` and `limitations`. Each call/access has `flowContext`: IDs of enclosing nodes **in that module**. Nodes have `id`, `kind`, `parentId`, `location`, optional `predicate`/`subject`, and `detail`. IDs are valid for the report's source revision; do not persist them across edits. Locations use file URIs and zero-based UTF-16 ranges. Human citations add one to line numbers.

| Construct | Observations |
| --- | --- |
| IF/ELSE | Full IF predicate and separate Then/Else statement regions. THEN requires TRUE; ELSE covers FALSE or UNKNOWN. Predicate calls are outside either branch. |
| WHILE | Predicate, loop body and nested BREAK/CONTINUE observations; no iteration count or exit-target graph. |
| Searched/simple CASE | WHEN predicates/match values, THEN/ELSE expression regions and simple CASE input. A CASE inside a value does not guard the whole statement or prove short-circuit evaluation. |
| TRY/CATCH | Associated source regions and nested statements, without exhaustive exception routing. |
| RETURN, THROW, RAISERROR | Exit/error statement locations. Bare THROW is distinguished; RAISERROR does not imply unconditional transfer. Message text is not exported as a field. |
| GOTO/labels | Statement/name locations and an explicit `unstructured-control-flow` issue; no jump binding. |
| Transactions/savepoints | BEGIN (including DISTRIBUTED), COMMIT, ROLLBACK and SAVE statements, with original name/variable previews when present. No inferred active transaction lifetime or outcome. |

For the public [payroll fixture](../samples/understanding-evaluation/modules.sql), `find_procedures("dbo.ProcessPayroll")` selects the exact dbo source, then `analyze_procedure` returns an If node whose predicate preview is `@State = 'OH'`. Its Then child encloses the `dbo.ApplyOhioRules` call. That callee's UPDATE remains in the callee's own module. The analyzer does not propagate the OH predicate or bind the caller's argument into its row filter.

Ask Claude normally:

> Explain dbo.ProcessPayroll. Which calls and writes are inside the Ohio branch? Show the exact predicate and source lines, then list early exits, error handlers and transaction/savepoint statements. Separate observed structure from runtime assumptions and report unknowns.

The bundled explanation skill resolves context IDs and reads evidence ranges. Automatic selection depends on the model. On Apple Silicon, explicitly invoke `/sqlscriptdom-lsp-osx-arm64:explain-procedure dbo.ProcessPayroll focusing on branches and transaction errors` if needed. Use your installed platform name on other systems.

Predicate/subject previews are at most 256 UTF-16 code units, remain surrogate-safe, and carry a `truncated` flag plus the **full original expression range**. Read that range before interpreting an incomplete expression. Each module has at most 256 control nodes, sharing the existing 1,000 module / 2,000 report fact budgets. Missing nodes produce visible `control-flow-limit` and module truncation. No context, an unavailable body or an incomplete map can establish unconditional execution.

Sequential reachability, dominance, fallthrough conditions after an early exit, GOTO targets and predicate truth are not computed. Statements after RETURN/THROW remain source observations. TRY/CATCH does not catch every error; CASE evaluation has runtime caveats. Transaction state/balance, implicit transactions, XACT_ABORT, isolation and actual commit/rollback success are not inferred. Row filters, parameter/value propagation and column lineage remain subsequent work. See Microsoft's [CASE semantics](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/case-transact-sql?view=sql-server-ver17) and [TRY/CATCH boundaries](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/try-catch-transact-sql?view=sql-server-ver17).

Tests cover exact predicates/ranges, nested loop/branch membership, function calls inside conditions and CASE arms, early exits without invented guards, transaction/savepoint and catch/rethrow observations, Unicode/CRLF, node/preview limits, MCP structured/text parity, and authoritative/hidden catalog bodies. Native package and marketplace installation evidence is recorded in [milestones](milestones.md). These checks establish supported facts, not superiority over Grep/Read; reviewed paired tasks remain necessary.
