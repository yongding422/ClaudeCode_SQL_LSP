# Milestone evidence

## M0 — complete (2026-10-11)

- Empty working directory bootstrapped as a Git repository; the full supplied plan preserved at the root.
- Official documentation and exact package assets reviewed; decisions in ADR 0001.
- Four real projects: Core, Server, Core.Tests, Server.IntegrationTests; central package versions, dependency locks, formatting rules, warnings-as-errors, three-platform CI configured.
- `dotnet restore` passed. `dotnet build --no-restore` passed with zero warnings/errors.
- `dotnet test --no-build --no-restore`: **2 passed, 0 failed, 0 skipped**.
- Parser AST/visitor/tokens/error offsets verified against the actual package. Independent JSON-RPC client initialized a real child process with fragmented input, verified framed stdout, and completed shutdown/exit.
- No database or Claude authentication needed. Claude Code 2.1.201 is installed; plugin runtime integration is an M5 gate.

M1 may proceed after this successful build/test gate.

## M1 — complete (2026-10-11)

- Real ScriptDOM parser service, independent token capture, AST batch visitor, CREATE/ALTER/CREATE OR ALTER procedure/view/function outlines and CREATE/ALTER table outlines.
- UTF-16 source coordinates with CRLF/LF/CR, Chinese text, emoji, tabs, identifier selection ranges and sequential full/incremental edits tested.
- Versioned document snapshots, cooperative cancellation, obsolete-result rejection, close/reopen identity checks, diagnostics replacement/clearing, oversized-buffer recovery and disk restoration after save/close.
- Stable per-document workspace index; the 24-file sample initializes, returns outlines before files open, then opens every file without syntax errors. Unsaved edits update outlines.
- Actual stdio handlers for sync, diagnostics and document symbols; generic wire tests cover fragmented input, lifecycle, parallel requests, rapid versions, request cancellation and flat/hierarchical client capability negotiation. Unimplemented methods are not advertised.
- Embedded JSON Schema validates supported initialization options with **JsonSchema.Net 9.4.0**. Invalid configuration disables analysis and reports schema errors after the handshake; the verified framework workaround is documented in ADR 0002.

Final verification on Windows:

| Check | Result |
| --- | --- |
| `dotnet restore --locked-mode` | Passed |
| `dotnet build --no-restore --configuration Release` | Passed; 0 warnings, 0 errors |
| `dotnet test --no-build --no-restore --configuration Release` | **53 passed: 38 core, 15 process integration; 0 failed, 0 skipped** |
| `dotnet format --no-restore --verify-no-changes` | Passed |
| `git diff --check` | Passed |
| `dotnet list package --vulnerable --include-transitive` | No vulnerable packages reported in any project by configured NuGet source |
| `dotnet publish src/SqlLsp.Server --no-restore --configuration Release -o claude-plugin/server` | Passed; runnable DLL, apphost and dependencies produced |
| All process integration tests with `SQLLSP_TEST_SERVER_DLL` pointing at the published DLL | **15 passed**, including cancellation and diagnostics |
| PowerShell 7 launch wrapper against published DLL | Framed initialize/shutdown/exit smoke passed; no stdout contamination |
| `claude plugin validate ./claude-plugin --strict` on Claude Code 2.1.201 | Validation passed |

Artifacts: source in `src/`, tests in `tests/`, samples in `samples/sql-workspace/`, dev manifest in `claude-plugin/`, published output in ignored `claude-plugin/server/`, launch examples in `scripts/`, cross-platform CI in `.github/workflows/ci.yml`.

Known limitations: M1 implements syntax and outlines, not semantic navigation. Workspace watcher updates and qualified resolution are M2; database/hybrid functionality is M3/M4. The schema covers the implemented initialization subset only. Actual Claude LSP requests remain unverified M5 work; strict manifest validation is not an end-to-end Claude claim. Linux/macOS CI is configured but has not run on this local Windows host. See `semantic-limitations.md` for details.

Stop at the completed M1 gate for this request. Next executable milestone: M2 qualified identifier normalization, conservative scope resolution, definition/references/workspace symbols and file create/change/delete handling, each with false-positive and ambiguity fixtures.

## M2 — complete (2026-10-11)

- [x] Qualified names retain AST-decoded identifier components, escaped delimiters and omitted multipart components. Symbol identities include project context, server/database/schema/name and object kind.
- [x] Extract static EXEC, FROM/JOIN/named relations, selected scalar/table-valued functions and table-only ALTER/DROP uses, with source ranges and provenance. Dynamic/remote/variable/numbered procedure targets remain unsupported.
- [x] Implement actual compiled `textDocument/definition`, `textDocument/references` and `workspace/symbol` handlers. Definition selects only a unique verified anchor; references respect `includeDeclaration` and exclude unresolved/ambiguous/unsupported uses.
- [x] Local bindings handle CTEs, nested/correlated aliases, module parameters, scalar/table variables and temporary table lifetimes conservatively. Duplicate/forward names, aliases from nested JOIN subqueries, named EXEC argument labels and schema/case collisions have false-positive tests.
- [x] Native and client file events refresh creates, changes, deletes and file/directory renames. Open overlays survive disk updates/deletion until close. Independent disk job identities prevent old reads from overwriting newer snapshots; URI paths with spaces/Chinese text and multi-root isolation are tested.
- [x] Add the three-file payroll fixture to the existing sample: `payroll-caller.sql` references `dbo.ProcessPayroll` in `payroll-procedure.sql`, which uses `dbo.Payroll` in `payroll-table.sql`. Sample now contains 27 files.

Final verification on Windows:

| Check | Result |
| --- | --- |
| `dotnet restore --locked-mode` | Passed; pinned dependency graph unchanged |
| `dotnet build --no-restore --configuration Release` | Passed; 0 warnings, 0 errors |
| `dotnet test --no-build --no-restore --configuration Release` | **84 passed: 62 core, 22 process integration; 0 failed, 0 skipped** |
| `dotnet format --no-restore --verify-no-changes` | Passed |
| `git diff --check` | Passed |
| `dotnet list package --vulnerable --include-transitive` | No vulnerable packages reported in any project by configured NuGet source |
| `dotnet publish src/SqlLsp.Server --no-restore --configuration Release -o claude-plugin/server` | Passed |
| All process integration tests against the published DLL via `SQLLSP_TEST_SERVER_DLL` | **22 passed** |
| `claude plugin validate ./claude-plugin --strict` | Validation passed for development plugin 0.2.0 |

No database was required or contacted. Dependencies remain .NET SDK 10.0.401, ScriptDOM 180.117.0, OmniSharp 1.0.0 and JsonSchema.Net 9.4.0, with existing pinned test packages. The compiled APIs and design are recorded in ADR 0003. Native watcher and full cross-file navigation behavior were verified with a separate process and generic LSP client. Claude interaction and Linux/macOS runtime results remain unverified; CI is configured.

Known limits: this is conservative static workspace resolution, not a full SQL Server binder. Column/projected-expression binding, full collation semantics, cross-server mapping, dynamic SQL and complex unimplemented table-source aliases are not claimed. Reference queries currently scan the in-memory index; performance/stress measurements remain later gates. Unsupported configuration still fails closed. See `semantic-limitations.md` for exact coverage.

Next milestone: M3 opt-in read-only SQL Server catalog discovery, secure connection profiles, permitted metadata/module retrieval, controlled definition snapshots and permission/cancellation integration fixtures. M2 completes the requested offline continuation; online/hybrid modes remain absent.

## M3 — complete (2026-10-11)

- [x] Add a real SqlServer provider project using pinned **Microsoft.Data.SqlClient 7.1.1** and locked dependencies; compiled APIs and official sources recorded in ADR 0004.
- [x] Validate explicitly enabled profiles with environment-only secrets, approved endpoint/database checks, required TLS, readOnly=true and bounded settings. Offline mode ignores enabled profiles; online mode requires explicit selection when multiple profiles are enabled and contacts only the selected profile.
- [x] Discover visible tables/views/procedures/functions, column types/nullability, parameter types/output flags, available module source and incomplete static catalog dependencies. Record server version, collation/default schema and capture timestamp. Restricted metadata and encrypted/renamed modules are real SQL Server fixtures.
- [x] Bound objects/per-object members/dependencies/module size and aggregate storage, parameterize filters, use async cancellation and explicit connect/command/total-load timeouts, serialize acquisitions, retry only one transient read failure, cache within TTL and reject expired fallback. Shutdown cancellation and invalid provider limits have deterministic tests.
- [x] Normalize catalog objects into the existing source-aware symbols/index, with catalog identities overriding old module headers after rename. Online lexical bindings re-use ASTs with database comparison rules once metadata arrives. Online mode does not scan the repository unless explicitly configured.
- [x] Navigate database sources through controlled file URIs with private ACLs/modes, read-only files, metadata-only descriptions when original SQL is unavailable, immutable catalog outlines and bounded generation cleanup. LSP references use visible module/buffer AST ranges; catalog dependencies do not acquire invented source ranges.
- [x] Seed and test a disposable SQL Server 2022 Developer container with generated credentials, dedicated metadata/limited users and a random loopback port. No existing server was configured or touched. The fixture owns all DDL and removes its own container. Add an opt-in Linux live CI job alongside the default three-platform suite.

Final verification on Windows with Docker's Linux engine:

| Check | Result |
| --- | --- |
| `dotnet restore --locked-mode` | Passed; six projects, updated locks |
| `dotnet build --no-restore --configuration Release` | Passed; 0 warnings, 0 errors |
| Full Release suite with `SQLLSP_RUN_SQLSERVER_TESTS=1` | **115 passed: 62 core, 31 wire/process, 22 provider/catalog; 0 failed, 0 skipped** |
| Provider/catalog suite contents | 17 deterministic tests plus 5 real SQL Server cases |
| Default Release suite without live opt-in | 110 passed, 5 explicitly skipped live cases; no database required |
| `dotnet format --no-restore --verify-no-changes` | Passed |
| `git diff --check` | Passed |
| NuGet vulnerability audit including transitive packages | No vulnerable packages reported by configured source |
| Publish server and test both integration projects using the published DLL | Passed: 31 wire/process plus 22 provider/catalog tests |
| `claude plugin validate ./claude-plugin --strict` | Passed for development plugin 0.3.0; actual Claude requests remain M5 |
| Docker fixture cleanup | No labeled fixture containers remain after completion |

The live image is pinned to `sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090`. Live tests verify schema collisions, columns/types, parameters/output flags, visible module bodies, dependencies, encrypted text, unchanged source after rename, restricted metadata permissions, limits, hostile schema filters, cancellation, case-insensitive search/bindings, source file navigation, module references and online repository isolation. Private files are removed on normal server shutdown. See [online setup](online.md) for reproducible commands and security/freshness limits.

Known limits: one selected online profile; hybrid precedence/multi-target mapping and drift remain M4. Metadata collection is not transactionally atomic, cached permissions can last until TTL, and force termination can leave private files. SQL authentication is verified; Entra extensions and other deployment authentication setups are not. Full SQL collation/version binding, dynamic SQL and missing source coverage are not claimed. CI is configured, but Linux/macOS host results and real Claude Code LSP behavior are not local evidence.

Next milestone: M4 explicit hybrid mappings, buffer/workspace/database precedence with preserved candidates and provenance, definition drift, and offline degradation under database failure.
