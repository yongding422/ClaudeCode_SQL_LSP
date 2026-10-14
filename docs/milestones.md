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

## M4 — complete (2026-10-11)

- [x] Validate explicit initialized-root mappings to enabled profiles, canonicalize directory URIs, reject unsafe/missing/duplicate mappings and keep unmapped projects offline. Only mapped hybrid profiles are contacted; online retains its one selected profile behavior.
- [x] Filter shared symbols by context/database/schema/name/kind and appropriate comparison rules before source selection. Preserve all candidates with a separate selected target; local/unsaved declarations win by default for one mapped identity. Configured database preference works, while duplicate declarations, schema/kind collisions and competing profile identities stay ambiguous.
- [x] Supply permitted catalog schema descriptions when local DDL is missing, retain source/capture provenance, and collect verified local/deployed AST references from equivalent declaration anchors. Never invent spans for catalog dependency records.
- [x] Compare safely parsed module token hashes with Match/Different/Unavailable/Ambiguous states. Ignore formatting/comments/acquisition headers; preserve body values. Expose comparison/provenance/timestamps in workspace symbol data and bounded log notifications. Hidden/encrypted/table-only/malformed source remains unknown; no behavioral equivalence/change is claimed.
- [x] Isolate per-profile caches/files/status and limit acquisition to two concurrent sources under a shared 30-second budget. Failed/expired catalogs are excluded while local analysis and healthy mapped profiles remain available. Last verified comparison rules can support local fallback. Stale definition fallback remains explicitly disabled.
- [x] Add deterministic core/wire fixtures plus real SQL Server hybrid cases for precedence, edited buffer comparison, close restoration, encrypted source, schema fallback, duplicate guards, independent mappings/profile failures and continued local navigation after a stopped-server outage and cache expiry. Only the owned disposable fixture is stopped/restarted; no existing database is used.

Final verification on Windows with Docker's Linux engine:

| Check | Result |
| --- | --- |
| `dotnet restore --locked-mode` | Passed; existing dependency pins/locks unchanged |
| Release build | Passed; 0 warnings, 0 errors |
| Full Release suite, live opt-in enabled | **145 passed: 76 core, 43 wire/process, 26 provider/catalog; 0 failed, 0 skipped** |
| Live SQL Server cases | **9 passed**: M3's five plus four hybrid cases |
| Default suite without live opt-in | **136 passed, 9 explicitly skipped**; no database needed |
| Publish and full suite with `SQLLSP_TEST_SERVER_DLL` pointing at published DLL | **145 passed**, including live hybrid/outage wire behavior |
| `dotnet format --no-restore --verify-no-changes` | Passed |
| `git diff --check` | Passed |
| NuGet vulnerability audit with transitive packages | No vulnerable packages reported by configured source |
| Strict Claude development manifest validation | Passed for plugin 0.4.0; actual Claude interaction remains M5 |
| Disposable fixture cleanup | No labeled SQL LSP containers remain after completion |

Dependencies remain .NET SDK 10.0.401, ScriptDOM 180.117.0, OmniSharp 1.0.0, JsonSchema.Net 9.4.0 and SqlClient 7.1.1 with existing test pins. Process-test class parallelism is bounded to two; actual request concurrency/cancellation fixtures remain active. No database execution/deployment capabilities were added. Configuration and implementation decisions are documented in [hybrid setup](hybrid.md) and ADR 0005.

Known limits: normalized token comparison is not semantic drift analysis; original table DDL and inaccessible source cannot be compared. Cross-server names, full collation/USE rebinding, runtime mappings/reconfiguration, large-index performance and stale-cache opt-in are not implemented. Cache freshness and private file lifetime retain M3 limits. Local evidence is Windows plus Docker's Linux SQL Server engine; Linux/macOS host CI and actual Claude Code LSP behavior are separate gates.

Next milestone: M5 real Claude Code LSP/plugin verification, reproducible distribution/installation, performance/security/stress hardening and documented client/platform results.

## M5 — complete (2026-10-11)

- [x] Recheck official Claude plugin/LSP semantics against installed 2.1.201 and validate the minimal 0.5.0 manifest. Document current-version fields and separate manifest validation from actual client requests in ADR 0006.
- [x] Build self-contained win-x64/linux-x64/osx-arm64 plugin ZIPs with pinned .NET 10.0.12, locked RID dependency graphs, bundled apphosts, dependency/license notices, checksums, deterministic archive metadata and source provenance. Provide local install/demo/troubleshooting instructions and native packaging CI gates; do not publish or mutate global client settings.
- [x] Run real signed-in Claude sessions using public isolated fixtures. The final extracted Windows plugin path contains spaces/Chinese text; Claude registers the server, starts the apphost, receives initialize, sends didOpen, receives one ScriptDOM diagnostic after Edit, and shuts down cleanly. Default behavior works without a compatibility flag. Record sanitized evidence and the navigation-tool exposure limitation explicitly.
- [x] Add startup without PATH/dotnet discovery, child-environment secret redaction, unknown-method/concurrent requests, rapid multi-document edits/two-root isolation, 200-file churn, bounded UTF-8/BOM reads, unexpected initialization failures, and linked-root/ancestor fixtures. Canonicalize test temp paths for macOS's standard /var alias while retaining production's strict link rejection.
- [x] Bound active ScriptDOM parsing to four, release completed disk job bookkeeping, pool bounded file reads, reject database attachment/user-instance/failover options before connection, and suppress unexpected initialization exception details while answering the handshake and disabling analysis.
- [x] Measure real stdio startup, 200KB diagnostics, 1,000-file warm definitions and 10,000-file indexing/memory. Record standalone results and a contended target miss without converting wall-clock targets into unreliable unit test assertions.

Verification on Windows with the owned disposable SQL Server fixture:

| Check | Result |
| --- | --- |
| Locked restore and Release build | Passed; 0 warnings, 0 errors |
| Full functional suite with live opt-in | **156 passed: 79 core, 48 wire/process, 29 provider/catalog; 0 failed**; benchmark separately opt-in |
| Live SQL Server cases | **9 passed**; 20 deterministic provider cases |
| Explicit standalone performance fixture | **1 passed**; raw public measurement JSON committed |
| Default no-database suite | **147 passed, 10 explicitly skipped** (nine live SQL cases and one benchmark) |
| Extracted self-contained Windows apphost suites | **48 wire/process + 29 provider/catalog passed**, including live SQL and PATH-empty startup; benchmark skipped |
| Actual Claude session on extracted plugin | Passed diagnostics smoke; initialize/didOpen/nonempty diagnostics/shutdown observed, exit 0 |
| Strict plugin validation | Development plus all three RID plugin manifests passed |
| Cross-publish | All three RID archives built; Windows executed locally, other native host runs remain CI gates |
| Formatting, PowerShell syntax, diff whitespace checks | Passed |
| NuGet audit including transitive packages | No vulnerable packages reported by configured nuget.org source |

Small-workspace startup measured 1.13 s; 200KB diagnostic p95 292 ms; 1,001-file warm definition p95 18 ms. Ten-thousand-file indexing took 9.47 s with 201.6 MiB sampled working set. A contended diagnostic run missed the 300 ms target (487 ms p95). These are synthetic host measurements, not universal performance guarantees. See [performance evidence](performance.md).

M5 acceptance is met through actual Claude diagnostics and generic LSP navigation proof. Claude's LSP navigation tool was absent from the tested constrained 2.1.201 sessions; no successful Claude definition/references claim is made. Linux/macOS native execution/client sessions, fresh-OS dependency certification, signed/public marketplace distribution, other database authentication setups and larger complex/hybrid workloads remain unverified. The server trusts its local client and does not enforce a process-wide memory quota; abrupt termination/private snapshot and metadata freshness limits remain documented.

See [installation](installation.md), [client evidence](claude-verification.md), [security/resource model](security.md), [limitations](semantic-limitations.md) and ADR 0006. Next is optional M6: choose a focused feature such as hover or richer binding, with complete confidence/coverage tests before broader completion/rename/MCP work.

## M6 — remaining work deferred; first hover increment complete (2026-10-13)

- [x] Compile/register actual `textDocument/hover` with OmniSharp 1.0.0 and disable it on failed initialization. Reuse definition selection and precise requesting-document UTF-16 ranges; missing/ambiguous/unsupported bindings return null.
- [x] Add bounded ScriptDOM-derived declaration signatures and lexical types, excluding defaults/body expressions and labeling syntax/unknown column or implicit-nullability limits. Surface authoritative permitted catalog names, types/parameters/columns, coverage and capture time without reconstructing hidden DDL.
- [x] Respect hybrid source preference/unsaved overlays and requesting-root comparison context. Test real catalog/restricted accounts, renamed/encrypted modules, database preference/duplicates and continued local hover after expired/outage catalog removal.
- [x] Negotiate plaintext/Markdown with safe text fences and display-only control sanitization. Verify incremental changes/close restoration, request cancellation, hostile identifiers, UTF-16/CRLF/emoji, direct catalog anchors and preview/content limits.
- [x] Demonstrate actual Claude Code 2.1.201 hover with public SQL and client initialize/hover/shutdown traffic. An initial default-flags run succeeded; a final patched apphost compatibility probe succeeded in 146 ms. Another repeat exposed no LSP tool despite registration. Record evidence/variability rather than claiming uniform availability.
- [x] Address newly published Oct 13 PKCS high/moderate advisories by pinning PKCS **9.0.21**, updating locks and bundling runtime **10.0.13**. Audit all projects again and run real SQL/TLS catalog tests. SDK 10.0.401, ScriptDOM 180.117.0, OmniSharp 1.0.0, JsonSchema.Net 9.4.0 and existing test pins remain unchanged.

| Verification | Result |
| --- | --- |
| Locked restore / Release build | Passed; 0 warnings/errors |
| Patched full functional suite with owned SQL Server opt-in | **181 passed: 94 core, 54 wire/process, 33 provider/catalog; 0 failed**; benchmark separately opt-in |
| Live SQL Server cases | **11 passed**; 22 deterministic provider/catalog cases |
| Default suite | **170 passed, 12 skipped** (11 live cases plus benchmark); no database required |
| Extracted patched Windows apphost suites | **54 wire/process + 33 provider/catalog passed**, including hover/live SQL and PATH-empty startup |
| Packaged performance fixture | Passed on traced repeat; earlier 10,000-file initialize exceeded its 60-second deadline |
| Final patched Claude hover probe | Actual LSP call/result and protocol response observed; signature/source matched; exit 0 |
| RID publishing | win-x64/linux-x64/osx-arm64 builds; Windows executed locally; native other hosts remain CI gates |
| Post-patch NuGet audit | No vulnerable packages reported across all projects by configured source |
| Formatting / diff checks | Passed |

Packaged runtime 10.0.13 measured 27-file startup 1.31 s, 200KB diagnostic p95 345 ms (target miss), and 1,001-file warm definition p95 13 ms. Its 10,001-file indexing took 58.41 s, near the deadline; startup variability is unresolved and needs profiling before performance guarantees. See [raw measurements and caveats](performance.md). Real Claude hover is demonstrated in documented sessions, not guaranteed in every constrained session or every platform.

M6 is **not complete and its remaining work is deferred**, following the user's direction on 2026-10-13 to focus the plugin on SQL code search. The completed hover increment and dependency security fixes remain available. Contextual completion, semantic tokens, richer scope/column resolution, confidence-gated rename, optional DacFx evaluation and a separate MCP adapter are deferred proposals; none are advertised as implemented. This priority change supersedes the earlier recommendation to implement completion next. See [hover contract](hover.md), ADR 0007 and [client evidence](claude-verification.md).

Proposed next work, to be scoped around SQL code search:

1. Verify real Claude definition, references and workspace search calls and document tool availability with reproducible evidence.
2. Improve search relevance and qualified, root-scoped matching while preserving source provenance and conservative ambiguity handling.
3. Expand verified static reference/dependency coverage, with explicit limits for dynamic SQL and incomplete bindings.
4. Profile indexing and startup variability, then improve reliability and performance on larger SQL workspaces.

These are roadmap priorities, not claims of implemented features or a new completed milestone. The original implementation specification remains the historical baseline.

## Distribution update — macOS and GitHub marketplace (2026-10-13)

Plugin 0.6.1 adds Intel Mac packaging alongside Apple Silicon, Windows x64 and Linux x64. Remaining M6 work stays deferred; this update supports distribution and SQL search/navigation usage.

[Native CI](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38500332165) passed all nine jobs. Default suites passed 170 tests per Windows/Linux/macOS ARM64 host, with 12 deliberate skips; the owned Linux SQL Server fixture passed all 33 provider tests including 11 live cases. All four native packaged apphosts passed 54 wire and 22 deterministic provider tests. Both Mac architectures additionally passed the wire suite after native `unzip` extraction, preserving executable modes and handling spaces/Chinese paths. Formatting and marketplace integrity tests passed on all default hosts. [Evidence](evidence/macos-ci.json) records counts and limits.

The GitHub catalog is generated from those exact clean-source CI ZIPs, with platform-specific manifest names and checksum pins. Strict catalog validation passed using isolated Claude Code 2.1.224, the minimum documented version for archive sources. [Publishing instructions](publishing.md) and [macOS usage](macos.md) describe installation. A separate manual workflow tests actual marketplace installation and runs the installed apphost's wire suite in isolated Claude configurations; these tests make no model calls and do not establish Mac Claude hover/navigation behavior.

Release [0.6.1](https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/tag/v0.6.1) is public. Published asset digests match all catalog pins. [Marketplace installation CI](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38501092077) passed on all four platforms using Claude 2.1.224: registration, checksum-pinned ZIP install, strict manifest validation and 54 wire tests per installed apphost. Mac executable modes survived the actual Claude archive installer without a chmod repair. The final source/catalog commit's [nine build/test jobs](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/38500948752) also passed. Remaining M6 work is still deferred.
