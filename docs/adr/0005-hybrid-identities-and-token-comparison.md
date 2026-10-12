# ADR 0005: Hybrid identities, source selection and conservative comparison

Date: 2026-10-11. Status: accepted for M4.

## Decision

Use explicit initialized-root mappings to enabled database profile IDs. Retain workspace context IDs and database profile IDs rather than rewriting all sources into one short-name namespace. An optional default profile supplies the execution/database comparison context. Only mapped profiles participate; unmapped projects remain offline. URI/path validation reuses actual initialized roots and never interprets arbitrary paths as approval for a database profile.

Extend the existing SqlNavigationIndex with a plain Core source policy, not a second set of protocol handlers. Filter by context/database/schema/name/kind before applying source preference. Preserve ResolutionResult.Candidates and add a separate selected target. Equivalent local/deployed sources can select local/unsaved navigation; duplicate local declarations, competing profiles, schemas or kinds remain ambiguous. Apply default-schema fallback across the merged source set, so a deployed object in the primary schema is not displaced by a local object in a fallback schema. References compare equivalent verified anchors while preserving source provenance.

Each database session owns its M3 reader/cache/private files. Limit profile acquisition to two concurrent tasks and a shared 30-second refresh/materialization deadline. Catch sanitized per-profile failures independently, exclude expired catalog symbols and retain local analysis and healthy profiles. Keep last verified identifier rules for local binding; do not retain expired definitions implicitly. Only the false stale-cache policy is supported. Existing provider query/authentication and dependency pins are unchanged.

## Drift and LSP exposure

Compute hashes only for safely parsed original procedure/view/function declarations. Actual pinned ScriptDOM token types and Identifier.DecodeIdentifier are confirmed by package XML, compilation and token fixtures. Normalize comments/whitespace, keyword case, identifier quoting and acquisition/header differences. Keep body values and identifier spellings. SHA-256 agreement reports normalized text agreement, not semantic equivalence; Different does not prove behavioral change. Catalog-only descriptions, hidden source, malformed definitions and ambiguous candidates remain unavailable/ambiguous.

Expose source/provenance and comparison status/timestamps in WorkspaceSymbol.data and source-aware container labels, preserving all rows. Compiled tests verify OmniSharp WorkspaceSymbol.Data accepts JObject and serializes through the actual wire client. Send bounded comparison summaries through window/logMessage without module text or credentials. Do not add hover, rename, deployment, semantic diagnostics or custom execution methods to claim drift support.

## Verification and limits

Core fixtures cover paired identity selection, duplicate local declarations, competing profiles, schema fallback, unmapped targets, qualified database restriction, shared AST references, outage fallback and literal/formatting-sensitive hashing. Generic wire tests validate configuration failures, degraded local navigation, profile isolation and disabling comparison. Disposable SQL Server tests verify live local/unsaved/database precedence, unknown encrypted source, table metadata fallback, comparison changes/close restoration, independent profile failures and a real stopped-server outage after cache expiry. Process test parallelism is bounded to two classes while intra-client concurrency/cancellation fixtures still exercise real parallel requests.

Metadata collection and text comparison are not full SQL Server semantic binding. Cross-server mappings, runtime configuration changes, full collation/version behavior and lexical rebinding across differing USE contexts remain unclaimed. URI snapshots retain M3 privacy/lifetime constraints. Actual Claude Code behavior and distribution/performance hardening remain M5.
