# ADR 0001: .NET, parser, protocol framework and client contract

Date: 2026-10-11. Status: accepted, subject to executable milestone gates.

## Evidence and decisions

- Use the installed .NET SDK **10.0.401**, pinned without roll-forward in `global.json`, targeting `net10.0`. Microsoft's [support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) identifies .NET 10 as active LTS through 2028-11-14. `dotnet --version` succeeds. This host's `dotnet --info` throws in workload reporting; restore/build/test are the authoritative development checks.
- NuGet's [ScriptDOM version feed](https://api.nuget.org/v3-flatcontainer/microsoft.sqlserver.transactsql.scriptdom/index.json) lists **180.117.0** as the latest stable version observed. Use that exact package, with its compatible net8.0 asset. The [Microsoft source](https://github.com/microsoft/SqlScriptDOM) and installed package XML confirm `TSqlParser.CreateParser(SqlVersion, bool)`, `Parse(TextReader, out IList<ParseError>)`, UTF-16 `Offset`, tokens, `TSqlFragmentVisitor`, and procedure/view/function/table AST types. `ApiSpikeTests` compiles and exercises these APIs. Parser dialect is configurable; the initial default is SQL 170, not a universal SQL 160 assumption.
- Evaluate and select **OmniSharp.Extensions.LanguageServer 1.0.0**, the latest stable in its [version feed](https://api.nuget.org/v3-flatcontainer/omnisharp.extensions.languageserver/index.json). Its nuspec supplies a native net10.0 asset and pins matching JsonRpc, LanguageProtocol and Shared packages to 1.0.0 (repository commit `63d80771aed3e4957dfbabb3103935fe33d67bba`). The [official implementation guide](https://github.com/OmniSharp/csharp-language-server-protocol/blob/master/docs/lsp.md) documents stdio streams, registration, lifecycle and cancellation. Select it only after the independent process-level handshake test passes; avoid maintaining a custom transport.
- Target the stable [LSP 3.17 contract](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/), UTF-8 JSON bodies with byte-counted Content-Length framing and UTF-16 positions. Explicitly report UTF-16 when supported by the framework; it remains the required default. Do not advertise definition, references, completion or rename in M1.
- Tests use **xUnit 2.9.3**, **xunit.runner.visualstudio 4.0.1** and **Microsoft.NET.Test.Sdk 18.10.1**, verified against official NuGet version feeds. Central package management pins direct versions; committed lock files pin the resolved graph. No SqlClient dependency is needed until M3.

## Claude Code verification

The current [official plugin reference](https://code.claude.com/docs/en/plugins-reference), read on the date above, documents `.claude-plugin/plugin.json` for metadata and `.lsp.json` at the plugin root. LSP entries require `command` and `extensionToLanguage`; `args`, `transport`, `initializationOptions` and timeouts are supported. The current reference states that all language servers are run over stdio. `claude plugin validate <directory>` checks discovery/schema; `--plugin-dir <directory>` loads a development plugin. Launch must resolve a real binary; `dotnet run` is unsuitable because build messages can corrupt stdout.

Installed `claude --version`: **2.1.201**. Current documentation contains later-version fields, so use only the basic candidate manifest keys and record actual validator results. Real Claude diagnostics/definition interaction and distribution remain **M5**, separate from generic LSP interoperability; schema validation must never be reported as end-to-end Claude success.

## M0 gate

Build the actual parser/visitor/error-location spike and a separate stdio server process. The test client splits request frames, validates all stdout as JSON-RPC frames, and completes initialize/shutdown/exit. Run `dotnet build` and all tests before M1. Record results in `docs/milestones.md`.
