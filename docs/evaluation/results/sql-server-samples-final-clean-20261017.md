# SQL Server Samples: measured results

Model: `deepseek-v4-pro` through `api.deepseek.com`; plugin 0.16.0. 12 questions × 2 repeats × 2 conditions; order=baseline-first. 2 concurrent client(s) within each phase; workflow=prescribed; fresh SQL-only workspaces. No SQL was executed.

| Metric | Read/Grep/Glob | SQL plugin + file tools |
|---|---:|---:|
| Successful sessions | 24 | 24 |
| Strict passing answers | 8 | 10 |
| Mean factual score | 92.9% | 94.1% |
| Mean citation score | 100.0% | 97.9% |
| Mean time (seconds) | 17.0 | 31.6 |
| Median time (seconds) | 16.9 | 31.5 |
| Mean reported uncached input tokens | 6,652 | 14,448 |
| Mean reported total input tokens (including cache) | 21,372 | 46,390 |
| Mean reported output tokens | 1,602 | 1,817 |
| Client-reported cost total (USD) | 1.9363 | 3.2072 |
| Tool failures | 0 | 0 |
| Oversized tool results | 0 | 0 |
| Sessions invoking SQL MCP | 0 | 23 |
| Sessions receiving analysis JSON (may include summary/pages) | 0 | 22 |
| Sessions receiving a bounded summary | 0 | 15 |
| Sessions with Resolved/Partial analysis (not Unresolved) | 0 | 13 |

Total input is the sum of reported input, cache-read and cache-write tokens across client requests. It is not unique source tokens. Reported costs use the client's accounting, not a verified DeepSeek invoice.

## Per-question results

Scores average the two repeats. Tokens include cached input; time includes client/MCP startup and tool failures. All assigned runs stay in the denominator.

| Question | Grep facts | Plugin facts | Grep seconds | Plugin seconds | Grep input tokens | Plugin input tokens |
|---|---:|---:|---:|---:|---:|---:|
| Q01-order-footprint | 83.3% | 91.7% | 13.7 | 36.3 | 23,721 | 87,911 |
| Q02-invoice-eligibility | 100.0% | 100.0% | 20.1 | 30.5 | 35,496 | 53,034 |
| Q03-price-formula | 100.0% | 100.0% | 18.3 | 34.0 | 13,985 | 33,281 |
| Q04-login-password | 87.5% | 81.2% | 14.7 | 28.9 | 10,504 | 38,902 |
| Q05-sensor-upsert | 87.5% | 100.0% | 23.8 | 30.8 | 26,448 | 36,000 |
| Q06-dynamic-reseed | 87.5% | 93.8% | 17.1 | 35.2 | 40,384 | 41,823 |
| Q07-price-callers | 100.0% | 100.0% | 18.2 | 33.4 | 25,938 | 57,641 |
| Q08-ticket-bindings | 100.0% | 100.0% | 15.6 | 32.1 | 13,641 | 46,466 |
| Q09-json-update | 100.0% | 100.0% | 19.0 | 35.0 | 26,044 | 52,020 |
| Q10-json-format | 100.0% | 100.0% | 19.0 | 29.1 | 16,445 | 32,292 |
| Q11-external-clr | 100.0% | 100.0% | 10.1 | 24.5 | 8,243 | 28,089 |
| Q12-fulfill-variants | 68.8% | 62.5% | 14.5 | 29.6 | 15,611 | 49,218 |

## Review and limitations

Each criterion is 0 (missing/wrong), 0.5 (partly correct), or 1 (correct). Equal-weight mean within task, then across tasks. Score source citation support separately (0/0.5/1). A passing answer needs all criteria correct, supported citations, no material unsupported claim, and a successful client result. Review answers against source; do not grade by keyword presence or self-reported confidence. Reviewer is the implementing Codex agent, not an independent human. Failures stay in the denominator.

The implementing agent reviewed the answers against the pre-run rubric and source. This is not independent human review. Baseline-first order, uncontrolled OS/provider caches and load, related procedures, selected questions and few repeats limit generalization. This measures the cold prescribed workflow, not warm persisted-cache behavior or a separate LSP-versus-MCP/codebase-memory experiment. Availability without actual MCP calls does not establish an analyzer benefit.

Frozen corpus SHA256: `24023744adea38b138dd041e5134a66f5ac4fa211060f0ba6976e15aeebc00d7`. Source revision: `e5d7bb90ca0b695ac14f61458047bfb5a498459e`. Full questions/rubric, raw transcripts, answers, ungraded metrics and explicit review notes are retained alongside this report.

Clean native artifact `dc70df8d5b16a06c94ddaa1405d548b876e9e9f9a3bd2ff91a65873f611a9285`; source `2b37f30`; Windows ZIP `990588c2a8a6b0c1fabd0a2223a5019b5d256b4ac0b35a892f49cd3ae95f5e9b`. [Curated evidence](../../evidence/benchmark-final-clean-20261017.json), [predeclared protocol](../final-candidate-protocol.md), [first combined artifact](sql-server-samples-combined-first-20261017.md), [separate earlier availability cohort](sql-server-samples-available-first-20261017.md). All earlier results remain retained. No superiority claim.
