# Benchmark-driven SQL understanding improvement plan

Date: 2026-10-17. Status: proposed implementation backlog; no performance fixes or superiority claims are implied by this plan.

Delivered alongside this plan: the [rerunnable maintainer skill](../../.claude/skills/benchmark-sql-plugin/SKILL.md), a preparation/execution/review/comparison controller, and validation against the frozen corpus. R1–R7 product changes remain planned. The skill is available as a Claude Code project skill and an installed Codex skill.

The objective is useful, accurate stored-procedure investigation with less developer/model work. Keep the existing read-only, offline-first ScriptDOM architecture and independent MCP/LSP adapters. General IDE milestone M6 remains deferred. Do not add hooks, a mandatory daemon, automatic SQL execution or an automatic database connection.

The [48-session result](../evaluation/results/sql-server-samples-20261017.md) remains the released **0.14.0 baseline**. Retain its raw evidence and frozen rubric. Build a separate candidate artifact and new study for each measured change; never rewrite the baseline to make a fix look better.

## Evidence, uncertainty and coverage

| Finding | Evidence | Required response |
|---|---|---|
| Discovery appends excessive diagnostics | `ProcedureTools.FindProcedures` includes workspace `SourceParseIssues`; one match has about 1 KB of metadata and 216 KB of diagnostics; 41 client overflows | R1: scoped summaries, explicit pagination and serialized-size budgets |
| Fresh workspace initialization is expensive | 24 cold initializations: mean 9.95 s, median 9.98 s | R0/R3: instrument stages, measure existing live-process reuse, then improve cold/restart paths |
| Report persistence does not restore the parsed workspace | `WithIndex` initializes sources before cache use; `SqlAnalysisCacheContext` explicitly does not serialize ASTs | R3: separate a materialized discovery index from report caching and on-demand verified parsing |
| Most treatment sessions did not get supported analysis | 5/24 sessions obtained Resolved module analyses; one extra got Unresolved for a script statement | R4: direct file/position path, compact results, bounded recovery and selective tool use |
| Workspace contains unrelated database applications | Whole corpus shares the configured root; actual reports include relation ambiguity | R2: explicit project/database contexts and scoped resolution |
| Four copied files are not explained by the reported index count | 1,099 copied SQL files versus 1,095 indexed documents | R0/R2: account for each input; diagnose rather than guessing the reason |
| Some answers still misread source/report evidence | Dynamic CREATE PROCEDURE misclassified; THROW state confused with severity; absence of calls overinterpreted | R5: stronger facts, limitations, citations and evaluation of interpretations |
| Quality-drop causality is not isolated | Small selected suite, two repeats, implementing-agent grading, baseline-first order | R6: ablations, matched reruns and independent/blinded review when available |

The diagnostic list is **not literally unlimited**: `MaxAnalysisFacts=1000`. The bug is inappropriate scope and budgeting for a tool response; `maxResults` limits modules only. Diagnose the four-file gap and the six non-overflow tool errors before assigning them to a particular component.

## Ordered delivery and gates

| Slice | Work | Dependency | Exit evidence |
|---|---|---|---|
| R0 | Preserve benchmark; add stage/coverage instrumentation and rerunnable skill | None | Repeatable preparation, candidate fingerprint, complete accounting, no invented grades |
| R1 | Repair discovery response contract and overflow recovery | R0 | Actual Claude client accepts all discovery responses on frozen corpus |
| R2 | Scope workspaces and make coverage/freshness explicit | R0; integrate with R1 | All 1,099 inputs accounted for; unrelated project names do not silently bind |
| R3a | Improve current-session reuse, refresh and cold scheduling | R0/R2 | Stage measurements improve; no stale answers or duplicate full refreshes |
| R3b | Persist reusable discovery/index facts; parse selected sources on demand | R3a and cache ADR/spike | Restart reduces parsing without weakening freshness/resolution |
| R4 | Use compact, task-appropriate analysis with bounded calls | R1/R2; benefits from R3 | Normal procedure workflow reaches useful evidence without compulsory discovery |
| R5 | Strengthen semantic evidence and interpretation tests | R1/R2; refine during R4 | Named regression cases have correct facts and explicit unknowns |
| R6 | Run ablations, fresh matched benchmark, then realistic workload cohorts | R1–R5 as available | Report quality and resource tradeoffs, including regressions |
| R7 | Package and publish a verified candidate | R6 correctness/usability gates | Four-platform packaged tests and marketplace load smoke pass |

Use independently reviewable commits/PRs. Run relevant tests after each slice and the complete build/test suite before a release. A payload-only candidate should be measured before combining it with indexing changes, so its effect is observable. Reuse/scope/routing/semantic slices remain separate if any needs more work.

## R0 — Establish trustworthy measurement and diagnosis

1. Preserve source, rubric, task and original result hashes. Record candidate **binary/package hashes**, build commit/dirty provenance and actual manifest version; a source checkout or version number alone is not proof of the code being run.
2. Split startup into enumeration, file read/decoding, parse/extraction, context binding, index construction, watcher reconciliation, report-cache validation and presentation. Record parse counts and bytes by scope, not just elapsed time. Preserve queue time separately; do not add overlapping measurements as if serial.
3. Add a bounded file ledger: discovered / indexed / parse-error-but-indexed / excluded-size / excluded-policy / unreadable / failed / pending. Each discovered file has one terminal inclusion status or an explicit pending state; parse-error status can be an attribute of an indexed file. Do not leak raw source into normal metric records.
4. Correlate tool request IDs, actual serialized/wire bytes, oversized/rejected results, denied/malformed arguments, analyzer support status and fallback causes. Keep backend time separate from client final-answer time, provider usage and correctness.
5. Diagnose the missing four files with deterministic local scans; reproduce each exclusion/failure in a fixture. Classify the other six tool errors from transcripts, including any wrong-path requests. Do not treat every client error as a parser error.
6. Deliver `$benchmark-sql-plugin`: preflight, freeze, execute, grade-template, review/report and historical comparison. Preflight/dry-run do not invoke the model. A real invocation uses fresh SQL-only workspaces, existing credentials via environment and explicit candidate artifacts.

Primary areas: `scripts/evaluate-understanding.py`, `scripts/prepare-sample-study.py`, `scripts/report-sample-study.py`, `SqlLsp.Mcp/PluginMetrics.cs`, `SqlLsp.Core/DocumentStore.cs`, `WorkspaceFiles.cs` and `SqlLsp.Server/ServerState.cs`.

Exit: no unaccounted source files or telemetry categories in the diagnostic fixture; benchmark controls validated; every quality score has explicit review notes, and missing usage remains unknown.

## R1 — Make tool responses small and complete about their limits

- Keep discovery focused on identities, signatures, source/selection, revision, scope and coverage summary. Return diagnostic **counts and affected-file counts**, not the full workspace issue list on each name lookup. A narrow query must not inherit hundreds of unrelated issue records.
- Budget module results and diagnostics independently, then enforce an overall serialized result budget. Initial target: discovery's **complete JSON-RPC result frame at most 16 KiB** on the reference corpus. Use a smaller internal content budget with headroom for structured/text duplication, escaping and SDK envelopes; verify the actual transport rather than equating `json.Length` with bytes or tokens.
- Use deterministic ordering and an opaque cursor bound to query, scope, sort key, index generation and options. A source-generation change invalidates the cursor explicitly. Expose returned/total counts, `hasMore`, reason for truncation and next cursor. Never truncate serialized JSON or source spans mid-value.
- Make issue details explicitly requested, scoped and paginated. Prefer extending the existing discovery interface initially; evaluate a separate diagnostics operation only if the interface becomes confusing. `get_metrics` must remain cheap and must not initialize a SQL index.
- Preserve availability/partial/coverage warnings in the smallest response. If even one identity exceeds the budget, return an explicit bounded recovery outcome with a selection handle/path reference; do not silently claim zero matches.
- Permit exact `documentUri` + UTF-16 position/revision analysis when the file is known. A discovery error must not force a repeated identical query or an attempt to open a giant single-line dump.
- For analysis responses, keep the compatible full format explicitly selectable and budget it too. Introduce a versioned compact default for the plugin-facing workflow; provide further pages/expansion for omitted evidence. List counts and incomplete coverage visibly.

Tests: hundreds/thousands of parse issues, 0/1/many matches, long Unicode paths/identifiers, repeatable pagination, changed-generation cursors, cancellation, structured/text serialization, meaningful truncation, and real packaged MCP/Claude discovery. The 41-overflow corpus becomes a permanent regression fixture or deterministically generated equivalent.

Exit: **zero response-size overflows** in the frozen 48-session rerun, no malformed JSON/citation loss, no hidden issue suppression, no retry of an identical oversized response. Latency/token improvement is measured, not assumed from response bytes.

## R2 — Resolve within a source context and report coverage honestly

- Add explicit offline project scopes: root/include/exclude plus stable context ID, optional database/default schema and configured comparison rules. Use `.sqlproj` and static `USE` statements as evidence where supported, not as a license to execute project scripts or evaluate arbitrary MSBuild imports.
- Adopt nearest supported project context when unambiguous. Explicit mappings win; overlapping roots, file-to-project membership and unknown projects remain visible. Do not treat all sample applications as one SQL database.
- Scope lookup by the requested file/project/database. Same-named objects in unrelated contexts remain separate. Cross-project/database resolution requires an explicit mapping or sufficiently qualified, verified identity; preserve candidate ambiguity otherwise.
- Include scope ID, active source generation and relevant coverage counts in results. A negative answer says what was searched and excluded. Keep SQLCMD variables, unsupported syntax/dialects, unreadable files, encrypted/external modules and dynamic SQL visible as limitations.
- Make source decoding, byte-size limits and path handling consistent across copy/index/analysis. Preserve symlink/reparse protections and test Windows/macOS/Linux path and case behavior. A valid UTF-16 span must continue to refer to the exact decoded revision.
- Reconcile **all 1,099 copied files**, not just the files relevant to passing questions. Expected exclusions are acceptable only when accurately counted, reasoned and surfaced.

Tests: identical `dbo.Product` in two projects, explicit cross-database call, `.sqlproj` membership/overlap, bracket/collation rules, `USE`, SQLCMD fragments, BOM/UTF-16, long paths, inaccessible/deleted/renamed files, watcher overflow and linked paths.

Exit: unrelated sample projects do not create false matches for scoped requests; real ambiguity stays visible; every missing file has a reason. No broad completeness claim from a partial index.

## R3 — Reuse work without returning stale analysis

### R3a: exploit and improve existing live-process behavior first

`Program.cs` already owns one `ProcedureTools` instance, and `WithIndex` retains its `ServerState`. Do not add a daemon to implement reuse that already exists. Measure multiple requests in the **same** MCP process first, including an edited file and an added competing declaration.

Profile full rescans and per-request `NavigationAsync` reconstruction. Investigate startup scan/watcher reconciliation overlap as a hypothesis; retain the existing startup-gap protection until an equivalent race-safe mechanism passes tests. Introduce immutable source-generation snapshots and single-flight builds; publish a generation atomically and reuse it while relevant inputs are unchanged. Cancellation of one waiter must not corrupt shared state or block unrelated clients indefinitely. Keep memory/parse concurrency bounded.

### R3b: persist a materialized discovery index

The current report cache requires a validated current index before lookup and does not serialize ASTs. Enabling it cannot by itself remove cold workspace initialization. Use an ADR plus a compiling spike to choose versioned bounded snapshot files or SQLite; add a dependency only after locked restore/build/package validation. Treat SQLite as an option, not an assumed existing feature.

Persist file inventory/hashes/decoding, scoped declarations and selection spans, references/dependency candidate sets, diagnostic summaries and supported extracted facts. Do not serialize ScriptDOM AST implementation objects or deserialize executable/runtime types. Discovery should be served from verified materialized facts; load/parse the selected module and required dependencies on demand for richer analysis. This requires a real materialized-index/query interface: do not manufacture empty ASTs to satisfy today's `DocumentSnapshot` contract.

Cache identity includes canonical physical root, project/database mappings, file content hashes, parser package/dialect/options, resolver/format versions and comparison rules. Validate current files before serving facts. Metadata-only size/mtime checks are insufficient for verified persisted reuse: cover same-size/same-time edits, replacements, additions, deletions and case changes. Return a labeled stale/refreshing status or wait; never silently return old facts as current. Added declarations can change ambiguity and must invalidate affected candidate sets, not just previously selected callees.

Use atomic generation publication, bounded storage, eviction and writer coordination. Multiple processes can read immutable generations; interrupted/corrupt/incompatible snapshots fall back to bounded rebuild. Persisted reuse is opt-in with clear local-storage disclosure. Preserve existing disabled starters and user settings. Keep online catalog caching governed by explicit profiles, permission/source stamps and freshness policy; offline benchmarking never gains an implicit connection.

Tests: fresh process cache hit, changed caller/callee, added competing definition, deleted/renamed file, parser/config/scope version change, corrupt/partial snapshot, read-only folder, concurrent readers/writers, cancellation, relocation and source permission loss. Verify identical facts/citations against a fresh full parse, not just faster timings.

Initial engineering goals on recorded reference hardware (not current results): same-process discovery p95 <250 ms, verified index restore plus discovery p95 <2 s on the 1,099-file corpus, and at least 50% reduction in observed ~10 s cold initialization without hiding validation time. Revise targets openly if correctness-preserving measurements show a different practical limit.

## R4 — Make analysis proportional to the developer's question

| Request | Preferred workflow |
|---|---|
| Exact text/literal or a short known script statement | Read/Grep directly; do not force module discovery |
| Known procedure/file and structural question | Direct scoped compact analysis; discover only if identity/position is missing |
| Procedure name with unknown location | One scoped discovery page, then compact analysis with revision |
| Calls, caller impact or cross-file data effects | Scoped dependency evidence with bounded expansion and explicit unresolved edges |
| Large business-rule investigation | Relevant call/branch/effect/value facts, then targeted source reads and selective expansion |

Extend the direct path with a validated file + declaration selector if needed; do not make the model guess a line number or bind an arbitrary overload. Keep the existing exact-position path supported. Prefer `maxDepth=1` with a small module budget for narrow requests; increase only when the question requires it. Reuse module/revision references already present in a report.

Keep tool descriptions concise and measure their client context cost. Make the `explain-procedure` skill choose the useful path instead of always requiring `find_procedures`. The experiment observed **zero Skill and zero LSP calls**, so changing skill wording alone will not fix direct MCP use. Improve tool descriptions, defaults and result recovery fields too. No mandatory hooks or LSP process startup for MCP analysis.

After an unsupported/oversized response, use one appropriate recovery path or a clearly labeled fallback; do not repeat unchanged expensive requests. Verify actual source ranges for business arithmetic, transaction/error behavior and ambiguous dynamic DDL. Emit a concise, citeable explanation with relevant unknowns rather than a structural-data dump.

Measure three questions separately: plugin **available** with natural selection, plugin **prescribed** by workflow, and forced tool use for diagnostic ablation. Never hide extra context cost by removing real tool descriptions from only one arm.

Exit: routine supported module questions reach usable compact evidence without dumping files; simple controls do not require plugin calls; normal cases need at most one discovery and one analysis before relevant source verification. These are routing objectives, not a ban on legitimate additional investigation.

## R5 — Improve evidence and reduce interpretation errors

1. Model string-contained module definitions separately from runtime invocation edges. A dynamic `CREATE PROCEDURE` may install a future caller; executing the DDL is not immediate execution of the installed body.
2. Expose `THROW` error number/message/**state** with named fields, distinguish new-error versus rethrow, and retain source evidence. Do not label the third argument severity. Add a conservative observation for RETURN after unconditional THROW, without pretending to solve arbitrary SQL control flow.
3. Preserve SELECT-assignment retention/unknown row cardinality and loop uncertainty in compact output. Do not collapse an unknown/no-row possibility into a known NULL value or ten distinct fulfilled orders.
4. Represent transaction/sequence observations accurately: allocation before BEGIN TRAN, sequence consumption not restored by transaction rollback, unknown ambient transactions and unreachable error-return statements. Do not infer that failed invoicing consumed no sequence values.
5. Keep direct table DML, expanded callee effects, temporal/trigger possibilities and external/opaque bodies separate. An empty calls/accesses list means no extracted facts under current coverage, not proof of no effects. Explicitly distinguish modeled facts from unmodeled engine effects; add trigger/history metadata support only with verified source identity and tests.
6. Preserve complete relevant expressions and exact citations for price/JSON business logic. Add focused regressions for the literal percentage formula and OPENJSON case-sensitive matching. Prefer source-supported interpretation to intended-business-rule guesses.
7. Test analyzer fact accuracy separately from end-to-end model correctness. Correct extraction does not guarantee a correct natural-language answer; deterministic output fields and focused source evidence can reduce ambiguity but cannot guarantee model reasoning.

Add fixtures to `SqlLsp.Core.Tests`, `SqlLsp.Mcp.IntegrationTests` and relevant LSP parity tests. Include negative and partial cases, unchanged existing DTO clients, full/compact equivalence and known fact-budget truncation. Runtime/engine claims not inferable from local syntax require authoritative semantics or explicit unknowns; no live user DB is required to test these source facts.

Exit: correct and citeable structural facts for every named regression, honest remaining limits, and improved reviewed answers on frozen tasks. Do not add a confidence number that has not been calibrated against correctness.

## Configuration and interface compatibility

Keep user-facing behavior in validated configuration. Extend `.sqlscriptdom.json` through a documented schema version/compatibility decision for response budgets, offline project scopes, index persistence and retention. It currently configures storage only; source/database settings remain selected by `--config`. Define clear precedence and reject contradictory duplicate source settings rather than silently creating two competing configurations. A plugin settings change must not auto-enable DB access, persistent source storage, metrics or hooks.

Keep existing MCP methods and positional arguments valid. Add optional query scope/cursors/selectors and version response formats explicitly. For compact defaults, provide explicit `detail=full` compatibility and document whether a new client-facing contract version is required. Retain full information through deliberate pagination/expansion, not a huge mandatory result. Bind cursors/selection handles to source generation and exact context.

## R6 — Prove fixes under controlled comparisons

### Immediate rerun

Use the same 12 frozen tasks, source hashes, gold, model/effort, 450-word instruction, timeout, repeat count and two-condition baseline-first schedule as 0.14.0. Run a **fresh Grep baseline** with the candidate; the historical baseline is descriptive context, not a substitute for current controls. Verify actual candidate artifact hashes, loaded version, MCP connection and tool calls.

Maintain raw evidence and all failures. Grade from a blank template, never copy old answer grades. Check citations and unsupported claims. Blinded/independent review is preferable where available; disclose the implementing-agent review otherwise. Report means, medians, tail observations, paired per-question outcomes, uncached input, cache-read/write, total input, output, reported costs, tool calls, support/coverage and startup/fallback time. Use conventional midpoint medians consistently across native/Python importers or document any remaining difference.

### Diagnostic ablations and realistic cohorts

Measure payload-only, scope-only, reuse-only and routing changes separately where practical, with no altered gold. Server-only probes isolate enumeration/parse/query/serialization costs without model latency. Native process reuse and report cache hits are distinct groups.

Add separately labeled modes after the cold rerun works: **cold empty index**, **fresh process with persisted verified index**, **same live MCP process**, and **one-file edit plus dependency invalidation**. Include build/prewarm/validation cost and a realistic session total; do not compare warm plugin work against cold Grep without labeling it.

Preserve the original 12-task suite. Create additional frozen cohorts for simple literal lookups, long/multi-hop procedures, cross-project ambiguity, incomplete/dynamic/external SQL and edited-source freshness. Define new gold before model runs; do not select only questions the plugin wins. Use task-level grouping for related procedures. A new provider/model is a separate matched experiment. Codebase-memory would be a separately configured third arm with verified tools/version/coverage, not a proxy for Grep.

### Decision gates

- Correctness/usability release gates: no known stale/cross-project false bindings, no overflow on ordinary discovery, complete source accounting, valid citations/unknowns and no protocol regression. All deterministic tests pass.
- Initial product-improvement target: on the structural/complex-procedure cohort, **at least 20% lower total reported input or 20% lower final-answer time**, at factual/citation quality at least as good as its matched Grep arm. Treat this as an engineering target, not an achieved result or statistical proof.
- Simple-control target: adaptive plugin availability should keep median latency/token overhead within 10% of matched file-tool baseline. Publish failures to meet it rather than forcing simple tasks through analysis.
- Increase repetitions/holdout tasks before a general marketing claim; report uncertainty and provider/cache/order limits. If the target is unmet, keep Grep as the default for that task class and state the measured niche, if any.

## R7 — Release and rollout

Build/test the unchanged LSP contract plus new shared-core/MCP paths. Verify Windows x64, Linux x64, macOS arm64 and macOS x64 native archives, dependency locks, checksums, executable permissions, packaged skill/config schemas and actual Claude loading. Recheck official integration/dependency documentation when implementation changes those contracts. No globally installed user plugin is changed merely to run a candidate experiment.

Publish only after deterministic correctness/usability gates pass and the benchmark is reviewed. A repair release may ship without a speed advantage, but its notes must say so. Include the source/artifact IDs, workload/model/settings and cold/warm labels with measured claims. Preserve the previous release and disableable persistence for rollback; unknown/corrupt caches rebuild safely. This plan does not require general IDE features or a new background service.

## Definition of done

The developer can choose a SQL module, get bounded scoped evidence with accurate citations and visible limits, reuse verified work across realistic sessions/restarts, and rerun `$benchmark-sql-plugin` to see whether a particular candidate improves accuracy, time or token use. Every causal claim is supported by a measured stage or a labeled hypothesis; no claim of being better than Grep is made without matched reviewed results.
