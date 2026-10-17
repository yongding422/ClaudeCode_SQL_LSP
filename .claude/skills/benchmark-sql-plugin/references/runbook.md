# Rerun the frozen SQL plugin benchmark

Run from the SqlScriptDOM_LSP repository. Python 3.11+ and Claude Code with the protocol's isolation flags are required. Native plugin artifacts must match the host, include `build.json`, `.mcp.json`, `.lsp.json`, manifests and executables, and use the offline `--stdio` entry point. `check` does not prove the server runs: verify the candidate's packaged MCP integration tests or a separately labeled connectivity pilot before paid sessions. Do not silently use an older artifact when candidate packaging fails.

Source revision: `e5d7bb90ca0b695ac14f61458047bfb5a498459e`. Frozen questions/rubric: `docs/evaluation/sql-server-samples-suite.json`. Retained historical aggregate: `docs/evidence/sql-server-samples-evaluation-20261017.json`. Full protocol: `docs/evaluation/sql-server-samples-study.md`. Improvement backlog: `docs/plans/benchmark-driven-improvement-plan.md`.

## Prepare and execute

Use the user's actual source/candidate/output paths below. Each output directory is new; source and plugin paths are read-only inputs. `deepseek-v4-pro` reproduces this machine's original model. If the user chooses another model/provider, run it in both arms and label the historical mismatch rather than claiming an exact reproduction.

```powershell
python scripts/rerun-sample-study.py check --plugin D:/path/to/candidate
python scripts/rerun-sample-study.py prepare --source D:/Project/sql-server-samples --plugin D:/path/to/candidate --model deepseek-v4-pro --output artifacts/sample-study-candidate-UNIQUE
python scripts/rerun-sample-study.py run --study artifacts/sample-study-candidate-UNIQUE --dry-run
python scripts/rerun-sample-study.py run --study artifacts/sample-study-candidate-UNIQUE
```

These Python commands also work on macOS/Linux with their native artifact and absolute source paths. No PowerShell-specific syntax is required. `prepare` freezes 1,099 SQL files on the original corpus, tasks/rubric, candidate runtime bits, runner/controller hashes and settings. `run` rechecks them and refuses changed inputs or an already existing run directory. The original 2 MiB per-file limit excludes one oversized countries seed script; do not silently change that rule. The helper never copies a prebuilt SQL index or old answer into a participant workspace.

Default settings can be changed explicitly at preparation (`--repeats`, `--workers`, `--timeout`, `--budget`), but differences are part of the experiment. A client-reported budget cap is not an independent guarantee of provider billing. Preflight requires no model call; an authorized full run uses up to 48 sessions by default. Do not change credential/configuration values to match the historical provider without the user's direction.

`prepare --workflow prescribed` is the default and preserves the historical instruction. `available` lets the model choose tools; `forced` is a diagnostic analysis-first ablation. Each requires a new study and is recorded as a historical workflow mismatch. See `docs/evaluation/final-candidate-protocol.md` for frozen task groups, remaining holdout limits and no-model reuse probes. Reports distinguish bounded summaries from consumed/expanded body facts.

Use `prepare --suite docs/evaluation/sql-server-samples-additional-suite.json --workflow available` for the separately frozen six-question cohort (24 sessions at two repeats). The default suite stays unchanged. Preparation validates source-backed evidence and freezes a separate rubric hash; its historical comparison must report that rubric/workflow mismatch. Define new gold before running new questions, and report each cohort separately.

## Review and compare

```powershell
python scripts/rerun-sample-study.py grade-template --study artifacts/sample-study-candidate-UNIQUE
```

This creates `grades.template.json` with **null** scores. Read `rubric.json`, then each `runs/run-NNNN/answer.txt` and supporting transcript/source. Save the completed review as `grades.json`; an answer with no requested facts still needs an explicit zero and explanation. The reporting validator rejects blank/missing scores. The template never overwrites an existing file.

```powershell
python scripts/rerun-sample-study.py report --study artifacts/sample-study-candidate-UNIQUE --grades artifacts/sample-study-candidate-UNIQUE/grades.json
```

Outputs include `report.md`, `reviewed-results.csv/json` and `baseline-comparison.md/json`. Raw `runs/metrics.csv`, per-session metrics/answers/transcripts, frozen manifests and `rerun-plan.json` remain local. The historical comparison reports matching dimensions and mismatches. It is descriptive even if all recorded dimensions match; uncontrolled provider/OS caches, load and selected tasks still limit causal claims. The report measures the plugin-assisted MCP workflow; zero LSP calls cannot demonstrate an LSP-specific benefit.

After review, `python scripts/summarize-workload-cohorts.py --reviewed-results <study>/reviewed-results.json --output <study>/cohorts.json` applies the predeclared structural and simple-control targets, with matched-session validation, unknown-usage handling and quality gates. It reports nearest-rank p95 observations and midpoint medians. A prescribed workflow cannot establish the natural-availability simple-control target. Retain per-question regressions even when a cohort mean improves.

## Interpret and recover

- Overflow/rejected result: count the failure. Do not count a saved giant payload as evidence the model consumed or successfully analyzed it. Total model input includes provider cache-read/write categories, not the byte size of saved files.
- Unresolved analysis: count the request, not a usable supported module report. Fallback answers remain in the assigned treatment arm.
- Timeout/budget/process failure: preserve the session and give missing answers zero quality in the all-assigned result. Missing usage remains null. Do not resume by rerunning only unfavorable questions.
- CLI/auth/candidate failure: report the concrete prerequisite failure without printing secrets. Correct a broken setup only within the user's scope, retain failed evidence, and prepare a new directory. Do not change global plugins, source SQL or the frozen rubric as recovery.
- Corpus/source revision changes: prepare a new suite version only after reviewing source-backed gold. Historical matching should visibly fail until the dataset/rubric identity is understood.
- Suspicious speedup: verify actual loaded candidate, observed model, MCP calls, source visibility, successful reports, quality and cache condition before crediting it.
- User stops a run: retain incomplete evidence and stop owned processes. Do not turn a follow-up status question into a restart or a scheduled automation.

## Maintenance checks

```powershell
python -m unittest discover -s scripts -p "test_*.py" -v
```

Maintain the repository copy at `.claude/skills/benchmark-sql-plugin/`. The same skill can be installed under Codex's skills directory (`$CODEX_HOME/skills`, or `~/.codex/skills` when unset); update the installed copy from this source after edits. Do not package this maintainer skill into `claude-plugin/skills`; participant workspaces exclude `.claude/` so it cannot leak gold into the measured sessions.

Claude Code project skill discovery/invocation follows [official skill documentation](https://code.claude.com/docs/en/skills). This skill uses portable name/description frontmatter; it does not add hooks, a subagent requirement or automatic runtime commands.
