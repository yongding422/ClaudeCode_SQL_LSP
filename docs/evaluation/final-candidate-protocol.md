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
