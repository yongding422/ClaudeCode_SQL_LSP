# New stored-procedure benchmark — 2026-10-19

**The plugin covered more required facts but did not improve acceptance as written. It used more time and input tokens than Read/Grep/Glob on this small cold-workflow cohort.**

Three new primary procedures × two repeats × two arms: 12 successful sessions, no tool failures, no retries. All answers met the 450-word whitespace count. Same `deepseek-v4-pro` through `api.deepseek.com`, Claude Code 2.1.302, low effort, two workers within each baseline-first phase, seed 42, 180 seconds and $0.50 per-session reported cap ($6 total cap). Source: 1,099 frozen SQL files at `e5d7bb90ca0b695ac14f61458047bfb5a498459e`.

| Measure | Read/Grep/Glob | SQL plugin + file tools |
|---|---:|---:|
| Accepted as written | 0/6 | 0/6 |
| Needs major correction | 6/6 | 6/6 |
| Answers with critical errors | 3/6 | 2/6 |
| Answers with material unsupported extra claims | 4/6 | 4/6 |
| Required-fact coverage | 63.3% | 71.7% |
| Evidence support | 50.0% | 50.0% |
| Uncertainty handling | 58.3% | 50.0% |
| Mean seconds | 26.71 | 36.44 |
| Median seconds | 24.65 | 36.52 |
| Mean uncached input tokens | 9,752.5 | 14,779.2 |
| Mean cache-read input tokens | 20,565.3 | 35,498.7 |
| Mean total input tokens | 30,317.8 | 50,277.8 |
| Mean output tokens | 2,589.2 | 2,591.7 |
| Client-reported cost total, USD | 0.742646 | 0.938621 |

Plugin mean time was 36.4% higher and mean total input 65.8% higher. Required-fact coverage was 8.3 percentage points higher, but none of the 12 answers passed the entire predeclared acceptance checklist. These are reviewer judgments, not observed developer satisfaction. Both arms produced useful observations; incomplete essentials and consequential mistakes blocked acceptance. Cost across both arms was $1.681267 by client accounting, not a verified provider invoice. Total input includes cache-read and cache-write categories; it is not unique source tokens.

The native candidate is still labeled 0.16.0 but contains commit `cbdcf2983df44c6e0e47279af73f3c16533be413`, clean build; fingerprint `5ab8d15ae520b69bc63e91ffa77881abe97c9402aed19a1e3f25130cf6927b12`. It is the tested follow-up artifact, not an assumption about the installed marketplace version. Prior full local/native tests and [all nine CI jobs](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/39194136016) passed for this code. No source code, installed plugin or SQL was changed during measurement.

## Per-question review

| Task | Grep facts | Plugin facts | Grep critical answers | Plugin critical answers |
|---|---:|---:|---:|---:|
| N01-receipt-scope-review | 55.0% | 65.0% | 2/2 | 1/2 |
| N02-supplier-payment-handover | 75.0% | 80.0% | 0/2 | 1/2 |
| N03-delivery-status-investigation | 60.0% | 70.0% | 1/2 | 0/2 |

- **Receipt scope:** both arms find the missing purchase-order predicate. Several then invent deterministic accumulated totals for a multi-match UPDATE. SQL Server documents that result as undefined; this is not a SUM. Some answers also imply independent per-PO commits or restrict the defect to shared stock items, overlooking unrelated different items. [UPDATE semantics](https://learn.microsoft.com/en-us/sql/t-sql/queries/update-transact-sql), [nested commits](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/commit-transaction-transact-sql).
- **Supplier payment:** both arms explain the local work list and negated supplier grouping. All omit essential NULL purchase-order/invoice-link and date details from the predeclared handover. A plugin answer turns a generic SELECT-retention warning into a stale-ID claim despite fresh NULL locals; another says missing lookup IDs silently insert NULL even though TransactionTypeID is NOT NULL. [Local initialization](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/declare-local-variable-transact-sql).
- **Delivery status:** both arms find the unguarded JSON/computed-column mismatch. Selection joins/date and runtime/caller qualifications are often incomplete; permanent retry suppression is overclaimed. One Grep answer offers a root `$.Status` regression predicate even though status is inside Events, with a NULL comparison that cannot detect the suggested cases. Proposed tests were not executed. [JSON_VALUE](https://learn.microsoft.com/en-us/sql/t-sql/functions/json-value-transact-sql), [NULL comparison](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/null-and-unknown-transact-sql).

## What the tool actually delivered

All six treatment sessions connected only the intended SQL MCP server. They made 10 discovery and six analysis calls. Five analyses resolved and delivered 40 cited brief facts in initial paged responses; one returned Unresolved with no facts. There were zero report-expansion, LSP or Skill calls, zero explicit scope requests and no scope choices returned. Consequently, this cohort does not exercise the leaner expansion envelopes or demonstrate the duplicate-scope recovery fix. The five resolved briefs include transaction contracts, local table transfer and ordinary relation/SELECT facts; delivery is not proof the model used them correctly. One answer retained an independent-commit claim despite receiving transaction caveats.
The unresolved invocation supplied documentUri only, omitting moduleRef, declaration and position; the default position was the blank first line. It received an explicit unsupported-position explanation, then fell back to source reading. This remains in the plugin arm. It is a selector/routing failure to recover from, not proof that the saved procedure is unanalyzable.
Each treatment process performed cold inventory/parsing: measured initialization 7.44–9.16 seconds for 1,099 files. This is a plausible contributor to the time gap, not an isolated causal estimate; initialization/queue intervals can overlap and must not be summed as end-to-end time. The workspace reports 371 files with parse errors and 687 diagnostics across the heterogeneous corpus; that does not mean only 371 files were indexed or that every selected procedure failed.
Canonical complete SQL payloads totaled 93,479 bytes across 16 calls, versus 11,855 report-only and 40,764 brief-only bytes. Discovery/envelopes and briefs therefore matter; report-only counts understate delivered context. These are parsed JSON sizes, not wire frames or model token estimates. Provider-reported input remains the resource outcome. Grep used 38 total tool calls and the plugin arm 37, so fewer calls did not imply fewer tokens.

## Controls, review and reuse

Cold fresh-client conditions include startup/indexing. Persistent index/report caches and backend metrics recording were disabled; OS/provider caches and phase load remain uncontrolled. Baseline ran first. This run cannot answer whether a persisted or same-live-process workflow is faster, nor isolate LSP from MCP or compare codebase-memory.
The source snapshot and acceptance overlay were frozen prospectively before any sessions. The implementing agent checked every answer against SQL and documented semantics before opening the private arm mapping or raw usage/tool logs. Some answers reveal their tools and the reviewer knew the implementation/specification, so arm_labels_seen was conservatively recorded as true. There was no independent human review. Developer acceptance, correction minutes and actual clarification effort remain unknown. Three selected related tasks and two repeats support descriptive results only.
This is a separate new-procedure cohort, not a rerun of the earlier five investigations or original 12 questions. Historical answers/grades remain unchanged. The retained 0.14.0 comparison explicitly flags the changed rubric; do not attribute before/after score differences to the fixes. No additional paid rerun was started.

[Prospective questions](../sql-server-samples-new-procedures-v1.json) · [Acceptance rubric](../sql-server-samples-new-procedures-acceptance-v1.json) · [Full durable evidence, all answers and reviews](../../evidence/sql-server-samples-new-procedures-20261019.json)

Local artifacts: `artifacts/quality-followups-benchmark-20261018/` contains frozen inputs, the candidate fingerprint, raw runs/metrics, factual reports, tool audit and historical comparison. `artifacts/quality-followups-benchmark-review-20261018/` contains the prospective contract, submitted review and private mapping. `artifacts/quality-followups-acceptance-results-20261019/` contains the acceptance report/results. Input/output hashes are retained in the durable evidence.
