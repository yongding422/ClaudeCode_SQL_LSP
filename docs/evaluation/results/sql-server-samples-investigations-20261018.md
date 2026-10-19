# SQL investigation benchmark: correctness and acceptance

Completed 20 fresh sessions: five investigation questions, two repeats, Grep/Read/Glob versus the SQL plugin plus file tools. **The plugin had a small reviewer-rated quality advantage, while taking longer and using more tokens. Neither arm reliably produced answers accepted as written.** This selected, small study does not establish general superiority.

| Primary quality outcome | Grep + reading | SQL plugin + file tools |
|---|---:|---:|
| Answers accepted as written | 1/10 | 2/10 |
| Answers requiring minor edits | 1/10 | 0/10 |
| Answers requiring substantial correction | 6/10 | 7/10 |
| Rejected answers | 2/10 | 1/10 |
| Answers with critical errors | 6/10 | 4/10 |
| Answers with material unsupported additional claims | 4/10 | 5/10 |
| Mean required-fact coverage | 79.25% | 84.25% |
| Mean evidence support | 60% | 70% |
| Mean uncertainty handling | 60% | 70% |
| Actual developer acceptance | Unmeasured | Unmeasured |
| Correction minutes / necessary follow-up questions | Unmeasured | Unmeasured |

Acceptance means the **implementing agent's explicit review under the frozen rubric**, not an actual developer's approval. Review was unblinded; arm labels were seen. No independent human review was performed. Required-fact coverage is an equal-question mean of 0/0.5/1 criterion scores, not the proportion of wholly correct answers. Critical-error and unsupported-claim sets overlap; their counts must not be added.

## Per-question findings

| Investigation | Grep accepted / assigned | Plugin accepted / assigned | Source-backed finding |
|---|---:|---:|---|
| Order-creation handover | 0/2 | 2/2 | Both plugin answers correctly explained the unreachable failure RETURN and sequence/transaction boundary. Grep added incorrect failure-status or input-constraint advice. |
| Invoice change review | 0/2 | 0/2 | Grep had one answer requiring a terminology edit and one with a wrong CATCH claim. Plugin answers added incorrect current skipping, sequence-gap or failure-return claims. |
| Password investigation | 1/2 | 0/2 | One Grep answer was complete and cautious. Plugin answers omitted required failure detail or asserted a returned -1/external-login invariant not established by the source. |
| Fulfillment bug review | 0/2 | 0/2 | One plugin answer correctly identified retained variables and proposed a three-order exhaustion test. Other answers missed or misdiagnosed the mechanism; transaction assumptions/completeness also prevented full acceptance. |
| Operational reseeding | 0/2 | 0/2 | Grep introduced incorrect general final-value formulas. Plugin answers improved that reasoning but omitted required temporary-result details or added an incorrect unused-sequence NULL caveat. |

There were two plugin-only accepted pairs, one Grep-only pair, no pairs where both were accepted, and seven where neither was accepted. These are ten paired question/repeat observations, **not ten independent procedures**.

The decision labels distinguish harmful mistakes from completeness gaps. For example, one reseeding plugin answer had no critical error but lacked mandatory `#CurrentValue`/temporary-write explanation in the frozen full-trace rubric. It therefore needed completion; that is different from recommending the wrong runtime behavior. Do not interpret every nonaccepted answer as equally dangerous, or the strict reviewer acceptance rate as measured user satisfaction. Some declaration/detail requirements are stringent; any relaxation belongs in a future rubric version, not a regrade to improve this result.

## Why fact coverage alone is insufficient

Several answers covered every requested fact while adding a consequential false claim. The acceptance review preserved the coverage score and blocked acceptance separately. Examples include assuming invalid customer IDs cannot raise foreign-key errors, claiming validation never reaches CATCH despite its enclosing TRY, or presenting descriptive external-login metadata as an enforced hash invariant.

The fulfillment hazard follows from variables declared outside the loop and no reset before later SELECT assignments. SQL Server retains a variable's existing value when that SELECT returns no rows. This supports the source review; it was not demonstrated by executing the sample database. [Microsoft SELECT assignment documentation](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/select-local-variable-transact-sql?view=sql-server-ver15).

One plugin answer invented a NULL `current_value` for an unused sequence. For an existing visible sequence, Microsoft documents a nonnullable value and the creation start value before first use. A missing metadata row is a different circumstance. [Microsoft sys.sequences documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sequences-transact-sql?view=sql-server-ver17).

Review also checked sequence cache defaults and atomic-block behavior against official documentation. These references were used by the reviewer, not injected into participant prompts or used to change the frozen gold. [CREATE SEQUENCE](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-sequence-transact-sql?view=sql-server-ver17), [atomic blocks](https://learn.microsoft.com/sql/relational-databases/in-memory-oltp/atomic-blocks-in-native-procedures).

## Secondary resource measurements

| Measurement | Grep + reading | SQL plugin + file tools |
|---|---:|---:|
| Successful client sessions | 10/10 | 10/10 |
| Mean first-answer wall time | 36.14 s | 46.98 s |
| Median first-answer wall time | 35.40 s | 46.71 s |
| Mean reported uncached input tokens | 10,941 | 17,191 |
| Mean reported cache-read input tokens | 19,686 | 51,354 |
| Mean reported total input tokens | 30,627 | 68,545 |
| Mean reported output tokens | 2,495 | 2,810 |
| Tool failures retained | 0 | 2 |
| Oversized tool results | 0 | 0 |
| Client-reported cost | $1.269122 | $1.818763 |

The plugin was about **30% slower** and used **124% more reported total input tokens**. Total experimental client-reported cost was **$3.087885**, excluding review effort and any different account billing. Total input sums uncached, cache-read and cache-write categories across model requests; all 20 sessions reported these categories. Cache-write input was zero. First-answer wall time does not include unmeasured developer verification/correction time and is not time to an accepted answer.

## Tool use and experimental controls

- Candidate: native Windows x64 **0.16.0**, clean build source `2b37f30195ad0276e69c7a9d255e99cc3826495f`, runtime fingerprint `dc70df8d5b16a06c94ddaa1405d548b876e9e9f9a3bd2ff91a65873f611a9285`. The existing release was not modified or republished.
- Source: SQL Server Samples `e5d7bb90ca0b695ac14f61458047bfb5a498459e`; 1,099 frozen SQL files; corpus SHA-256 `24023744adea38b138dd041e5134a66f5ac4fa211060f0ba6976e15aeebc00d7`.
- Model/client: `deepseek-v4-pro` at `api.deepseek.com`, Claude Code `2.1.302`, low effort, 450-word answer constraint. All 20 final answers fit that constraint using whitespace word counts.
- Protocol: cold fresh-client sessions, saved index/report caches disabled, prescribed plugin-first workflow, baseline first, two workers per phase, two repeats, seed 42, 180-second timeout, $0.50 reported per-session cap. The samples folder's enabled `.sqlscriptdom.json` is deliberately not inherited by isolated participants.
- The acceptance contract was frozen **before** execution. All 20 answers received fresh explicit review. Null templates remain preserved; no historical grades were reused, failed answers discarded or unfavorable sessions retried.
- All ten treatment sessions called MCP: **50 calls** total. Eight received paged summaries, but only four consumed expanded Resolved/Partial body facts. Summary receipt alone is not successful body analysis.
- Both reseeding sessions encountered ambiguous identities across `wwi-ssdt` and `wwi-dw-ssdt` and fell back to file tools. Two explicit file/declaration selector errors occurred in the second repeat; all are retained. Ambiguous results and file fallback do not count as a supported semantic resolution.
- No LSP or Skill calls occurred in either arm. No codebase-memory arm, database connection or SQL execution was used. SQL source and global plugin installation were unchanged. OS/provider caches and load were uncontrolled.

The native preflight completed actual discovery calls before paid execution. Benchmark tooling validation passed **52 tests**, with one Windows symlink test skipped. Frozen source/rubric/candidate identity, baseline isolation, measured tool calls, complete paired schedules and report provenance checks passed; there were no isolation audit findings.

## Evidence and interpretation

The [durable evidence](../../evidence/sql-server-samples-investigations-20261018.json) contains every acceptance grade, finding, source note, paired result and client measurement. The [five questions](../sql-server-samples-investigations-v1.json), [acceptance rubric](../sql-server-samples-investigations-acceptance-v1.json) and [review protocol](../answer-acceptance.md) are versioned separately from the historical 12-question suite. The ordinary historical comparison explicitly reports the changed cohort/rubric; these new percentages must not be substituted into earlier results.

Full local artifacts are retained under:

```text
artifacts/acceptance-investigations-prepared-20261018/   # SQL snapshot, transcripts, answers, metrics, factual grades
artifacts/acceptance-investigations-review-20261018/     # prospective contract, anonymous packet, explicit reviews, blank user feedback
artifacts/acceptance-investigations-results-20261018/    # acceptance report and JSON
```

This is preliminary evidence from five related, previously studied SQL tasks and two repeats, with implementing-agent review. It shows some useful plugin-assisted answers alongside substantial remaining reliability and retrieval problems. It does not measure actual developer approval, correction effort, live SQL results, persisted-cache performance, statistical confidence, or an LSP-specific benefit. No general claim that the plugin beats Grep is supported.
