# ADR 0008: Static call hierarchy for procedure understanding

Status: accepted.

Claude's `outgoingCalls` first sends `textDocument/prepareCallHierarchy`; version 0.6.1 had no handler and returned Method not found. Add the standard prepare/incoming/outgoing request group rather than treating a call-graph question as a generic text search. This supports stored-procedure understanding while the remaining editor-oriented M6 backlog stays deferred.

Use the compiled OmniSharp 1.0.0 `CallHierarchyHandlerBase` with three Handle overloads and SQL registration. The installed package uses Newtonsoft JToken data even though the current upstream source now shows System.Text.Json; a compilation spike verified the actual package shape. Generic framed process tests verify capability registration, item/data round trips, recursion, grouped call ranges, changes and unknown targets. Follow [the LSP contract](https://raw.githubusercontent.com/microsoft/language-server-protocol/gh-pages/_specifications/lsp/3.17/language/callHierarchy.md).

Add `SqlStaticReference.IsInvocation` only at genuine EXEC/function/TVF AST invocation sites. Keep aliases/column qualifiers as references for existing navigation without pretending they execute the function again. The navigation index resolves edges using current source/context rules, owns each invocation by its procedure/function range, groups repeated sites and preserves cycles as one-level edges. It does not execute SQL or embed model calls/hooks.

Prepare items retain qualified identity, URI/ranges and a document revision. Reject stale/tampered identity/ranges rather than binding them to a moved or changed module. Partial-coverage detail distinguishes recognized resolved/ambiguous/missing/unsupported calls and unavailable bodies. Metadata-only/encrypted modules do not become proven leaves. Incoming edges use the selected source anchor, avoiding silent merging of deployed/local execution graphs. Duplicate identities remain ambiguous. Rooted callable graphs omit top-level script and view callers; ordinary references can still expose their supported uses.

Build a fresh 0.6.2 release and update the checksum-pinned marketplace from native CI assets. Keep actual Claude model requests separate from generic LSP, catalog and installer evidence. Read/write footprints, control-flow/data-flow extraction, an automatic explanation skill and opt-in aggregate telemetry remain proposals in [the product assessment](../stored-procedure-understanding.md), not advertised implemented methods.
