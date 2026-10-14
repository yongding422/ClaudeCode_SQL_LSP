# Installation and demonstrations

## Install from the GitHub marketplace

Use Claude Code **2.1.224 or later**. Claude downloads the plugin package automatically; you do not need to download/extract a ZIP or install .NET.

```sh
claude plugin marketplace add yongding422/ClaudeCode_SQL_LSP
```

Install one entry for your computer:

| Computer | Install command |
| --- | --- |
| Apple Silicon Mac | `claude plugin install sqlscriptdom-lsp-osx-arm64@sqlscriptdom-lsp-marketplace` |
| Intel Mac | `claude plugin install sqlscriptdom-lsp-osx-x64@sqlscriptdom-lsp-marketplace` |
| Windows x64 | `claude plugin install sqlscriptdom-lsp-win-x64@sqlscriptdom-lsp-marketplace` |
| Linux x64 | `claude plugin install sqlscriptdom-lsp-linux-x64@sqlscriptdom-lsp-marketplace` |

Start a fresh `claude` session from your SQL project. No `--plugin-dir` flag is required. See [Mac usage](macos.md) for SQL search examples. Inside Claude, `/plugin marketplace add yongding422/ClaudeCode_SQL_LSP` adds the marketplace, and `/plugin` opens the plugin browser.

## Optional: build and install a local package

The remaining package commands are for source developers and manual installation. Marketplace users can skip them.

Build from this repository with PowerShell 7 and .NET SDK 10.0.401:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
./scripts/package.ps1 -Runtime win-x64
./scripts/test-package.ps1 -Runtime win-x64
```

Use `linux-x64` on Linux x64, `osx-arm64` on Apple Silicon and `osx-x64` on Intel Mac. CI builds and exercises each target on its native OS/architecture. Locally, Windows is the verified host; configured CI is not proof that another platform has passed. See [macOS setup](macos.md) and [GitHub marketplace distribution](publishing.md) for dedicated instructions and release status. No global Claude configuration or plugin installation is changed by these scripts.

Each 0.7.0 ZIP contains `.claude-plugin/plugin.json`, `.lsp.json`, `.mcp.json`, separate self-contained LSP/MCP apphosts with pinned .NET 10.0.13 runtimes, the explanation skill, documentation, samples, dependency/license inventory and build provenance. SHA-256 sidecars verify integrity; they are not publisher signatures. Locked RID graphs and disabled trimming/AOT/single-file preserve reflection-dependent APIs. Source revision and dirty-source status are recorded in `build.json`.

Extract all files, including dotfiles. Keep the directory together. On Windows:

```powershell
Get-FileHash ./sqlscriptdom-lsp-0.6.2-win-x64.zip -Algorithm SHA256
Expand-Archive ./sqlscriptdom-lsp-0.6.2-win-x64.zip -DestinationPath 'C:/Tools/SQL LSP'
claude plugin validate 'C:/Tools/SQL LSP' --strict
cd 'C:/Tools/SQL LSP/samples/sql-workspace'
claude --plugin-dir 'C:/Tools/SQL LSP'
```

On Linux x64:

```sh
sha256sum -c sqlscriptdom-lsp-0.6.2-linux-x64.zip.sha256
unzip sqlscriptdom-lsp-0.6.2-linux-x64.zip -d "$HOME/tools/sqlscriptdom-lsp"
chmod +x "$HOME/tools/sqlscriptdom-lsp/server/sqlscriptdom-lsp"
cd "$HOME/tools/sqlscriptdom-lsp/samples/sql-workspace"
claude --plugin-dir "$HOME/tools/sqlscriptdom-lsp"
```

On Apple Silicon macOS:

```sh
shasum -a 256 -c sqlscriptdom-lsp-0.6.2-osx-arm64.zip.sha256
unzip sqlscriptdom-lsp-0.6.2-osx-arm64.zip -d "$HOME/tools/sqlscriptdom-lsp"
chmod +x "$HOME/tools/sqlscriptdom-lsp/server/sqlscriptdom-lsp"
claude plugin validate "$HOME/tools/sqlscriptdom-lsp" --strict
cd "$HOME/tools/sqlscriptdom-lsp/samples/sql-workspace"
claude --plugin-dir "$HOME/tools/sqlscriptdom-lsp"
```

Use `osx-x64` in the two archive commands for Intel macOS. See [Mac build, use and troubleshooting](macos.md).

Self-contained .NET still requires OS native libraries. Linux x64 targets glibc distributions, not Alpine/musl; follow Microsoft's [native dependency guidance](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install). macOS may apply downloaded-binary quarantine policies. Unsigned builds require the user's normal local execution policy; this project does not bypass it. x86/Windows ARM64/Linux ARM64 distributions are not provided.

## Public sample demonstrations

Ask Claude to read `broken.sql`, make a small Edit that leaves invalid syntax, and report the automatic SQL diagnostics. The client starts the server when an SQL Edit triggers it; loading the manifest alone does not demonstrate parsing. Alternatively use the isolated fixture:

```powershell
./scripts/test-claude.ps1 -PluginPath 'C:/Tools/SQL LSP' -Diagnostics
```

This spends up to $1 of Claude usage, takes at most 55 seconds, creates only public payroll fixtures outside the repository, excludes other settings/hooks/MCP servers and allows only Read/Edit/LSP tools. Authentication must already be available. It preserves local ignored evidence and fails unless the client log confirms initialization, a nonempty diagnostic notification, and graceful shutdown. The public temp fixture path is printed for inspection. Raw evidence is not intended for publication.

For a generic LSP client, launch `server/sqlscriptdom-lsp.exe --stdio` on Windows or `server/sqlscriptdom-lsp --stdio` on Unix. Select `sql` for `.sql`, send an absolute sample root URI in `initialize`, then `initialized`. Requests use zero-based UTF-16 positions. In `payroll-caller.sql`, `textDocument/definition` at line 2, character 15 navigates to `payroll-procedure.sql`; references from the procedure declaration include the caller, with the declaration governed by `includeDeclaration`. `documentSymbol` returns the procedure; `workspace/symbol` searches `ProcessPayroll`. Independent process tests exercise these exact operations, incremental diagnostics and multi-root behavior.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Plugin loads but no language server starts | Edit an SQL file; inspect Claude's debug log for registration and launch. Restart a new session after changing the plugin. |
| `dotnet` not found/unsafe location | Use the self-contained package's absolute executable command. Do not put build commands or a shell pipeline in `.lsp.json`. |
| Generic client hangs after launch | Send Content-Length framed JSON-RPC on stdin. The process has no interactive prompt; stdout is protocol bytes only. |
| LSP navigation tool absent | See the concrete 2.1.201 limitation in `claude-verification.md`. Do not substitute model guesses for tool evidence. Diagnostics may still work. |
| Invalid configuration disables capabilities | Read `window/showMessage`, validate `initializationOptions` against `configuration.schema.json`, restart with corrected options. Runtime reconfiguration is not implemented. |
| Missing/ambiguous definition | Check project roots, schema/database/case rules and duplicate migrations. Use physical roots without linked ancestors (on macOS, `/private/var/...` instead of `/var/...` for temp workspaces). Dynamic SQL, remote targets and incomplete bindings have no fabricated result. |
| SQL Server unavailable or metadata partial | Inspect safe status codes, environment variable presence and permissions without printing secrets. Expired catalog definitions disappear; local analysis remains available in hybrid mode. |
| Startup issues | Temporarily set `SQLLSP_TRACE_STARTUP=1` to emit two source-free initialization markers on stderr. Large workspaces are scanned before initialize completes; allow sufficient timeout. |

Installation testing extracts the ZIP into a path containing spaces/Chinese text and runs actual wire/catalog suites against its apphost. A dedicated startup test clears PATH and points DOTNET_ROOT to a nonexistent location while verifying syntax diagnostics. This proves runtime independence on the tested host, not a fresh-OS installation or all-platform certification.
