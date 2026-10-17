# ADR 0015: Project storage settings

Status: accepted for 0.13.0.

## Context

Metrics and persistent reports required shell environment variables or a custom MCP launch. Marketplace users need an editable configuration file that survives plugin upgrades and works across operating systems.

## Decision

Read `<workspace>/.sqlscriptdom.json` once at MCP startup, or the single file selected by `--settings`. Use a versioned, embedded JSON Schema for the two local-storage sections, `metrics` and `analysisCache`. Each requires explicit `enabled=true` to activate through the file. Relative directories resolve beside that file. Existing absolute directory flags override environment variables, which override file settings.

Keep the existing explicit SQL source configuration and LSP initialization contract separate. Automatic project discovery cannot select online/hybrid profiles. Do not search ancestors or user-global locations, merge files, expand shell expressions, fetch schema URLs, introduce hooks or add NuGet dependencies. Bundle an inactive example and schema, not an active configuration file.

## Consequences and verification

Settings changes require restarting the MCP process. Malformed, oversized, inaccessible or unsupported settings stop startup before protocol output; errors omit private values. Missing automatic configuration preserves disabled defaults. Metrics reads remain independent of SQL initialization. Report freshness, storage permissions/retention and failure handling are unchanged.

Real-process integration tests exercise marketplace-style stdio startup, process restart and report reuse, retained metrics, BOM/Unicode paths, standalone workspace/file selection, precedence, disabled defaults and invalid-file rejection. Native and installed-package tests run the same cases.
