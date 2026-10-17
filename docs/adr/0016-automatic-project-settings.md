# ADR 0016: Automatic starter settings

Status: accepted for 0.14.0; extends ADR 0015.

## Decision

When the MCP server starts without an explicit `--settings` selector, create `<workspace>/.sqlscriptdom.json` if absent. The starter exposes version 1, metrics and report-cache directories with both features disabled. Existing settings remain authoritative and are never overwritten, reformatted or repaired automatically. Standalone summary, evaluation and cache-prebuild commands do not create starter settings.

Use the existing MCP startup path, with no install/session hook, shell wrapper or additional dependency. Installing a plugin is independent of choosing the workspace in which its server will start. Project settings appear when that workspace's MCP process starts, not when an archive is downloaded globally.

Publish a complete temporary file with a no-overwrite move so concurrent process starts cannot expose partially written JSON. Remove only the owned temporary file. Unwritable workspaces produce a private generic stderr notice and continue with parsed/default options. Protocol stdout remains JSON-RPC only. Invalid existing files still fail schema validation before server startup, preserving user contents.

## Verification

Real-process tests cover first startup without SQL initialization, byte/time preservation, concurrent starts, explicit file selection, standalone commands, selected workspace behavior, invalid existing files and read-only Unix workspaces. Metrics and cache opt-in, SQL/database source configuration and LSP behavior remain unchanged.

References: [Claude plugin manifest](https://code.claude.com/docs/en/plugins-reference) and [hook lifecycle](https://code.claude.com/docs/en/hooks). Startup scaffolding uses the existing MCP entry point rather than introducing a new lifecycle hook.
