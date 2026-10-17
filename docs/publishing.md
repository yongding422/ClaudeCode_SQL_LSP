# GitHub marketplace distribution

Target repository: [yongding422/ClaudeCode_SQL_LSP](https://github.com/yongding422/ClaudeCode_SQL_LSP). It can host source, CI, the `.claude-plugin/marketplace.json` catalog and GitHub Release ZIP assets. This is an independently hosted Claude Code marketplace; publishing it does not list it in Anthropic's official marketplace.

Current release [0.10.0](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/tag/v0.10.0) adds compact evidence, citations, shared MCP source state and separate request measurements. [Native CI](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38675234892) passed all nine jobs; each package passed 60 LSP, 24 MCP and 28 deterministic provider tests. Published asset digests match the catalog. [Evidence](evidence/compact-reports-ci.json) records actual marketplace installation separately. See [usage and boundaries](compact-reports.md) and [the workflow diagram](plugin-workflow.md).

[Actual 0.10.0 marketplace installation](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38676207666) passed on all four platforms. Each installed package passed strict validation and 60 LSP + 24 MCP tests. Mac executable modes survived without repair; this check makes no model calls.

Release [0.6.2](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/tag/v0.6.2) is public with static call hierarchy and four native packages. Its final [native build/test CI](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38510961317) passed all nine jobs, including both Mac architectures. Published digests match the catalog pins. [Actual Windows Claude requests](evidence/claude-call-hierarchy-windows.json) returned outgoing and incoming procedure relationships; current [marketplace installation checks](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38511900535) remain separate from model-call evidence. The prior [0.6.1 installation run](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38501092077) is historical, not proof of 0.6.2 call hierarchy.

The 0.6.2 installation workflow passed on all four platforms: actual GitHub marketplace registration, archive install, strict validation and 60 functional wire tests per installed apphost. Mac executable permissions survived installation without repair. These checks used isolated Claude configurations and made no model calls. [Recorded counts](evidence/call-hierarchy-ci.json) retain the native/installer scope separately from [Windows model calls](evidence/claude-call-hierarchy-windows.json).

The catalog generator uses the documented [archive source](https://code.claude.com/docs/en/plugins/marketplace-reference#archive-plugin-source) with immutable versioned release URLs and SHA-256 pins. **Archive installation requires Claude Code 2.1.224 or later.** The locally verified 2.1.201 supports extracted plugins via `--plugin-dir`; it cannot validate/install this newer source type. Do not treat catalog JSON validation as installation or Mac client proof.

## Prepare a release

Run locked restore, Release build and the complete default test suite first. Build each platform package with its marketplace identity:

```powershell
foreach ($runtime in @('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')) {
    ./scripts/package.ps1 -Runtime $runtime -Marketplace
}
./scripts/test-package.ps1 -Runtime win-x64 -Marketplace
./scripts/new-marketplace.ps1
```

Native Linux and Mac package tests must run on their corresponding CI runners. Cross-publishing on Windows proves artifact construction, not native execution. CI supplies separately tested ZIPs and sidecars in its platform artifacts; release those exact files and regenerate the catalog from them. A different rebuild can have a different checksum/provenance. `-Marketplace` outputs under `artifacts/packages/marketplace`, preserving ordinary local packages. The plugin manifest names include the RID and match their catalog entry names; install only one platform entry.

`new-marketplace.ps1` verifies every checksum, embedded plugin name/version, RID, self-contained build identity, LSP/MCP configs, skill and apphosts before writing the catalog. It neither pushes nor uploads anything. Upload all four matching ZIPs/sidecars to the corresponding versioned release and ensure it is publicly downloadable before committing the generated catalog. Binary assets stay in Releases, outside Git source history. For a private repository, archive downloads need separate authenticated-download setup; the prepared catalog targets public assets.

Test the actual install on a current Claude client, from an isolated configuration, before calling marketplace installation verified. Actual publication also requires authenticated GitHub access and green native package CI. A draft release can hold assets for review but its URLs are not usable for public installation.

## Install the published release

With Claude Code 2.1.224 or later:

```sh
claude plugin marketplace add https://github.com/yongding422/ClaudeCode_SQL_LSP.git
```

Install the entry for your computer:

| Computer | Install command |
| --- | --- |
| Apple Silicon Mac | `claude plugin install sqlscriptdom-lsp-osx-arm64@sqlscriptdom-lsp-marketplace` |
| Intel Mac | `claude plugin install sqlscriptdom-lsp-osx-x64@sqlscriptdom-lsp-marketplace` |
| Windows x64 | `claude plugin install sqlscriptdom-lsp-win-x64@sqlscriptdom-lsp-marketplace` |
| Linux x64 | `claude plugin install sqlscriptdom-lsp-linux-x64@sqlscriptdom-lsp-marketplace` |

Start a fresh Claude session from your SQL project. The plugin is installed with the normal user scope; no `--plugin-dir` flag is needed. Use [the SQL sample prompts](macos.md#use-sql-searchnavigation). The GitHub marketplace commands and source behavior follow [Anthropic's marketplace documentation](https://code.claude.com/docs/en/plugin-marketplaces).

For an existing installation, refresh the catalog and update your platform entry, then restart Claude:

```sh
claude plugin marketplace update sqlscriptdom-lsp-marketplace
claude plugin update sqlscriptdom-lsp-osx-arm64@sqlscriptdom-lsp-marketplace
```

Use `osx-x64`, `win-x64` or `linux-x64` instead of `osx-arm64` for the other platforms. The update commands were checked against Claude Code 2.1.224's CLI help.
