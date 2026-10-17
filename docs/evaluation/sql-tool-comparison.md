# SQL investigation: Grep/Read, codebase-memory and this plugin

Research checked 2026-10-15. **No general speed or model-token advantage has been demonstrated for this plugin yet.** Measure verified final answers, including setup cost. LSP versus MCP alone cannot establish superiority.

## What differs

This research uses **DeusData/codebase-memory-mcp**, pinned source `8df0639c6268afe9888979e72b9797aa2cc1de7c`. The user's installed repository/version is unconfirmed and its MCP tools/binary are unavailable in this session. These are source observations, not a runtime comparison or exhaustive negative feature audit.

Codebase-memory is **not a plain-text-only SQL index**: it uses Tree-sitter parsing and a persistent SQLite graph exposed through MCP, alongside text/source search. [Project documentation](https://github.com/DeusData/codebase-memory-mcp/tree/8df0639c6268afe9888979e72b9797aa2cc1de7c)

Its selected-language resolvers also use the name “LSP.” For example, the inspected Python API walks Tree-sitter nodes with an internal type/scope registry. That internal semantic analysis should not be confused with a client-facing LSP protocol process, and it is not evidence of SQL language-server support. [Internal Python resolver API](https://github.com/DeusData/codebase-memory-mcp/blob/8df0639c6268afe9888979e72b9797aa2cc1de7c/internal/cbm/lsp/py_lsp.h)

The inspected SQL registration recognizes procedure/function declarations, tables/views, invocation nodes, columns and IF/CASE branch node types. [Language registration source](https://github.com/DeusData/codebase-memory-mcp/blob/8df0639c6268afe9888979e72b9797aa2cc1de7c/internal/cbm/lang_specs.c)

Its SQL call extractor reads the callee name from an invocation's object reference. This establishes SQL-specific extraction; it does not alone establish correct T-SQL EXEC named/default/OUTPUT binding or database/schema disambiguation. Those are test questions for the installed build. [Call extraction source](https://github.com/DeusData/codebase-memory-mcp/blob/8df0639c6268afe9888979e72b9797aa2cc1de7c/internal/cbm/extract_calls.c)

There is explicit table/view DDL extraction and FROM/JOIN usage collection for relation dependencies. Saying it has no SQL relationships would be inaccurate. Other semantic coverage remains unverified here. [Definition and relation source](https://github.com/DeusData/codebase-memory-mcp/blob/8df0639c6268afe9888979e72b9797aa2cc1de7c/internal/cbm/extract_defs.c)

| Need | Grep/Read with an agent | codebase-memory MCP, inspected source | This plugin |
| --- | --- | --- | --- |
| Business vocabulary, comments, dynamic SQL strings | Direct matches/full source | Text/source search alongside graph | Use Grep/Read; opaque targets stay unknown |
| Whole-repository, cross-language structure | Repeated search/read/reasoning | Persistent graph is central | T-SQL scope; no application-to-SQL binding promise |
| SQL declarations/relationships | Agent verifies candidates | SQL grammar/extraction present; test dialect cases | ScriptDOM, conservative T-SQL binder, source evidence/ambiguity |
| EXEC parameters, branches, table effects | Possible through source reasoning | Exact T-SQL report parity unverified | Bounded structured reports; default/OUTPUT boundaries and limitations |
| SQL Server catalog/hybrid | Needs equivalent export/tool | Live catalog parity unverified | Explicit read-only profiles, catalog rules, visibility/provenance |
| Unsaved changes | Depends on buffer access | Unsaved-buffer parity unverified | Versioned LSP overlays; MCP sees saved/catalog sources |
| Reuse after restart | OS/model caches differ | Persistent SQLite graph | Opt-in JSON reports; index/AST still rebuilt |

Our actual contracts are [procedure analysis](../procedure-analysis.md), [value tracing](../value-tracing.md), [control maps](../control-flow.md), [hybrid resolution](../hybrid.md) and [report persistence](../persistent-analysis-cache.md). Neither parser establishes general runtime behavior, dynamic SQL targets or complete row/column lineage.

## Why retain LSP?

LSP provides standard document synchronization, diagnostics and navigation. Its versioned change protocol requires clients to synchronize documents before querying, which supports investigation while editing unsaved SQL. [Official didChange specification](https://raw.githubusercontent.com/microsoft/language-server-protocol/gh-pages/_specifications/lsp/3.17/textDocument/didChange.md)

Our independent LSP and MCP processes call shared C# parsing/resolution code. **MCP does not call or require LSP.** For explaining saved procedures, MCP can supply the report alone. LSP adds editor navigation, unsaved overlays and diagnostics. SQL awareness comes from ScriptDOM and the binder/report implementation, not protocol method names. Another MCP server could implement those semantics. Watchers and incremental persistent indexes also do not require LSP.

A useful combined workflow is general graph discovery, SQL-specific evidence, then Grep/Read for vocabulary and unsupported cases. Measure that combination separately rather than attributing its result to one tool.

## Existing evidence

The [0.10.0 Windows capture](../evidence/compact-reports-pair-windows.json) used one public task, one repeat, the same observed model (`deepseek-v4-pro`), and no independent scoring:

| Observed metric | SQL plugin | Grep/Read |
| --- | ---: | ---: |
| Final-answer wall time | 135.661 s | 79.493 s |
| Reported input tokens | 13,487 | 4,188 |
| Cache-read tokens | 35,968 | 7,168 |
| Output tokens | 3,068 | 2,094 |
| Client-reported cost | $0.166274 | $0.080679 |

This favors Grep/Read on time/usage. Both answers had issues. Preserve this result: smaller report bytes and successful tools do not overturn it. Raw token categories remain separate; backend billing rules differ.

Codebase-memory's author preprint reports approximately tenfold fewer tokens across 31 repositories, with quality **83% versus 92%** for file exploration. It notes one model, author grading and one repository per language. That is an author-reported quality/usage tradeoff, not our T-SQL comparison. [Paper and limitations](https://arxiv.org/html/2603.27277v1)

The historical language benchmark's SQL section used Flyway; function/class answers came from Python/Bash/Java, and the text-search row says actual SQL DDL was not indexed. Its historical score is neither a procedure-understanding benchmark nor proof of current failure. [Pinned benchmark](https://github.com/DeusData/codebase-memory-mcp/blob/8df0639c6268afe9888979e72b9797aa2cc1de7c/docs/BENCHMARK.md#sql-flywayflyway)

## Controlled study

1. Freeze gold answers first: at least 30 representative procedures with independently reviewed assertions for identities/calls, named/default/OUTPUT arguments, branches/exits, reads/writes, collisions, quoting, temp objects and dynamic/hidden SQL. Include easy vocabulary tasks and deliberate unknowns. Use public or approved de-identified SQL.
2. Use four arms: **A Grep/Read**, **B codebase-memory + Grep/Read**, **C SQL MCP + Grep/Read, LSP disabled**, **D SQL MCP + LSP + Grep/Read**. C versus A/B measures SQL reports; D versus C isolates LSP. Same questions, source visibility and answer budgets; allow A to reason/filter results. Freeze instructions/settings and verify actual tool calls/results.
3. Fix backend/model/version, effort, client, repository, parser/rules, hardware and deadlines. Fresh model sessions; at least three paired repeats; randomized balanced arm order; no shared answers. Record uncontrolled OS/model caches. Keep timeouts, unavailable tools and missing usage as outcomes.
4. Separate cold indexing, warm same-process queries and restarted validated-disk-cache queries. Total workload time includes setup/indexing; report amortization for 1, 10 and 100 questions. Verify edits/deletes/new ambiguity invalidate every index. Unsaved buffers are a separate experiment; unequal visibility invalidates a saved-folder comparison.
5. Separate folder and database studies. Folder arms receive exactly the same SQL export. Live metadata arms need equivalent approved metadata, permissions and capture times. An informational advantage from catalog access cannot be attributed to LSP.
6. Blind-score final answers: dependency precision/recall, business-rule assertions, citations, unknown handling and human correction time. Penalize invented dependencies and unsupported certainty. Define empty gold sets explicitly; zero denominators are not automatically 100%.
7. Report paired ratios, medians/p95, failures and 95% confidence intervals, stratified by task class. Bootstrap by procedure, preserving its repeated runs. Predefine the correctness gate. A possible target is no meaningful correctness regression and at least 20% lower median tokens or time; **this is not an achieved result**.

## Metrics and attribution

| Layer | Capture | Interpretation |
| --- | --- | --- |
| Client/model | Input/output, prompt-cache read/write tokens, observed model, reported cost | Actual client/backend usage; missing stays null |
| Task | Wall time to final answer, correctness, timeout, correction time | Fast wrong answers fail the quality gate |
| Tools | Successful calls/results/failures, returned bytes, files read, coverage/truncation | Bytes are not model tokens; repeated context can dominate |
| SQL backend | Queue, initialization, navigation, analysis, presentation, cache validation/read/write | Excludes inference and some transport; overlapping intervals cannot simply be summed into task time |
| Index/cache | Build/restart time, indexed/skipped/error counts, freshness, hits, disk/RAM | Include setup and coverage limits; a hit does not prove completeness |

Compute reduction as `1 - tool / baseline` for an explicitly defined, consistently measured time or token category. Keep negative reductions and publish raw categories. Never substitute report UTF-8 byte reductions or backend milliseconds for model savings. When correctness differs, report the tradeoff rather than an unconditional win. A disk hit still sends a report to the model.

The [existing runner](../../scripts/evaluate-understanding.py) captures plugin versus Grep/Read events, timings and optional answers. The 0.11.0 collector adds cache intervals and enabled/hit counts without cache paths. **It does not yet implement** the codebase-memory arm, LSP ablation, clustered confidence intervals or blind grading. Those are the next experiment changes after fixing the competitor build and reviewed dataset. No three-tool runtime benchmark was conducted here.

Collection remains local and aggregate-only by default. Transcripts/answers/SQL/paths require explicit project approval. Controlled runs need no hooks or background telemetry. The plugin measures backend work; only the client can capture model usage and complete task time.

## Product direction

For this project's stored-procedure understanding goal, prioritize the MCP reports and their SQL evidence. Keep existing LSP navigation available, with general M6 editor work deferred. LSP is useful when the task needs live buffers; it is not a prerequisite for folder/catalog analysis or persistence.

The next performance work should follow profiling on large SQL repositories: incremental persistent semantic indexes and smaller task-specific retrieval may matter more than additional LSP methods. The current report cache still hashes the loaded source context and reparses at restart. Include tool-schema/skill overhead and any duplicated LSP/MCP indexing in measured costs. Verify exact T-SQL coverage in the installed codebase-memory build before claiming a competitive feature advantage. Its broader application graph and this SQL-specific engine may be complementary.

## 0.12.0 collection implementation

[Local plugin metrics](../plugin-metrics.md) now persist bounded numeric backend observations, provide get_metrics and standalone summary/compare commands, and import client CSV for descriptive paired token/time ratios. The capture runner is included in packages. This does not add competitor/LSP-ablation capture, independent grading or statistical confidence intervals, and it does not establish performance superiority.
