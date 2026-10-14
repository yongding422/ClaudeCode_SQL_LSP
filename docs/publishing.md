# GitHub marketplace distribution

Target repository: [yongding422/ClaudeCode_SQL_LSP](https://github.com/yongding422/ClaudeCode_SQL_LSP). It can host source, CI, the `.claude-plugin/marketplace.json` catalog and GitHub Release ZIP assets. This is an independently hosted Claude Code marketplace; publishing it does not list it in Anthropic's official marketplace.

Version 0.6.2 adds static call hierarchy. Its initial [native build/test CI](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38509727816) passed all nine jobs, including both Mac architectures. [Actual Windows Claude requests](evidence/claude-call-hierarchy-windows.json) returned outgoing and incoming procedure relationships. Final release assets/catalog installation are separate gates; the prior public [0.6.1 release](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/tag/v0.6.1) has [four-platform marketplace installation evidence](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38501092077). Do not use that older install run as proof of 0.6.2 call hierarchy.

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

`new-marketplace.ps1` verifies every checksum, embedded plugin name/version, RID, self-contained build identity, LSP config and apphost before writing the catalog. It neither pushes nor uploads anything. Commit the generated catalog together with the release source, upload all four matching ZIPs/sidecars to release `v0.6.2`, and ensure the release is publicly downloadable before advertising installation. Binary assets stay in Releases, outside Git source history. For a private repository, archive downloads need separate authenticated-download setup; the prepared catalog targets public assets.

Test the actual install on a current Claude client, from an isolated configuration, before calling marketplace installation verified. Actual publication also requires authenticated GitHub access and green native package CI. A draft release can hold assets for review but its URLs are not usable for public installation.

## Install release 0.6.2

With Claude Code 2.1.224 or later:

```sh
claude plugin marketplace add yongding422/ClaudeCode_SQL_LSP
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
