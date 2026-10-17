# SQL Server Samples: measured results

Model: `deepseek-v4-pro` through `api.deepseek.com`; plugin 0.16.0. 12 questions × 2 repeats × 2 conditions; order=baseline-first. 2 concurrent client(s) within each phase; workflow=prescribed; fresh SQL-only workspaces. No SQL was executed.

| Metric | Read/Grep/Glob | SQL plugin + file tools |
|---|---:|---:|
| Successful sessions | 24 | 24 |
| Strict passing answers | 11 | 9 |
| Mean factual score | 92.9% | 94.6% |
| Mean citation score | 100.0% | 100.0% |
| Mean time (seconds) | 16.9 | 28.6 |
| Median time (seconds) | 16.6 | 28.1 |
| Mean reported uncached input tokens | 7,235 | 14,524 |
| Mean reported total input tokens (including cache) | 22,728 | 51,478 |
| Mean reported output tokens | 1,580 | 1,954 |
| Client-reported cost total (USD) | 2.0023 | 3.3588 |
| Tool failures | 0 | 14 |
| Oversized tool results | 0 | 0 |
| Sessions invoking SQL MCP | 0 | 24 |
| Sessions receiving analysis JSON (may include summary/pages) | 0 | 21 |
| Sessions receiving a bounded summary | 0 | 15 |
| Sessions with Resolved/Partial analysis (not Unresolved) | 0 | 8 |

Total input is the sum of reported input, cache-read and cache-write tokens across client requests. It is not unique source tokens. Reported costs use the client's accounting, not a verified DeepSeek invoice.

## Per-question results

Scores average the two repeats. Tokens include cached input; time includes client/MCP startup and tool failures. All assigned runs stay in the denominator.

| Question | Grep facts | Plugin facts | Grep seconds | Plugin seconds | Grep input tokens | Plugin input tokens |
|---|---:|---:|---:|---:|---:|---:|
| Q01-order-footprint | 83.3% | 91.7% | 16.8 | 33.6 | 38,526 | 98,788 |
| Q02-invoice-eligibility | 100.0% | 100.0% | 19.2 | 31.3 | 42,332 | 60,395 |
| Q03-price-formula | 100.0% | 100.0% | 20.0 | 29.9 | 10,961 | 32,532 |
| Q04-login-password | 81.2% | 93.8% | 15.3 | 26.2 | 16,529 | 38,617 |
| Q05-sensor-upsert | 93.8% | 100.0% | 22.9 | 29.7 | 27,107 | 52,429 |
| Q06-dynamic-reseed | 93.8% | 87.5% | 16.7 | 27.7 | 25,109 | 41,701 |
| Q07-price-callers | 100.0% | 100.0% | 16.9 | 25.6 | 29,450 | 41,330 |
| Q08-ticket-bindings | 100.0% | 100.0% | 13.2 | 29.7 | 10,878 | 55,264 |
| Q09-json-update | 100.0% | 100.0% | 21.8 | 32.6 | 29,000 | 79,734 |
| Q10-json-format | 100.0% | 100.0% | 16.6 | 32.5 | 17,968 | 40,550 |
| Q11-external-clr | 100.0% | 100.0% | 10.0 | 20.0 | 8,232 | 28,848 |
| Q12-fulfill-variants | 62.5% | 62.5% | 13.3 | 24.8 | 16,646 | 47,556 |

## Review and limitations

Each criterion is 0 (missing/wrong), 0.5 (partly correct), or 1 (correct). Equal-weight mean within task, then across tasks. Score source citation support separately (0/0.5/1). A passing answer needs all criteria correct, supported citations, no material unsupported claim, and a successful client result. Review answers against source; do not grade by keyword presence or self-reported confidence. Reviewer is the implementing Codex agent, not an independent human. Failures stay in the denominator.

The implementing agent reviewed the answers against the pre-run rubric and source. This is not independent human review. Baseline-first order, uncontrolled OS/provider caches and load, related procedures, selected questions and two repeats limit generalization. This measures the cold prescribed workflow, not warm persisted-cache behavior or a separate LSP-versus-MCP/codebase-memory experiment. Availability without actual MCP calls does not establish an analyzer benefit.

Frozen corpus SHA256: `24023744adea38b138dd041e5134a66f5ac4fa211060f0ba6976e15aeebc00d7`. Source revision: `e5d7bb90ca0b695ac14f61458047bfb5a498459e`. Full questions/rubric, raw transcripts, answers, ungraded metrics and explicit review notes are retained alongside this report.

Artifact: `e19da6cb79d2931ff30b17821113491b18c7888702906c9aa5e0ccb302363b6c`, clean package source `9e23fb8`, Windows ZIP `4e97b5054e4d1f91241dc872602f52e2fe70b0ae1fe3a258c66f55e250db9c41`. This is the first combined artifact, before the mixed-selector paging repair. Raw/graded evidence: `artifacts/sample-study-final-ci-20261017`; [curated aggregate](../../evidence/benchmark-combined-first-20261017.json). All answers reviewed by the implementing agent; no independent/blinded review. No speed/token superiority is established. Original suite/baseline retained unchanged.

Packaging audit: this earlier candidate included maintainer benchmark docs/rubrics. No reads of those files were observed in restricted transcripts, but their presence is a contamination risk. The clean post-repair artifact is evaluated separately; do not treat these quality scores as an independent holdout.
