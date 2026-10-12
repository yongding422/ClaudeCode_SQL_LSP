# Hybrid setup and definition comparison

Hybrid mode indexes the repository and explicitly mapped SQL Server profiles. Pass initialization options such as these, using an actual file URI for an initialized workspace directory:

```json
{
  "sql": { "mode": "hybrid", "parserVersion": "170" },
  "databases": [{
    "id": "approved-dev",
    "enabled": true,
    "environment": "development",
    "server": "localhost,1433",
    "database": "PayrollDev",
    "connectionStringEnv": "SQL_LSP_DEV_CONNECTION",
    "readOnly": true
  }],
  "resolution": {
    "workspaceMappings": [{
      "workspaceRoot": "file:///D:/Project/Payroll/",
      "databaseProfiles": ["approved-dev"],
      "defaultDatabaseProfile": "approved-dev"
    }],
    "preferWorkspaceDefinitions": true,
    "reportDefinitionDrift": true,
    "allowStaleDatabaseCache": false
  }
}
```

On Linux/macOS the URI might be `file:///home/me/Payroll/`. Mappings must name existing, non-link roots supplied in initialize.rootUri/workspaceFolders. Trailing directory separators and Windows URI case differences are normalized; duplicate mappings, invalid paths, disabled/missing profiles and defaults outside the mapped list fail closed. Each root maps only the listed profiles. Enabled profiles with no hybrid mapping are not contacted. Unmapped roots retain independent offline resolution. Non-file/outside-root buffers do not inherit a mapped profile.

See [online setup](online.md) for environment secrets, TLS, permissions, limits and read-only file handling. The same rules apply in hybrid mode. Profiles are never enabled by environment labels alone. Configuration does not execute scripts, provision grants or deploy database changes.

Switching a saved configuration to offline mode does not require its mapped profiles to remain enabled. Offline validates configuration shape/references but does not load mappings, inspect credentials or contact a database.

## Selecting an identity and source

A single mapped profile supplies the default database/comparison context. With several mapped profiles, defaultDatabaseProfile explicitly selects the context for unqualified names and names targeting that profile's database. A different qualified database or USE context can select only profiles explicitly listed in the mapping. Without a default, uniquely visible candidates may resolve, but competing profiles remain ambiguous; select a default for predictable lexical comparison and declaration database defaults. SQL four-part/cross-server names remain unsupported.

Local variables, parameters, aliases and CTEs resolve before global names. Global candidates are filtered by project/profile, database, schema, name, kind and applicable identifier rules. Only one logical object identity with no duplicate local/catalog declarations permits source preference. An open buffer replaces the disk snapshot for its own URI; then the editable local declaration wins over the corresponding deployed declaration. Two migration files declaring the same object remain ambiguous even if one is open. A table/view kind collision or competing schemas/profiles cannot be resolved by source priority.

Set preferWorkspaceDefinitions=false to select deployed navigation for an otherwise unambiguous paired identity. If no permitted catalog candidate exists, the verified local declaration remains usable. When local DDL is absent, permitted catalog metadata supplies navigation to its read-only snapshot; metadata-only table descriptions provide verified columns/types without inventing original DDL.

Core ResolutionResult retains every candidate and a separate selected target. LSP definition returns the selected location only. workspace/symbol keeps separate local/database rows, with source context and source kind in containerName. Its data contains source, provenance, capturedAt, drift, driftCapturedAt and driftDatabaseProfiles when available. All rows remain navigable. Clients may not display arbitrary symbol data; window/logMessage also summarizes comparison, limited to 100 rows plus the total count.

References remain verified AST locations. A mapped local/deployed identity can collect both repository/buffer and visible database-module references from either declaration anchor. Ambiguous uses are excluded. Catalog dependency views retain separate incomplete coverage and do not acquire invented source spans. Database modules resolve within their originating profile; they do not choose a local project arbitrarily when multiple workspaces share a profile. includeDeclaration includes the selected/clicked verified anchor.

## Definition comparison

Drift reporting defaults on and can be disabled independently of navigation with reportDefinitionDrift=false. Comparison uses SHA-256 of normalized ScriptDOM tokens from safe procedure/view/function declarations. It ignores whitespace/comments, CREATE/ALTER/CREATE OR ALTER acquisition headers, and the declaration's own qualified name (important when stored module text predates sp_rename). Identifier quoting and keyword case are normalized. Body identifier spellings, string literals, numeric values, parameters and module options remain significant.

| Status | Meaning |
| --- | --- |
| Match | Normalized declaration tokens agree; semantic equivalence is not proven. |
| Different | Normalized declaration tokens differ; behavioral change is not proven. |
| Unavailable | No unique visible, safely parsed original module text is available, or the catalog profile is unavailable. |
| Ambiguous | Competing identities or duplicate declarations prevent comparison. |

Formatting/comments alone do not produce Different. Case/linguistic equivalents and optional syntax changes can still produce text differences. Original CREATE TABLE text is not available from these catalog queries, so table comparison remains Unavailable rather than fabricating schema drift. Hidden, encrypted, oversized or malformed modules also remain unknown. Capture timestamps describe metadata freshness; they do not claim transactional DDL consistency.

## Failure and freshness

Each mapped profile owns an independent bounded cache/snapshot source. At most two acquisitions run concurrently, under a shared 30-second refresh/materialization budget plus configured per-profile limits. Navigation/search refreshes after TTL; it can wait for a failed connection to time out. Syntax diagnostics, outlines and local file updates remain independent of database reads. Failed profiles remove expired catalog candidates, preserve local navigation and do not remove other healthy profiles. Last verified identifier/default-schema rules may remain available for local binding after a failure; expired definitions are not reused.

allowStaleDatabaseCache=false is the supported policy; true is rejected. Cached permissions/data can last within TTL, and previously opened private files may remain until shutdown. Unavailable status and comparison timestamps are explicit. On later successful acquisition, current catalog sources and comparisons rebuild. No database mutation or rename capability is introduced.

The disposable integration suite verifies local/unsaved precedence, configured database preference, metadata-only table fallback, formatting-insensitive and changed-body comparison, unknown encrypted source, module/reference provenance, close-to-disk restoration, isolated mappings, a failed profile alongside a healthy one, and continued local navigation after stopping the seeded SQL Server and expiring its cache. Run with `SQLLSP_RUN_SQLSERVER_TESTS=1`; no existing database is used.
