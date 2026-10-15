# ADR 0013: opt-in persistent procedure reports

Date: 2026-10-15. Reuse folder/catalog reports across MCP restarts without weakening source freshness.

Persist canonical full reports as bounded private JSON, rather than serializing ScriptDOM ASTs, live server state or secrets. Core supplies protocol-independent fingerprints and source template mapping; MCP owns storage, eviction, prebuild CLI and metrics. LSP and unsaved buffers remain independent. No dependency or MCP tool is added.

Fingerprint the entire validated source set and parser/adapter/configuration/comparison identities because additions elsewhere can change ambiguity. Include catalog metadata and coverage. Neutralize only the generated acquisition timestamp while preserving layout. Normalize generated catalog paths, IDs, revisions and timestamps, then rebase to freshly acquired sources on hits. Validate expected revisions; unsupported comparison contexts bypass persistence.

Catalog TTL and unavailable-source rules remain authoritative. This is not offline database replay or a persisted reverse navigation index; startup still loads/parses sources. Checksums, private permissions, bounded reads, link rejection, atomic writes, writer leases and bounded eviction constrain storage. Cache failure must allow fresh analysis; prebuild explicitly reports incomplete coverage/retention.

Tests cover exact reconstruction, compact reuse, process restarts, live catalog rebasing, changed bodies/rules/configuration, ambiguity, stale revisions, hidden source, corruption, oversize, contention, eviction, permissions and Unix links. Measure validation/read/write separately; a hit does not establish model token savings. See [contract](../persistent-analysis-cache.md).
