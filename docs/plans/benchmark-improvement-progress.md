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
| R6 | R1 matched 48 sessions and first combined 48 sessions reviewed, plus six new frozen availability questions/24 sessions. First combined structural resource target and availability overhead target unmet. Post-measurement report-selector repair needs a fresh matched artifact run; no superiority claim. |
| R7 | First combined clean artifact passed all nine native/source/disposable-DB CI jobs. Repaired artifact native CI and final actual marketplace verification/release pending. Global installed plugin unchanged. |

See [R1 reviewed outcome](../evaluation/results/sql-server-samples-r1-20261017.md), [R0 diagnosis](../evidence/benchmark-r0-diagnosis-20261017.json), [R2 accounting](../evidence/benchmark-r2-coverage-20261017.json), [live probe](../evidence/benchmark-r3a-live-probe-20261017.json) and [restart probe](../evidence/benchmark-r3b-restart-probe-20261017.json). The discovery repair improves usability while the measured cold prescribed workflow still loses to Grep on latency/input.

Combined-candidate validation also caught and repaired native Unix URI restoration in persisted snapshots, an integration test expecting the pre-compact response shape, and selected-file errors being hidden by unrelated files reaching the global diagnostic limit. Native CI and the disposable SQL Server job verify the first two; deterministic scope-before-limit regression tests cover the last. Failed CI attempts remain retained as evidence, not successful checks.

The [first combined result](../evaluation/results/sql-server-samples-combined-first-20261017.md) had zero wire overflows but 14 invalid mixed-selector requests. A matching module identity can now accompany report pages while conflicting/stale identities remain rejected. All 329 local C# tests pass (18 environment skips); native revalidation is running. The [availability cohort](../evaluation/results/sql-server-samples-available-first-20261017.md) made no MCP calls, so it cannot establish an analyzer benefit; extra context cost remains visible. This is repair/usability work, not demonstrated Grep superiority.
