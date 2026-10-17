# Reuse saved procedure analysis

Version 0.11.0 adds an **opt-in JSON report cache** for the MCP process, covering saved SQL folders and explicitly configured online/hybrid catalogs. MCP still works without starting LSP. No hooks or additional service are required.

This saves completed reports across restarts. It does **not** serialize ScriptDOM ASTs or restore the complete navigation index. Sources are acquired, parsed and checked again before reuse. Startup indexing remains; current source visibility remains authoritative.

## Enable it in Claude Code

In 0.14.0, start Claude once from your SQL project folder to generate a disabled starter `.sqlscriptdom.json`. Edit its `analysisCache` section and restart Claude:

```json
{
  "version": 1,
  "analysisCache": { "enabled": true, "directory": ".sqlscriptdom/cache" }
}
```

This works on Windows, macOS and Linux. See [settings and precedence](plugin-settings.md). The existing absolute `SQLLSP_ANALYSIS_CACHE_DIR` environment variable remains an override.

Ask Claude to discover the procedure and call `analyze_procedure` using its URI, selection and revision. Responses include `cache.enabled`, `cache.status` and, when applicable, `cache.file`/`cache.createdAt`. First analysis normally says `Stored`; an unchanged request in a new process can say `Hit`. Full and compact/focused views share the canonical full report for the same expansion limits, then project the requested view.

Set `analysisCache.enabled` to false, remove any flag/environment override and restart Claude to disable persistence. It is off by default. Add `.sqlscriptdom/` to your project's `.gitignore`, or select an absolute private directory outside the repository. Do not commit cached reports. Stop using processes before removing cached data.

## Prebuild a folder or database

After a source Release build:

```sh
dotnet src/SqlLsp.Mcp/bin/Release/net10.0/sqlscriptdom-mcp.dll \
  --workspace /absolute/path/to/sql-project \
  --cache-directory /absolute/path/to/private-cache --build-cache
```

For an extracted native package use `mcp/sqlscriptdom-mcp` (`.exe` on Windows) with the same arguments. `--cache-directory` overrides the environment variable. Do not combine `--build-cache` with `--stdio`.

For a catalog, add `--config /absolute/path/to/online-options.json` with existing [online](online.md)/[hybrid](hybrid.md) options and environment-variable secrets. Connection strings are not stored. Only fixed read-only catalog queries run; analyzed SQL is never executed.

Prebuilding covers at most 256 indexed procedures/functions/views, each with depth 2 and at most 20 modules. Other interactive limits require another report. One JSON summary reports sources/issues, entry status, actual retention, limits and timings. Exit 0 requires available sources, no parse issues/truncation and all selected reports resolved and retained. Exit 2 means incomplete coverage/retention; inspect the summary. Invalid launch options or inaccessible workspace/configuration return exit 1. The summary includes identities/paths and is local project data. Large catalogs can exceed these limits: this is a bounded report cache, not a complete exported database index.

## Files and freshness

```text
<cache-directory>/analysis-v1/report-<sha256-request-key>.json
```

Each UTF-8 JSON envelope contains `format: 1`, `key`, `sourceFingerprint`, `createdAt`, `reportHash` and `report`. The full report has normalized source references. Catalog path/revision placeholders are internal tokens, not navigable files; MCP restores current snapshot paths, module IDs, revisions and capture times. Do not cite templates directly as current source evidence.

Keys cover selected source/position/expansion limits and a fingerprint of the complete loaded source set, bodies, identities, parser/tool builds, configuration/resolution rules, catalog metadata and comparison state. Another declaration can change ambiguity, so even an unrelated source change conservatively misses. Only the known generated catalog timestamp is neutralized, preserving layout. An `expectedRevision` must still match the current selected source.

Online/hybrid requests validate through existing catalog acquisition and TTL; a new process reloads metadata. Failed refreshes never replay expired database reports as an offline fallback. Hidden/encrypted source remains unavailable and changed visibility invalidates the context. Catalog acquisition is not a transactionally consistent DDL snapshot. MCP does not receive LSP unsaved overlays, and those buffers are not persisted here.

Limits: 8 MiB per file, 256 reports, 128 MiB total. The version directory uses current-user Windows ACLs or Unix directory 700/file 600. Linked/reparse paths are rejected. Exclusive writer leases and atomic replacement avoid partial writes; damaged/unavailable storage or contention permit fresh analysis. Eviction uses access-updated modification times only for age, never freshness. Only owned report/temp names are pruned. Checksums detect corruption, not malicious edits by someone controlling the same account.

Reports contain SQL-derived names, bounded snippets, source locations and metadata. They are not encrypted. Opaque EXEC payload previews remain suppressed, but reports are still sensitive project data. No remote telemetry is sent.

## Measurement

`cacheValidationMs`, `cacheReadMs` and `cacheWriteMs` join existing timings. A hit's `analysisMs=0` means Core report computation was skipped; parsing, validation, deserialization, projection and transport remain. Small reports can be faster to recompute. Sending the same report again does not automatically save model tokens.

[Local restart evidence](evidence/persistent-cache-windows.json) records eight stored reports then eight hits in a new process. [The study design](evaluation/sql-tool-comparison.md) separates backend work, usage and correctness.

[Release 0.11.0](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/tag/v0.11.0) is public. [Final native and marketplace evidence](evidence/persistent-cache-ci.json) verifies clean package source, four-platform actual installs, checksums and executable modes. These checks do not measure model answer quality or token savings.

The plugin can separately record [local metrics](plugin-metrics.md), including cache-hit rates and validation/read/write intervals across sessions. Enable `metrics` independently in the same config file. This does not change cache freshness rules or infer model-token savings.
