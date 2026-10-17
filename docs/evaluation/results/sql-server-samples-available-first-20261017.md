# SQL Server Samples: measured results

**On this suite, the released plugin was slower, used more reported input tokens, and did not improve overall factual coverage.**

Model: `deepseek-v4-pro` through `api.deepseek.com`; plugin 0.16.0. 6 questions × 2 repeats × 2 conditions; order=baseline-first. 2 concurrent client(s) within each phase; workflow=available; fresh SQL-only workspaces. No SQL was executed.

| Metric | Read/Grep/Glob | SQL plugin + file tools |
|---|---:|---:|
| Successful sessions | 12 | 12 |
| Strict passing answers | 4 | 3 |
| Mean factual score | 89.9% | 88.5% |
| Mean citation score | 100.0% | 100.0% |
| Mean time (seconds) | 14.4 | 16.4 |
| Median time (seconds) | 15.6 | 14.1 |
| Mean reported uncached input tokens | 5,625 | 9,236 |
| Mean reported total input tokens (including cache) | 14,222 | 26,068 |
| Mean reported output tokens | 1,398 | 1,391 |
| Client-reported cost total (USD) | 0.8084 | 1.0724 |
| Tool failures | 0 | 0 |
| Oversized tool results | 0 | 0 |
| Sessions invoking SQL MCP | 0 | 0 |
| Sessions receiving analysis JSON (may include summary/pages) | 0 | 0 |
| Sessions receiving a bounded summary | 0 | 0 |
| Sessions with Resolved/Partial analysis (not Unresolved) | 0 | 0 |

Total input is the sum of reported input, cache-read and cache-write tokens across client requests. It is not unique source tokens. Reported costs use the client's accounting, not a verified DeepSeek invoice.

## Per-question results

Scores average the two repeats. Tokens include cached input; time includes client/MCP startup and tool failures. All assigned runs stay in the denominator.

| Question | Grep facts | Plugin facts | Grep seconds | Plugin seconds | Grep input tokens | Plugin input tokens |
|---|---:|---:|---:|---:|---:|---:|
| H01-stock-search-literal | 100.0% | 100.0% | 8.8 | 10.3 | 6,186 | 13,487 |
| H02-supplier-search-joins | 91.7% | 100.0% | 8.4 | 10.4 | 6,302 | 13,605 |
| H03-cold-room-history-chain | 81.2% | 81.2% | 16.6 | 19.8 | 15,114 | 24,611 |
| H04-same-name-search-scopes | 100.0% | 91.7% | 18.3 | 25.5 | 28,977 | 54,889 |
| H05-sensor-error-boundary | 83.3% | 83.3% | 19.0 | 18.8 | 10,191 | 21,200 |
| H06-security-dynamic-ddl | 83.3% | 75.0% | 15.2 | 13.8 | 18,562 | 28,613 |

## Review and limitations

Frozen before candidate model runs. New questions on the same selected corpus, not an independent holdout. Implementing-agent source review: each criterion 0/0.5/1, citations 0/0.5/1 and explicit material-unsupported flag; no automatic keyword grades. Both repeats and all failures stay in the denominator. Simple controls H01/H02; structural and uncertainty cases H03/H04/H05/H06. Edited-source freshness uses deterministic integration fixtures, not static model prompts.

The implementing agent reviewed the answers against the pre-run rubric and source. This is not independent human review. Baseline-first order, uncontrolled OS/provider caches and load, related procedures, selected questions and two repeats limit generalization. This measures the cold available workflow, not warm persisted-cache behavior or a separate LSP-versus-MCP/codebase-memory experiment. Availability without actual MCP calls does not establish an analyzer benefit.

Frozen corpus SHA256: `24023744adea38b138dd041e5134a66f5ac4fa211060f0ba6976e15aeebc00d7`. Source revision: `e5d7bb90ca0b695ac14f61458047bfb5a498459e`. Full questions/rubric, raw transcripts, answers, ungraded metrics and explicit review notes are retained alongside this report.

Artifact: `e19da6cb79d2931ff30b17821113491b18c7888702906c9aa5e0ccb302363b6c`, clean package source `9e23fb8`, Windows ZIP `4e97b5054e4d1f91241dc872602f52e2fe70b0ae1fe3a258c66f55e250db9c41`. This is the first combined artifact, before the mixed-selector paging repair. Raw/graded evidence: `artifacts/sample-study-additional-ci-20261017`; [curated aggregate](../../evidence/benchmark-available-first-20261017.json). All answers reviewed by the implementing agent; no independent/blinded review. No speed/token superiority is established. Original suite/baseline retained unchanged.

Packaging audit: this earlier candidate included maintainer benchmark docs/rubrics. No reads of those files were observed in restricted transcripts, but their presence is a contamination risk. The clean post-repair artifact is evaluated separately; do not treat these quality scores as an independent holdout.
