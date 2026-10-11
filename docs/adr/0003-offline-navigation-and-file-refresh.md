# ADR 0003: conservative offline navigation and file refresh

Date: 2026-10-11. Status: accepted for M2.

## Names and context

Retain SqlScriptDOM `MultiPartIdentifier.Identifiers` components. Bracket/double-quote decoding already occurs in the AST; a decoded component can contain dots, so do not split a display name. Right-align one to four components into object/schema/database/server. Identity includes source context and kind. Each workspace root is a separate project; use the longest containing root. File buffers outside roots use their folder context, and opaque untitled buffers use independent URI contexts.

Track valid `USE` statements across batches. Explicit multipart database wins over `USE`, which wins over configured `defaultDatabase`. Schema matching uses explicit schema or optional `defaultSchema`; absent a schema, return a unique candidate only, preserving all candidates when ambiguous. Default case comparison is ordinal case-sensitive, with explicit ordinal case-insensitive opt-in. This does not infer server collation or a user's default schema. Never search another root/database or silently prefer a duplicate declaration in another file.

## Verified extraction and resolution

Use the pinned ScriptDOM 180.117.0 public AST APIs, confirmed in installed XML, compilable implementation and executable tests: `ExecuteSpecification.ExecutableEntity`, `ExecutableProcedureReference.ProcedureReference.ProcedureReference.Name`, `NamedTableReference.SchemaObject`, `FunctionCall.CallTarget`/`FunctionName`, table-valued function references, CTE expressions, query specifications, module parameters and variable/table-variable declarations. Procedure Number, ad-hoc and remote EXEC properties are explicitly guarded. No new dependencies are needed.

A separate semantic visitor records static references and local definitions. Query alias collection stops at nested/derived queries; correlation searches outer scopes. CTE scope ends with its statement. Modules cannot capture creation-batch variables. Duplicate aliases/locals and forward CTE use remain unsupported. Temporary DROP/recreation creates separate declaration anchors. Named EXEC argument labels are callee parameters and must not be mistaken for caller variable uses. ALTER TABLE is an outline/use, never a new global definition; table-only statements cannot bind views.

Resolution returns `Resolved`, `Ambiguous`, `Unresolved` or `Unsupported`, plus candidates/explanation. Navigation requires a unique verified anchor. References include only uses resolving to that anchor; includeDeclaration is respected. Provenance remains in core results. Standard LSP locations do not have SQL provenance/status fields: empty results cover ambiguity/unresolved/unsupported, and coverage limits are documented rather than attaching nonstandard fields.

## Protocol and indexing

Use compiled OmniSharp 1.0.0 `IDefinitionHandler`, `IReferencesHandler`, `IWorkspaceSymbolsHandler` and `IDidChangeWatchedFilesHandler`. Their contracts were checked against the [maintainer feature source](https://github.com/OmniSharp/csharp-language-server-protocol/tree/master/src/Protocol/Features) and verified with an independent framed child-process client. Target [LSP 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/); a definition may serialize as one Location or an array. Advertise only implemented features and disable all analysis on invalid configuration, retaining ADR 0002 behavior.

Read a completed immutable effective workspace snapshot for each request. Store disk job identities/cancellation separately from buffer jobs; only the current disk job may replace its snapshot, and open buffers always win. Native filesystem events debounce at 100 ms. File events refresh individually, directory moves/removals and overflow reconcile known/new URIs. Client file events follow the same guarded refresh pipeline. Native watch setup failure logs a status and preserves client notifications/re-open refresh.

Keep symbols and semantic uses in memory; reference queries currently scan and resolve indexed uses rather than adding persistence or a reverse cache before benchmarks. M3 will introduce an opt-in metadata provider through this source-aware pipeline; M2 neither connects to SQL Server nor executes SQL.

## Verification gate

Core tests cover normalized quotes/escapes, schemas/databases/case, project separation, unknown/ambiguous names, aliases (including nested JOIN predicates), CTE shadowing/forward names, variables/parameters/temp lifetimes, dynamic/remote/numbered targets, kind filtering, cancellation, disk races and URI renames. Process tests exercise actual definition/references/workspace requests, unsaved overlays, native/client file events and multiple roots. Final build/test and published-binary evidence are recorded in `docs/milestones.md`.
