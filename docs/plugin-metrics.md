# Check and compare plugin metrics

Version 0.12.0 adds **opt-in local MCP metrics**, a `get_metrics` tool, backend summaries/comparisons and an importer for the existing client evaluation CSV. No hooks, remote collector or extra service are required. Recording is off by default.

## Enable recording, then ask Claude

On macOS/Linux, start a new Claude session after setting an absolute physical directory:

```sh
export SQLLSP_METRICS_DIR="$HOME/.cache/sqlscriptdom-metrics/my-project"
cd /path/to/sql-project
claude
```

On Windows PowerShell:

```powershell
$env:SQLLSP_METRICS_DIR = Join-Path $env:LOCALAPPDATA 'SqlScriptDOM/metrics/my-project'
Set-Location C:/path/to/sql-project
claude
```

Use a separate directory for each project/experiment condition. Ask procedure questions normally, then ask:

> Use the SQL plugin's get_metrics tool with includePreviousSessions=true. Show cold/warm request counts, median/p95 time, errors, cache hits, report sizes and reported issues. Explain missing values and collection limits. Do not infer token savings or answer accuracy from backend metrics.

`get_metrics()` defaults to the latest 1,024 completed requests in the current MCP process. `get_metrics(includePreviousSessions=true)` reads retained files across processes. It does not initialize SQL, query a database, or count itself. `observedInProcess` and `persistenceWriteFailures` expose collection/recording state. If disabled, it returns `enabled=false` and no invented measurements. Restart Claude after changing the directory or disabling the environment variable. Collection cannot reconstruct earlier sessions.

Start the CLI from the shell where you set the variable. If your environment deliberately enables `CLAUDE_CODE_MCP_ALLOWLIST_ENV=1`, shell variables are not inherited by stdio MCP servers; provide the metrics variable through the server's explicit environment configuration or use `--metrics-directory` in an explicit server launch instead. Keep the configured environment policy. [Official Claude environment reference](https://code.claude.com/docs/en/env-vars)

## What is measured

| Measurement | Meaning |
| --- | --- |
| Successful/error/canceled requests | Calls that entered discovery, analysis or CLI prebuild handlers; SDK rejection before handler entry is outside this scope |
| Cold/warm p50, p95 and mean milliseconds | Successful handler durations, including validation, queueing, source initialization, analysis and response construction; excludes metrics recording, protocol transport and model time |
| Phase totals and measured-request counts | Queue, initialization, navigation, Core analysis, presentation and cache validation/read/write; failed requests may lack phase data |
| Serialized report/payload UTF-8 bytes | Actual serialized JSON size of the report or structured payload, including escaping; excludes the JSON-RPC envelope and duplicate text representation; **not tokens** |
| Cache-enabled reports, statuses and hit rate | Hits divided by cache-enabled report attempts, including CLI prebuild entries; discovery does not count as a cache attempt |
| Indexed documents, reported modules/calls/accesses/issues | Observed report counts, with resolved status and truncated modules; repeated requests repeat facts, and issue occurrences may repeat across sections |
| Version/build, source mode, session and timestamps | Helps identify mixed builds/modes and the observed window; it does not prove the same questions or source revision were used |

Cold means the request initialized that MCP process's SQL source state. It says nothing about OS, disk-report or model prompt caches. Handler errors before source initialization have unknown cold/warm status. Percentiles use nearest-rank successful observations. Error/cancellation counts remain visible; faster failures do not improve successful latency statistics.

Metrics cover MCP, not the separate LSP process. They do not observe Grep, model input/output tokens, end-to-end final-answer time, correctness or complete SQL coverage. Summary `modelTokens` and `finalAnswerCorrectness` remain null. Enable [report caching](persistent-analysis-cache.md) separately with `SQLLSP_ANALYSIS_CACHE_DIR` if wanted; a hit skips Core report work but does not eliminate source validation or model input.

## Compare backend runs

Record the same questions against the same saved SQL in fresh sessions, first using a `baseline` metrics directory, then a `current` directory. Keep parser/resolution settings and source visibility fixed. Change only the condition being tested, such as full versus compact reports or report caching. Repeat and alternate order; keep cold/warm groups separate. The server does not retain question text or source hashes to verify experimental parity.

The packaged MCP executable has standalone commands. Set `SQL_PLUGIN_ROOT` below to your installed/extracted plugin directory; on Windows use its `.exe` with PowerShell's call operator. No .NET installation is required for native packages:

```sh
"$SQL_PLUGIN_ROOT/mcp/sqlscriptdom-mcp" --metrics-summary \
  --metrics-directory "$HOME/.cache/sqlscriptdom-metrics/current" \
  --compare-metrics "$HOME/.cache/sqlscriptdom-metrics/baseline" \
  > backend-comparison.json
```

Omit `--compare-metrics` for one directory. Source developers can use `dotnet src/SqlLsp.Mcp/bin/Release/net10.0/sqlscriptdom-mcp.dll` with the same arguments. Neither summary command scans SQL or changes metric files. Output is JSON containing both summaries and matching operation/cold-state groups. Mean/median time and mean report-byte reductions use `1 - current / baseline`: 0.20 means 20% lower; negative values mean regression. Missing or zero baselines stay null. Different details, builds or modes remain visible; these descriptive ratios do not establish a controlled comparison.

Exit 0 means readable data or an empty directory. Exit 2 indicates partial/unavailable data or an explicit read-window limit. Invalid options or inaccessible evaluation input return exit 1. An empty directory means no retained observations, not proof no plugin work occurred.

## Compare with Grep/Read and capture actual tokens

Use the bundled evaluation runner for controlled client captures. It needs Python 3 and an authenticated Claude CLI, but no additional Python packages. It runs actual model sessions under a per-run budget; it does not measure ordinary past conversations. Keep outputs outside the SQL workspace:

```sh
python3 "$SQL_PLUGIN_ROOT/evaluation/evaluate-understanding.py" \
  --workspace /absolute/path/to/sql-project \
  --tasks /absolute/path/to/reviewed-questions.json \
  --plugin "$SQL_PLUGIN_ROOT" --model <exact-model-id> --repeats 3 \
  --output /absolute/path/to/evaluation-output

"$SQL_PLUGIN_ROOT/mcp/sqlscriptdom-mcp" \
  --evaluation-summary /absolute/path/to/evaluation-output/metrics.csv \
  > client-comparison.json
```

Questions use the [existing task format and runner options](procedure-analysis.md#measure-against-grepread). The runner isolates fresh SQL-only workspaces and clears inherited report-cache/metric directories. Its CSV contains client-reported token/cache-token categories, cost, actual tool outcomes and final-answer wall time. Token/cache categories stay separate; missing values stay null. Reported cost is not a promise about subscription billing.

The importer groups all known arms, retains failure counts, and pairs successful runs by task/repeat against Grep only when recorded model, observed model, effort, repository revision, CLI version, start condition and SQL-file count agree. Duplicate keys and mismatched/incomplete runs do not enter ratios. Raw arm totals still include failures. It accepts quoted CSV, ignores unrelated columns, emits no task names/answers, and bounds input to 4 MiB/10,000 rows. Invalid rows or duplicate keys return exit 2 with a summary. This command reads numeric evidence; it does not launch models or infer tokens from bytes.

Reviewer score columns remain external inputs. `qualityGate` explicitly says it was not evaluated automatically. A fast, cheap wrong answer is not a win. Use the [four-arm research design](evaluation/sql-tool-comparison.md) for representative procedures, independent grading and confidence intervals. The existing runner captures plugin versus Grep/Read; codebase-memory and LSP-ablation capture remain future work, even though the importer can summarize those labeled arms if supplied.

## Storage and privacy

Records are newline-delimited JSON:

```text
<metrics-directory>/metrics-v1/session-<random-process-id>-<segment-id>.jsonl
```

Only fixed measurement fields are written: no SQL, procedure names, source paths, query/focus text, credentials or exception messages. The tool's `directory` field identifies the configured location but is not written into event records. Data is local and not encrypted; project sizes and timestamps can still be sensitive.

The version directory uses current-user Windows ACLs or Unix directory mode 700/file mode 600. Linked/reparse paths are rejected. Segments rotate at 512 KiB, with at most 32 owned segments/16 MiB retained. An exclusive writer lease prevents concurrent rotation/eviction races; contention can drop a disk observation and increments the process's write-failure counter. Analysis continues. Closed append records survive restart; a crash can leave an incomplete last line, which the reader ignores and reports. Records are not signed and user edits can change them.

Summaries describe retained observations, not lifetime totals. Current memory keeps 1,024 requests; disk reads are capped at 16,384 valid events and 32 segments. `windowLimited` flags explicit memory/read bounds; ordinary earlier retention eviction is not reconstructible. Invalid/truncated records increment `ignoredRecords`. After restart, the prior process's dropped writes cannot be recovered. Stop using processes before deleting the configured metrics directory. No unrelated files are pruned.
