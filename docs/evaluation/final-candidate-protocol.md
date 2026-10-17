# Final candidate evaluation protocol

Freeze this protocol before running the combined candidate. Keep the original 12 questions/gold and 0.14.0 results unchanged. R1's discovery-only 48-session result is retained separately; its outcome is not a substitute for a new matched Grep arm.

Run 12 tasks × two repeats × two arms, prescribed workflow, `deepseek-v4-pro`, effort low, two clients per phase, baseline first, seed 42, fresh SQL-only copies, 180-second timeout and $0.50 client-reported per-session cap. Verify the packaged native candidate/MCP tools and fingerprint before preparation. Grade every new answer from a blank template; implementing-agent review remains disclosed when no independent reviewer is available. Preserve all failed/rejected/unresolved sessions.

Predeclare task groups from the existing frozen suite:

| Group | Questions | Role |
|---|---|---|
| Structural/behavior | Q01, Q02, Q04, Q05, Q06, Q07, Q08, Q12 | Transitive effects, branches/errors, caller bindings, dynamic DDL and cross-database variants |
| Short-source controls | Q03, Q09, Q10, Q11 | Literal arithmetic, short JSON handling, ad-hoc script and external declarations |

These related task groups are descriptive, not independent holdouts. Test the 20% time/total-input target only against the matched structural group at no worse reviewed quality. Report all regressions and uncertainty. The simple-control 10% objective concerns natural plugin availability, so a prescribed run cannot establish it.

The harness now has separately recorded `--workflow prescribed|available|forced`. Prescribed preserves the exact original prompt instruction; available lets the client choose file/plugin tools, and forced is a diagnostic ablation. A changed workflow is an explicit historical mismatch and must never be silently substituted. New frozen realistic cohorts/independent review and greater repetitions remain necessary before broad superiority claims. No paid available/forced run is implied merely by adding these modes.

Body evidence metrics distinguish an actual resolved/partial module report or expanded body values from a paged summary alone. Summary counts/metadata do not become proof that a developer/model consumed the detailed analysis. Correctness remains explicit source review rather than tool status.

Server-only probes label empty-cache construction, verified persisted restart and same-live-process work separately. Edit/added-candidate/deletion/parser-change tests verify invalidation. Native four-platform checks remain separate from framework-dependent exploratory timings. No warm plugin result is compared to cold Grep without its label or excluded build/validation cost.

Codebase-memory requires a separately configured, versioned third arm with verified tools/coverage. Those graph tools are unavailable in this chat; Grep is not a proxy for that MCP. External SQL access remains disabled; no approved user SQL Server exists. This evaluation performs no SQL execution or global plugin changes.

## Additional frozen cohort

Before final candidate execution, freeze [six additional questions and source-backed criteria](sql-server-samples-additional-suite.json). Use two repeats/two arms (24 sessions), the same model/client/caps/SQL snapshot, and `--workflow available`. H01/H02 are simple known-source controls; H03–H06 investigate multi-file effects, same-name modules, error ordering and dynamic security DDL. These are new questions on a familiar selected corpus, not a blinded independent holdout. Original Q11 remains the external-body uncertainty control. Edit/freshness behavior is covered by deterministic materialized-index integration fixtures (same-size/time edit, competing declaration, rename/delete and parser/config changes); a static question is insufficient to test freshness.

Report this availability cohort separately from the original prescribed 48 sessions. Evaluate the 10% median time/total-input overhead target on H01/H02, and the 20% structural target on H03–H06 at no worse factual/citation quality. Do not pool them into the historical rubric. Total authorized candidate evaluation: 72 fresh sessions, each with the same $0.50 client-reported cap, maximum reported cap $36. No automatic model retries or exclusions.

## Post-measurement paging repair

The first combined artifact at `9e23fb8` completed all 72 sessions, with explicit source review retained. Its prescribed arm had zero wire overflows but 14 calls mixed a matching `moduleRef` with `reportRef` and were rejected. The availability arm made no MCP calls, so its resource/quality differences do not establish an analyzer benefit. Preserve both experiments, every answer and their grades unchanged.

The repair at `c79ce14` accepts only a redundant matching report-root identity/scope and retains stale-generation checks. It also clarifies page parameters. Before measuring it, predeclare a **new** original-suite matched comparison: 48 fresh sessions, the same two repeats/model/provider/order/caps and blank answer review. This adds a $24 maximum client-reported cap; it is a changed-artifact experiment, not a retry replacing failed sessions. The earlier availability cohort remains separately labeled evidence for the first artifact and must not be silently relabeled as the repaired artifact's outcome. No further model reruns are automatic merely because an engineering target is missed.

Final artifact packaging additionally excludes maintainer `docs/evaluation`, `docs/evidence`, `docs/plans` and the benchmark skill; archive/catalog/install checks enforce this. Earlier native packages copied all docs, including rubrics and reviewed results. Restricted transcripts show no reads of this material, but its presence is a contamination risk, so earlier results carry that qualification. The final matched comparison must use the cleaned package, not the first artifact or a docs-inclusive rebuild. Ordinary runtime documentation and the explain-procedure skill remain legitimate treatment components.
