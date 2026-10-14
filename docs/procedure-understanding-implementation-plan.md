# Plan to close the procedure-understanding gaps

Status: increments 1 and 2 are implemented in 0.7.0 source; see [usage and boundaries](procedure-analysis.md) and milestone release evidence. Subsequent increments remain proposed. This extends the [assessment](stored-procedure-understanding.md) around understanding complex stored procedures. Version 0.6.2 supplies static call hierarchy. Completion, semantic coloring, rename and other general M6 editor enhancements remain deferred.

## Architecture

Build a reusable procedure analysis service in `SqlLsp.Core` over the retained ScriptDOM AST and immutable navigation snapshots. Reuse existing source identity, scope, resolution, revision and cancellation rules. LSP handlers and a proposed read-only MCP adapter consume this service; neither should contain an independent SQL analyzer. Claude explains the facts and reads additional source when needed. Analysis never executes the SQL being inspected.

The report should contain:

- Exact module identity, source kind, source revision and body availability.
- Parameters, direct calls and direct relation accesses, each with operation, source span and resolution status.
- Coverage issues with locations and reasons, including dynamic SQL, unresolved/ambiguous targets, parse errors and unavailable bodies.
- Later, branch/error/transaction structure and conservative value-flow edges.
- Expansion limits, truncation and the source revisions used for every expanded module.

Keep direct observations separate from expanded dependencies. A procedure that calls another procedure which updates a table has a transitive possible write; it does not contain that UPDATE itself. A view read should retain its view identity while separately expanding supported underlying relations. An unavailable or unresolved dependency prevents claims of complete coverage.

## 1. Read/write footprints and unified coverage

This is the first implementation increment. Classify relation use through AST statement context, rather than treating every relation reference as a read:

| Construct | Required facts |
| --- | --- |
| SELECT, joins and subqueries | Relation reads; associate aliases and CTEs with their scoped sources |
| INSERT / INSERT SELECT | Target insert and any source reads |
| UPDATE / DELETE with FROM | Resolve the target alias to its relation; retain other relation reads and predicate locations |
| MERGE | Source reads and possible target actions from its clauses; do not claim every action executes |
| SELECT INTO | Source reads and creation/population of the destination |
| INSERT EXEC | Destination insert and the invoked procedure |
| Temp tables and table variables | Local identities and operations, distinct from persistent table effects |
| Dynamic SQL, remote or unresolved names | Explicit coverage issues; no guessed relation identity |

Represent insert/update/delete/create separately instead of collapsing everything into a generic write. Account for OUTPUT INTO destinations. Reuse scoped resolution for CTEs and aliases; a CTE name is not a persistent table. Opaque constructs such as remote rowsets must produce a limitation rather than disappearing. Mark trigger, synonym and runtime-generated effects as outside supported coverage unless a subsequent feature explicitly analyzes them.

Acceptance: reviewed fixtures for every supported construct, schema collisions, quoted names, nested scopes, temp objects, dynamic SQL and unavailable catalog bodies. Each expected fact includes a verified range. Tests must catch both missed effects and invented effects. Counts describe the supported analysis categories, never a percentage of all possible SQL behavior.

## 2. Deliver the report to Claude and automate the workflow

Standard call hierarchy remains the interface for calls. Do not encode table reads as calls or rely on call-item metadata that Claude's renderer may omit.

Add a small read-only MCP adapter with a proposed `analyze_procedure` tool. The initial contract accepts an explicit workspace/source context and an unambiguous module selector, returns the bounded report above, and defaults to at most two call levels and 20 modules. Ambiguous selectors return candidates instead of silently picking one. Expansion stops at cycles, unavailable sources and configured limits. These are proposed names and behavior, not tools available in 0.6.2.

[Claude plugins can bundle MCP servers](https://code.claude.com/docs/en/mcp#plugin-provided-mcp-servers). Package the adapter in the same platform marketplace archives so users retain one plugin installation. This brings forward only the higher-level analysis adapter from the optional M6 scope; it does not resume the deferred editor features. Verify the chosen MCP SDK and protocol dependencies before implementation.

If LSP and MCP run as separate processes, they must not pretend to share live buffers. Reuse the same core code and configuration, but report the actual revision each process analyzed. Initially the adapter can analyze saved workspace/catalog snapshots and explicitly identify that limitation; an unsaved-buffer bridge needs its own contract and tests. In hybrid mode, show which source was selected and any verified drift.

Bundle `skills/explain-procedure/SKILL.md` with a description covering procedure explanation, business-rule tracing and impact questions. [Claude can select relevant skills automatically, and plugin skills are also directly invokable](https://code.claude.com/docs/en/skills). The workflow should:

1. Locate the correct source/database/schema/module.
2. Obtain its report and expand only relevant callees.
3. Search for business literals such as Ohio/OH and read the governing source ranges.
4. Explain inputs, possible effects, branches and errors with file/line citations.
5. Report unknowns, limits and tool failures; use source search for unsupported cases.

Automatic skill selection is model behavior, not a guarantee. Supply a manual skill command as well. No hooks are needed for this workflow. Initially the skill can use existing LSP plus Read/Grep; once the adapter ships, update it to prefer the structured report.

Acceptance: protocol integration tests for the adapter, strict plugin validation, installed-package tests on Windows/Linux/macOS ARM64/x64, and actual Claude tool-call evidence. Test ambiguous selection, stale revisions, cancellation, unavailable tools and explicit fallback. The answer must expose coverage issues that existing LSP rendering can omit.

## 3. Control flow, transactions and errors

Extract nested IF/ELSE, CASE, WHILE, TRY/CATCH, RETURN, THROW/RAISERROR and transaction/savepoint statements with conditions and source ranges. Associate calls and relation effects with their enclosing branches. Describe possible paths, not the path taken by an actual execution. Transaction statements are observations; avoid asserting balanced commits or guaranteed rollback without a supported path analysis.

Acceptance: nested branches, early returns, loops, transaction/savepoint paths and catch/rethrow fixtures. A business-rule query must identify the predicate guarding an effect and the evidence location, or explicitly say the relationship is unresolved.

## 4. Parameter and value tracing

Start with bounded tracing of module parameters, local assignments and EXEC arguments. Map named/positional arguments only when the target signature and argument binding are verified. Then add supported temp-table/table-variable propagation. Treat full column lineage as a separate later expansion, not a prerequisite for the first useful release.

Acceptance: parameter renaming across callees, default/named arguments, output parameters, reassignment, scoped locals and unsupported expressions. Trace edges carry spans and assumptions. Dynamic values and runtime table contents remain unknown.

## 5. Search and indexing performance

Associate literal/text hits with their containing module and semantic dependencies. Grep remains useful for business vocabulary; the analyzer supplies object identity and structural context. Add compact report sections and pagination to avoid repeatedly sending whole modules to Claude.

Profile the current sequential initial scan before changing it. Introduce bounded parallel parsing and incremental reverse indexes where measurement justifies them, preserving atomic snapshot publication and overlay precedence. Cache analysis using source content/revision, dialect, resolution configuration and relevant catalog revisions, not filenames alone.

Acceptance: cold/warm measurements on the existing large-workspace fixture, edits/deletes/renames invalidating results correctly, bounded memory/concurrency, cancellation and unchanged semantic answers. Choose numeric performance budgets from reproducible baseline measurements.

## 6. Prove value and capture metrics

Start the evaluation runner alongside increment 1 so later features have a baseline. Compare fresh Claude sessions with Grep/Read against the same model, question and repository with the plugin plus Grep/Read. Use reviewed gold tasks, at least 30 representative procedures and three paired repeats; retain failures and separate cold/warm runs.

Collect client events for elapsed time, actual tool calls, failures, usage/cost where exposed and final answers. Review dependency precision/recall, behavior assertions, citations, unknown handling and correction effort. Server-only metrics cannot measure model usage or answer correctness.

Then add opt-in local server aggregates for request duration/outcome, index/cache behavior and resolution/coverage counts. Avoid SQL text, source paths, credentials and module bodies in default records; keep telemetry off LSP stdout. Hooks are optional for a later interactive collector, not required for the first controlled benchmark.

Acceptance: capture tests include startup failure/retry, timeout, missing usage fields and fallback. Publish the task definitions, scoring rules and aggregate paired results without private SQL. A proposed gate is no correctness regression and at least 20% lower median time or tokens on supported tasks; it is not an achieved claim. See the [measurement design](stored-procedure-understanding.md#measuring-value-against-grep).

## First deliverable

Ship increments 1 and 2 as the smallest useful procedure-understanding release: verified read/write footprints, visible unknowns, a structured read-only report and a bundled explanation workflow. Establish the benchmark baseline during that work. Add flow and value tracing in subsequent independently tested increments. Run relevant core/protocol/provider tests and native packaging checks before each release; use deterministic providers and owned disposable SQL Server fixtures while no approved external test server is available.
