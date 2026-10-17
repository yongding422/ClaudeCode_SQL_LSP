# SqlScriptDOM LSP

A working T-SQL language server in C#/.NET for SQL code search and stored-procedure understanding. **M0–M5 are complete; general M6 editor work stays deferred.** Version 0.14.0 adds automatic starter [project configuration](docs/plugin-settings.md) for [inspectable local metrics and comparison commands](docs/plugin-metrics.md) and [opt-in persistent JSON analysis reports](docs/persistent-analysis-cache.md), alongside [compact evidence reports, ready-to-cite source lines and shared MCP source state](docs/compact-reports.md). Reports include bounded value tracing, verified EXEC bindings, control maps and table footprints, supported by the explanation skill. Existing features include stdio LSP lifecycle, incremental synchronization, SqlScriptDOM diagnostics, symbols, definition/references navigation, static incoming/outgoing call hierarchy, hover, workspace overlays, opt-in SQL Server metadata, mapped hybrid resolution, and self-contained packaging. See [procedure analysis and usage](docs/procedure-analysis.md), [the full specification](SqlScriptDOM_LSP_Implementation_Plan.md), and [release evidence](docs/milestones.md).

[How the plugin works — workflow diagram](docs/plugin-workflow.md)

The **0.16.0 candidate** implements the [benchmark-driven fixes](docs/plans/benchmark-improvement-progress.md): [bounded discovery](docs/discovery-response.md), [scoped resolution](docs/offline-project-scopes.md), [compact analysis and selective expansion](docs/bounded-analysis.md), [live reuse](docs/workspace-reuse.md), [opt-in persisted discovery facts](docs/persistent-discovery-index.md) and [stronger semantic evidence](docs/semantic-evidence.md). The default `analyze_procedure` response is compact with depth 1/eight modules; request `detail="full"` for the compatible report, using bounded pages when necessary. These repairs do not establish a performance advantage; the final matched evaluation and native rollout are tracked in the progress document.

Version 0.14.0 automatically creates a disabled starter [`.sqlscriptdom.json` in the workspace when MCP first starts](docs/plugin-settings.md). Edit it to enable metrics/report reuse and restart Claude. Existing files and directory overrides remain supported; SQL/database source options remain explicit. If the MCP tools are missing, [check the installed version and restart the session](docs/installation.md#if-the-sql-mcp-tools-are-missing).

## Build and test

Install .NET SDK **10.0.401** (pinned in `global.json`), then run from the repository root:

```sh
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build --no-restore
dotnet format --no-restore --verify-no-changes
```

No database is required for the default suite or offline mode. Direct dependency versions are in `Directory.Packages.props`; lock files pin transitive dependencies. CI runs the default suite on Windows, Linux and macOS, with a separate disposable SQL Server job. Local verification has been performed on Windows.

With Docker running Linux containers, run the opt-in live suite in PowerShell:

```powershell
$env:SQLLSP_RUN_SQLSERVER_TESTS = '1'
dotnet test -c Release
Remove-Item Env:SQLLSP_RUN_SQLSERVER_TESTS
```

The fixture creates and removes its own SQL Server Developer container, binds a random port to loopback, seeds only that database, and tests dedicated restricted accounts. It never connects to an existing database. See [online setup](docs/online.md) for configuration, permissions and test details.

## Launch

Publish once, then launch the published DLL directly. Build/run output must never enter an LSP connection.

```sh
dotnet publish src/SqlLsp.Server -c Release --no-restore -o claude-plugin/server
dotnet publish src/SqlLsp.Mcp -c Release --no-restore -o claude-plugin/mcp
dotnet claude-plugin/server/sqlscriptdom-lsp.dll --stdio
```

`scripts/start-server.ps1` and `scripts/start-server.sh` launch the same published server. The process waits for Content-Length framed LSP messages on stdin; it is not an interactive SQL shell. stdout carries only JSON-RPC; operational messages use stderr.

## Client configuration

Point an LSP client at `dotnet` with the absolute DLL path and `--stdio`. Use language ID `sql` for `.sql` buffers. Pass a `rootUri` or `workspaceFolders` in `initialize` to scan local files. The 27-file [sample workspace](samples/sql-workspace) contains procedures, tables, views and functions. Try navigating `dbo.ProcessPayroll` in `payroll-caller.sql` to `payroll-procedure.sql`, then finding its references.

Optional `initializationOptions`, validated against the embedded [JSON Schema](docs/configuration.schema.json):

```json
{
  "sql": { "dialect": "tsql", "mode": "offline", "parserVersion": "170", "quotedIdentifier": true },
  "workspace": { "maxFileSizeBytes": 2097152 },
  "resolution": { "caseSensitive": true }
}
```

Available parser versions: 80–180 in increments of 10, verified with real parsers. Default: 170 with QUOTED_IDENTIFIER on. Positions explicitly use UTF-16 even when a client also offers UTF-8. Invalid options disable analysis and report schema locations through `window/showMessage`; restart with corrected options. Configuration currently uses initialization options only; application-level config files and runtime reconfiguration are later work.

Offline name comparison defaults to ordinal, case-sensitive matching. Set `resolution.caseSensitive` to false for ordinal case-insensitive comparison. Optional `defaultSchema` and `defaultDatabase` supply explicit offline context. Database names from `USE` or multipart identifiers take precedence. Without a default schema, an unqualified reference resolves only when one candidate exists in the same project/database; collisions return no definition. These rules do not discover a live SQL Server collation or user default schema.

Scanning excludes `bin`, `obj`, `node_modules`, `.git`, links/reparse points, and oversized files. File reads remain inside configured roots. Open buffers override disk; edits replace each document snapshot atomically. Closing clears diagnostics and re-reads the disk definition. Stale versions and superseded parse results cannot overwrite current data. An oversized buffer clears analysis and can recover when edited below the limit.

Native filesystem watchers refresh creates, changes, deletes, and file/directory renames after a short debounce. Client `workspace/didChangeWatchedFiles` notifications also refresh the index. Disk updates preserve open overlays, and closing a deleted file drops its overlay. Each workspace root has an independent source context.

The server advertises incremental text synchronization, document symbols, definition, references, hover, call hierarchy and workspace symbol search. Hierarchical outline clients receive `DocumentSymbol`; other clients receive `SymbolInformation`. Procedures/functions map to LSP Function, tables to Class, and views to Interface. Definition returns a verified declaration location, or an empty result for ambiguous, unresolved or unsupported targets. References include supported static object uses and verified alias qualifiers, with the declaration controlled by `includeDeclaration`. [Call hierarchy](docs/call-hierarchy.md) groups supported static procedure/function invocations with partial coverage and source-revision validation. [Hover](docs/hover.md) describes the selected definition's kind, syntax signature or visible catalog metadata, source, capture/buffer version and available comparison state. It returns null for unsupported or ambiguous bindings and does not infer projected column types.

## Install the Claude Code plugin

Install the [published release](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/latest) through this repository's GitHub marketplace. Use Claude Code **2.1.224 or later** for archive installs; check `claude --version` and use `claude update` if needed. On an Apple Silicon Mac:

```sh
claude plugin marketplace add https://github.com/yongding422/ClaudeCode_SQL_LSP.git
claude plugin install sqlscriptdom-lsp-osx-arm64@sqlscriptdom-lsp-marketplace
cd /path/to/your/sql-project
claude
```

For Intel Mac use `sqlscriptdom-lsp-osx-x64`; Windows x64 uses `sqlscriptdom-lsp-win-x64`, and Linux x64 uses `sqlscriptdom-lsp-linux-x64`, each followed by `@sqlscriptdom-lsp-marketplace`. Install one entry for your platform. The packages include the runtime; no .NET installation is required. See [marketplace setup](docs/publishing.md) and [Mac usage](docs/macos.md).

The plugin uses Claude's built-in LSP integration plus read-only MCP analysis and an explanation skill, with no hooks. See [procedure analysis](docs/procedure-analysis.md) and [evaluation](docs/evaluation.md) for actual tool checks and a fair task-level comparison.

For complex procedure investigation, [the assessment](docs/stored-procedure-understanding.md) records implemented footprints/workflows, remaining gaps and accuracy/time/usage metrics. [Static call hierarchy](docs/call-hierarchy.md) remains available; general M6 editor enhancements stay deferred.

Build a self-contained package with PowerShell 7 and the pinned SDK. Supported package targets are `win-x64`, `linux-x64`, `osx-arm64` (Apple Silicon), and `osx-x64` (Intel Mac):

```powershell
./scripts/package.ps1 -Runtime win-x64
./scripts/test-package.ps1 -Runtime win-x64
```

Extract your local `artifacts/packages/sqlscriptdom-lsp-0.14.0-win-x64.zip` into a stable directory. It includes the .NET **10.0.13** runtime, so users need no .NET SDK/runtime installation. Launch a new Claude session from a SQL workspace with the extracted plugin's absolute path:

```powershell
claude --plugin-dir 'C:/Tools/sqlscriptdom-lsp'
```

The bundled `.lsp.json` invokes its executable directly, including when the extraction path contains spaces. See [installation, demonstrations and troubleshooting](docs/installation.md). Offline mode is the default. Database access requires explicit options and environment-variable secrets; see [online](docs/online.md) and [hybrid](docs/hybrid.md) setup.

Mac users can follow [the macOS guide](docs/macos.md) for native package selection, checksum verification and Claude SQL search prompts. [GitHub marketplace distribution](docs/publishing.md) describes release preparation and platform-specific install commands; archive installs require Claude Code 2.1.224 or later.

Claude Code **2.1.201 on Windows** has been tested with actual server initialization, automatic SqlScriptDOM diagnostics after an Edit, and a real `LSP` hover call returning kind/signature/source. Initial tool enumeration does not reliably describe deferred LSP availability. Definition/references, outlines and search are verified independently over LSP; their current Claude behavior remains separate evidence. [Client evidence](docs/claude-verification.md) records reproducible smoke commands and limits.

For source development, the [plugin directory](claude-plugin) targets a published DLL and requires a .NET 10 runtime. After publishing:

```sh
claude plugin validate ./claude-plugin --strict
claude --plugin-dir ./claude-plugin
```

Manifest validation is separate from client behavior. Prefer the self-contained plugin on Windows: the installed Claude CLI intermittently rejected the development plugin's `dotnet` command lookup.

## Scope and next milestone

M2 implements qualified workspace resolution for procedures, tables/views and selected function calls. CTEs, nested query aliases, module parameters, local variables and temporary/table-variable names use local bindings before global lookup. Quoted identifiers retain their AST components, including embedded dots and escaped delimiters. Ambiguous names never select an arbitrary declaration. Dynamic SQL, remote and numbered procedure targets remain unsupported.

M3 adds `sql.mode=online` with explicitly enabled profiles and environment-variable secrets. Online mode indexes catalog objects and visible module AST references without scanning the repository by default. Navigation opens ordinary `file:` URIs in a private, read-only snapshot directory; hidden/encrypted source is represented only by clearly labeled metadata descriptions. [Online setup](docs/online.md) explains the validated options and limitations.

M4 adds `sql.mode=hybrid` with explicit workspace/profile mappings, local and unsaved navigation preference, catalog fallback, source provenance and conservative module drift reporting. Duplicate declarations and competing identities remain ambiguous. Failed profiles remove expired catalog targets while local analysis stays available. See [hybrid setup](docs/hybrid.md).

M5 adds packaging, real Claude diagnostics evidence, security/stress/startup regression tests and measured performance. Hover and static call hierarchy are complete; 0.7.0 adds procedure footprints, coverage and MCP/skill delivery. Version 0.8.0 adds structural control-flow contexts; sequential reachability and transaction outcomes remain unsupported. Version 0.9.0 adds bounded value dependencies and EXEC bindings; concrete values, function argument boundaries and column lineage remain unsupported. Version 0.10.0 adds compact presentation, citations, source-state reuse and request timing measurements. Version 0.11.0 adds validated report reuse across MCP restarts; source acquisition/parsing still run. See the [Grep/codebase-memory comparison and measurement design](docs/evaluation/sql-tool-comparison.md). Next priorities are reviewed product evaluation, larger-workspace indexing and bounded temp/table-variable propagation. Completion, semantic tokens, rename and DacFx remain deferred. See [the understanding plan](docs/procedure-understanding-implementation-plan.md), [benchmarks](docs/performance.md) and [security model](docs/security.md). No analyzed SQL is executed.

See [milestone evidence](docs/milestones.md), [technology decisions](docs/adr/0001-technology-selection.md), [architecture](docs/architecture.md) and [limitations](docs/semantic-limitations.md).
