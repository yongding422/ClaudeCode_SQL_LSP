# ADR 0002: M1 snapshots, positions and validated configuration

Date: 2026-10-11. Status: accepted.

Keep source acquisition and parsing independent of LSP types. Use document URI as the per-document index key and immutable declaration/diagnostic arrays; complete source-aware object identities and SQL name resolution are M2. Do not collapse names across schemas or documents.

Each open/change installs a new overlay identity before scheduling parsing. Cancel old work, reject older document versions, and verify both identity and cancellation under the publication lock. This protects close/reopen even when the version resets. Deterministic fake-parser tests deliberately finish older work last; process tests exercise rapid notifications and cancellation. Disk and buffer sources replace the same index entry. Closing re-reads disk instead of retaining an obsolete pre-save copy.

Use UTF-16 explicitly, with zero-based coordinates derived from source offsets rather than ScriptDOM's one-based Line/Column. Retain the token stream independently: dialect rejection can return a null AST. Verify every exposed dialect with the installed parser factory.

Validate the M1 initialization-options subset against an embedded Draft 2020-12 schema. **JsonSchema.Net 9.4.0** was confirmed using its [official NuGet feed](https://api.nuget.org/v3-flatcontainer/jsonschema.net/index.json) and [maintainer API documentation](https://docs.json-everything.net/schema/basics/). Its current API builds/evaluates JsonElement; clone the schema element so it survives disposal of the source JsonDocument. Pin the package and its dependency graph. Report schema paths/keywords without including user-supplied values.

Executable invalid-config tests showed that throwing from the pinned OmniSharp initialize callback causes initialize to remain unanswered (including JsonRpcException and RpcErrorException). Resolve this with a fail-closed state: complete lifecycle initialization, disable all analysis capabilities, create no source provider/index, and report configuration errors through `window/showMessage` after `initialized`. Require restart with valid settings. The tests assert this behavior and graceful shutdown. A successful lifecycle handshake in this state is not a claim that analysis configuration succeeded.

Full application config files, runtime updates, online/hybrid keys and configurable globs are deferred until their corresponding functionality exists. The schema rejects unsupported keys. No database package, connection or SQL execution is introduced in M1.
