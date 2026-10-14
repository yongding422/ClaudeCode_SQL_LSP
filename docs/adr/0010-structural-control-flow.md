# ADR 0010: structural control-flow contexts

Date: 2026-10-14. Implement increment 3 of the approved procedure-understanding plan using the existing ScriptDOM 180.117.0 AST and shared core report. General M6 editor features remain deferred. No dependencies or hooks are added.

Compiled API probes against the pinned package verified IF/WHILE predicates and bodies, CASE clause/input/result fields, TRY/CATCH StatementLists, exit/error statements and transaction names. `SqlControlFlow` traverses these actual types, retaining original source ranges, separate predicate/result/body regions and parent IDs. Relation/call facts receive IDs by source enclosure without introducing another name resolver. Catalog metadata remains authoritative for module identity even when stored headers predate a rename.

Choose a structural map rather than implying a full CFG. Lexical enclosure alone cannot establish sequential reachability, jump targets, runtime predicate truth, exhaustive error routing or transaction correctness. ELSE means FALSE or UNKNOWN, and CASE expression regions do not guard an enclosing DML target. Caller predicates are not propagated into callees. These limitations travel in the report and explanation skill. Dynamic SQL remains opaque and metadata-only bodies have no invented map.

Bound previews to 256 UTF-16 units with surrogate-safe cuts and full expression ranges. Bound nodes to 256 per module within the existing shared fact budgets. Expose truncation and unstructured jumps as coverage issues; IDs are revision-local. Existing saved-snapshot and cancellation rules apply. MCP serializes the same core data in structured content and JSON text. Tests use an independent protocol client and the owned disposable SQL fixture; analyzed user SQL is never executed.

Future path-sensitive reasoning, argument/value tracing and transaction-state analysis need separate contracts and reviewed fixtures. This increment does not claim complete runtime paths or comparative benefit over grep.
