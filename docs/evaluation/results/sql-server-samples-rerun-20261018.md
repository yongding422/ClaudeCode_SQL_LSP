# SQL Server Samples: requested 0.16.0 rerun, 2026-10-18 UTC

**On this suite, the released plugin was slower, used more reported input tokens, and did not improve overall factual coverage.**

Model: `deepseek-v4-pro` through `api.deepseek.com`; plugin 0.16.0. 12 questions × 2 repeats × 2 conditions; order=baseline-first. 2 concurrent client(s) within each phase; workflow=prescribed; fresh SQL-only workspaces. No SQL was executed.

| Metric | Read/Grep/Glob | SQL plugin + file tools |
|---|---:|---:|
| Successful sessions | 24 | 24 |
| Strict passing answers | 8 | 9 |
| Mean factual score | 92.9% | 91.8% |
| Mean citation score | 91.7% | 97.9% |
| Mean time (seconds) | 19.3 | 32.2 |
| Median time (seconds) | 18.0 | 30.2 |
| Mean reported uncached input tokens | 6,957 | 14,257 |
| Mean reported total input tokens (including cache) | 22,605 | 46,316 |
| Mean reported output tokens | 1,588 | 2,077 |
| Client-reported cost total (USD) | 1.9756 | 3.3415 |
| Tool failures | 0 | 0 |
| Oversized tool results | 0 | 0 |
| Sessions invoking SQL MCP | 0 | 24 |
| Sessions receiving analysis JSON (may include summary/pages) | 0 | 22 |
| Sessions receiving a bounded summary | 0 | 16 |
| Sessions with Resolved/Partial analysis (not Unresolved) | 0 | 10 |

Total input is the sum of reported input, cache-read and cache-write tokens across client requests. It is not unique source tokens. Reported costs use the client's accounting, not a verified DeepSeek invoice.

## Per-question results

Scores average the two repeats. Tokens include cached input; time includes client/MCP startup and tool failures. All assigned runs stay in the denominator.

| Question | Grep facts | Plugin facts | Grep seconds | Plugin seconds | Grep input tokens | Plugin input tokens |
|---|---:|---:|---:|---:|---:|---:|
| Q01-order-footprint | 83.3% | 100.0% | 20.2 | 30.0 | 34,353 | 63,620 |
| Q02-invoice-eligibility | 100.0% | 100.0% | 25.5 | 28.1 | 41,382 | 46,076 |
| Q03-price-formula | 100.0% | 100.0% | 18.8 | 36.2 | 10,796 | 33,271 |
| Q04-login-password | 81.2% | 87.5% | 18.0 | 33.4 | 17,462 | 37,547 |
| Q05-sensor-upsert | 93.8% | 93.8% | 28.4 | 32.0 | 19,462 | 45,398 |
| Q06-dynamic-reseed | 87.5% | 87.5% | 21.0 | 30.7 | 38,289 | 50,756 |
| Q07-price-callers | 100.0% | 83.3% | 20.0 | 44.8 | 27,737 | 61,322 |
| Q08-ticket-bindings | 100.0% | 87.5% | 18.8 | 26.8 | 13,516 | 43,580 |
| Q09-json-update | 100.0% | 100.0% | 15.8 | 32.8 | 15,206 | 61,960 |
| Q10-json-format | 100.0% | 100.0% | 18.7 | 37.7 | 21,360 | 32,120 |
| Q11-external-clr | 93.8% | 93.8% | 11.7 | 26.4 | 9,870 | 31,944 |
| Q12-fulfill-variants | 75.0% | 68.8% | 15.0 | 27.0 | 21,832 | 48,194 |

## Review and limitations

Each criterion is 0 (missing/wrong), 0.5 (partly correct), or 1 (correct). Equal-weight mean within task, then across tasks. Score source citation support separately (0/0.5/1). A passing answer needs all criteria correct, supported citations, no material unsupported claim, and a successful client result. Review answers against source; do not grade by keyword presence or self-reported confidence. Reviewer is the implementing Codex agent, not an independent human. Failures stay in the denominator.

The implementing agent reviewed the answers against the pre-run rubric and source. This is not independent human review. Baseline-first order, uncontrolled OS/provider caches and load, related procedures, selected questions and few repeats limit generalization. This measures the cold prescribed workflow, not warm persisted-cache behavior or a separate LSP-versus-MCP/codebase-memory experiment. Availability without actual MCP calls does not establish an analyzer benefit.

Frozen corpus SHA256: `24023744adea38b138dd041e5134a66f5ac4fa211060f0ba6976e15aeebc00d7`. Source revision: `e5d7bb90ca0b695ac14f61458047bfb5a498459e`. Full questions/rubric, raw transcripts, answers, ungraded metrics and explicit review notes are retained alongside this report.

## Interpretation of this rerun

Against the fresh matched Grep arm, plugin mean elapsed time was **66.5% higher** and reported total input was **104.9% higher**. Its required-fact score was 1.04 percentage points lower; citation support was 6.25 points higher. Strict passing answers were 9/24 versus 8/24, and answers containing a material unsupported claim were 11/24 versus 13/24. These measure different aspects of quality; the extra strict pass and better citations do not establish a speed/token advantage or eliminate the factual regressions.

Per-question facts improved for Q01 order footprint (+16.7 points) and Q04 login/password (+6.25); regressed for Q07 static callers/dynamic definitions (-16.7), Q08 ticket error boundary (-12.5) and Q12 fulfillment variants (-6.25); the other seven were unchanged. Both ticket treatment answers wrongly conclude that work continues after a bare THROW. Neither treatment fulfillment answer explains stale SELECT-assignment variables; the native-code declarations alone do not answer that behavior question. Each answer's criterion scores, source checks and material extra claims are retained in the review.

All 24 treatment sessions made MCP calls (90 calls total). Twenty-two received analysis JSON; sixteen received bounded summaries; only ten consumed a Resolved/Partial body report or expansion. These counts overlap and are not correctness scores. Ambiguous identities led to file fallback for reseeding. Some models described report paging as truncation and read source rather than expanding it. Zero client overflows/rejections were recorded: successful pagination must not be relabeled as a failed method. The treatment still made 45 Read calls, versus 55 for Grep, alongside its added discovery/analysis calls. This observed duplicated workflow is consistent with overhead; this study does not isolate causal contributions from initialization, prompt size and model routing.

The predeclared structural group failed both the resource and quality gates: time ratio 1.515 and total-input ratio 1.852, with 88.5% facts versus 90.1% for Grep. The 20% reduction objective is not met. The natural-availability simple-control target is inapplicable to this prescribed run; the separate availability cohort was not rerun.

## Candidate and verification

Used the unchanged released Windows x64 0.16.0 package built from `2b37f30195ad0276e69c7a9d255e99cc3826495f`, clean source and self-contained. Runtime fingerprint: `dc70df8d5b16a06c94ddaa1405d548b876e9e9f9a3bd2ff91a65873f611a9285`. Release ZIP SHA256: `990588c2a8a6b0c1fabd0a2223a5019b5d256b4ac0b35a892f49cd3ae95f5e9b`. Maintainer rubrics/prior results are excluded from the participant plugin. Source and packaged runtime files were revalidated before reporting; no product implementation changed for this rerun.

The current benchmark helper tests completed with 31 passes and one Windows symlink skip. A new no-model native MCP preflight completed two requests. The exact candidate's prior C#/packaging/database/native-install CI is retained in [the verified release record](../../evidence/benchmark-fixes-ci.json); those historical checks are not represented as newly rerun tests. All 48 clients succeeded; there were no tool errors, missing results or overflow classifications. Experiment isolation audit found no violations. Original source worktree retained only its preexisting untracked `.sqlscriptdom.json`; no SQL, DB connection, global plugin update or authentication-file copying occurred.

The new study was prepared at 2026-10-18 01:52 UTC (2026-10-17 evening EDT). Both arms used Claude Code 2.1.302, deepseek-v4-pro at api.deepseek.com, effort low, seed 42, two workers, 180-second timeout and a $0.50 reported cap per session. Total reported cost was $5.317155, not an independently verified invoice.

## Historical context and retained evidence

Compared descriptively with the immutable 0.14.0 study, plugin mean time changed from 42.51 to 32.17 seconds, total input from 51,481 to 46,316, factual score from 90.6% to 91.8%, tool errors from 47 to zero and overflows from 41 to zero. Recorded corpus/rubric/model/client/settings dimensions match. These are historical observations, not a causal improvement estimate: the fresh matched Grep comparison remains primary, and implementing-agent grading/provider load can vary.

The prior clean 0.16.0 study is retained separately: its Grep/plugin means were 17.00/31.63 seconds and factual scores 92.9%/94.1%. The same bits now produced 19.32/32.17 seconds and 92.9%/91.8% respectively. Do not replace, pool selectively, or reuse grades from that study; the changed task-level answers show why two repeats are insufficient for a broad superiority claim.

- [Questions, criteria and source evidence](../sql-server-samples-suite.json)
- [Frozen study protocol](../sql-server-samples-study.md) and [predeclared groups](../final-candidate-protocol.md)
- [New reviewed evidence, all 48 rows and review notes](../../evidence/sql-server-samples-rerun-20261018.json)
- [All artifacts: raw sessions/answers, blank template, completed grades and metrics](../../../artifacts/sample-study-rerun-20261018-0153/)
- [New reviewed metrics CSV](../../../artifacts/sample-study-rerun-20261018-0153/reviewed-results.csv), [JSON](../../../artifacts/sample-study-rerun-20261018-0153/reviewed-results.json), [raw metrics](../../../artifacts/sample-study-rerun-20261018-0153/runs/metrics.csv)
- [Cohort gates](../../../artifacts/sample-study-rerun-20261018-0153/cohorts.json), [failure audit](../../../artifacts/sample-study-rerun-20261018-0153/client-failures.json) and [0.14.0 comparison](../../../artifacts/sample-study-rerun-20261018-0153/baseline-comparison.md)
- [Prior clean 0.16.0 report, unchanged](sql-server-samples-final-clean-20261017.md)

No LSP or Skill requests were observed. This measures MCP-assisted cold fresh-client use; it cannot establish a benefit specific to LSP, a warm persisted index, or codebase-memory MCP. The conclusion for this selected suite remains that the released plugin is reliable at the protocol level but has not demonstrated the intended performance/token advantage over Grep plus reading.
