# Claude Code verification

Version 0.6.2 adds actual call hierarchy. A Windows Claude Code 2.1.224 session made successful `outgoingCalls` and `incomingCalls` requests returning dbo.RunPayroll → dbo.ProcessPayroll, with initialize/prepare/outgoing/incoming/shutdown transport evidence. An initial incoming sync failed while the client server was starting; retry succeeded. [Sanitized evidence](evidence/claude-call-hierarchy-windows.json) retains that failed attempt and the reported $0.068642 cost. This does not establish Mac model-call behavior or a speed/usage advantage over grep. Native packaging/install checks remain separate. Reproduce using `scripts/test-claude.ps1 -CallHierarchy` against an extracted 0.6.2 plugin; the smoke requires actual correlated successful tool results and protocol responses.

A repeat with sequential outgoing/incoming instructions completed both actual calls with zero failed attempts and reported cost $0.057582. The earlier startup race remains evidence; the repeat does not establish universal reliability. The smoke counts textual client error results even when Claude does not mark them `is_error`.

Update, 2026-10-13: plugin **0.6.0** demonstrated an actual Claude `LSP` hover call and protocol response containing the payroll procedure kind/signature/source. The same installed 2.1.201 initially listed only Read, yet successfully used LSP later; initial tool enumeration alone must not be treated as proof of availability/unavailability. See [hover evidence](evidence/claude-hover-windows.json) and [coverage](hover.md). The M5 diagnostics and failed navigation attempts below remain historical evidence; definition/references/online/hybrid client operations are not newly claimed by this hover result.

The final patched apphost (.NET 10.0.13, PKCS 9.0.21) returned real hover in 146 ms in a child-only `ENABLE_LSP_TOOL=1` compatibility probe, cost $0.052019, with initialize/hover/shutdown confirmed. Raw ignored logs: `artifacts/claude-smoke-b7a6429d799d480085a73a1984a8e680`. A default-flags earlier session also succeeded (154 ms); another repeat registered the plugin/server but exposed no LSP tool and never launched it. This is observed variability, not an assertion that the compatibility flag guarantees availability or is an official configuration requirement. The smoke fails on absent calls/protocol responses. Other client operations remain unverified.

Tested: **Claude Code 2.1.201**, Windows x64, local plugin **0.5.0**, bundled .NET 10.0.12 apphost. Official [plugin configuration](https://code.claude.com/docs/en/plugins-reference) and [code intelligence behavior](https://code.claude.com/docs/en/plugins/code-intelligence) were checked separately from execution. Strict validation passed for the development and self-contained manifests.

## Demonstrated client behavior

A new signed-in CLI session loaded `sqlscriptdom-lsp` via `--plugin-dir`. The final diagnostics smoke used default client behavior, without ENABLE_LSP_TOOL. Its working directory was a fresh public payroll fixture outside the Git repository; other settings/hooks/MCP servers were excluded. Only Read/Edit/LSP were allowed. Claude actually edited the caller's EXEC statement to `SELECT FROM;`.

The client's debug log recorded the real transport sequence: server registration, bundled executable start, initialize request/response, initialized, didOpen for `languageId=sql`, a received publishDiagnostics notification containing **one diagnostic**, and shutdown response/exit. The source-free server trace also confirmed initialization received/completed. This proves Claude received SqlScriptDOM diagnostics. It does not prove every future client UI/model will display them, nor imply all exposed server capabilities are callable through Claude's tool surface.

The successful fixture using a ZIP extracted into a path containing spaces and Chinese characters exited 0 and cost $0.050841. It is summarized in [sanitized evidence](evidence/claude-windows.json); raw local evidence is ignored under `artifacts/claude-smoke-9c498785124a4d29b6c9845ec469a125`. Paths, session identifiers, authentication details, reasoning and raw SQL/client logs are omitted from committed evidence. No existing SQL Server was contacted and no global plugin/settings changes were made.

Reproduce with an extracted plugin:

```powershell
./scripts/test-claude.ps1 -PluginPath 'C:/Tools/SQL LSP' -Diagnostics
```

The smoke checks actual client protocol messages and the fixture edit; model prose alone cannot pass. Client debug format changes may require updating the evidence parser. The fixture is public, so its local logs may be inspected; do not reuse raw log publication practices with private SQL or connection secrets.

## Capabilities and limits

| Operation | Independent stdio tests | Claude 2.1.201 isolated session |
| --- | --- | --- |
| Initialize/shutdown/exit | Passed | Observed on actual bundled executable |
| Incremental synchronization / ScriptDOM diagnostics | Passed | Edit-triggered didOpen and one diagnostic received |
| Definition / references | Passed with verified SQL locations | Tool unavailable; no successful LSP navigation call claimed |
| Document/workspace symbols | Passed | Not exercised through Claude |
| Online/hybrid catalog navigation | Passed with an owned disposable SQL Server | Not exercised through Claude; default smoke stays offline |
| Hover, completion, rename, semantic tokens | Not advertised | Not claimed |

In navigation attempts, the session initialization tool list contained Read (or Read/Edit), with no LSP tool, even though its manager registered the SQL server. A child-only `ENABLE_LSP_TOOL=1` probe did not change that outcome. This is a concrete limitation of the tested constrained sessions, not a conclusion that all Claude 2.1.201 interactive sessions lack navigation. The flag is not a required plugin setting. No client binary was patched or globally updated. The script's navigation mode fails if real LSP calls/results are absent.

The development DLL plugin intermittently failed launch with `Command 'dotnet' not found or is in an unsafe location (current directory)`. The absolute bundled executable resolved that launch failure and demonstrated real diagnostics. Use the package when reproducing; manifest validation alone did not catch this environment-dependent launch issue.

Further client versions, interactive sessions, Linux/macOS Claude behavior and online/hybrid client exposure need their own evidence. Generic clients can exercise all implemented capabilities today; see `installation.md` and the process integration tests. M5's acceptance permits documenting a client limitation while proving generic interoperability, and the diagnostics portion is additionally demonstrated in Claude itself.
