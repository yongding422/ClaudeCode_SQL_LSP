# Live workspace reuse

The server retains immutable document and navigation generations. Reconciliation rereads and compares decoded contents, so unchanged sources reuse their AST/extraction while same-size/same-time edits still reparse. Initialization/rescans schedule at most four files concurrently; ScriptDOM's existing global parse limit remains in force. Startup-gap reconciliation is retained and can reread files; it no longer reparses every unchanged source.

Offline navigation builds are serialized and published only after successful completion. Stable document objects and static project-context generation reuse the built index. A canceled waiter does not replace a valid index. Edits and added/deleted competing declarations replace the relevant generation before subsequent analysis. Discovery reads module metadata directly instead of constructing rich reports just to obtain revisions.

`timings.navigationReused` distinguishes reuse from construction. Coverage reports cumulative parse attempts, actual bytes read, read/decode and parse/extraction worker time. Cumulative worker times can overlap and must not be added to request wall time. SQL and source paths are absent from normal numeric metrics; the explicitly requested file ledger contains paths and inclusion outcomes.

Run the read-only, no-model probe with a native candidate:

```sh
python scripts/probe-mcp-workspace.py --server /path/to/plugin/mcp/sqlscriptdom-mcp --workspace /path/to/sql --output artifacts/live-probe.json
```

For a framework-dependent build, use `--server /absolute/path/to/dotnet --assembly /absolute/path/to/sqlscriptdom-mcp.dll`. The probe supplies explicit disabled settings in its own temporary folder and does not scaffold settings in the source workspace. Report cold first request separately from later requests; this is a backend stage probe, not an answer-quality/token benchmark. Its results cannot establish persisted-restart behavior.
