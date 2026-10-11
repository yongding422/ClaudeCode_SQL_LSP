# SqlScriptDOM LSP — Implementation Plan and Development Handoff

**Status:** Implementation-ready technical plan  
**Date:** 2026-10-11  
**Working project name:** `sqlscriptdom-lsp`  
**Target:** SQL Server T-SQL language intelligence for Claude Code and standards-compliant LSP clients

## 1. Mission and Agent Instructions

Build a production-oriented, cross-platform T-SQL Language Server in C#/.NET, powered by Microsoft's SqlScriptDOM parser. The server must support **Offline (repository)**, **Online (SQL Server metadata)**, and **Hybrid (both)** modes through one unified semantic/indexing architecture. Package it as a Claude Code LSP plugin. An optional MCP adapter for higher-level SQL impact analysis is a later deliverable, not a prerequisite for LSP functionality.

**Instructions for the implementing coding agent:**
1. Treat this document as the product specification, architecture proposal, backlog, and acceptance checklist. Implement in small, independently tested milestones; do not stop at scaffolding or pseudo-code.
2. Before installing dependencies or coding plugin integration, check the current official Claude Code plugin/LSP documentation, current SqlScriptDOM NuGet API, OmniSharp package compatibility, and SDK support. Record confirmed versions and decisions in `docs/adr/`.
3. Prefer a working, testable vertical slice over broad unfinished abstractions. Start with a functioning stdio LSP handshake plus parse diagnostics, then indexing and navigation.
4. Never invent SqlScriptDOM AST members, OmniSharp handler signatures, Claude Code plugin keys, or SQL Server metadata guarantees. Validate uncertain APIs in compilable spikes/tests.
5. Do not require a live database for unit tests or for Offline mode. All database access is opt-in and read-only. Never run analyzed SQL scripts against the server.
6. At the end of each milestone, run the build and tests; document results and known limitations. If blocked, provide concrete failures and the next executable step.

## 2. Goals and Scope

### Required outcomes
- A standalone .NET executable acting as an LSP server over stdio, speaking JSON-RPC with `Content-Length` framing and clean stdout.
- Incremental text-document synchronization; robust SqlScriptDOM parsing and syntax diagnostics.
- Extraction of T-SQL definitions, locations, and static references into a normalized symbol graph.
- Offline repository navigation without SQL Server connectivity.
- Online introspection of approved SQL Server databases, including object definitions and catalog metadata where permitted.
- Hybrid resolution with configurable precedence, provenance, ambiguity handling, and local-vs-deployed definition drift detection.
- Supported LSP features implemented honestly and tested with a generic LSP client; Claude Code integration verified separately.
- Repeatable build, packaging, example repository, CI, documentation, and security controls.

### Non-goals for MVP
- Executing arbitrary SQL; deployments, migrations, data writes, or query execution-plan analysis.
- Full SQL Server Management Studio or SSDT parity.
- Reliable analysis of dynamic SQL or encrypted stored procedures.
- First-class PostgreSQL/MySQL/Oracle support (SqlScriptDOM targets T-SQL).
- MCP features before LSP basics pass end-to-end verification.

### Operating modes
| Mode | Input sources | Default behavior | Failure handling |
|---|---|---|---|
| `offline` | Workspace `.sql` / optionally `.sqlproj`-related files | Resolve only indexed local objects; no connection attempted | Return partial results with explicit unresolved/ambiguous status |
| `online` | Configured SQL Server catalogs and permitted module definitions | Resolve database objects and references | Surface connectivity/permissions status; never invent definitions |
| `hybrid` | Workspace plus one or more SQL Server targets | Unsaved buffer > local file > database **for editable definitions**; keep all candidates | Degrade to offline if database is unavailable; surface provenance |

**Important:** An object identity must include connection/project context, database, schema, object name, object kind, and appropriate SQL Server collation/case rules. Never use the short object name as a global unique key.

## 3. Technical Stack and Constraints

- Language/runtime: **C# and an actively supported .NET LTS SDK**, pinned through `global.json` after checking currently supported versions and dependencies.
- Parser: `Microsoft.SqlServer.TransactSql.ScriptDom` / [microsoft/SqlScriptDOM](https://github.com/microsoft/SqlScriptDOM). Configure parser dialect/compatibility rather than hardcoding T-SQL 160.
- LSP framework: evaluate [OmniSharp/csharp-language-server-protocol](https://github.com/OmniSharp/csharp-language-server-protocol), including compatible versions, or a maintained equivalent. Record an ADR; validate a minimal `initialize` exchange before committing.
- Database provider: `Microsoft.Data.SqlClient`, least-privilege access to SQL Server catalog views, parameterized filters, bounded async queries.
- Indexing: start with in-memory immutable/snapshot indexes; introduce SQLite persistent cache only after benchmarks show need.
- Testing: xUnit or NUnit; mock/fake catalog provider; a small disposable SQL Server integration environment when available (otherwise marked optional CI job).
- Supported platforms: Windows, Linux, macOS where runtime and required authentication modes are available.

## 4. Architecture

```text
Claude Code / VS Code / generic LSP test client
                 |
          LSP JSON-RPC / stdio
                 |
         SqlLsp.Server
    lifecycle, capabilities, text sync
                 |
       SqlLsp.Intelligence
  parsing / spans / scopes / identifiers
  symbol graph / resolution / diagnostics
                 |
     Unified Source Registry + Index
          /                  \
  WorkspaceSource       SqlServerSource
  .sql / buffers      metadata + definitions
          \                  /
      provenance + resolution policy
                 |
        [future] MCP adapter
   impact analysis / drift inspection
```

### Layer responsibilities
- **Server:** LSP IO, capabilities, cancellation, document notifications, request handlers, logging to stderr, graceful shutdown.
- **Parsing:** parser version selection; AST/parse errors; offsets-to-LSP positions; tolerant behavior around malformed documents.
- **Intelligence:** AST visitors, scopes, normalization, object and column resolution, symbol/reference graph, confidence and provenance.
- **Providers:** source-specific acquisition, freshness and permissions; never embed LSP request types in provider interfaces.
- **Index:** atomic per-document replacement, cross-document reverse references, source-aware identities, invalidation, optional persistence.
- **Configuration:** validated configuration plus environment-based secrets and safe defaults.

### Suggested repository
```text
sqlscriptdom-lsp/
  global.json
  SqlScriptDomLsp.sln
  src/
    SqlLsp.Server/              # executable, LSP handlers and transport
    SqlLsp.Core/                # parser, AST visitors, semantic models
    SqlLsp.Indexing/            # workspace index, symbols and references
    SqlLsp.Providers.Workspace/ # filesystem and unsaved buffer source
    SqlLsp.Providers.SqlServer/ # metadata adapter, connection management
    SqlLsp.Configuration/       # typed validated options
    SqlLsp.Mcp/                 # optional; later phase
  tests/
    SqlLsp.Core.Tests/
    SqlLsp.Indexing.Tests/
    SqlLsp.Server.IntegrationTests/
    SqlLsp.SqlServer.IntegrationTests/
  samples/sql-workspace/
  claude-plugin/
    .claude-plugin/plugin.json
    .lsp.json
  docs/
    architecture.md
    semantic-limitations.md
    adr/
    setup.md
    troubleshooting.md
  .github/workflows/ci.yml
  README.md
```

## 5. Core Domain Model

```csharp
public enum SqlSourceKind { UnsavedBuffer, Workspace, Database }
public enum SqlObjectKind { Table, View, Procedure, Function, Trigger, Type, Synonym, Column, Parameter, Variable, Cte, TempTable, Unknown }

public sealed record SqlObjectIdentity(
    string SourceContextId,
    string? Database,
    string? Schema,
    string Name,
    SqlObjectKind Kind);

public sealed record SourceLocation(
    Uri DocumentUri,
    int StartOffset,
    int EndOffset,
    SqlSourceKind SourceKind);

public sealed record SqlSymbol(
    SqlObjectIdentity Identity,
    SourceLocation? DefinitionLocation,
    string? DefinitionText,
    string? ContentHash,
    bool IsEditable,
    string Provenance);

public sealed record SqlReference(
    SqlObjectIdentity? ResolvedTarget,
    SourceLocation UseLocation,
    string RawName,
    string ReferenceKind,
    double Confidence);
```

These are **illustrative architecture contracts**, not mandatory exact C# types. Refine after confirming AST and LSP APIs. Maintain a separate `ResolutionResult` with statuses `Resolved`, `Ambiguous`, `Unresolved`, and `Unsupported`, plus candidate symbols and explanations. A reference without a verified target must never be silently treated as resolved. Support quoted/bracketed identifiers, default schemas, multipart identifiers, collation-sensitive comparison, variable scopes, aliases, CTEs and temporary objects progressively.

### Provider contracts
```csharp
public interface ISqlSourceProvider
{
    string SourceId { get; }
    Task<SourceSnapshot> LoadAsync(CancellationToken cancellationToken);
    Task<SqlSymbol?> FindDefinitionAsync(
        SqlObjectIdentity identity, CancellationToken cancellationToken);
    Task<IReadOnlyList<SqlReference>> FindReferencesAsync(
        SqlObjectIdentity identity, CancellationToken cancellationToken);
}
```

Design the index so workspace-only and database-only usage share the same handler/semantic pipeline. Provider-specific reference results must have provenance and may be incomplete.

## 6. SQL Parsing, Coordinates and Document Lifecycle

1. Choose a SqlScriptDOM `TSql*Parser` version via validated configuration; handle `QUOTED_IDENTIFIER` and parsing options.
2. Capture AST, parser errors, tokens and full source text. Use AST visitors to extract definitions and known reference nodes.
3. Store source spans based on SqlScriptDOM offsets and token locations; convert to LSP **zero-based** line/character positions using the negotiated position encoding (default UTF-16 unless negotiated otherwise). Cover CRLF, LF, surrogate pairs, emoji, tabs, and Unicode comments.
4. On `didOpen`, use in-memory buffer instead of disk. On `didChange`, apply version-checked edits correctly; discard older parse/index work. On `didSave`, synchronize with disk as needed. On `didClose`, drop overlay and restore indexed on-disk state.
5. Debounce heavy indexing as appropriate, but publish up-to-date diagnostics and honor cancellation. Clear stale diagnostics when documents close, become excluded, or change identity.
6. Do not let malformed SQL crash the server. Continue to index safely extractable symbols only when confidence allows.

## 7. SQL Server Online Provider

### Catalog inputs
- `sys.objects`, `sys.schemas`, `sys.tables`, `sys.views` — discover named objects.
- `sys.columns`, `sys.types` — column types, nullability and schema details.
- `sys.parameters` — procedure/function parameters.
- `sys.sql_modules` — available module definitions (subject to visibility and encryption).
- `sys.sql_expression_dependencies` — useful but incomplete static dependency information.

Use `Microsoft.Data.SqlClient`, async cancellation, explicit connection/command timeouts, concurrency limits, retry only for transient read failures, and pagination or bounded retrieval for large catalogs. Never assume catalog dependency views cover dynamic SQL, temp tables, or all cross-database/cross-server references. Detect database collation or configurable identifier comparison rules. Record source database version and snapshot time. Never print connection strings, SQL credentials, full sensitive module source, or access tokens to routine logs.

### Multiple databases
A connection profile has a stable ID, server/instance, database, environment label, enabled flag, and secret reference. A workspace-to-database mapping specifies which connection(s) may participate in resolution. Require explicit mapping when names are ambiguous; never accidentally resolve development workspace symbols from a production server.

### Read-only design
Metadata browsing is opt-in. Advise a dedicated account with minimal metadata visibility; do not claim `ApplicationIntent=ReadOnly` by itself enforces authorization. Do not expose an arbitrary SQL execution API. Check and document the needed grants for each operation. Use TLS according to deployment security requirements.

## 8. Hybrid Resolution and Drift

Resolution algorithm:
1. Identify the current document, current connection/project context, and lexical scope.
2. Resolve local variables, parameters, aliases, CTEs and temporary names before global object lookup.
3. Collect candidates from unsaved buffers, workspace index and mapped database indexes.
4. Filter by multipart identifier, kind, SQL Server identifier rules and visibility.
5. For navigation to editable definitions, prefer unsaved > repository > deployed object, **only when identity is unambiguous**.
6. Preserve all matching candidates and provenance. Return ambiguity rather than selecting by short name.
7. Where a workspace object and a mapped database object correspond, compare normalized definitions/hashes if available and mark drift without implying that formatting-only differences are semantic changes.
8. If the database becomes unavailable, retain safe, time-stamped cache only under configured freshness rules and clearly indicate degraded status.

`Find References` must distinguish indexed repository references, catalog-provided dependencies and AST-parsed database module references, and must report coverage limits. Do not claim comprehensive references to dynamic SQL.

## 9. LSP Feature Roadmap

| Priority | LSP method | Minimum useful behavior | Exit condition |
|---|---|---|---|
| P0 | `initialize`, `shutdown`, `exit` | Advertise only implemented capabilities | Real framed JSON-RPC handshake passes |
| P0 | `didOpen`, `didChange`, `didClose` | Versioned document overlays | Open/change/close fixture passes |
| P0 | `textDocument/publishDiagnostics` | Parser diagnostics with accurate ranges | Syntax errors and clearing tested |
| P0 | `textDocument/documentSymbol` | Procedures, views, tables, functions | Names/kinds/ranges tested |
| P1 | `workspace/symbol` | Workspace and permitted catalog search | Cross-file lookup tested |
| P1 | `textDocument/definition` | Verified object declaration location | Alias/schema/multipart cases tested |
| P1 | `textDocument/references` | Scoped static references and provenance | False-positive tests pass |
| P2 | `textDocument/hover` | Kind, signature, source and documented metadata | Hover reflects effective source |
| P2 | `textDocument/completion` | Context-aware symbol/column suggestions | Context and cancellation tested |
| P2 | `textDocument/semanticTokens/full` | Token categories where accurate | Encoding/ranges tested |
| P3 | `textDocument/rename` | Safe, validated multi-location edits | No ambiguous/unsafe rename enabled |

Do **not** advertise rename until complete, transactional, deterministic workspace-edit behavior is verified; do not silently modify live databases. Database-only definitions should use a client-supported virtual-document strategy or a controlled local read-only snapshot. Test whether the target client can open custom URI schemes; otherwise use managed file URIs with access controls and source-map information.

## 10. Configuration Contract

Example `sql-lsp.json` (proposed application-level format, not a preexisting standard):
```json
{
  "sql": { "dialect": "tsql", "parserVersion": "160", "mode": "hybrid" },
  "workspace": {
    "include": ["**/*.sql"],
    "exclude": ["**/bin/**", "**/obj/**", "**/node_modules/**"],
    "maxFileSizeBytes": 2097152
  },
  "databases": [
    {
      "id": "local-dev",
      "enabled": true,
      "connectionStringEnv": "SQL_LSP_DEV_CONNECTION",
      "environment": "development",
      "readOnly": true
    }
  ],
  "resolution": {
    "defaultDatabaseProfile": "local-dev",
    "preferWorkspaceDefinitions": true,
    "reportDefinitionDrift": true,
    "allowStaleDatabaseCache": false
  },
  "indexing": { "debounceMs": 250, "databaseCacheTtlSeconds": 300 }
}
```

Requirements: JSON schema validation, helpful error messages, no inline credentials in committed config, per-workspace profiles, cancellation, excluded paths, symlink/path traversal protection, and clear modes of failure. Avoid hard-coding parser version 160 for all SQL Server versions.

## 11. Claude Code Plugin Integration

Package a **real executable or launch wrapper** and a plugin root containing `.claude-plugin/plugin.json` and `.lsp.json`. A candidate configuration is:

`claude-plugin/.claude-plugin/plugin.json`:
```json
{
  "name": "sqlscriptdom-lsp",
  "version": "0.1.0",
  "description": "T-SQL language intelligence via SqlScriptDOM"
}
```

`claude-plugin/.lsp.json`:
```json
{
  "sqlscriptdom": {
    "command": "sqlscriptdom-lsp",
    "args": ["--stdio"],
    "extensionToLanguage": { ".sql": "sql" }
  }
}
```

**Integration verification gate:** The above is a *candidate* manifest. Before shipping, compare with the **current official Anthropic Claude Code plugin reference** and installed Claude Code version for schema, plugin search paths, command resolution, LSP availability, and supported client requests. Validate plugin discovery, server start, `initialize`, and at least one real SQL definition/diagnostics call. `claude --plugin-dir ./claude-plugin` is a suggested development invocation to verify, not proof of a working integration. Record any client limitations and a reproducible fallback test using a generic LSP client.

Plugin packaging must not assume `dotnet` is installed unless the installation guide states the requirement. Prefer RID-specific self-contained distributions or a documented installer/wrapper. Provide Windows PowerShell and Unix shell launch examples. Log to **stderr**, never stdout.

## 12. Milestones and Ordered Work Backlog

### M0 — Technology validation and repository bootstrap
- [ ] Verify official Claude Code LSP/plugin semantics and LSP protocol version.
- [ ] Choose .NET SDK and pin exact NuGet versions in dependency management.
- [ ] Build a compilable SqlScriptDOM AST visitor and error-location spike.
- [ ] Build an OmniSharp (or alternative) stdio `initialize` / `shutdown` / `exit` spike.
- [ ] Set up solution, formatting, linting, tests, CI and ADRs.

**Acceptance:** `dotnet build` and `dotnet test` pass; a protocol test client initializes the server without stdout contamination; ADRs document verified APIs.

### M1 — First useful Offline vertical slice
- [ ] Implement parser service and parser diagnostics with accurate UTF-16 positions.
- [ ] Implement text synchronization, snapshots, cancellations and stale-result protection.
- [ ] Extract CREATE/ALTER procedures, views, functions and tables using AST visitors.
- [ ] Implement `documentSymbol` and stable workspace indexing.
- [ ] Publish diagnostics and re-publish after edits.

**Acceptance:** A sample 20+ file repository opens; syntactically broken SQL reports an accurate error; fixing it clears diagnostics; symbol lookup reflects unsaved edits.

### M2 — Offline navigation
- [ ] Build qualified identifier normalization, lexical scope model and definition index.
- [ ] Recognize EXEC procedure targets, FROM/JOIN tables/views, selected function invocations, and static named references.
- [ ] Add `definition`, `references`, and `workspace/symbol`.
- [ ] Handle schema collisions, case/collation caveats, CTE shadowing, aliases and unknown objects conservatively.
- [ ] Add incremental reindexing for file creates/changes/deletes and rename-safe URI handling.

**Acceptance:** Cross-file procedure definition and reference tests pass; deliberately ambiguous names never produce arbitrary definition jumps; no DB connection needed.

### M3 — Online metadata mode
- [ ] Implement opt-in SQL Server profiles and secure connectivity.
- [ ] Discover catalog objects, columns, parameters, visible module text and available dependency metadata.
- [ ] Normalize into the shared symbol model and source-aware index.
- [ ] Implement data retrieval caches, bounded queries, cancellation and safe fallback for inaccessible/encrypted modules.
- [ ] Expose database symbol lookup and read-only definitions via tested supported URI strategy.

**Acceptance:** Integration tests against a seeded test SQL Server validate discovery, schema, parameters, module bodies, definitions and permission-limited behavior; online mode does not scan repository unless configured.

### M4 — Hybrid resolution and drift
- [ ] Merge buffer/workspace/database candidates without losing provenance.
- [ ] Implement configured context mapping, source priority and ambiguity response.
- [ ] Support schema fallback from database when local DDL is missing.
- [ ] Implement definitions/hash-based drift detection with source timestamps.
- [ ] Verify offline degradation with server unavailable.

**Acceptance:** Local modified definition wins navigation in mapped context; missing local table resolves from DB; mismatched deployed procedure is surfaced as drift; disconnected DB does not disable local navigation.

### M5 — Claude Code plugin, distribution and hardening
- [ ] Validate actual current Claude Code plugin/LSP configuration.
- [ ] Provide local plugin, packaged executable and install instructions.
- [ ] Run a real Claude Code end-to-end fixture and record exposed capabilities.
- [ ] Add performance, security, stress, log-redaction, multi-workspace and startup tests.
- [ ] Publish README, limitations, troubleshooting and demos.

**Acceptance:** A clean-machine install can launch the server; a new Claude Code session recognizes the plugin and can exercise at least diagnostics or definition navigation, as genuinely supported by that client. If unavailable due to client limitations, document blocker and prove generic LSP interoperability; never claim unverified Claude Code success.

### M6 — Optional enhancements (after M5)
- [ ] Hover, completion, semantic tokens and advanced scope/column resolution.
- [ ] Safe rename only after complete reference-resolution confidence model.
- [ ] Optional DacFx-backed richer schema model (evaluate licensing, overhead, API suitability).
- [ ] Separate `SqlLsp.Mcp` tool adapter offering `analyze_sql_impact`, `compare_schema_definitions`, and `explain_dependency_path` with explicit incomplete-coverage metadata.

## 13. Test Matrix

### Parser/position tests
- Valid CREATE/ALTER, views, functions, triggers, batches (`GO`), comments, quoted identifiers, malformed/incomplete SQL, multiple parser versions, CRLF/LF, Chinese text, emoji and escaped identifiers.

### Semantic/index tests
- Same-name objects in distinct schemas/databases; bracketed/quoted multi-part identifiers; CTE vs table shadowing; temp tables; aliases; variable names and scope; missing DDL; SQL Server case sensitivity; dynamic SQL marked unsupported; cross-file changes and deletes.

### Provider tests
- No database, unreachable database, revoked metadata access, encrypted module, unexpected null definition, canceled retrieval, multiple DB profiles, catalog cache expiration, protection of production profiles, no SQL execution.

### LSP protocol tests
- Initialize/shutdown/exit, framing with split reads, client capabilities, UTF-16 range conversion, full and incremental edits, version races, parallel requests, cancellation, clear/replace diagnostics, empty results and malformed messages.

### End-to-end sample fixtures
1. `dbo.ProcessPayroll` declared in one file and `EXEC dbo.ProcessPayroll` in another → definition and references resolve.
2. `dbo.Employee` missing locally but available in an approved dev database → hybrid column metadata resolves.
3. Local and deployed `dbo.ProcessPayroll` differ → local definition is preferred, drift is flagged.
4. `sales.ProcessPayroll` and `dbo.ProcessPayroll` both exist → no arbitrary resolution when schema omitted.
5. Database unavailable → local parsing/indexing remains operational.
6. Query uses `EXEC(@sql)` → explicitly unsupported dynamic target, not a fabricated dependency.

## 14. Quality and Performance Targets

Treat these as initial **engineering targets to measure and revise**, not guaranteed properties:
- Startup to first valid `initialize` response: target < 2 s on a developer workstation after executable launch.
- Parser diagnostics for typical <= 200 KB script: target p95 < 300 ms after edits on reference hardware.
- Workspace definition lookup after indexing: target p95 < 100 ms for a 1,000-file fixture.
- No protocol-corrupting stdout logs or unbounded connection/query concurrency.
- Memory usage and 10,000-file indexing throughput benchmarked, with documented hardware/results.
- No high-severity known security defects in default offline mode or connection handling.

## 15. Risks, Tradeoffs and Explicit Limitations

| Risk | Mitigation |
|---|---|
| SqlScriptDOM gives syntax but not a semantic SQL binder | Conservative staged scope resolver; test ambiguous references |
| Deployed DB differs from repo | Provenance, mapped contexts, hashes and visible drift state |
| Dynamic SQL and encrypted definitions | Return unknown/partial, never overpromise reference completeness |
| Multi-database and collation semantics | Qualified identities, mappings and collation-aware comparison |
| Claude Code plugin behavior changes | Current official docs, integration smoke tests, generic LSP fallback |
| SQL Server metadata can be sensitive | Opt-in, least privilege, redact secrets, explicit cache retention |
| Custom URI support differs by client | Test custom schemes, offer managed read-only file snapshots |
| Large workspaces / expensive AST parsing | Incremental indexes, cancellation, bounded work and later persistence |
| Framework/SDK mismatch | Compile-time spike and pinned versions in M0 |

## 16. Definition of Done

- [ ] Builds and tests pass in CI on targeted platforms.
- [ ] Offline navigation genuinely works without a database connection.
- [ ] Online metadata lookup works with configured minimal permissions; no arbitrary SQL execution.
- [ ] Hybrid precedence, ambiguity, provenance and database-unavailable behavior are tested.
- [ ] LSP advertised capabilities exactly match implemented features.
- [ ] Character positions, edits, cancellation and symbol URIs are correctly handled.
- [ ] Claude Code plugin integration is verified on a documented version or explicitly marked blocked with evidence.
- [ ] README provides setup, samples, environment-variable connection configuration, security model and known limitations.
- [ ] No secret values are committed or logged; no live database mutation functionality exists.

## 17. First Development Session: Exact Execution Order

1. Inspect the current directory and initialize the git repository and .NET solution if absent.
2. Verify the latest applicable official Claude Code plugin/LSP spec, SqlScriptDOM public API, LSP spec and maintained C# LSP framework. Capture URLs, dates, version constraints and findings in `docs/adr/0001-technology-selection.md`.
3. Create minimal solution projects (`Server`, `Core`, `Core.Tests`, `Server.IntegrationTests`) rather than every future project at once.
4. Implement a runnable LSP stdio process with verified lifecycle methods; send all logs to stderr.
5. Implement SqlScriptDOM parsing, a real `didOpen`/`didChange` flow, syntax diagnostics and `documentSymbol` for one stored procedure.
6. Add a deterministic LSP protocol integration test for the exact lifecycle and SQL snippet.
7. Run build/tests and fix failures. Commit the working vertical slice.
8. Summarize file paths, exact versions, commands run, tests passed, remaining limitations and the next M1/M2 tasks.

### Starter SQL fixture
```sql
CREATE PROCEDURE dbo.ProcessPayroll @PayrollId int AS
BEGIN
    SELECT PayrollId FROM dbo.Payroll WHERE PayrollId = @PayrollId;
END;
GO
EXEC dbo.ProcessPayroll @PayrollId = 42;
```

### Copy/paste prompt for a new development session

> You are the lead C#/.NET language-server engineer implementing **SqlScriptDOM LSP**. Read `SqlScriptDOM_LSP_Implementation_Plan.md` in full and follow it as the source of truth. Start immediately with **M0**, then implement a working **M1 vertical slice**: real stdio LSP initialization, document sync, SqlScriptDOM diagnostics and document symbols with integration tests. Verify actual current dependency APIs and official Claude Code plugin configuration; record architecture decisions. Do not substitute pseudocode for working code, do not assume a database exists, and do not advertise unimplemented features. Run `dotnet build` and `dotnet test`, fix failures, and report exact artifacts, tests and next tasks. Continue to M2 if M1 is fully passing and time permits.

## 18. References (consult latest version when implementing)

- [Microsoft SqlScriptDOM source](https://github.com/microsoft/SqlScriptDOM)
- [OmniSharp C# Language Server Protocol](https://github.com/OmniSharp/csharp-language-server-protocol)
- [OmniSharp LSP implementation guide](https://github.com/OmniSharp/csharp-language-server-protocol/blob/master/docs/lsp.md)
- [LSP specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/)
- [Official Claude Code documentation](https://code.claude.com/docs/en/plugins-reference)
- [Claude Code LSP integration overview supplied for this project](https://inferloop.dev/claude-plugins/lsp-integration/lsp-overview/)
- [SQL Server catalog view documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/catalog-views-transact-sql)
- [SQL expression dependencies documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sql-expression-dependencies-transact-sql)

---
**Handoff note:** Begin with a compilation-and-protocol spike, not the database integration. Preserve the two-source architecture from day one, but deliver functionality vertically. Any claim of support must be backed by an executable test.
