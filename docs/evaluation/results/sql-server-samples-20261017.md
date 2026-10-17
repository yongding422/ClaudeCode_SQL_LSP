# SQL Server Samples: measured results

**On this suite, the released plugin was slower, used more reported input tokens, and did not improve overall factual coverage.**

Model: `deepseek-v4-pro` through `api.deepseek.com`; plugin 0.14.0. 12 questions × 2 repeats × 2 conditions, baseline first. Two concurrent clients within each phase; fresh SQL-only workspaces. No SQL was executed.

| Metric | Read/Grep/Glob | SQL plugin + file tools |
|---|---:|---:|
| Successful sessions | 24 | 24 |
| Strict passing answers | 12 | 7 |
| Mean factual score | 94.1% | 90.6% |
| Mean citation score | 100.0% | 93.8% |
| Mean time (seconds) | 18.9 | 42.5 |
| Median time (seconds) | 18.8 | 39.1 |
| Mean reported uncached input tokens | 6,493 | 12,654 |
| Mean reported total input tokens (including cache) | 19,779 | 51,481 |
| Mean reported output tokens | 1,535 | 2,681 |
| Client-reported cost total (USD) | 1.8595 | 3.5931 |
| Tool failures | 0 | 47 |
| Oversized tool results | 0 | 41 |
| Sessions invoking SQL MCP | 0 | 24 |
| Sessions with an inline analysis report | 0 | 6 |
| Sessions with Resolved/Partial analysis (not Unresolved) | 0 | 5 |

Total input is the sum of reported input, cache-read and cache-write tokens across client requests. It is not unique source tokens. Reported costs use the client's accounting, not a verified DeepSeek invoice.

## Per-question results

Scores average the two repeats. Tokens include cached input; time includes client/MCP startup and tool failures. All assigned runs stay in the denominator.

| Question | Grep facts | Plugin facts | Grep seconds | Plugin seconds | Grep input tokens | Plugin input tokens |
|---|---:|---:|---:|---:|---:|---:|
| Q01-order-footprint | 91.7% | 83.3% | 20.5 | 47.4 | 26,648 | 84,274 |
| Q02-invoice-eligibility | 100.0% | 100.0% | 23.6 | 39.5 | 32,996 | 54,664 |
| Q03-price-formula | 100.0% | 91.7% | 23.1 | 39.1 | 10,783 | 29,369 |
| Q04-login-password | 87.5% | 100.0% | 15.7 | 36.6 | 13,661 | 34,846 |
| Q05-sensor-upsert | 87.5% | 87.5% | 25.3 | 66.9 | 33,099 | 98,850 |
| Q06-dynamic-reseed | 87.5% | 87.5% | 19.3 | 39.4 | 25,560 | 48,839 |
| Q07-price-callers | 100.0% | 50.0% | 20.0 | 40.1 | 32,387 | 36,870 |
| Q08-ticket-bindings | 100.0% | 100.0% | 16.8 | 38.3 | 11,164 | 37,574 |
| Q09-json-update | 100.0% | 100.0% | 17.1 | 37.7 | 14,817 | 48,012 |
| Q10-json-format | 100.0% | 100.0% | 17.8 | 48.5 | 13,434 | 57,879 |
| Q11-external-clr | 100.0% | 100.0% | 11.7 | 37.3 | 6,631 | 36,278 |
| Q12-fulfill-variants | 75.0% | 87.5% | 16.2 | 39.2 | 16,164 | 50,316 |

## Review and limitations

Each criterion is 0 (missing/wrong), 0.5 (partly correct), or 1 (correct). Equal-weight mean within task, then across tasks. Score source citation support separately (0/0.5/1). A passing answer needs all criteria correct, supported citations, no material unsupported claim, and a successful client result. Review answers against source; do not grade by keyword presence or self-reported confidence. Reviewer is the implementing Codex agent, not an independent human. Failures stay in the denominator.

The implementing agent reviewed the answers against the pre-run rubric and source. This is not independent human review. Baseline-first order, uncontrolled OS/provider caches and load, related procedures, selected questions and two repeats limit generalization. This measures a cold, instructed plugin-assisted workflow, not a warm persisted-cache workflow or a separate LSP-versus-MCP/codebase-memory experiment.

Frozen corpus SHA256: `24023744adea38b138dd041e5134a66f5ac4fa211060f0ba6976e15aeebc00d7`. Source revision: `e5d7bb90ca0b695ac14f61458047bfb5a498459e`. Full questions/rubric, raw transcripts, answers, ungraded metrics and explicit review notes are retained alongside this report.

## Interpretation and next fixes

The released plugin did **not** outperform Read/Grep/Glob on this cold-workflow suite. The plugin arm took **2.25 times as long** on average and used **2.60 times the reported total input tokens**, with lower reviewed factual coverage (90.6% versus 94.1%). Uncached input also increased (12,654 versus 6,493 per answer). These are descriptive results for the selected questions and configured model, not a general SQL-tool ranking.

Each fresh MCP process also initialized the workspace before answering. The 24 recorded cold initializations averaged 9.95 seconds (median 9.98; range 9.26–10.99), which is included in end-to-end latency. The experiment did not isolate how much of the remaining difference came from retries, tool descriptions, responses, extra model turns or provider variation.

The plugin was connected in all 24 treatment sessions. Discovery overflowed **41 times**, in every treatment session; five sessions obtained Resolved module analyses, while 19 did not. One additional session got an Unresolved response for an ad-hoc script statement. All 48 client sessions returned answers; none timed out. The 47 tool failures include the 41 overflows and six other tool errors. No LSP or Skill calls were observed, so this run mainly measures the MCP-assisted workflow and cannot isolate a benefit from LSP.

One cause of the discovery overflows is observable in the retained JSON files: `find_procedures` filters matching modules, but also includes **672 workspace-wide source issues** on every query. A one-match pilot response contains about 1 KB of module metadata alongside over 200 KB of source issues. Across the full study, 41 saved discovery responses range from **216,702 to 253,078 UTF-8 bytes**. Models sometimes described these as an unfiltered module index; inspection shows that diagnosis is inaccurate. The workspace-wide issue list dominates the payload. The code caps diagnostics at 1,000 entries, but that cap is too large for this tool response; maxResults limits module matches only, and discovery has no small diagnostic byte budget. The server reports 1,095 indexed documents versus 1,099 copied SQL files; the four-document coverage gap was not diagnosed in this study and should not be treated as proof of complete indexing.

Quality was mixed: the plugin arm improved mean rubric coverage on login/password handling and fulfillment variants, regressed on order-transaction explanation, pricing comparisons and pricing-call classification, and tied on seven questions. On the pricing-call question, one plugin-arm answer explicitly mistook a dynamic CREATE PROCEDURE body for immediate pricing execution. On the sensor procedure, both plugin repeats confused THROW's state argument with severity despite obtaining analyzer reports. The third THROW argument is state; a newly raised THROW exception has severity 16. [Microsoft THROW documentation](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/throw-transact-sql?view=sql-server-ver15).

Two other review checks use documented engine behavior: no-row SELECT assignment can retain a previous variable value ([SELECT @local_variable](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/select-local-variable-transact-sql?view=sql-server-ver17)); default OPENJSON key matching is case-sensitive ([OPENJSON](https://learn.microsoft.com/en-us/sql/t-sql/functions/openjson-transact-sql?view=sql-server-ver16)). Explicit grades and notes are retained for all 48 answers. Quality scores are an implementing-agent review of a pre-run rubric, not independent or blinded human judgments; strict-pass counts are especially sensitive to review of extra unsupported assurances.

The next implementation priorities supported by these observations are:

1. Bound/paginate discovery diagnostics, with issue counts and an explicit truncation indicator. A narrow name query should return matching module identities and a small coverage summary.
2. Expose file/index coverage gaps and database/project scope clearly, so unrelated sample applications do not create misleading relation ambiguity. Preserve warnings about unresolved SQL, dynamic DDL, external implementations and unmodeled trigger/history effects.
3. Make compact analyzer-first recovery practical with exact file/position input when discovery is incomplete. Strengthen workflow guidance about THROW state versus severity and why an empty call list does not prove absence of indirect effects.
4. Rerun this frozen suite after those changes. Keep this released-version result as the baseline; add a separately labeled warm-cache experiment later. Do not claim token savings before measuring them.

## Evidence and verification

The study retains `manifest.json`, `tasks.json`, `rubric.json`, `grades.json`, `reviewed-results.csv/json`, `discovery-diagnostics.json`, `measurement-code.json`, all 48 answers/transcripts, and a separate excluded two-session connectivity pilot under `artifacts/sample-study-20261017`. Credentials are not copied into the study. Original SQL checkout and global plugin configuration were not changed. Server metrics and persisted analysis caches stayed disabled.

The corpus and rubric/task hashes were verified after measurement, and the measured runner hash was unchanged. All observed models matched deepseek-v4-pro; baseline sessions had no MCP servers or non-file tool calls; treatment sessions connected only the SQL analysis MCP server. All 48 runs reported complete token/cache categories. The released MCP executable's `--evaluation-summary` accepted 48 rows, matched 24 pairs and reported no setting mismatch or invalid row. Its quality gate correctly remained unscored. Its native median uses the lower middle observation for even counts; this report uses Python's conventional midpoint median. Ratio-of-means comparisons here also differ from the native importer's mean of per-pair ratios.

Python harness verification: **16 passed, 1 skipped** (Windows host does not permit creation of the symbolic-link fixture). No server implementation changes were made during the experiment. The local working changes add the benchmark suite, preparation/review scripts and runner controls.

