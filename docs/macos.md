# macOS and Claude Code

Release [0.7.0](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/tag/v0.7.0) includes both LSP and read-only MCP analysis plus the explanation skill. [Marketplace install CI](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38574241007) passed on Apple Silicon and Intel Mac, with 60 LSP and 8 MCP tests per installed package. These tests prove native installation/protocol behavior; Mac model tool calls remain unverified. See [procedure-analysis usage](procedure-analysis.md).

Install this plugin through the GitHub marketplace. Claude downloads and installs the matching package automatically. You do not need to download a ZIP, install .NET, or configure SQL Server for offline SQL search.

## Install from the marketplace

Use Claude Code **2.1.224 or later**. Check `claude --version`; if needed, run `claude update`.

In Terminal, add the marketplace once:

```sh
claude plugin marketplace add yongding422/ClaudeCode_SQL_LSP
```

On an **Apple Silicon Mac** (Apple M-series chip):

```sh
claude plugin install sqlscriptdom-lsp-osx-arm64@sqlscriptdom-lsp-marketplace
```

On an **Intel Mac**:

```sh
claude plugin install sqlscriptdom-lsp-osx-x64@sqlscriptdom-lsp-marketplace
```

Install only the entry for your Mac. Then start a fresh Claude session from your SQL project:

```sh
cd "$HOME/projects/your-sql-project"
claude
```

The plugin stays installed for later sessions. A `--plugin-dir` flag is not needed for a marketplace installation.

You can also add the marketplace from inside Claude with `/plugin marketplace add yongding422/ClaudeCode_SQL_LSP`, then open `/plugin` and select the matching Mac entry in Discover.

## Use SQL search/navigation

Ask Claude about an object in your SQL project, for example:

> Find the definition of dbo.ProcessPayroll and its static references using LSP. Show actual file paths and lines, and report unavailable tools explicitly.

Replace `dbo.ProcessPayroll` with an object in your project. No manual server launch is required.

To verify actual LSP calls and compare search accuracy with grep, use [the public fixture and evaluation guide](evaluation.md). The plugin uses Claude's LSP integration and does not install hooks or intercept text searches.

For complex procedure questions, see [the automatic investigation workflow and metrics proposal](stored-procedure-understanding.md). [Call hierarchy](call-hierarchy.md) is supported in 0.6.2; 0.6.1 returns Method not found for those requests.

## Verification and compatibility

[Marketplace installation verification for 0.6.2](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38511900535) passed on both Mac architectures with Claude Code 2.1.224, followed by 60 real LSP tests against each installed apphost, including prepare/incoming/outgoing calls. User Claude settings were isolated and these tests made no model calls.

Use a macOS version supported by the bundled .NET 10 runtime: currently macOS 14, 15 or 26, according to [Microsoft's support table](https://learn.microsoft.com/en-us/dotnet/core/install/macos). Version 0.6.2 native wire/catalog tests passed on `macos-15` (Apple Silicon) and `macos-15-intel` (Intel): each ran 60 wire tests against both Unix unzip and .NET ZipFile extraction, plus 23 deterministic provider tests. See [native CI](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38510961317) and [recorded counts](evidence/call-hierarchy-ci.json). Actual Claude model tool calls on Mac still need separate evidence.

## Optional: install a local ZIP

This alternative is for testing a local build or using an older Claude client. Normal marketplace users can skip this section. Copy the matching ZIP and its `.sha256` sidecar from [release 0.6.2](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/tag/v0.6.2) onto your Mac, or use a trusted local build from `artifacts/packages`. Use a fresh destination directory; keep the entire extracted package together.

In Terminal on Apple Silicon:

```sh
cd "$HOME/Downloads"
shasum -a 256 -c sqlscriptdom-lsp-0.6.2-osx-arm64.zip.sha256
unzip sqlscriptdom-lsp-0.6.2-osx-arm64.zip -d "$HOME/tools/sqlscriptdom-lsp-0.6.2"
chmod +x "$HOME/tools/sqlscriptdom-lsp-0.6.2/server/sqlscriptdom-lsp"
claude plugin validate "$HOME/tools/sqlscriptdom-lsp-0.6.2" --strict
cd "$HOME/tools/sqlscriptdom-lsp-0.6.2/samples/sql-workspace"
claude --plugin-dir "$HOME/tools/sqlscriptdom-lsp-0.6.2"
```

On an Intel Mac, replace `osx-arm64` with `osx-x64` in both archive commands. An Intel shell reports `x86_64` from `uname -m`; a native Apple Silicon shell reports `arm64`. A shell running under Rosetta can also report `x86_64` on Apple Silicon: use the Apple chip shown in About This Mac and prefer the native ARM64 package. Package manifests launch their bundled executable directly with `--stdio`, including paths containing spaces; there is no shell pipeline or build output in the LSP connection.

### Local sample demonstration

In the sample session, ask:

> Read payroll-caller.sql. Use LSP hover at line 3, column 15 and report the signature and source returned by the tool.

Then try:

> Use LSP goToDefinition on payroll-caller.sql at line 3, column 15, then findReferences on the returned declaration. Show the actual file paths and lines. Report tool unavailability explicitly.

For this optional local ZIP installation, start Claude from the physical directory containing your SQL project:

```sh
cd "$HOME/projects/your-sql-project"
claude --plugin-dir "$HOME/tools/sqlscriptdom-lsp-0.6.2"
```

Claude decides how to expose LSP operations. Windows has actual client evidence for navigation/hover/call hierarchy and the 0.7.0 analysis workflow. Mac model calls still need separate evidence. The 0.7.0 implementation includes read-only MCP tools and an explain-procedure skill; see [usage and coverage](procedure-analysis.md).

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
pwsh -File ./scripts/test-claude.ps1 -PluginPath "$HOME/tools/sqlscriptdom-lsp-0.6.2" -Hover
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

For marketplace installation, see [publishing and installation](publishing.md).
