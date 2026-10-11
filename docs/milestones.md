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
