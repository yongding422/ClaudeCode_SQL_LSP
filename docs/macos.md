# macOS and Claude Code

The self-contained packages target Apple Silicon (`osx-arm64`) and Intel (`osx-x64`). No .NET installation or SQL Server is required for offline SQL search. Building packages from source requires PowerShell 7 and .NET SDK 10.0.401; using a package requires neither.

Use a macOS version supported by the bundled .NET 10 runtime: currently macOS 14, 15 or 26, according to [Microsoft's support table](https://learn.microsoft.com/en-us/dotnet/core/install/macos). Native wire/catalog tests passed on `macos-15` (Apple Silicon) and `macos-15-intel` (Intel): each ran 54 wire tests against both Unix unzip and .NET ZipFile extraction, plus 22 deterministic provider tests. See [native CI](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38500332165) and [recorded counts](evidence/macos-ci.json). Actual Claude model tool calls on Mac still need separate evidence.

## Install a local ZIP

Copy the matching ZIP and its `.sha256` sidecar from a trusted build onto your Mac. Local build outputs are under `artifacts/packages`; published release assets, once available, are at [GitHub Releases](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases). Use a fresh destination directory; keep the entire extracted package together.

In Terminal on Apple Silicon:

```sh
cd "$HOME/Downloads"
shasum -a 256 -c sqlscriptdom-lsp-0.6.1-osx-arm64.zip.sha256
unzip sqlscriptdom-lsp-0.6.1-osx-arm64.zip -d "$HOME/tools/sqlscriptdom-lsp-0.6.1"
chmod +x "$HOME/tools/sqlscriptdom-lsp-0.6.1/server/sqlscriptdom-lsp"
claude plugin validate "$HOME/tools/sqlscriptdom-lsp-0.6.1" --strict
cd "$HOME/tools/sqlscriptdom-lsp-0.6.1/samples/sql-workspace"
claude --plugin-dir "$HOME/tools/sqlscriptdom-lsp-0.6.1"
```

On an Intel Mac, replace `osx-arm64` with `osx-x64` in both archive commands. An Intel shell reports `x86_64` from `uname -m`; a native Apple Silicon shell reports `arm64`. A shell running under Rosetta can also report `x86_64` on Apple Silicon: use the Apple chip shown in About This Mac and prefer the native ARM64 package. Package manifests launch their bundled executable directly with `--stdio`, including paths containing spaces; there is no shell pipeline or build output in the LSP connection.

## Use SQL search/navigation

In the sample session, ask:

> Read payroll-caller.sql. Use LSP hover at line 3, column 15 and report the signature and source returned by the tool.

Then try:

> Use LSP goToDefinition on payroll-caller.sql at line 3, column 15, then findReferences on the returned declaration. Show the actual file paths and lines. Report tool unavailability explicitly.

For your repository, start Claude from the physical directory containing the SQL project:

```sh
cd "$HOME/projects/your-sql-project"
claude --plugin-dir "$HOME/tools/sqlscriptdom-lsp-0.6.1"
```

Claude decides how to expose LSP operations. Hover and automatic diagnostics have actual Windows client evidence; definition/references and workspace search have independent stdio test evidence. Those results do not establish successful Claude calls on Mac. There is no custom SQL slash command or MCP tool adapter in this release.

## Build and verify on a Mac

```sh
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build --no-restore -c Release
pwsh -File ./scripts/package.ps1 -Runtime osx-arm64
pwsh -File ./scripts/test-package.ps1 -Runtime osx-arm64
```

Use `osx-x64` for native Intel verification. `test-package.ps1` checks the archive checksum, extracts to a path containing spaces/Chinese text, and runs real process/provider tests against the bundled apphost. SQL Server cases are opt-in; ordinary tests need no database. The startup test empties PATH and supplies an absent DOTNET_ROOT, proving bundled-runtime startup on the tested host.

For a bounded real Claude probe with public fixtures, a signed-in Claude CLI and PowerShell 7:

```sh
pwsh -File ./scripts/test-claude.ps1 -PluginPath "$HOME/tools/sqlscriptdom-lsp-0.6.1" -Hover
```

This can spend up to $1 and fails unless it observes actual hover and transport evidence. Do not run the binary directly to test it interactively: it waits for framed LSP input.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Bad CPU type / exec format error | Use the matching native package; choose Apple Silicon or Intel explicitly. |
| Permission denied | Restore the apphost's executable bit with the `chmod` command above. |
| macOS blocks downloaded executable | Builds are unsigned and not notarized. Use your normal macOS security approval flow for a trusted build; no installer removes quarantine automatically. |
| No definitions from a temp or linked directory | Use its physical path. `/var` is typically a link to `/private/var`; the server rejects linked ancestors when scanning. |
| Plugin registered but LSP unavailable | Restart a fresh Claude session and inspect client diagnostics. Registration alone does not prove a callable LSP tool. See [client evidence](claude-verification.md). |

For marketplace installation after publication, see [publishing and installation](publishing.md).
