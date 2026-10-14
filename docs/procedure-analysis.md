# Explain a stored procedure with the plugin

The 0.7.0 implementation adds verified table operation footprints, visible coverage issues, bounded dependency expansion, read-only MCP tools and a bundled Claude skill. It keeps the existing LSP navigation and static call hierarchy. Publication/native-client evidence is recorded separately in [milestones](milestones.md); source implementation alone is not marketplace availability.

[Release 0.7.0](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/tag/v0.7.0) is public. [Marketplace installation](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38574241007) passed on all four platforms with both protocols exercised. Update your platform entry through the marketplace, then start a fresh Claude session.

[Native source CI](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38570366592) passed all nine jobs, including both Mac architectures. [Windows Claude evidence](evidence/claude-procedure-analysis-windows.json) verifies automatic skill selection and real MCP results. Mac model calls remain unverified.

After updating your platform's marketplace entry and starting Claude from the SQL repository, ask:

> Explain dbo.ProcessPayroll and how Ohio is handled. Show direct and possible transitive table effects, relevant calls, source citations and unknown dependencies.

Claude can select the explanation skill automatically. To request it explicitly on an Apple Silicon Mac:

```text
/sqlscriptdom-lsp-osx-arm64:explain-procedure dbo.ProcessPayroll focusing on Ohio
```

Use `sqlscriptdom-lsp-osx-x64`, `sqlscriptdom-lsp-win-x64` or `sqlscriptdom-lsp-linux-x64` for other packages. Automatic skill selection is not guaranteed; actual tool results establish that analysis ran.

## Available tools

`find_procedures(query, maxResults)` returns procedures/functions/views with exact source identities, file URIs, revisions and selection positions. Multiple candidates stay visible. `analyze_procedure(documentUri, line, character, maxDepth, maxModules, expectedRevision)` selects a verified source and returns a structured report and readable JSON text. Positions are **zero-based UTF-16**, not the one-based positions used in some Claude LSP operations. Use discovery results directly; citations displayed to humans add one to the line number.

The report distinguishes SELECT reads, INSERT, UPDATE, DELETE, CREATE, DROP and TRUNCATE. MERGE reports possible clause actions. INSERT SELECT/EXEC and OUTPUT INTO retain both source/call facts and destination effects. CTEs, aliases, temp tables and table variables reuse scoped binding. Local identities are separate from persistent table identities. Persistent creation targets without an existing indexed definition retain their syntax/name and an unresolved identity, rather than fabricating a catalog object.

Default expansion is two levels and 20 modules; supported bounds are depth 0..4 and modules 1..40. Each module contains its own direct effects and source revision. View reads can expand underlying reads; callee effects are transitive possibilities. Cycles stop. Per-module output is limited to 1,000 facts and the report to 2,000 facts, with visible truncation/expansion issues. Coverage lists and static-analysis limitations are part of the tool output, not hidden LSP item metadata.

MCP analyzes **saved workspace/catalog snapshots**. It receives no unsaved LSP buffers. Save changes before analyzing or use LSP for unsaved navigation. An optional expected revision rejects changed source. Source kinds, metadata timestamps, source statuses and relevant hybrid drift remain visible; do not combine evidence from different versions silently.

## Coverage boundaries

Dynamic SQL, remote rowsets, missing/ambiguous objects, unavailable bodies and writes through CTEs have explicit issues. View writes report the verified view target, but underlying write effects/triggers remain unknown. Built-in effects, synonyms, runtime values and implicit engine behavior are not fully analyzed. An empty access/call list never proves no effects.

Control flow, transactions, error paths, argument/value propagation and full column lineage are not yet deterministic analyzer features. The skill reads source to interpret these and labels the distinction. No analyzed SQL is executed. The server makes no AI model calls and includes no hooks or remote telemetry collector; the MCP SDK uses AI tool abstractions internally.

## Configuration and development

The adapter defaults to the current working directory and offline mode. For explicit source configuration, set `SQLLSP_MCP_CONFIG` to an absolute file path, or launch with `--config`. The file follows [the same schema](configuration.schema.json) as LSP initialization options. Enable online profiles/mappings explicitly and supply credentials through their configured environment variables. MCP configuration and LSP client initialization options are independent and must agree if both should use the same context.

```sh
dotnet publish src/SqlLsp.Server -c Release -o claude-plugin/server
dotnet publish src/SqlLsp.Mcp -c Release -o claude-plugin/mcp
claude --plugin-dir ./claude-plugin
```

Packaged marketplace installations contain both self-contained apphosts; developers do not need these build commands or .NET. The MCP process accepts `--stdio`, `--workspace <physical-directory>` and `--config <file>`. Protocol stdout contains only JSON-RPC; operational messages go to stderr.

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

Default output contains aggregate numeric measurements/task IDs, not SQL, paths, prompts or answers. `--keep-answers` and `--keep-transcripts` explicitly retain local evidence that can include private source details. Review answer correctness/citations and fill the blank scoring columns independently. Missing usage stays unknown; reported cost may differ from subscription billing. Each client process is fresh; OS/model cache conditions are uncontrolled and reported as such. A public smoke pair validates collection only. Use the [30-task measurement design](stored-procedure-understanding.md#measuring-value-against-grep) before drawing comparative conclusions.
