# Hover (M6 first increment, plugin 0.6.0)

The completed hover increment remains supported. Remaining M6 work is deferred as of 2026-10-13 while the roadmap prioritizes SQL code search and navigation; see [milestone priorities](milestones.md).

`textDocument/hover` returns kind, signature, source/provenance, definition file/context, available buffer version or catalog capture time, and permitted documentation. Reference hover uses the same uniquely selected target as definition navigation. Declaration hover inspects the declaration anchor itself. Ambiguous, missing, remote, dynamic, unsupported and malformed bindings return null; comments, literals and unbound column tokens receive no invented hover.

Workspace and unsaved signatures come from safely parsed ScriptDOM declaration nodes. Procedures/functions show parameter names/types and declared OUTPUT/READONLY/VARYING modifiers; functions show scalar or table return syntax. Parameter defaults and module/initializer bodies are omitted. Tables preview declared columns and explicit NULL/NOT NULL clauses; omitted nullability, computed-column types, primary-key implications, migration effects and view projection types are not inferred. Parameters and scalar/table variables show verified lexical types where available; CTE/derived alias/temp bindings remain conservative descriptions, not inferred row shapes.

Selected database targets use authoritative catalog names, including objects renamed after their stored headers were created. Visible parameter/column types, lengths, precision/scale, output/nullability flags, return metadata, coverage and capture timestamps are labeled as catalog observations. Unicode character lengths convert catalog byte lengths to character counts; alias/CLR types do not acquire fabricated builtin type arguments. READONLY/default/body properties missing from the catalog projection are not inferred. Hidden/encrypted/oversized module bodies retain metadata-only descriptions, not reconstructed DDL. Catalog parameter limits explicitly prevent an apparently complete parameterless signature.

Hybrid reference hover follows configured workspace/database preference and the current unsaved overlay. It includes an available comparison status and catalog capture time, with the existing warning that token comparison does not prove behavioral equivalence. Database-selected hover uses only the requesting workspace's comparison; a direct database snapshot has no arbitrary local counterpart comparison. Duplicate declarations remain ambiguous even with database preference. After failed refresh/expiry, local hover remains available and expired catalog hover disappears.

The response range is the reference/identifier range in the requesting document, using zero-based UTF-16 positions. Invalid/split-surrogate cursor positions return null. Incremental edits, close restoration and request cancellation follow the existing snapshot/index path. Metadata refresh can incur the documented online/hybrid timeout; hover is not an independent database execution API. Invalid initialization disables hover with the other analysis capabilities.

The first supported client content preference selects plaintext or Markdown. Absent/empty preferences default to plaintext. Markdown is a plain-text code block with a fence longer than any embedded backtick run, so object names cannot introduce HTML, links or images. Control/format characters are neutralized only in display text, never in identities. Hover previews cap members at 16 and plain content at 16,384 UTF-16 characters; content shortening does not alter underlying resolution/reference coverage. Requested analysis output can still contain source names/types/paths: it is not a claim of secret-free output. Operational logs do not dump hover content.

For a generic client, send `textDocument/hover` at line 2, character 15 in `payroll-caller.sql` after initialization with the sample workspace root. It returns the selected `ProcessPayroll` signature and source. `hoverProvider` is advertised only with the compiled handler; completion, semantic tokens, rename and MCP remain unadvertised/unimplemented M6 work.

Real Claude hover can be probed with public fixtures:

```powershell
./scripts/test-claude.ps1 -PluginPath 'C:/Tools/SQL LSP' -Hover
```

This consumes bounded Claude usage and fails unless a real LSP hover call/result contains the expected signature/source. Loading a plugin, a model description or successful diagnostic delivery is not proof of Claude hover support. M5's verified diagnostics remain separately documented; current hover client limitations and the full test evidence are in `milestones.md`. Linux/macOS native execution remains a CI gate.

Claude 2.1.201 on Windows returned real hover for 0.6.0 in documented sessions, including the final patched apphost. A repeat session exposed no LSP tool despite registration. See `claude-verification.md`: the initial tool list is not reliable availability evidence, and neither default flags nor the child-only compatibility probe are a universal availability guarantee. Packaged 10,000-file indexing also showed substantial startup variability; see `performance.md` before assuming a 60-second deadline is sufficient.
