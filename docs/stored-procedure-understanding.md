# Understanding complex stored procedures: assessment and proposal

The product's main job is to help a developer understand a complex procedure and answer questions such as "where is Ohio handled?". That requires reliable evidence about dependencies and behavior, followed by an explanation. A language server supplies verified source facts; Claude interprets those facts. The plugin itself is deterministic C# and does not call an AI model or execute analyzed SQL.

For implementation order and acceptance tests, see the [procedure-understanding implementation plan](procedure-understanding-implementation-plan.md). The first increment's [analysis tools and explanation workflow](procedure-analysis.md) are implemented in the 0.7.0 source; release/client evidence is recorded in milestones. Flow/value tracing, indexing improvements and server telemetry remain subsequent work.

## Current foundation and remaining gaps

Existing strengths are SqlScriptDOM parsing, declaration locations and signatures, scoped static references, unsaved changes, source-aware workspace/catalog navigation, and conservative handling of ambiguous targets. Version 0.6.2 adds the three standard call hierarchy methods, grouped static call sites, procedure/function traversal and per-item partial-coverage information. The remaining general M6 editor enhancements stay deferred.

| Priority | Capability | Current state | Why it matters for understanding a procedure |
| --- | --- | --- | --- |
| P0 | Incoming/outgoing procedure and function calls | Added for supported static calls in 0.6.2; client should traverse with depth/size limits | Establishes entry points, callees, recursive paths and boundaries where logic moves into another module |
| P0 | Table/view reads and writes | 0.7.0 adds scoped operation footprints and bounded view/callee expansion; runtime/implicit effects and CTE/view write binding remain partial | Distinguishes lookup reads from DML effects and preserves direct versus transitive possibilities |
| P0 | Honest coverage report | 0.7.0 MCP output includes per-module issues, body availability, revisions, expansion limits and static limitations | Empty results cannot establish no dependencies; missing/dynamic/ambiguous sources stay visible |
| P1 | Control flow, errors and transaction map | AST is retained; structured IF/CASE/loop/TRY-CATCH/transaction/return summaries are missing | Shows possible branches, early exits, rollback/error paths and the locations that govern Ohio-specific behavior |
| P1 | Parameter and data-flow tracing | Parameter/local declarations are available; assignments, argument mapping, column lineage and cross-module propagation are missing | Traces state/region values through variables, temp tables, filters and called procedures |
| P1 | Automatic explanation workflow | 0.7.0 bundles explain-procedure, using MCP analysis plus LSP/Read/Grep; automatic selection remains model behavior | Developers can ask business questions or explicitly invoke the skill |
| P1 | Robust large-workspace indexing | In-memory indexing works; startup variability and sequential initial scans remain | Makes repeated investigation practical in large repositories rather than paying a long cold-start cost |
| P2 | Full-text discovery combined with semantic verification | Grep and LSP are separate tools; unified relevance/path search is missing | Finds literals such as Ohio, OH and region codes, then associates each hit with its module and verified dependencies |
| P2 | Structured source context and evidence export | 0.7.0 provides bounded structured module reports; further compactness/pagination is future work | Keeps file/range evidence and deployed-vs-local identity clear |
| P2 | Measured product value | A paired client evaluation runner exists; a reviewed representative benchmark and server telemetry remain outstanding | Establishes whether developers get more correct answers with less time, effort and usage |

Do not prioritize completion, semantic coloring or rename for this goal. First add read/write footprints and unified coverage, then control-flow/argument tracing, and then a reusable explanation workflow. Dynamic SQL, runtime branches, triggers, synonyms, cross-server targets and inaccessible catalog modules remain explicit coverage gaps rather than inferred facts. Static analysis alone cannot prove which branch ran or the contents of live data.

## Automatic developer workflow today

After installing the plugin, a developer can ask:

> Explain dbo.ProcessPayroll, focusing on how Ohio payroll is handled. Locate the procedure, inspect its outgoing calls and incoming callers, and expand relevant callees up to two levels. Read the actual definitions. Show the inputs, table reads/writes, Ohio-related conditions, transactions/error handling, and open questions. Cite file paths and lines for each conclusion. Distinguish verified LSP locations from text interpretation and leave unsupported dynamic SQL unresolved. Do not execute SQL.

With 0.7.0, Claude can use the bundled skill and MCP analysis for verified module locations, operation footprints and visible coverage issues. It reads relevant bodies and uses text search for business literals/aliases. Control-flow and value-flow descriptions still come from source reasoning. An answer should include bounded dependencies, a source-cited behavior narrative and explicit unknowns; it should not claim complete impact analysis. The LSP/Read/Grep instructions below also work with older releases.

To make that workflow a project default, a team can add this short instruction to its own `CLAUDE.md`:

```markdown
When explaining T-SQL stored procedures, locate the correct schema/database/source first.
Use the SQL LSP tool for definitions, outgoingCalls and incomingCalls when available.
Expand only relevant callees: at most two levels and 20 modules; stop at cycles.
Read the verified source locations. Use Grep for business literals and unresolved SQL.
Explain inputs, reads/writes, branches and transaction/error handling with file:line citations.
Label facts from LSP, interpretations from source, and unknown/ambiguous dependencies.
If LSP fails, report the failure and fallback explicitly. Do not execute analyzed SQL.
```

[Claude reads project instructions at session startup](https://code.claude.com/docs/en/memory). They guide model behavior rather than guaranteeing tool selection; verify actual calls. No hooks are required for this workflow. This repository does not alter a developer's personal project instructions automatically.

The 0.7.0 bundled skill guides procedure explanation and impact questions using the structured analysis adapter. It supplies Claude instructions; the server contains no AI model. Standard LSP calls remain calls, while MCP carries relation footprints and coverage. Deterministic control-flow/value tracing remain future increments.

## Measuring value against grep

The hypothesis is **better verified procedure understanding with lower effort**, not simply more matches or faster raw search. A broad grep candidate list is not a fair baseline for a finished Claude explanation. Use the same model, effort, repository snapshot, question and answer format in both arms:

1. Baseline: Claude uses Grep/Read and normal source reasoning, with LSP unavailable or explicitly prohibited.
2. Treatment: Claude has the plugin and the workflow above, with Grep still allowed for business text and unresolved cases.

Use at least 30 representative reviewed procedures and three paired repeats per question; alternate/randomize arm order and start fresh chats. Include deep call chains, schema collisions, repeated call sites, quoted identifiers, temp tables, views, complex writes, transactions, dynamic SQL and missing/deployed-only sources. Record cold-start tasks separately from warm sessions. Freeze configuration, module limits, case/database defaults and inclusion of declarations. Count timeouts and unavailable LSP sessions as failures, not removed samples.

Human reviewers should create gold locations, dependency/side-effect sets and key behavior assertions before the runs. Hide gold answers from Claude. Use blinded review where practical, and score unsupported cases separately from resolvable tasks.

| Metric | What to capture |
| --- | --- |
| Correct entry-point identity | Exact source/database/schema/procedure selection |
| Dependency precision/recall | Correct callee/read/write identities and call locations, with consistent granularity |
| Behavior accuracy | Gold assertions such as the actual Ohio guard and the write it controls; count wrong claims |
| Citation correctness | Percentage of factual assertions supported by the cited source locations |
| Unsupported-target handling | Dynamic, ambiguous and unavailable cases honestly left unknown; false "complete" answers count as errors |
| Developer time/effort | Median and p95 time to an accepted answer, follow-up corrections and files manually inspected |
| Model usage | Input/output tokens, cache usage and reported cost for the full task; reported cost is not necessarily subscription billing |
| Tool usage and availability | LSP/Grep/Read calls, failures, fallback rate and modules visited |

Report paired differences and uncertainty, with results by task class and all failures included. A reasonable initial acceptance proposal is no drop in behavior/citation correctness and at least 20% lower median task time or token usage on supported tasks. Treat this as a proposed target, not an achieved result. A speed improvement accompanied by missed writes or false dependency claims is not a win. The existing 10,001-file startup variability means cold tasks may favor grep.

## How to capture metrics

Use [the external evaluation runner](procedure-analysis.md#measure-against-grepread). It correlates structured CLI tool-use/result IDs, counts tool types/failures, records elapsed wall time and collects final usage where exposed. Aggregate reports omit SQL/transcripts; accuracy columns require independent review. Existing capability smokes remain separate checks with restricted tools and fixed prompts.

Then add **opt-in server telemetry** for method name, duration, success/cancellation, indexed-file counts, cold/warm state, cache hits and resolved/ambiguous/missing/unsupported counts. Write compact aggregates to a chosen local file or stderr, never LSP stdout. Avoid SQL text, business literals, paths, credentials and module bodies in default metric records. Current release does not implement this telemetry switch.

Server telemetry alone cannot measure Claude tokens/cost, Grep calls, developer understanding or answer correctness. Those need client events and reviewed gold tasks. Optional PostToolUse-style hooks could observe client tools for interactive measurement, but they are a later opt-in design; the shipped plugin currently installs no hooks and does not send telemetry anywhere. An external runner is the clearest first implementation because it controls both experimental arms and avoids changing ordinary developer sessions.

An actual 0.6.2 Windows Claude smoke illustrates this distinction: an incoming-call file sync failed during client startup, then a retry returned the correct graph. Both operations ultimately worked, but server request timings alone would miss the failed client attempt. [The evidence](evidence/claude-call-hierarchy-windows.json) records the failure instead of presenting only successful requests.

Suggested report columns:

```text
task_id, repo_revision, arm, model, cold_start, elapsed_ms, lsp_calls,
grep_calls, read_calls, failures, input_tokens, output_tokens, reported_cost,
dependency_precision, dependency_recall, behavior_score, citation_score,
unsupported_handling_score, reviewer_corrections
```

For a five-minute capability check rather than a product benchmark, use [the small evaluation fixture](evaluation.md). For call hierarchy coverage and freshness rules, see [the call hierarchy contract](call-hierarchy.md).
