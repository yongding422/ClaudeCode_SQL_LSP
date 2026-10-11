# M1 architecture

`SqlLsp.Server` hosts OmniSharp 1.0.0 for LSP 3.17 framing, lifecycle, request scheduling, capability registration and request cancellation. A shared `ServerState` validates initialization options against an embedded JSON Schema, sets up the offline source and converts plain core ranges/diagnostics into protocol types. Handlers implement actual compiled package interfaces; only text synchronization and document symbols are registered.

`SqlLsp.Core` is independent of the LSP framework. `SqlParser` creates an actual ScriptDOM parser for the selected dialect for each parse. It retains source, tokens, AST (or an empty fallback when ScriptDOM returns null), syntax diagnostics and top-level declarations. A batch visitor extracts CREATE, ALTER and CREATE OR ALTER procedure/view/function declarations and CREATE/ALTER tables. It deliberately does not recurse into local table declarations inside procedure bodies. Errors intersecting a declaration cause that declaration to be omitted.

`SourceText` translates .NET UTF-16 offsets to zero-based positions and applies sequential edits, with full replacements supported. CRLF is one line break; LF and CR are also supported. Positions past a line's length clamp to its end. Negative/out-of-document ranges and edits splitting surrogate pairs are rejected atomically.

`WorkspaceFiles` acquires only bounded local `.sql` files, excludes common build folders and reparse points, and checks configured-root containment before reads. `DocumentStore` holds disk snapshots and open overlays, replaces the effective per-URI index atomically, cancels superseded jobs, and verifies overlay identity before publishing diagnostics. Reopen starts a new identity even if its version number resets. Symbol requests await the current parse using their own cancellation token; canceling a request does not close the buffer. Close removes the overlay, clears diagnostics, and refreshes the saved disk source.

This unified snapshot/index path leaves room for source-aware symbols and providers in M2–M4 without placing protocol types in provider contracts. M1 intentionally creates four projects rather than empty future assemblies.
