---
name: benchmark-sql-plugin
description: Rerun or review the SqlScriptDOM plugin versus Grep benchmark after a candidate change, preserving the frozen SQL Server Samples suite and comparing reviewed accuracy, time and reported tokens with the retained 0.14.0 result. Use for this repository's evaluation workflow, not ordinary SQL investigation.
---

Run a controlled developer-workflow comparison, not a benchmark designed to make the plugin win. This is a maintainer skill; it is separate from the distributed SQL plugin and must not enter participant workspaces.

Resolve the SqlScriptDOM_LSP repository from the current checkout or a user-supplied path. This machine's known checkout is `D:/Project/SqlScriptDOM_LSP`; the sample source is `D:/Project/sql-server-samples`. Verify paths and the pinned source revision. The executable helper is `<repo>/scripts/rerun-sample-study.py`. Read [the runbook](references/runbook.md) for commands, outputs and recovery.

Choose the operation from the request:

- **Plan, check or dry-run:** inspect prerequisites and candidate fingerprints; prepare inputs if useful, then use `run --dry-run`. These do not invoke a model.
- **Rerun after fixes:** use an explicitly identified built native candidate, run the controlled sessions, review all answers against the frozen rubric, then generate the comparison. A request merely to create this skill or plan fixes does not start another paid run.
- **Review existing run:** use retained answers/transcripts; do not rerun the model to fill an evidence gap or copy historical grades.

For a real rerun:

1. Read the repository's `docs/evaluation/sql-server-samples-study.md` and frozen suite. Keep `docs/evidence/sql-server-samples-evaluation-20261017.json` and original artifacts unchanged. Confirm the candidate's version, build provenance and runtime-file fingerprint; never assume the globally installed marketplace copy contains local fixes. Use actual packaged integration/pilot evidence to verify MCP tools load before committing to a full run.
2. Run the relevant build/tests for candidate changes. Use `check`, then `prepare` into a new directory outside source/plugin. Pin an explicit model; record provider and client version. Defaults reproduce 12 tasks × 2 repeats × 2 arms = 48 fresh sessions, effort low, baseline first, two workers, 180-second timeout and a $0.50 client-reported cap per session. State the run count and cap before execution; reported cost is not a provider invoice. Existing authorization to run the benchmark is sufficient unless the requested scope has changed.
3. Keep the same questions, source visibility and answer constraints. The baseline has Read/Grep/Glob only; the treatment adds only the candidate SQL plugin. The runner isolates other MCP servers, skills, hooks and inherited configuration. It does not execute SQL, access a DB, change global plugin installation or copy credentials. Isolated clients need existing environment-based authentication; do not copy login files or expose tokens to make them work.
4. Execute the prepared study. Record failures, unsupported results and fallback honestly. Do not automatically retry failed sessions, discard them or change the gold after observing results. If the experiment setup is broken, retain it as invalid/pilot evidence, fix the setup and start a new labeled study. On a user stop, stop the owned runner/children and preserve partial evidence; do not automatically restart it.
5. Generate a blank grade template. Read every answer and relevant source citations; fill each 0/0.5/1 criterion, citation score, material-unsupported flag and review note. A tool saying it succeeded is not a correctness grade. Never reuse old scores or prefill passing grades. Keep missing token usage unknown. Review must distinguish a Resolved/Partial module report from an Unresolved response or fallback-only answer. Disclose implementing-agent review when no independent/blinded reviewer is available.
6. Run `report` with the completed grades. Report the fresh matched Grep arm first; compare with retained 0.14.0 separately and disclose every model/provider/corpus/rubric/client/setting mismatch. Include failures and per-question regressions, input/cache/output categories and end-to-end time. Do not claim a causal or general performance advantage from a historical comparison alone.

The helper's current protocol is **cold fresh-client only**. Same-process reuse, persisted-index restart, edited-file, natural-selection or codebase-memory comparisons require separately labeled harness support; do not relabel this run as those modes. The benchmark's original workflow prescribes trying analysis first, and that instruction must not be silently changed in a historical reproduction.

Return links to the new report, question/rubric inputs, reviewed/raw metrics and retained baseline comparison. State what was actually executed, tests and any incompleteness. Preserve the result if Grep wins.
