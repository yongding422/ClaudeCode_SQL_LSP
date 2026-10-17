# ADR 0014: inspectable local metrics

Date: 2026-10-16. Developers need persistent measurements they can inspect and compare. Backend response timings alone disappear with sessions and cannot establish model-token savings.

Add opt-in MCP instrumentation through `SQLLSP_METRICS_DIR`/`--metrics-directory`. Capture fixed numeric fields for handler-entered requests, outcome, cold/warm state, existing phases, serialized report/payload bytes, report facts/issues, cache statuses and build/mode. Do not collect SQL, names, paths, arguments, focus or exception text. Default recording is disabled. The `get_metrics` tool reads current/recent observations without initializing SQL or counting itself. LSP remains independent.

Use bounded private JSONL segments with a writer lease, rotation and owned-file eviction. Preserve complete lines across restarts; report partial/invalid input and write failures. Metrics failure must not fail analysis. Memory/disk summaries describe retained windows, with missing measurements null. Successful latency percentiles exclude errors, whose counts remain visible. Issue/fact counts are not precision, recall or proof of complete coverage.

Standalone summary/comparison modes expose descriptive backend ratios by operation and cold state. A separate evaluation CSV importer reads the existing client's real usage/time fields, validates pairing/settings and retains failures/missing data/negative reductions. No token estimate is derived from report size. Reviewer scores stay external, and no correctness gate is automatically passed. Bundle the existing Python capture runner for optional controlled comparisons; no hooks or background collector are needed.

Validate through real MCP/native CLI processes: default-off and no-index reads, environment activation, cold/warm/cache/error counts, privacy canaries, cross-process retention, unavailable storage, contention/rotation/eviction, corruption/oversize/links, ratio math, quoted CSV, missing tokens, failures, settings mismatches and duplicate pair keys. Existing core/LSP/catalog suites guard source behavior. See [usage and limits](../plugin-metrics.md).
