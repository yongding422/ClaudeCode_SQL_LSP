# Explain a stored procedure with the plugin

Candidate contract version 2 adds [direct selectors and bounded evidence pages](bounded-analysis.md), [scoped discovery](discovery-response.md) and optional [verified persisted discovery](persistent-discovery-index.md). Its defaults are compact/depth 1/eight modules; use `detail="full"` explicitly for original DTO fields. The release history below describes 0.14.0 and earlier defaults. The [R1 matched rerun](evaluation/results/sql-server-samples-r1-20261017.md) fixes discovery overflows but still loses to Grep on time/input; later changes require their own measurement.

[Actual 0.14.0 marketplace installation](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/39045608531) passed on Windows, Linux, Apple Silicon and Intel Mac with strict validation. Installed packages passed 60 LSP tests each, plus 63 MCP tests on Windows (three Unix-only skips) or 66 on Unix. The optional performance case was skipped. Automatic starter creation, preservation and concurrent startup were exercised against the installed executables; Unix also exercised read-only workspaces. Mac executable modes survived without repair. [Final evidence](evidence/automatic-settings-ci.json) records clean source, native/install jobs, dependency checks, installed-server discovery and all eight release digests. No model calls were made.


Version 0.12.0 adds [inspectable local metrics and comparison commands](plugin-metrics.md), alongside [opt-in persistent JSON reports and bulk prebuilding](persistent-analysis-cache.md). The 0.10.0 features remain: [compact reports and direct source citations](compact-reports.md), optional value focus, reuse of expanded callees and per-request initialization/analysis timings. The skill requests compact reports; the original full DTO remains the compatible default. [The workflow diagram](plugin-workflow.md) shows how Claude, MCP, LSP and ScriptDOM work together.

The report includes [bounded parameter/value dependencies](value-tracing.md), verified EXEC bindings, [structural control contexts](control-flow.md), table footprints, coverage issues and bounded callee expansion. Existing LSP navigation and static call hierarchy remain available.




[Release 0.14.0](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/tag/v0.14.0) is public with four checksum-pinned native packages from clean source 24916c1a1b3f1bcde6ff7f50c3a9a90ff5c52c6e. [Native CI](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/39044930760) passed all nine jobs. Update your platform entry and restart Claude completely. MCP generates a disabled [`.sqlscriptdom.json`](plugin-settings.md) starter only when absent. Existing settings and explicit SQL source configuration remain authoritative. The metrics tool does not initialize SQL.

The [0.10.0 public paired smoke](evidence/compact-reports-pair-windows.json) verified compact analysis and separate timing collection, but the plugin remained slower and used more model tokens than Grep/Read on that single task. A [normal-autoload retry](evidence/claude-compact-reports-windows.json) completed with correct UPDATE/predicate/argument citations; its earlier timeout remains recorded. Tool use is not proof of answer accuracy or overall value. Mac model calls remain unverified. Historical 0.9.0 measurements remain in [milestones](milestones.md).

After updating your platform's marketplace entry and starting Claude from the SQL repository, ask:

> Explain dbo.ProcessPayroll and how Ohio is handled. Show direct and possible transitive table effects, relevant calls, source citations and unknown dependencies.

Claude can select the explanation skill automatically. To request it explicitly on an Apple Silicon Mac:

```text
/sqlscriptdom-lsp-osx-arm64:explain-procedure dbo.ProcessPayroll focusing on Ohio
```

Use `sqlscriptdom-lsp-osx-x64`, `sqlscriptdom-lsp-win-x64` or `sqlscriptdom-lsp-linux-x64` for other packages. Automatic skill selection is not guaranteed; actual tool results establish that analysis ran.

## Available tools

`find_procedures(query, maxResults)` returns procedures/functions/views with exact source identities, file URIs, revisions and selection positions. Multiple candidates stay visible. `analyze_procedure(documentUri, line, character, maxDepth, maxModules, expectedRevision, detail, focus)` selects a verified source and returns matching structured/text JSON. Input positions and full-report ranges are **zero-based UTF-16**. Optional `detail="compact"` shares evidence and formats **one-based** `evidence.text` citations directly. Optional focus selects connected value details with explicit omitted counts while retaining all effects/calls/coverage. Use discovery positions directly and do not add one again to compact evidence. [The compact contract](compact-reports.md) explains shared targets and all coordinate fields.

The report distinguishes SELECT reads, INSERT, UPDATE, DELETE, CREATE, DROP and TRUNCATE. MERGE reports possible clause actions. INSERT SELECT/EXEC and OUTPUT INTO retain both source/call facts and destination effects. CTEs, aliases, temp tables and table variables reuse scoped binding. Local identities are separate from persistent table identities. Persistent creation targets without an existing indexed definition retain their syntax/name and an unresolved identity, rather than fabricating a catalog object.

Default expansion is two levels and 20 modules; supported bounds are depth 0..4 and modules 1..40. Each module contains its own direct effects and source revision. View reads can expand underlying reads; callee effects are transitive possibilities. Cycles stop. Per-module output is limited to 1,000 facts and the report to 2,000 facts, with visible truncation/expansion issues. Coverage lists and static-analysis limitations are part of the tool output, not hidden LSP item metadata.

MCP analyzes **saved workspace/catalog snapshots**. It receives no unsaved LSP buffers. Save changes before analyzing or use LSP for unsaved navigation. An optional expected revision rejects changed source. Source kinds, metadata timestamps, source statuses and relevant hybrid drift remain visible; do not combine evidence from different versions silently.

## Coverage boundaries

Dynamic SQL, remote rowsets, missing/ambiguous objects, unavailable bodies and writes through CTEs have explicit issues. View writes report the verified view target, but underlying write effects/triggers remain unknown. Built-in effects, synonyms, runtime values and implicit engine behavior are not fully analyzed. An empty access/call list never proves no effects.

Each module's `controlFlow` maps branches/loops/errors/transactions; call/access `flowContext` IDs identify enclosing regions. `valueFlow` adds parameter/local definitions, possible source IDs, condition/filter reads and verified EXEC named/positional/default/OUTPUT boundaries. Its source-completeness flag does not mean a known runtime value. All records share the existing budgets, with explicit per-section limits and safe preview truncation. Empty context does not establish unconditional execution. Sequential reachability, transaction correctness, function-argument binding, SQL conversions, runtime contents and full column lineage remain unsupported. The skill reads source for interpretation and labels the distinction. No analyzed SQL is executed. The server makes no AI model calls and includes no hooks or remote telemetry collector; the MCP SDK uses AI tool abstractions internally.

## Configuration and development

The adapter defaults to the current working directory and offline mode. Local metrics/cache settings come from [`.sqlscriptdom.json`](plugin-settings.md) in that workspace; 0.14.0 creates a disabled starter at first MCP startup without overwriting an existing file. For explicit source configuration, set `SQLLSP_MCP_CONFIG` to an absolute file path, or launch with `--config`. The source file follows [the same schema](configuration.schema.json) as LSP initialization options. Enable online profiles/mappings explicitly and supply credentials through their configured environment variables. MCP configuration and LSP client initialization options are independent and must agree if both should use the same context.

```sh
dotnet publish src/SqlLsp.Server -c Release -o claude-plugin/server
dotnet publish src/SqlLsp.Mcp -c Release -o claude-plugin/mcp
claude --plugin-dir ./claude-plugin
```

Packaged marketplace installations contain both self-contained apphosts; developers do not need these build commands or .NET. The MCP process accepts `--stdio`, `--workspace <physical-directory>`, `--settings <storage-settings-file>` and `--config <source-options-file>`. Protocol stdout contains only JSON-RPC; operational messages go to stderr.

## Measure against Grep/Read

The external runner uses Python's standard library and an authenticated Claude CLI. It sends the same task to fresh SQL-only copies with Grep/Read versus plugin plus Grep/Read, fixes model/effort, randomizes arm order and records actual tool results, failures, time and usage. It does not install hooks or change normal client configuration.

```sh
python scripts/evaluate-understanding.py \
  --workspace samples/understanding-evaluation \
  --tasks samples/understanding-evaluation/tasks.json \
  --plugin /absolute/path/to/built/plugin \
  --model <exact-Claude-model-id> --repeats 3 --keep-answers
```

On Windows, use one line or PowerShell continuation, and pass `--claude` with the native Claude executable path if your command resolves to a shell shim. The output directory must be outside the SQL workspace. The task file accepts only IDs/questions; keep reviewed gold answers outside copied SQL. No source instructions, task files or gold JSON are copied to model workspaces.

Default output contains aggregate numeric measurements/task IDs, not SQL, paths, prompts or answers. Version 0.10.0 also aggregates correlated server queue/initialization/navigation/analysis/presentation milliseconds and canonical report byte counts separately from whole-task latency and model usage. Missing/old-client timings remain unknown; request intervals exclude startup/transport/model and background watcher work. `--keep-answers` and `--keep-transcripts` explicitly retain local evidence that can include private source details. Review answer correctness/citations and fill the blank scoring columns independently. Missing usage stays unknown; reported cost may differ from subscription billing. Each client process is fresh; OS/model cache conditions are uncontrolled and reported as such. A public smoke pair validates collection only. Use the [30-task measurement design](stored-procedure-understanding.md#measuring-value-against-grep) before drawing comparative conclusions.

## Persistent report reuse (0.11.0)

Enable `analysisCache` in [`.sqlscriptdom.json`](plugin-settings.md) and restart Claude to save reports across MCP restarts. The existing SQLLSP_ANALYSIS_CACHE_DIR override remains supported. The standalone --build-cache command prebuilds bounded reports for a folder or configured catalog. Source acquisition, parsing and freshness validation still run. See [usage, format, limits and metrics](persistent-analysis-cache.md) and [the comparative evaluation design](evaluation/sql-tool-comparison.md).

## Inspect local metrics (0.12.0)

Enable `metrics` in [`.sqlscriptdom.json`](plugin-settings.md), restart Claude, then ask it to call get_metrics. The existing SQLLSP_METRICS_DIR override remains supported. Current or retained-session summaries report cold/warm time, errors, cache hits, bytes and reported fact/issue counts. Standalone summary/compare and client evaluation CSV commands are available in the packaged MCP executable. See [commands, comparison rules and limits](plugin-metrics.md). This third read-only MCP tool does not initialize SQL. It cannot observe Grep or model tokens; client captures remain separate.
