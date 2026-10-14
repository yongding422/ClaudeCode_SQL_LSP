# Static procedure/function call hierarchy (0.6.2)

The server implements `textDocument/prepareCallHierarchy`, `callHierarchy/outgoingCalls` and `callHierarchy/incomingCalls`, and advertises `callHierarchyProvider` only when initialization succeeds. These are actual LSP requests, not hooks. Claude can use its `outgoingCalls` and `incomingCalls` operations when the client exposes them.

Prepare accepts a supported uniquely resolved procedure/function use, a callable declaration, or an otherwise unbound position inside a callable's body. Unsupported/ambiguous references, tables/views, local variables, invalid UTF-16 positions and duplicate declarations produce an empty result. Node ranges cover the module and selection ranges cover its name.

Outgoing calls include uniquely resolved named EXEC procedure targets, schema-qualified scalar function calls and table-valued function invocations. Alias declarations and qualified column uses remain navigation references but are not additional call sites. Repeated invocations group by selected target with sorted/deduplicated ranges in the caller document. Incoming calls group verified sites by owning procedure/function. Recursion is represented as an edge; the server returns one level per request rather than recursively walking forever. Clients should cap traversal depth/module count and track visited anchors.

These are **partial static graphs**. Dynamic/variable/remote/numbered EXEC targets, missing or ambiguous definitions, built-ins without indexed definitions, views/top-level script callers and unsupported syntax do not acquire fabricated edges. Use ordinary references/text search for broader usage discovery. No call graph can guarantee runtime targets or behavior. SQL text is never executed.

Each item includes source kind and partial-coverage detail, plus data containing identity, revision and body-availability/recognized-invocation counts. An unavailable catalog body is labeled unknown, not a verified leaf. Counts describe the recognized invocation subset, not all syntax or whole-database completeness. Incoming results reflect selected-source edges; workspace and deployed counterparts are not silently merged into one execution graph.

Claude's tool renderer may show only names/locations/call sites rather than the item's coverage data/detail. When explaining behavior, inspect the actual source and report that limitation instead of interpreting an empty rendered result as a complete dependency report. A unified coverage/context output is a proposed next feature.

Hierarchy items preserve a source revision, full/selection ranges and qualified context identity between requests. Items from changed buffers/disk files or replaced catalog generations return empty results; prepare again. Tampered or missing data cannot attach to a different symbol. New requests use immutable current snapshots and observe cancellation. Open overlays, close restoration, root isolation, hybrid source preference, authoritative renamed catalog names and hidden-body limits use the existing source/resolution rules.

Generic stdio tests exercise all three requests, repeated/cyclic calls, signatures/ranges, functions/TVF aliases, comments/strings, dynamic/missing/ambiguous targets, source edits, Unicode/CRLF, invalid initialization and tampered items. Catalog tests include authoritative renamed identities and real owned SQL Server calls/hidden modules. Native package/client installation and real Claude model calls are recorded separately in milestone evidence.

Example after locating a procedure's declaration in Claude:

> Use LSP outgoingCalls for this procedure. Expand relevant callees up to two levels, stop at cycles, and cite the actual call sites. List unresolved/unsupported targets separately. Then read the definitions to explain Ohio-related behavior; do not execute SQL.

Implementation follows [the LSP call hierarchy contract](https://raw.githubusercontent.com/microsoft/language-server-protocol/gh-pages/_specifications/lsp/3.17/language/callHierarchy.md), with the installed OmniSharp 1.0.0 handler/data types verified by compilation and real process tests. Procedure understanding priorities and metric proposals are in [the assessment](stored-procedure-understanding.md).
