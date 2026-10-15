# Compact reports and source citations

Version 0.10.0 adds an optional compact view to `analyze_procedure`. The bundled explanation skill requests it automatically. No new tool, hook, package dependency, database access or SQL execution is introduced. The default remains `detail="full"` for existing clients.

Use the URI, zero-based selection and revision from `find_procedures`:

```json
{
  "documentUri": "file:///workspace/modules.sql",
  "line": 0,
  "character": 21,
  "expectedRevision": "<returned revision>",
  "detail": "compact",
  "focus": ["@State"]
}
```

`focus` is optional: at most eight variable names/expression snippets of 1–64 characters. Matching is a case-insensitive literal substring over reported variable names, expression previews and input names. It is a presentation filter, not SQL identifier resolution or natural-language semantic search. It does not inspect suppressed dynamic SQL payloads. A match to a truncated preview can miss text beyond that preview; use Read or remove focus when needed.

The filter retains connected variable/definition details and verified caller-input/formal links across expanded modules at the exact reported revision. It preserves every module, call, argument binding, table effect, control node, coverage issue and limitation. Unsupported facts and facts with uncertain input sources also remain. Required boundary/source facts and formal anchors are retained even if focus did not select them. `focusMatched=false` and `omittedFacts`/`omittedVariables` distinguish no lexical match or filtered detail from absent behavior. Focus does not narrow the Core parser's work or expansion budget and can be broad when many facts share variables. Use an unfocused compact report or full report for complete retained detail.

## Shared evidence

`report.format` is `compact-v1`. Each module retains its exact identity, source kind, revision, metadata timestamp, body availability and truncation. Module-local variable/fact/control IDs remain bookkeeping. Short module IDs such as `m0` are scoped to this report and cannot be cached independently of its revisions.

| Field | How to use it |
| --- | --- |
| `sources` | Source ID to exact file URI and display path. Workspace paths are relative; external catalog snapshots retain an absolute path. |
| `evidence` | Evidence ID to source ID, one-based UTF-16 start/end coordinates, and ready-to-cite `text`, e.g. `modules.sql:12`. Ends remain exclusive. |
| `at`, `definition`, `parameter` | References to the evidence table, rather than repeated URI/range objects. |
| `notes` | Deduplicated explanation strings. `note` and `limitations` reference these strings; read them as part of interpreting a fact. |
| `target.module`, `targetModule` | An already expanded module in this report. Reuse its identity/revision/body facts before repeating discovery or analysis. Unexpanded targets retain an identity and source anchor; value calls also retain the target revision and formal anchors. |
| `context` | Module-local enclosing control IDs (`flowContext` in full reports). Local relation anchors also use one-based evidence references. |

Use an access/call/predicate's evidence for that claim. A definition or enclosing BEGIN location is not an interchangeable citation. Compact evidence already adds one to protocol lines: do not add one again. Discovery inputs, full DTO locations and LSP coordinates remain zero-based. This reduces arithmetic errors but does not guarantee that a model chooses the right evidence or describes its meaning correctly.

The projection preserves possible-definition and OUTPUT/default boundaries. Sharing a callee report never substitutes a caller's concrete values into its symbolic inputs. Focus connectivity is useful for locating evidence, not proving an executed path, runtime value or row set. All existing [value-tracing](value-tracing.md) and [control-flow](control-flow.md) limitations apply.

## Per-request timings and evaluation

Both MCP tools include numeric `timings` in the requested response. They are local measurements, with no background collector or metric file. They contain no SQL, paths, object names or credentials.

| Field | Measured interval |
| --- | --- |
| `coldStart` | Whether this request initialized the process's shared source state. This is not an OS-cache or model-cache claim. |
| `queueMs` | Waiting for the shared analysis gate. |
| `initializationMs` | Source configuration/acquisition and initial parsing during initialization; near-zero initialization check on later requests. |
| `navigationMs` | Obtaining the current saved/catalog navigation snapshot, including any refresh work that call performs. |
| `analysisMs` | Module discovery/report analysis for the requested tool. |
| `presentationMs` | Constructing the compact projection, or selecting the full DTO. |

These intervals exclude process/client startup, final response serialization, transport, model inference and most client work. They must not be presented as full task latency. A watcher can perform parsing outside these intervals; they are request intervals, not total process CPU accounting. Aggregated intervals can overlap across concurrent requests, including queue waits; do not subtract their sum from whole-task time to estimate model time. Failed requests have no successful timing payload. Multiple processes each have their own cold state; saved/catalog and LSP unsaved snapshots remain independent.

The SDK's installed 2.2.0 documentation confirms that type-only `WithTools<T>()` constructs a target per invocation. Version 0.10.0 registers one explicitly owned instance with `WithTools(instance)`, so discovery and analysis reuse the source state and watchers. The host disposes this instance at shutdown. A cold/warm protocol regression and saved-edit revision tests verify reuse and freshness together; there is no permanent analysis-result cache.

The paired runner now aggregates correlated server timings and canonical UTF-8 report bytes separately from elapsed task time, model tokens, cache usage and cost. Old clients, missing fields and failed calls stay unknown. It never infers accuracy from these numbers or retains report contents in aggregate metrics. Duplicate stream events are counted once by tool-use ID. Continue to score behavior, source citations and unknown handling independently using the [evaluation design](stored-procedure-understanding.md#measuring-value-against-grep).
