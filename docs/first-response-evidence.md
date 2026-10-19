# Scoped selection and first-response evidence

Local candidate after 0.16.0; not yet a published marketplace update. This slice refines R2/R4 delivery. It does not implement a full control-flow engine, infer runtime values, change saved-cache freshness rules or demonstrate improved model-answer acceptance.

`analyze_procedure` now adds a `brief` object alongside the existing `report`. The compact default and explicit full DTO remain available. Oversized reports still return `reportRef` and bounded pages; the first response also carries selected citeable facts instead of only module counts. Expansion responses set `brief` to null so it is not retransmitted on every page.

Optional `intent` is `overview` (default), `transactions` or `dependencies`. It changes presentation priority only. Analysis depth/module budgets, resolution, cache identity and the retained report are unchanged by intent. There is no semantic question interpreter and no additional tool.

```json
{
  "documentUri": "file:///absolute/project/Sequences/ReseedAllSequences.sql",
  "declaration": "[Sequences].[ReseedAllSequences]",
  "scopeUri": "file:///absolute/project/",
  "intent": "dependencies"
}
```

Use an actual indexed file URI and an explicit physical project folder inside the workspace. Folder scoping excludes outside dependencies, which remain unresolved rather than being silently resolved from another application. Existing configured source contexts remain authoritative. A file URI alone does not disambiguate duplicate database identities. Discovery results now explain when scoped discovery is required; copy the moduleRef from that scoped result to preserve its folder/revision/generation. Ambiguous analysis returns a recovery instruction rather than selecting a guessed project.

The follow-up implementation offers an actionable `scopeChoice` on ambiguous SQL-only discovery candidates: its directory and a scoped `moduleRef`. Choose that handle when the shown physical folder matches the requested source; no second discovery call or URI reconstruction is needed. This is an explicit directory filter, not inferred project membership. Unscoped handles preserve ambiguity. No choice is offered for same-file duplicates, declared project duplicates, or already explicitly scoped requests. Source changes invalidate these handles. See [follow-up progress](plans/answer-quality-followups.md).

Declaration selectors accept one identifier or schema/name, including brackets, escaped brackets, quoted dots and double-quoted identifiers. Comparison follows the source's configured identifier rules. They reject comments, statements, empty name parts and database/server qualifiers. This is identifier normalization over the pinned ScriptDOM lexer, not SQL execution. An unqualified selector matching multiple declarations in the selected file still fails rather than choosing one. Genuine duplicate identities inside a scope remain ambiguous.

## Brief contract

`brief.format` is `procedure-brief-v1`. Each selected fact includes:

- Kind, name and the existing analyzer's explanatory detail.
- Module index, plus a module table with identity, revision, source kind, capture time and body availability.
- One-based UTF-16 coordinates with exclusive ends, a ready-to-use file:line citation and source URI/path.
- Its section path in the retained report, and applicable operation/status/local-object, flow-context or named THROW fields.

Expression previews retain their truncation flags. A brief does not evaluate them. Flow-context IDs refer to the retained control-flow report; absence of an inline predicate is not proof that an effect is unconditional.

Existing immediate RETURN-after-THROW issues and sequence/SELECT/atomic observations receive priority. Transaction intent promotes error and transaction nodes; dependency intent promotes calls and relation accesses. Repeated observation kinds are bounded so they cannot consume the entire brief. No new runtime/path conclusions are manufactured from these observations.

At most eight facts are initially selected. The server measures the complete structured/text JSON-RPC result against the configured analysis byte budget, including envelope headroom. If needed it first reduces module summaries, then selected brief facts. Every retained report value stays available through pages. This may increase pagination for some previously borderline compact responses; only client measurements can establish a net improvement.

`totalFacts`/`omittedFacts` count the eligible observation, RETURN issue, access, call and transaction/error-node candidates, not all SQL behavior or every retained value fact. Other control/value/parameter facts remain in `report`. `truncated`, issue/expansion counts and the source coverage envelope remain visible. Even zero eligible omissions never proves complete behavior. Unknown triggers, external implementations, actual row counts, predicate truth and ambient transaction outcomes remain unknown.

The findings are deliberately source-bound and read-only. Persistent index/report storage and metrics remain separately configured opt-ins. No hook, database connection or LSP startup is introduced.

## Verification

Real stdio integration tests cover quoted/escaped selectors, case-sensitive matching, malformed inputs, copied SQL projects with and without persisted indexing, scoped handle reuse, genuine ambiguity, 16/32 KiB complete-wire limits, critical citations on the initial paged response, named THROW state and unchanged retained full reports across intents.

The SQL Server Samples probe compares local framework-dependent code with the retained native 0.16.0 artifact on the same frozen corpus. That verifies protocol/fact delivery and selection recovery only. It is not a paid model benchmark, a cold/warm causal latency comparison or evidence of token/acceptance gains. Four-platform packaging, deployment and a fresh reviewed model comparison are separate release/evaluation steps.

[Retained corpus results](evidence/first-response-probe-20261018.json) and [validation/provenance](evidence/first-response-validation-20261018.json) record four completed functional comparisons. Quoted scoped reseeding selection resolves; unscoped duplicates remain ambiguous. Initial order analysis now includes RETURN/sequence observations and initial fulfillment analysis includes SELECT-retention evidence. All responses fit 32 KiB. The order response grows from approximately 8.8 KiB of overview metadata to 25.5 KiB with facts, so lower model usage must not be inferred from this change alone.

One earlier probe failed its scoped-status assertion before response logging was added. Its exact response and binary fingerprint were not retained, and the cause was not established. The subsequent retained run and three fixed repeat checks passed without an intentional product source change. This does not prove an absence of intermittent source-state behavior; the failure remains disclosed rather than relabeled as a successful check.

Final verification passed 346 C# tests with 18 environment/opt-in skips, including 17 new stdio cases, and 52 Python tests with one Windows symlink skip. Locked dependency restore, compilation, formatting, Claude manifest validation and skill YAML checks passed. The generic Codex skill checker does not accept the existing Claude-only `argument-hint` frontmatter field; that field is preserved. No product dependency or marketplace installation was changed.
