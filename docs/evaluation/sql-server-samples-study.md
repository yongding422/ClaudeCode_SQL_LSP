# SQL Server Samples: paired SQL understanding study

This study answers a practical question: does **Claude Code plus SqlScriptDOM 0.14.0** answer saved-SQL questions more accurately, faster, or with fewer reported tokens than the same model using Read/Grep/Glob? The baseline includes source reading and model reasoning. It is not a comparison against the execution time of a single `grep` command.

For a candidate rerun, use the [benchmark-sql-plugin maintainer skill](../../.claude/skills/benchmark-sql-plugin/SKILL.md) and `scripts/rerun-sample-study.py`. Its runbook preserves these controls, fingerprints candidate bits, creates blank review templates and generates a separate historical comparison. The original 0.14.0 evidence remains unchanged. Planned performance/quality repairs are in the [improvement plan](../plans/benchmark-driven-improvement-plan.md).

## Frozen inputs and rubric

[The question set and source-backed rubric](sql-server-samples-suite.json) contain 12 questions covering table effects, calls and argument bindings, transaction boundaries, business formulas, dynamic SQL, comments, JSON, database variants, and external CLR uncertainty. Questions include simple text-search controls and cases where the plugin may provide little benefit. The implementing Codex agent prepared the rubric from SQL before model runs. The same agent reviews answers; this is not independent human grading.

The corpus is Microsoft's local `sql-server-samples` checkout at revision `e5d7bb90ca0b695ac14f61458047bfb5a498459e`. Preparation copies SQL into a separate owned directory, excluding linked files, non-SQL material, files over 2 MiB, and build/configuration folders. Every copied file is hashed. Gold answers, questions, manifests, repository instructions and existing configuration never enter model workspaces. The original checkout is not modified. The manifest records exclusions and worktree status; the source commit alone is not a complete data identity.

## Treatment and controls

- Twelve questions, two repeats, two conditions: **48 fresh client sessions**. An initial two-session connectivity pilot is separate and excluded from final statistics.
- Requested model: `deepseek-v4-pro`, using the user's existing DeepSeek API configuration through Claude Code. This is not an Anthropic Claude model. Record the observed model, provider host and CLI version with results.
- Grep condition: only Read/Grep/Glob, no plugins, skills, hooks or MCP servers. Plugin condition: the same file tools plus the released SQL plugin's MCP, LSP and skill. Other plugins and MCP servers, including codebase-memory, are isolated out in both conditions.
- Both receive the identical question and response constraints (450 words and source citations). Treatment-specific workflow instructions ask the baseline to investigate with file tools and the plugin arm to try its analysis tools first, with file-tool verification/fallback. This measures that assisted workflow, not whether an uninstructed agent spontaneously selects the plugin.
- `--restricted` confines file tools to fresh SQL-only workspaces; isolated configuration, strict explicit MCP configuration and disabled hooks prevent inherited customizations. Credentials stay in the inherited environment and are never written to study files.
- Native Windows x64 plugin **0.14.0**, verified release ZIP checksum; no plugin implementation changes during measurement. The user's globally installed plugin is not changed.
- Per-session reported budget cap **$0.50**, timeout **180 seconds**, effort **low**. Two clients may run concurrently within a phase, identically in both conditions. SQL is never executed and no database connection is supplied.
- Baseline runs first, plugin runs second, as requested. Seed 42 shuffles task/repeat order identically in the two phases. Phases do not overlap. This does not counterbalance provider load, OS cache or model prompt-cache effects; report that limitation.
- Saved analysis caches and server metric recording remain disabled. Each client starts afresh; MCP initialization and parsing are included in end-to-end latency. Snapshot copy time is excluded. This is a cold workflow study, not a warm saved-cache benchmark.

## Measures and decision rule

Retain every run's raw stream, final answer, stderr, return code, outcome and metrics. Report timeouts, tool failures, denied tools, missing results and failures to use the analysis tools. Plugin availability is checked from real session initialization and actual tool calls/results, not from a model claiming it used the plugin. A fallback answer still belongs to its assigned condition.

Review each answer against the frozen rubric and cited SQL, assigning each factual criterion 0, 0.5 or 1. Report mean factual score with equal task weights, supported-citation score separately, material unsupported claims, and strict passing answers. Failed/missing answers receive zero in the all-assigned-runs result. Never infer correctness from call counts or token savings. Do not silently exclude a hard question or alter the rubric after seeing its answers.

Compare paired question/repeat results for client wall time, reported input/output tokens, cache-read/write tokens, tool calls and client-reported cost. Show uncached input separately from total reported input (input + cache read + cache write), since caching may shift cost without reducing total context. Missing usage stays unknown, never zero. Reported cost is not an independently verified invoice and custom provider pricing may not match it.

Call the plugin helpful on this suite only with evidence of higher factual/citation quality or lower resource use at comparable quality. Report per-question regressions. With 12 selected questions, related procedures and two repeats, do not generalize a percentage to all SQL codebases or claim statistical proof of superiority. This two-arm study does not compare codebase-memory and cannot isolate the contribution of LSP versus MCP/ScriptDOM.

## Reproduce locally

Use Python 3.11+, Claude Code with `--restricted` support, existing environment-based model credentials, and an extracted verified native plugin release. Keep the study output outside the source folder. Commands below use PowerShell; change paths as needed.

```powershell
python scripts/prepare-sample-study.py --source D:/Project/sql-server-samples --output artifacts/my-sample-study
python scripts/evaluate-understanding.py --workspace artifacts/my-sample-study/sql-snapshot --tasks artifacts/my-sample-study/tasks.json --plugin D:/path/to/extracted/plugin --model deepseek-v4-pro --source-revision e5d7bb90ca0b695ac14f61458047bfb5a498459e --repeats 2 --order baseline-first --workers 2 --restricted --timeout 180 --budget 0.50 --keep-answers --keep-transcripts --output artifacts/my-sample-study/runs
python -m unittest discover -s scripts -p test_evaluation_metrics.py -v
python -m unittest discover -s scripts -p test_sample_study.py -v
```

The runner produces ungraded metrics. Reviewing answers against `rubric.json` remains mandatory before interpreting an efficiency improvement. Answers and transcripts can contain source details; keep local until reviewed for publication.

Save each explicit review under its run directory key in a separate `grades.json`, e.g. `{"run-0001":{"criteria":[1,0.5,1],"citations":1,"material_unsupported_claim":false,"notes":"Source-backed review notes."}}`. Use exactly the number of criterion scores in that task's rubric. Once every run is reviewed, generate the report:

```powershell
python scripts/report-sample-study.py --study artifacts/my-sample-study --grades artifacts/my-sample-study/grades.json
```
