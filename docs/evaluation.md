# Verify SQL search and compare it with grep

Check two separate things: whether Claude actually uses the SQL LSP server, and whether its final answer improves on a text-search workflow. A correct answer produced entirely from Read/Grep is not evidence that the plugin ran. Installation success is not a navigation call. This release does not claim a measured overall advantage over grep.

## How it works; no hooks

The plugin registers `.sql` handling through `.lsp.json`. Claude starts its bundled C# server and communicates using LSP requests over stdio. On initialization, the server scans the configured SQL workspace and parses files with Microsoft's SqlScriptDOM into syntax trees. Its index stores declarations and supported static uses, retaining schema, database and workspace context. Definition/references requests return verified file locations; unsupported or ambiguous bindings return no invented target. File watchers and LSP document changes update the index. Parser diagnostics are delivered through LSP notifications after edits.

There are **no plugin hooks**, no model calls inside the server, and no interception of Grep/Bash. Claude decides when to use its LSP tool. Grep remains useful for discovering names and searching text; LSP resolves supported object uses. The default offline mode reads workspace SQL without connecting to a database. Online/hybrid metadata access is explicitly opt-in.

The 0.7.0 implementation adds read-only MCP discovery/analysis and an explanation skill alongside LSP. See [procedure analysis](procedure-analysis.md) for operation footprints, visible coverage and the paired client evaluation runner. The reference fixture below remains a navigation check, not a benchmark of the new explanation workflow.

```mermaid
flowchart LR
    A[Claude's LSP tool] -->|definition / references / hover| B[C# SQL server]
    B --> C[SqlScriptDOM parsing and symbol index]
    C --> D[Verified file paths and ranges]
    D --> A
```

## Five-minute fixture check

After marketplace installation, use a copy of this repository and start Claude in the small public fixture:

```sh
git clone https://github.com/yongding422/ClaudeCode_SQL_LSP.git
cd ClaudeCode_SQL_LSP/samples/search-evaluation
claude
```

This is a test workspace, not an additional plugin installation. No SDK, ZIP extraction, SQL execution or database is needed.

Ask:

> Read calls.sql. Use LSP goToDefinition on line 1, column 10, then LSP findReferences on the returned declaration. Report actual tool results, showing only call locations in calls.sql and excluding the declaration. Do not use Grep or infer references from text. If LSP is unavailable, say so.

Expand the tool calls in the conversation. Require actual successful LSP calls/results. If only Read/Grep ran, record **LSP not exercised**. For deeper inspection, launch `claude --debug-file ./claude-evaluation.log` and check for real initialize and `textDocument/definition` / `textDocument/references` traffic. Model statements such as "I used the plugin" are insufficient. Do not mistake an installed plugin or server registration for a successful request.

Gold answer: definition at `definitions.sql:1`; exactly two supported static calls at `calls.sql:1` and `calls.sql:2`.

For a separate parser check in this disposable fixture, ask Claude to create `scratch.sql` containing `SELECT FROM;`, report the actual automatic SQL diagnostics, then change it to `SELECT 1;` and confirm those diagnostics clear. Expand the diagnostic/tool output in the conversation. This checks parsing and document updates; it does not substitute for a successful definition/reference call. No SQL should be executed.

| calls.sql line | Meaning for dbo.SearchProbe |
| --- | --- |
| 1 | Verified call |
| 2 | Same object with bracketed identifiers; verified call |
| 3 | Different schema; audit.SearchProbe |
| 4 | Comment |
| 5 | String data |
| 6 | Dynamic SQL; unsupported target binding, not a verified static reference |
| 7 | Unqualified name; ambiguous between dbo and audit without a configured default schema |
| 8 | Different object: dbo.SearchProbeArchive |

The fixture's answer is checked by `Public_search_evaluation_returns_verified_calls_excludes_text_noise_and_keeps_ambiguity`, a real process integration test. Its success proves the server result, separately from your Claude session's tool availability. LSP wire positions are zero-based; user-facing sample lines above are one-based.

## Compare with raw text search

From the fixture directory, run:

```sh
rg -n '\bSearchProbe\b' calls.sql
rg -n 'dbo\.SearchProbe\b' calls.sql
```

The first query returns seven candidate lines (1–7), of which two are verified static calls to dbo.SearchProbe. The second returns four candidate lines (1, 4, 5, 6) and misses the bracketed call on line 2. LSP's expected reference answer contains lines 1 and 2 only.

This demonstrates a distinction between text candidates and resolved SQL references. It is not a fair claim that Claude with grep cannot get the same answer: Claude can read/filter candidates and reason about SQL, and stronger regexes can handle more spellings. Dynamic SQL remains valuable text-search evidence even though this plugin cannot bind it. Keep unknown targets explicitly unknown rather than claiming comprehensive impact analysis.

## Measure value on your own repository

Choose at least 20 reviewed tasks spanning exact definitions, reference finding, schema collisions, quoted identifiers, aliases, comments/strings, dynamic SQL and duplicate migration definitions. Write the gold locations and unsupported/ambiguous cases before testing. Keep workspace roots and name-comparison rules fixed.

For each task, run two fresh Claude sessions with the same model, effort and task wording. In one, require LSP navigation; in the other, require Read/Grep and prohibit LSP. Judge final answers, not raw candidate counts. Record the actual tool calls, a tool-unavailable failure, time until the final verified answer, number of tool calls, and available usage/cost. Alternate the order across repeats; avoid giving either session the other method's answer. Report first-session startup separately from warmed searches.

| Metric | Calculation / interpretation |
| --- | --- |
| Precision | Correct returned locations / all returned locations |
| Recall | Correct returned locations / gold locations |
| Definition success | Tasks with the exact correct definition / eligible definition tasks |
| Ambiguity handling | Unknown/ambiguous tasks correctly left unresolved / reviewed unknown tasks |
| LSP availability | Sessions with real successful LSP calls / sessions attempting LSP |
| Task latency | Median and p95 time to the verified answer, with cold and warm runs separated |
| Usage and effort | Tokens/cost where available, tool-call count, and files manually inspected |

Score dynamic/ambiguous cases separately from resolvable reference tasks. Treat omitted verified results as misses and tool unavailability as a failure; do not silently remove failed sessions. Use the same definition-inclusion policy and location granularity in both methods. Aliases can produce extra legitimate reference ranges on a line, so deduplicate consistently if scoring by line rather than exact range. Do not interpret a zero-denominator precision/recall case as 100%.

Existing [performance measurements](performance.md) report server startup and warm requests on synthetic Windows workloads, not a head-to-head grep or Claude task benchmark. A packaged 10,001-file run took 58.41 seconds to initialize, and another exceeded its deadline. Grep may be faster for one-off text searches; measure whether the plugin improves verified accuracy and reduces time/effort after indexing. More complete column binding and dynamic SQL analysis remain outside current coverage.

For saved-procedure investigation, see the [Grep/Read, codebase-memory and SQL MCP/LSP comparison](evaluation/sql-tool-comparison.md). It verifies the competitor's parser-based index, retains the unfavorable existing paired result, and specifies separate SQL-engine and LSP experiments. [Persistent report hits](persistent-analysis-cache.md) measure backend reuse, not automatic model-token savings.

Version 0.12.0 adds [local plugin metrics](plugin-metrics.md) with get_metrics, persistent backend observations, native summary/compare commands and a client CSV importer. Model usage still comes from actual client events; quality scores require external review. The plugin does not intercept Grep or install hooks.
