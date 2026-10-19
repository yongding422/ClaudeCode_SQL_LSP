# SQL Server Samples: prewarmed native index

Fresh model clients; same owned native MCP backend prewarmed before each measured question. Both arms preload SQL bytes. Warmup replies never enter model context. Saved caches disabled; SQL never executed.

| Metric | Grep + reading | Prewarmed plugin + file tools |
|---|---:|---:|
| Successful sessions | 24 | 24 |
| Strict passes | 9 | 12 |
| Factual score | 90.8% | 93.6% |
| Citation score | 95.8% | 100.0% |
| Measured question seconds | 18.10 | 23.30 |
| Warmup seconds | 3.86 | 10.15 |
| Warmup-inclusive seconds | 21.96 | 33.45 |
| Total reported input tokens incl. cache | 23,430 | 43,446 |
| Uncached input tokens | 7,574 | 13,942 |
| Cache-read tokens | 15,856 | 29,504 |
| Output tokens | 1,519 | 1,991 |
| Reported cost USD total | 2.0107 | 3.2218 |
| Tool errors | 0 | 0 |
| Overflows | 0 | 0 |
| Sessions consuming supported body analysis | 0 | 10 |

## Per-question results

| Question | Grep facts | Plugin facts | Grep seconds | Warm plugin seconds | Grep input | Plugin input |
|---|---:|---:|---:|---:|---:|---:|
| Q01-order-footprint | 91.7% | 91.7% | 18.91 | 21.46 | 51286 | 55266 |
| Q02-invoice-eligibility | 100.0% | 100.0% | 18.61 | 20.48 | 26730 | 40138 |
| Q03-price-formula | 100.0% | 100.0% | 19.95 | 26.40 | 10897 | 33324 |
| Q04-login-password | 75.0% | 81.2% | 15.47 | 20.36 | 16533 | 39158 |
| Q05-sensor-upsert | 93.8% | 93.8% | 20.95 | 19.72 | 33370 | 47818 |
| Q06-dynamic-reseed | 87.5% | 87.5% | 13.39 | 28.81 | 14702 | 42118 |
| Q07-price-callers | 91.7% | 100.0% | 28.82 | 36.32 | 38715 | 67205 |
| Q08-ticket-bindings | 93.8% | 100.0% | 14.60 | 22.26 | 13744 | 48650 |
| Q09-json-update | 100.0% | 100.0% | 21.10 | 24.14 | 27427 | 46580 |
| Q10-json-format | 100.0% | 100.0% | 20.62 | 23.45 | 18331 | 24014 |
| Q11-external-clr | 93.8% | 93.8% | 10.60 | 14.10 | 8250 | 28577 |
| Q12-fulfill-variants | 62.5% | 75.0% | 14.15 | 22.07 | 21169 | 48509 |

All assigned outcomes/fallbacks remain in their arm. Review is implementing-agent, not independent/blinded; two repeats on selected related questions, baseline first, OS/provider caches/load uncontrolled. Costs are client-reported, not invoices. Startup/copy exclusions: SQL snapshot copying excluded in both arms; file preloading and native warmup excluded only from measured-question time and included in warmup-inclusive time. No previous answer/context crosses questions. The relay adds transport overhead and replaces fresh MCP initialization with the exact prewarmed backend's supported initialization response; requested/negotiated protocol versions are retained. Candidate native binaries/instructions/tools unchanged.

This is a warm-server ablation, not a cold replication or a multi-turn conversation experiment. It does not measure LSP-specific benefit or codebase-memory. Historical comparisons have explicit cache-condition and relay-transport mismatches.

## Outcome

Prewarming eliminated source-index initialization from the measured SQL-tool calls, but the plugin did not meet the performance/token reduction objective. Against the fresh matched Grep arm it used **28.7% more question time** and **85.4% more total input tokens**. Reviewed factual coverage was **93.6% versus 90.8%** (+2.78 points), citation support 100% versus 95.8%, strict passes 12/24 versus 9/24, and material unsupported claims occurred in 8/24 versus 11/24 answers. These are different quality dimensions, not proof of general superiority.

The predeclared structural group improved facts from 87.0% to 91.1% and strict passes from 4 to 7/16, with no task-mean factual regressions. The structural resource objective still failed: question-time ratio 1.322 and input ratio 1.798; its quality gate passed. The natural-availability simple-control objective is inapplicable to this prescribed workflow. No extra availability cohort was run.

## Verified warm state and cost boundaries

All 24 treatment workspaces were prewarmed and verified using a newly owned native backend, at the exact SQL path subsequently used by the model client. Twenty-three sessions actually called SQL MCP; one ad-hoc script control used only file tools. All 85 measured native SQL calls came from their recorded backend PID and reported `coldStart: false`. Mean per-active-session reported initialization was about **0.00135 ms**, versus 7.264 seconds in the preceding cold study; these are server-stage counters, not additive causal decompositions of model time. The first warmup query in every backend was cold and the second warm, each with exactly 1,099 indexed files. The 371 files/687 parse errors were still reported, so warming is not proof of complete parse coverage.

Mean treatment preparation was 10.15 seconds: about 3.80 seconds reading all SQL bytes and 6.35 seconds starting/initializing/verifying the native backend. The Grep arm received the same SQL-byte preload, averaging 3.86 seconds. Adding preparation gives **33.45 seconds for the plugin versus 21.96 for Grep**. Reusing an existing backend can amortize index creation across later questions; this experiment reports individual prepared-question cost and does not simulate a whole multi-turn conversation. Both timing views exclude SQL-copy preparation and final owned-backend teardown. No warmup response or prior answer enters the model context.

The adapter forwards modern `server/discover` probes to the native server before returning that server's supported initialize response. Claude negotiated protocol 2025-11-25. It forwards actual tools/results rather than caching question-specific reports. The original packaged tool definitions, server instructions and native binary remain unchanged. The authenticated loopback/stdio transport adds overhead, so this is a labeled warm-server ablation rather than an exact reproduction of direct native stdio.

## What warming did not remove

Treatment still made 85 MCP calls plus 43 Read/10 Grep/10 Glob calls; the baseline made 56 Read/24 Grep/38 Glob calls. Twenty-one treatment sessions received analysis JSON, fifteen received bounded summaries, and only ten consumed supported body analysis/expansions; these counts overlap. Reseeding still returned ambiguous identities and used source fallback. The query itself still requires model reasoning, discovery, report expansion and source verification. There were zero actual tool errors/overflows in the completed valid study; a model describing report pagination as oversized is not a client failure.

Index creation uses local CPU and does not itself consume model tokens. Total model input here includes uncached input, cache-read and cache-write categories across requests; the recorded 43,446 versus 23,430 token difference remains after warming. All token values are known in the valid study. Model-provider prompt caches remain uncontrolled.

## Same-candidate cold comparison

The previous cold 0.16.0 run reported Grep/plugin means of 19.32/32.17 seconds and 22,605/46,316 total input tokens. This warm run reports 18.10/23.30 seconds and 23,430/43,446. The observed plugin question-time reduction is **27.6%**. This is descriptive: the warm condition and relay changed, different model answers were produced, and the matched Grep mean also changed. It does not establish a causal percentage or show that caching improves correctness. The new matched Grep comparison is primary.

That cold run's factual scores were 92.9%/91.8%; the new scores are 90.8%/93.6%. All 48 new answers were reread and graded from a blank template. Selected questions, only two repeats and implementing-agent review limit confidence; no historical grades were copied or pooled selectively. The retained 0.14.0 comparison explicitly marks cache-condition and MCP-transport mismatches.

## Setup recovery and accounting

The first attempted warm study is preserved as **invalid setup**, not pooled with these results. It completed 24 Grep sessions and two treatment sessions with no SQL tools, then two in-flight treatment sessions were stopped. The relay incorrectly required `initialize` as the client's first message, rejecting Claude's `server/discover` probe. The earlier protocol-version hypothesis was not the verified cause. No SQL-tool availability or answer credit is inferred from that failed connection. After correcting probe forwarding and adding a regression test, a real Claude pilot successfully called the same warm backend; then a new full 48-session study began. Nothing was selectively retried within the valid study.

The valid study reports **$5.232475** across both arms. The invalid setup reports $2.196047 known; the failed and successful connectivity pilots report $0.050945/$0.042368. Two interrupted sessions have unknown usage, retained as unknown rather than zero. These are client accounting values, not verified provider invoices. Each full-study session had a $0.50 reported cap; each connectivity pilot $0.10. Pilots/invalid sessions are excluded from primary metrics but not hidden from accounting.

## Reproduction and evidence

Model `deepseek-v4-pro` via `api.deepseek.com`, Claude Code 2.1.302, low effort, seed 42, baseline first, two workers, 180-second session timeout. Frozen source revision `e5d7bb90ca0b695ac14f61458047bfb5a498459e`, 1,099 SQL files, corpus hash `24023744adea38b138dd041e5134a66f5ac4fa211060f0ba6976e15aeebc00d7`.

Native Windows x64 0.16.0 built from clean source `2b37f30195ad0276e69c7a9d255e99cc3826495f`; runtime fingerprint `dc70df8d5b16a06c94ddaa1405d548b876e9e9f9a3bd2ff91a65873f611a9285`; ZIP SHA256 `990588c2a8a6b0c1fabd0a2223a5019b5d256b4ac0b35a892f49cd3ae95f5e9b`. Product C# code and binaries were unchanged. New benchmark harness checks: **35 passed, one Windows symlink skip**. Native wire and actual Claude connectivity pilots passed before the valid run. The exact candidate's prior C#/native/package checks remain in the release verification record; they were not newly rerun as part of this measurement.

Source SQL and global plugin configuration were not modified; the source folder retains only its preexisting untracked `.sqlscriptdom.json`. No credentials were copied, SQL executed or database connection used. There were no isolation/warmth audit findings. Zero LSP/Skill calls were observed, so this cannot establish an LSP-specific benefit or a comparison with codebase-memory.


- [Questions and source-backed rubric](../sql-server-samples-suite.json)
- [Warm experiment protocol and rerun commands](../warm-index-protocol.md)
- [Reviewed evidence, all 48 review notes and native warm-state proof](../../evidence/sql-server-samples-warm-20261018.json)
- [Reviewed metrics JSON](../../../artifacts/sample-study-warm-verified-20261018/reviewed-results.json), [CSV](../../../artifacts/sample-study-warm-verified-20261018/reviewed-results.csv), [raw metrics](../../../artifacts/sample-study-warm-verified-20261018/runs/metrics.csv)
- [Blank template](../../../artifacts/sample-study-warm-verified-20261018/grades.template.json), [completed new grades](../../../artifacts/sample-study-warm-verified-20261018/grades.json), [all raw sessions and warmup records](../../../artifacts/sample-study-warm-verified-20261018/runs/)
- [Cohort gates](../../../artifacts/sample-study-warm-verified-20261018/cohorts.json), [failure audit](../../../artifacts/sample-study-warm-verified-20261018/client-failures.json), [retained 0.14.0 comparison with explicit mismatches](../../../artifacts/sample-study-warm-verified-20261018/baseline-comparison.json)
- [Previous cold 0.16.0 report](sql-server-samples-rerun-20261018.md), [exact candidate CI record](../../evidence/benchmark-fixes-ci.json)
- [Invalid setup record](../../../artifacts/sample-study-warm-20261018-0240/INVALID-SETUP.json), [successful actual Claude connectivity pilot](../../../artifacts/warm-claude-connectivity-pilot-20261018-v2/runs/run-0001/warmup.json)
- [Warm runner](../../../scripts/warm-sample-study.py), [relay](../../../scripts/warm_mcp_relay.py), [regression tests](../../../scripts/test_warm_study.py)
