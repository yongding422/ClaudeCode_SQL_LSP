# Persist discovery facts independently of rich reports

Enable this offline-only option in `.sqlscriptdom.json`, then restart the MCP process:

```json
{"version":1,"indexCache":{"enabled":true,"directory":".sqlscriptdom/index"}}
```

It is disabled when absent or false, including existing generated starters. It does not enable metrics, report caching, hooks or database access. Explicit online/hybrid source modes reject this option with a visible message; their existing catalog permissions/freshness rules continue separately. Paths are relative to the settings file, following the existing storage rules.

Snapshots live in `<directory>/index-v1/workspace-<root-hash>.json.gz`. They contain losslessly compressed versioned JSON declarations, static references/local bindings, diagnostics, source/context revisions, raw SHA-256 content hashes, decoding and comparison metadata. They contain private SQL-derived identifiers/snippets and paths. No ScriptDOM AST or executable/runtime object is serialized. The [decision and compiling DTO spike](adr/0014-materialized-discovery-snapshots.md) choose existing .NET JSON/GZip primitives without a new package/native dependency.

Every discovery/analysis request enumerates current files and reads/hashes their actual bytes. Size/mtime alone never establishes freshness. Unchanged facts are reused across restart; edits, added competitors, renames/deletions and context/config/parser changes update the complete resolution candidate set. Discovery uses its own materialized query interface. Rich analysis parses the selected file and bounded static dependencies (including original EXEC signatures) as needed, while retaining all verified declarations for resolution/ambiguity. A body outside the 64-file/32-MiB demand budget is explicitly unknown. `parsedDocuments` is separate from total `indexedDocuments`; `indexCache` reports reused/extracted files, body parses and storage status.

The existing `analysisCache` can be enabled independently. Report reuse on this path is keyed to the complete verified fact generation/configuration, so adding an ambiguous definition invalidates it even if the selected procedure bytes did not change. The independent LSP process is not required to start.

Writes use a nonblocking exclusive lease, private permissions, link checks and atomic replacement. Readers retain immutable facts. At most eight owned snapshots/128 MiB are retained, with 32-MiB encoded and 128-MiB decoded limits per snapshot and bounded temporary writes; incompatible/corrupt/unavailable persistence falls back to extraction. Current inventory validation is capped at 10,000 files/128 MiB read per request and rejects larger scopes explicitly. Read-only/unavailable storage does not prevent in-memory extraction. Storage limits do not silently remove facts from a successful query. The corpus exceeded the first plain-JSON 32-MiB cap; compression addresses that without dropping facts, and decompression has its own hard limit.

Use the [server probe](workspace-reuse.md) with `--index-cache /owned/path` twice to distinguish empty-cache creation from verified restart, then compare with the live AST mode separately. Full content verification costs are included. This opt-in mode trades repeated verification for lower restart parsing and bounded retained ASTs; it is not presumed faster for every warm request. Do not relabel the frozen cold-client benchmark as a persisted-index measurement.
