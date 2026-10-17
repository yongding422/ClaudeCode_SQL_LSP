# Implementation progress

Original backlog: [R0–R7](benchmark-driven-improvement-plan.md). Baseline 0.14.0 remains immutable; general IDE M6 is deferred.

| Slice | Current state |
|---|---|
| R0 | Rerunnable skill, file ledger/read-parse instrumentation and all baseline failures diagnosed; explicit enumeration/telemetry limits retained. |
| R1 | Bounded discovery, separately paged issues/files, source-bound cursors and configurable 8–16 KiB actual-wire ceiling implemented/tested. Discovery-only 48-session rerun reviewed; no discovery overflows. R4 adds bounded 16–64 KiB analysis with deliberate expansion. |
| R2 | Explicit static project contexts/comparison/reference mappings and literal sqlproj membership implemented/tested. CTAS crash fixed; all 1,099 benchmark-sized sources indexed, zero unexplained failures. Unverified projects stay labeled. |
| R3a | Immutable live index/AST reuse, content-verified reconciliation and bounded scheduling implemented/tested; startup-gap protection retained. |
| R3b | Opt-in materialized facts, verified restart and bounded on-demand ASTs implemented/tested. 1,099 files restored with zero parses; one exploratory restart 3.54 s, above the 2 s target. No new NuGet/native dependency. |
| R4 | Implemented/tested: compact default, bounded report expansion, validated file/declaration and module selectors, source-bound evidence references and adaptive skill/tool routing. Full DTO reconstruction uses tested wire pages. |
| R5 | Implemented/tested: named THROW state/new/rethrow, conservative following-RETURN issue, embedded-definition text, sequence/SELECT/atomic/external observations and case-exact OPENJSON evidence; shared core/MCP/LSP regression gates pass. |
| R6 | R1 matched 48-session comparison complete/reviewed. Remaining ablations/final candidate and separately frozen cohorts pending; no superiority claim. |
| R7 | Pending final four-platform package/marketplace verification and evidence-backed release. Global installed plugin unchanged. |

See [R1 reviewed outcome](../evaluation/results/sql-server-samples-r1-20261017.md), [R0 diagnosis](../evidence/benchmark-r0-diagnosis-20261017.json), [R2 accounting](../evidence/benchmark-r2-coverage-20261017.json), [live probe](../evidence/benchmark-r3a-live-probe-20261017.json) and [restart probe](../evidence/benchmark-r3b-restart-probe-20261017.json). The discovery repair improves usability while the measured cold prescribed workflow still loses to Grep on latency/input.
