# Offline source contexts

Use explicit source options (`--config` / `SQLLSP_MCP_CONFIG` for MCP, initialization options for LSP) to define project boundaries. `.sqlscriptdom.json` still controls local storage/response budgets and cannot implicitly connect to a database.

```json
{
  "workspace": {
    "autoDetectProjects": true,
    "projectScopes": [
      { "id": "payroll", "root": "file:///path/to/sql/payroll/", "database": "Payroll",
        "defaultSchema": "dbo", "caseSensitive": false,
        "include": ["**/*.sql"], "exclude": ["generated/**"], "references": ["hr"] },
      { "id": "hr", "root": "file:///path/to/sql/hr/", "database": "HumanResources", "defaultSchema": "dbo" }
    ]
  }
}
```

Roots must remain inside physical initialized workspace roots. Explicit scopes take precedence; the unique nearest root wins. Equal overlapping roots are excluded visibly instead of selecting an arbitrary database. Includes/excludes are relative path globs (`*`, `**`, `?`), with OS path comparison. Excluded SQL files remain in the file ledger as `ExcludedPolicy`. Reference mappings permit cross-context resolution only for an explicitly database-qualified object; normal unqualified names stay local. Comparison/default schema/database rules belong to the selected scope. Static `USE` and explicit object qualifiers retain their existing precedence.

Without explicit scopes, the nearest `.sqlproj` supplies context only when exactly one project has matching literal, unconditional `Build Include` membership, with `Build Remove` applied. No imports, properties, conditions, targets or SQL are executed. Conditional/unknown/overlapping membership uses a file-specific context and visible limitation. Literal membership in a project with unevaluated imports remains labeled partial. Project changes trigger watcher reconciliation. Invalid/inaccessible/oversized projects cannot establish verified membership. Linked files/directories remain excluded.

Files outside any detected project retain their configured workspace-root context with an explicit unverified-project warning. A SQL-only copy has no project files and cannot recover the original project boundaries; configure scopes for such copies. This is not automatic database inference from folder names. Encrypted/external modules, SQLCMD and unsupported dialects retain source/parse limitations.

`find_procedures` accepts an optional exact `scopeId` obtained from an identity's `sourceContextId`. Module and issue pages report that scope's indexed count alongside workspace coverage. `documentUri` selects one exact file; file-ledger pages use this instead of `scopeId`. Direct analysis obtains scope from its source file and reports its context evidence/limitations.

The CTAS documentation crash is repaired separately from project membership: `CREATE TABLE AS SELECT` has no column definition list. It stays indexed with a warning that column types/nullability require further binding. The four original failing sample files are covered by the corpus reconciliation evidence.
