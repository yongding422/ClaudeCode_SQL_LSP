# Direct, bounded analysis contract (version 2)

For a known file/module, no discovery call is required:

```json
{"documentUri":"file:///path/to/modules.sql","declaration":"dbo.ProcessPayroll"}
```

Otherwise discover once and pass only its short `moduleRef` to `analyze_procedure`. It binds the exact file, UTF-16 selection, complete revision and source generation. Changed generations reject it explicitly. Original URI/zero-based line/character arguments remain valid; copy an entire `expectedRevision` value, including suffixes, when using that path. An exact file/declaration selector never picks an arbitrary duplicate. Literal/script questions can use Read/Grep directly.

The version-2 envelope defaults to `detail="compact"`, depth 1 and eight modules. This changes the previous omitted-detail default; clients requiring original DTO fields must pass `detail="full"` and may retain their explicit depth/module limits. Full and compact share the same verified canonical facts. Existing core/LSP DTOs are unchanged. Input coordinates remain zero-based UTF-16; compact citation text remains one-based.

Analysis responses have a default complete-wire ceiling of 32 KiB, counting structured/text duplication and envelope headroom. Configure `responses.analysisMaxBytes` in `.sqlscriptdom.json` (16,384–65,536 bytes). Large results return `format="paged-report-v1"`, module summaries/counts and `reportRef`. Omissions are explicit and never mean empty effects. A summary is not the original report with facts silently deleted.

Request a relevant returned JSON Pointer section:

```json
{"reportRef":"<returned reference>","section":"/modules/0/calls","offset":0,"maxItems":20}
```

Pages contain `path`/complete `value` pairs and `nextOffset`. An individually large object has `expand` instead, naming its child section. Long strings use `report-text-page-v1` text chunks with UTF-16 offsets; join in offset order. Original IDs, spans and reference keys remain intact. Compact `/evidence`, `/sources` and `/notes` provide citations/interpretations. Expand only needed facts; do not reconstruct every page for a narrow developer question. Clients needing full DTOs can reconstruct arrays/objects recursively through these same pages; integration tests verify the retained control map and exact locations.

A page needs no selection arguments. If a client repeats `moduleRef` alongside `reportRef`, the server accepts it only when it identifies that report's exact root module and scope; generation checks still reject stale references. A different module (including another declaration in the same file), a conflicting file or a different scope is rejected. This compatibility repair follows 14 mixed-selector errors observed in the retained first combined-candidate experiment; it does not silently switch a report to a new source.

References are process-local, expire after ten minutes, and are rejected after source-generation change. Retention is eight reports/eight MiB; eviction is explicit. Reports exceeding retention request narrower expansion/focus rather than returning malformed/oversized JSON. A stale reference permits one fresh selection; do not repeatedly issue an identical oversized request. `expand_report` metrics are separate from canonical `analyze_procedure` requests; numeric fact counts use the canonical analyzer result while report/frame byte counts describe the bounded presentation.

The updated explanation skill routes short text/script questions to file tools, known modules directly to scoped analysis, and unknown locations through one discovery page. MCP descriptions carry the same routing guidance because the baseline experiment selected no skills. LSP remains optional for navigation/unsaved buffers; no hook or companion service is needed for MCP.
