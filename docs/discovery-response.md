# Bounded discovery contract (0.16.0)

`find_procedures` keeps `query` and `maxResults` and adds optional `section`, `documentUri` and `cursor`. The response uses `formatVersion=2`. Existing module identities, targets, selections, revisions and statuses remain available. `sourceIssues` is now empty on ordinary discovery; `sourceIssueSummary` reports exact diagnostic and affected-file counts, including issues beyond the old 1,000-item analysis cap.

Use `section="issues"` to request diagnostic details or `section="files"` for the per-file inclusion ledger. Both are paged separately from modules. `documentUri` selects one exact file. Coverage distinguishes indexed, parse errors, oversized, excluded-policy, unreadable, failed, deleted and pending files. The enumeration limitation remains visible; a negative result never proves absence outside indexed sources.

Pages have `returned`, `totalMatches`, `hasMore`, `nextCursor`, `truncated`, `truncationReason` and `omittedOversized`. Reuse the cursor with identical options in the same server process. Cursors are authenticated, deterministic in ordering, and bound to source generation; edits explicitly reject old cursors. Never retry an unchanged oversized result. An individually oversized entry is counted with explicit source-inspection recovery instead of malformed/truncated JSON or a false zero-match answer.

The complete discovery JSON-RPC response is budgeted to 16 KiB, including both structured and text copies, escaping and envelope headroom. Actual stdio integration tests assert the transport size. Set a lower ceiling through the optional version-1 plugin settings extension:

```json
{"version":1,"responses":{"discoveryMaxBytes":8192}}
```

Valid limits are 8,192–16,384 bytes. Existing settings/starters remain valid and storage stays disabled by default. A page may contain fewer entries than `maxResults` because of its byte limit. The separately measured 0.15.0 discovery-only candidate kept the previous analysis defaults. The combined 0.16.0 candidate adds [bounded compact analysis and explicit expansion](bounded-analysis.md), [folder/project scopes](offline-project-scopes.md), and source-bound `moduleRef` selectors. Details remain available without repeating unrelated workspace diagnostics.
