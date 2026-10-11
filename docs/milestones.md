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
