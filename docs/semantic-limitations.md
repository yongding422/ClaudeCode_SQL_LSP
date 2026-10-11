# Current limits

- M1 supplies syntax diagnostics and declaration outlines. ScriptDOM is not a semantic SQL binder; missing tables and invalid column names do not produce semantic diagnostics.
- Definitions, references, workspace symbol search, scopes, aliases, collation-aware qualified identities and file watcher updates start in M2. Workspace files are scanned at initialize and re-read at close. Unopened file edits after startup require restarting until watchers are added.
- ALTER TABLE outlines identify the affected table; they do not imply a new table definition. Views map to LSP Interface and tables to Class because LSP has no SQL-specific kinds. Dynamic SQL is parsed as syntax, with no fabricated semantic target.
- Malformed statements may have no declaration; safely parsed declarations in other batches are retained. Parser diagnostics use the token span at the reported offset, or a point at EOF.
- UTF-16 is the only supported position encoding. Parsing cancellation is cooperative at tokenization and processing boundaries; ScriptDOM's internal AST parser has no CancellationToken API. Results are still discarded immediately if obsolete.
- Multi-root initialization works through the same per-URI index. Runtime workspace-folder/configuration changes and full user-defined include/exclude glob configuration are not yet implemented.
- Online/hybrid mode and live SQL execution are absent. Unsupported modes fail configuration validation. The M1 schema is the supported subset of the plan's future configuration contract; it rejects unsupported settings instead of silently accepting them.
- Self-contained RID distributions, high-volume stress/performance measurements, authentication modes and actual Claude Code LSP interaction remain later milestones. Windows tests are local evidence; Linux/macOS CI is configured but has not run locally.
