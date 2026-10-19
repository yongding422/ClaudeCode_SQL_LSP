# First-response candidate: five-investigation benchmark

The plugin produced two answers acceptable as written versus none for Grep in this fresh matched run. It still required more time and input tokens, and eight of ten plugin answers need major correction. This supports a limited quality benefit on these selected questions, not general superiority or a claim that the local fixes improved acceptance over the previous candidate.

Completed on 2026-10-18 America/New_York (UTC records cross into October 19). No model sessions were retried. All twenty completed successfully; zero tool errors or oversized-result failures. One plugin answer exceeded the requested 450 words (473); it remains assigned and was already judged to require major correction.

## Quality first

| Outcome | Grep + reading | Local SQL plugin + file tools |
|---|---:|---:|
| Accepted as written | 0 / 10 | 2 / 10 |
| Minor edits | 0 / 10 | 0 / 10 |
| Major correction | 10 / 10 | 8 / 10 |
| Rejected | 0 / 10 | 0 / 10 |
| Answers with critical errors | 3 / 10 | 2 / 10 |
| Answers with material unsupported extra claims | 6 / 10 | 6 / 10 |
| Required-fact coverage | 84.75% | 90.75% |
| Evidence support | 60% | 70% |
| Uncertainty handling | 60% | 70% |
| Actual developer acceptance / correction time | Unknown | Unknown |

All twenty final answers were reviewed against the frozen source and rubric before inspecting the private toolset mapping. The reviewer is the implementing Codex agent, familiar with the earlier cohort. Tool mentions can reveal treatment, so label withholding does not establish blinded or independent review. No human-developer feedback, correction time or follow-up effort was measured. Required-fact coverage is not a percentage of fully correct answers.

| Question | Grep accepted / 2 | Plugin accepted / 2 | Main remaining issue |
|---|---:|---:|---|
| Order handover | 0 | 1 | Missing pricing dependency in some answers; unsupported success guarantees |
| Invoice change review | 0 | 1 | Failure return, sequence allocation and regression cardinality errors |
| Password investigation | 0 | 0 | Correct predicates accompanied by unsupported client/schema assumptions |
| Fulfillment loop | 0 | 0 | Stale assignment found, but disk autocommit assumed; one SNAPSHOT error |
| Reseeding | 0 | 0 | Temporary-table trace omitted; some claims mishandle initial/live state |

Paired outcomes: plugin-only accepted 2, Grep-only 0, both 0, neither 8. One new per-question regression remains visible: a plugin fulfillment answer adds a wrong SNAPSHOT self-visibility explanation although its stale-assignment explanation is correct. The Grep fulfillment answers have other defects but not that additional SNAPSHOT error.

The frozen contract deliberately requires the helper temporary-table trace and native OWNER/SCHEMABINDING description. Some failed answers get the main conclusion right but omit these required details; they are completeness failures, distinct from critical errors. The rubric and all previous grades were left unchanged.

## Resources

| Mean per answer, except cost | Grep + reading | Local SQL plugin + file tools |
|---|---:|---:|
| End-to-end answer time | 26.90 s | 37.73 s |
| Median answer time | 27.71 s | 35.28 s |
| Uncached input tokens | 12,545.0 | 17,923.2 |
| Cache-read input tokens | 26,611.2 | 50,713.6 |
| Cache-write input tokens | 0 | 0 |
| Total input tokens | 39,156.2 | 68,636.8 |
| Output tokens | 2,625.7 | 3,031.6 |
| Reported cost, all ten answers | $1.416731 | $1.907628 |

The plugin took **40.3% longer** and used **75.3% more total input tokens** than the fresh Grep arm. Combined client-reported cost was **$3.324359**, under the announced $10 maximum reported cap. This is client pricing, not an independently verified provider invoice. All usage categories were available; no unknown usage was filled with zero.

## Did the new delivery work?

- All ten plugin sessions used MCP: 34 calls, including 20 analysis calls. No LSP or Skill calls were observed.
- Eight sessions received initial brief facts: 68 cited facts across twelve resolved initial reports, including RETURN-after-THROW, sequence, SELECT-retention and atomic-transaction observations. Delivery is directly observed; use of every fact is not inferred.
- Seventeen analysis calls specified an intent. No discovery or analysis call supplied scopeUri. Both reseeding sessions received only ambiguous analysis results and fell back to source reading, despite five responses containing selection-recovery guidance.
- Only one session expanded retained report sections. The frozen legacy classifier reports one supported-analysis session because it ignores briefs attached to paged summaries. That count must not be read as only one session receiving body evidence; the supplemental transcript audit records the eight brief deliveries.
- The canonical report-byte counter also excludes the new brief; it is not full response size or a token estimate. The reported token totals above are the actual client/provider categories.
- No non-SQL Read paths were observed. Seventeen analysis envelopes explicitly reported saved analysis caching disabled. The user source-folder config was not inherited by the isolated clients.

A useful success case is plugin run-0012: the answer says it read source to confirm CustomerTransactions because the brief omitted that target, then provides the complete five-table footprint. A remaining failure is the fulfillment explanation: an atomic-transaction observation already warns about ambient transactions, but the final answers still assert disk autocommit. Additional evidence availability alone does not ensure correct reasoning.

## Comparison with the preceding 0.16.0 investigation run

This is descriptive history. The model/provider/client, source/corpus, questions/rubric, runner/controller hashes and recorded execution settings match. Candidate code/skill bytes differ. Earlier review was implementing-agent unblinded; this review withheld labels until scores were submitted. Provider/cache/load conditions remain uncontrolled.

| Plugin outcome | Previous 0.16.0 | Local first-response candidate |
|---|---:|---:|
| Accepted as written | 2 / 10 | 2 / 10 |
| Answers with critical errors | 4 / 10 | 2 / 10 |
| Required-fact coverage | 84.25% | 90.75% |
| MCP calls | 50 | 34 |
| Mean total input tokens | 68,544.5 | 68,636.8 |
| Mean answer time | 46.98 s | 37.73 s |

Fewer MCP calls did **not** produce lower input usage: plugin input is essentially unchanged. Both arms ran faster this time (Grep 36.14 to 26.90 seconds), so the historical plugin time decrease cannot be attributed to the fix. Grep input grew from 30,627.2 to 39,156.2; the smaller relative token penalty therefore does not demonstrate plugin token savings. Plugin acceptance stayed at two of ten.

## Reproduction and retained evidence

Candidate runtime fingerprint: `46a946b55912cbed18f4dfcd5fa962f07bb1f0dd8891b3b55dbcbf3ceab93bcb`. Native Windows x64, self-contained, pinned runtime 10.0.13; build HEAD `53686fb97ad6eb5af27069e408161abe80c57ac6`, dirty source recorded. Manifest version remains 0.16.0, but these are local candidate bits, not the published marketplace artifact. Archive SHA-256: `cf646df995b2a418f8deac511b633dc00a858489d71718ca047f94dc26320270`.

Native locked publish, manifest validation and extracted-package tests passed: LSP 60 passed/1 skipped, MCP 95/3, provider 29/14. Python 52 passed/1 skipped. Core 162 passed in the prior unchanged-source validation; source hashes were checked before packaging. No dependencies changed, no SQL executed, no database access, no global plugin installation change and no release published. A package-test invocation before archive completion found a missing checksum and executed no tests; after packaging finished, the full package checks passed.

Protocol: five frozen investigation tasks × two repeats × two arms; deepseek-v4-pro on api.deepseek.com, Claude Code 2.1.302, low effort, baseline first, two workers, 180 seconds and $0.50 reported cap per session. Cold fresh clients include server initialization; saved caches disabled. Source revision e5d7bb90ca0b695ac14f61458047bfb5a498459e, 1,099 SQL files, corpus SHA-256 24023744adea38b138dd041e5134a66f5ac4fa211060f0ba6976e15aeebc00d7. Same related cohort, not a holdout. No codebase-memory arm or LSP-specific effect measured.

- [Questions and source-backed facts](../sql-server-samples-investigations-v1.json); [frozen acceptance requirements](../sql-server-samples-investigations-acceptance-v1.json).
- [Durable evidence, all answers and explicit reviews](../../evidence/sql-server-samples-first-response-20261018.json).
- [Acceptance report](../../../artifacts/first-response-acceptance-results-20261018/acceptance-report.md); [all acceptance results](../../../artifacts/first-response-acceptance-results-20261018/acceptance-results.json).
- [Reviewed factual results](../../../artifacts/first-response-study-20261018/reviewed-results.json); [raw metrics](../../../artifacts/first-response-study-20261018/runs/metrics.csv); [run transcripts and answers](../../../artifacts/first-response-study-20261018/runs).
- [Candidate and settings](../../../artifacts/first-response-study-20261018/rerun-plan.json); [preflight](../../../artifacts/first-response-study-20261018/preflight.json); [actual brief delivery audit](../../../artifacts/first-response-study-20261018/transcript-audit-v2.json).
- [Previous investigation report](sql-server-samples-investigations-20261018.md); [generated historical 0.14.0 comparison](../../../artifacts/first-response-study-20261018/baseline-comparison.md) is separate and mismatched on task/rubric cohort.
- [Earlier local functional validation](../../first-response-evidence.md), including the unclassified no-model probe failure; successful native/package/model checks do not establish its cause.

Review-only language checks used Microsoft documentation for [SELECT assignment retention](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/select-local-variable-transact-sql?view=sql-server-ver17), [sequence state](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sequences-transact-sql?view=sql-server-ver17), [nontransactional sequence allocation](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-sequence-transact-sql?view=sql-server-ver17), [atomic blocks and caller transactions](https://learn.microsoft.com/en-us/sql/relational-databases/in-memory-oltp/atomic-blocks-in-native-procedures?view=sql-server-ver17) and [SNAPSHOT visibility of own changes](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql?view=sql-server-ver17). These were not added to participant prompts or used to change the frozen gold.

## Next implementation priorities

1. Make explicit project/file selection easy to act on in discovery, preserving real ambiguity. Recovery text was delivered but neither reseeding session used a scope.
2. Complete the evidence used to explain caller transaction boundaries and temporary-table data movement, while checking final claims against known facts. Existing warnings were sometimes ignored; more output alone is not a demonstrated fix.
3. Reduce repeated context and metadata, then rerun this unchanged cohort plus an independent holdout. Fewer calls alone did not reduce token use. Independent SQL-developer review and measured correction effort remain unmeasured.
