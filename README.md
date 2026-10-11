# SqlScriptDOM LSP

A working offline T-SQL language server in C#/.NET. **M0 and M1** implement stdio LSP lifecycle, incremental document synchronization, SqlScriptDOM syntax diagnostics, document symbols, and workspace snapshots with unsaved-buffer overlays. The implementation specification is [the full plan](SqlScriptDOM_LSP_Implementation_Plan.md).

## Build and test

Install .NET SDK **10.0.401** (pinned in `global.json`), then run from the repository root:

```sh
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build --no-restore
dotnet format --no-restore --verify-no-changes
```

No database is required or accessed. Direct dependency versions are in `Directory.Packages.props`; lock files pin transitive dependencies. CI runs the offline suite on Windows, Linux and macOS; local verification has been performed on Windows.

## Launch

Publish once, then launch the published DLL directly. Build/run output must never enter an LSP connection.

```sh
dotnet publish src/SqlLsp.Server -c Release --no-restore -o claude-plugin/server
dotnet claude-plugin/server/sqlscriptdom-lsp.dll --stdio
```

`scripts/start-server.ps1` and `scripts/start-server.sh` launch the same published server. The process waits for Content-Length framed LSP messages on stdin; it is not an interactive SQL shell. stdout carries only JSON-RPC; operational messages use stderr.

## Client configuration

Point an LSP client at `dotnet` with the absolute DLL path and `--stdio`. Use language ID `sql` for `.sql` buffers. Pass a `rootUri` or `workspaceFolders` in `initialize` to scan local files. The 24-file [sample workspace](samples/sql-workspace) contains procedures, tables, views and functions.

Optional `initializationOptions`, validated against the embedded [JSON Schema](docs/configuration.schema.json):

```json
{
  "sql": { "dialect": "tsql", "mode": "offline", "parserVersion": "170", "quotedIdentifier": true },
  "workspace": { "maxFileSizeBytes": 2097152 }
}
```

Available parser versions: 80–180 in increments of 10, verified with real parsers. Default: 170 with QUOTED_IDENTIFIER on. Positions explicitly use UTF-16 even when a client also offers UTF-8. Invalid options disable analysis and report schema locations through `window/showMessage`; restart with corrected options. M1 accepts initialization options only; application-level config files and runtime reconfiguration are later work.

Scanning excludes `bin`, `obj`, `node_modules`, `.git`, links/reparse points, and oversized files. File reads remain inside configured roots. Open buffers override disk; edits replace each document snapshot atomically. Closing clears diagnostics and re-reads the disk definition. Stale versions and superseded parse results cannot overwrite current data. An oversized buffer clears analysis and can recover when edited below the limit.

The server advertises incremental text synchronization and document symbols. Hierarchical clients receive `DocumentSymbol`; other clients receive `SymbolInformation`. Procedures/functions map to LSP Function, tables to Class, and views to Interface.

## Claude Code development plugin

The [plugin directory](claude-plugin) contains a real manifest and `.lsp.json` targeting the published server. It requires a .NET 10 runtime. After publishing:

```sh
claude plugin validate ./claude-plugin --strict
claude --plugin-dir ./claude-plugin
```

Manifest validation is recorded separately from a real Claude session. **Claude Code end-to-end LSP behavior is not yet verified; it remains M5.** Generic LSP interoperability is exercised through an independent framed client launching the actual server process.

## Scope and next milestone

M2 adds qualified symbol identities/resolution, definition, references, workspace symbol search and file watcher updates. M1's index is keyed by document URI and preserves all declarations; it does not perform semantic name binding. Online/hybrid metadata, semantic diagnostics, completion, rename and MCP are not implemented or advertised. No analyzed SQL is executed.

See [milestone evidence](docs/milestones.md), [technology decisions](docs/adr/0001-technology-selection.md), [architecture](docs/architecture.md) and [limitations](docs/semantic-limitations.md).
