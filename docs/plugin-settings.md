# Configure local metrics and saved reports

Starting with 0.14.0, the plugin automatically creates **`.sqlscriptdom.json` in the workspace where its MCP server first starts**. Install/update the plugin, then start a fresh Claude session from your SQL project folder. No hook or manual MCP registration is needed.

The generated starter has `metrics.enabled=false` and `analysisCache.enabled=false`. The optional [persisted discovery index](persistent-discovery-index.md), `indexCache`, is also disabled when absent; enabling report storage alone still does not restore discovery facts. To enable metrics and saved reports, edit the file to:

```json
{
  "version": 1,
  "metrics": {
    "enabled": true,
    "directory": ".sqlscriptdom/metrics"
  },
  "analysisCache": {
    "enabled": true,
    "directory": ".sqlscriptdom/cache"
  }
}
```

Restart Claude after editing the settings. This works with the marketplace plugin on Windows, macOS and Linux, without setting shell variables or editing the installed plugin. Ask Claude to analyze a procedure, then ask it to call `get_metrics` with `includePreviousSessions=true`. Version 0.13.0 supports the same file, but requires creating it manually.

Creation happens on the first MCP server startup in each workspace, rather than during global plugin installation. Existing files retain their contents, formatting and modification time. Creation publishes complete JSON without replacing a file created concurrently. Read-only workspaces continue using current/default options if the starter cannot be written; a generic notice goes to stderr. No cache/metrics data directory is created by scaffolding alone.

The [example file](../samples/sqlscriptdom.example.json) and [JSON Schema](plugin-settings.schema.json) are bundled with each package. An optional `$schema` field can identify the schema for your editor. The runtime uses its embedded schema and never fetches `$schema` URLs.

Both features remain off when their section is absent, `enabled` is omitted, or `enabled` is false. A directory alone does not enable recording. If enabled without a directory, metrics use `.sqlscriptdom/metrics` and reports use `.sqlscriptdom/cache`. Explicit paths can be absolute or relative to the **settings file's directory**. Paths are literal: `~`, `$HOME`, `%LOCALAPPDATA%` and other variable substitutions are not expanded. Relative paths work across operating systems; escape backslashes in Windows JSON paths or use forward slashes.

Add `.sqlscriptdom/` to your project's `.gitignore`. Metrics contain numeric observations; cached reports contain SQL-derived names, snippets and locations. Both are local project data with existing private permissions, retention limits and link protections. Restart Claude after editing the file. Setting `enabled` to false stops future recording/reuse after restart; it does not delete existing data. Remove any corresponding environment/command-line override when disabling through the file.

## Selection and precedence

The MCP process checks exactly `<workspace>/.sqlscriptdom.json`. Workspace defaults to its working directory, or the directory selected with `--workspace`. It does not search parent folders, your home directory or the plugin installation. Starting Claude from a different folder therefore selects a different project configuration.

For an explicit settings file, launch with `--settings /path/to/settings.json`. Relative `--settings` paths resolve against the process working directory. The file replaces automatic discovery; files are not merged.

For each storage feature, precedence is:

1. `--metrics-directory` / `--cache-directory`.
2. Nonblank `SQLLSP_METRICS_DIR` / `SQLLSP_ANALYSIS_CACHE_DIR`.
3. The selected settings file.
4. Disabled.

Existing directory flags/environment variables continue to require absolute paths and explicitly enable their feature, even if the file says `enabled=false`. Blank inherited environment variables are ignored. Settings are validated even when storage paths are overridden. Unknown/duplicate fields, unsupported versions, malformed JSON, invalid types, inaccessible files and files over 64 KiB fail startup with exit 1 and a generic stderr message; values are not echoed. A missing automatically discovered file uses disabled defaults and is scaffolded at normal server startup. Standalone summary/evaluation/prebuild commands do not scaffold it. An explicitly requested missing file is an error, and explicit `--settings` use does not generate an extra project file. UTF-8 with or without a BOM is supported; comments/trailing commas are not.

This file configures MCP local storage and optional [response budgets](discovery-response.md). `responses.discoveryMaxBytes` accepts 8192–16384 (default 16384); it never enables storage. These optional fields extend version 1 while keeping existing files valid. SQL parsing, resolution and approved database profiles retain their existing explicit `--config` / `SQLLSP_MCP_CONFIG` [source options](configuration.schema.json). They cannot be supplied in `.sqlscriptdom.json`. The LSP process still receives its own client initialization options.

## Standalone commands

Summary and prebuild commands use the same project settings:

```sh
"$SQL_PLUGIN_ROOT/mcp/sqlscriptdom-mcp" --workspace /path/to/sql-project --metrics-summary
"$SQL_PLUGIN_ROOT/mcp/sqlscriptdom-mcp" --workspace /path/to/sql-project --build-cache
```

Use `mcp/sqlscriptdom-mcp.exe` with PowerShell's call operator on Windows. Set `SQL_PLUGIN_ROOT` to the installed/extracted plugin folder. The first command reads retained metrics without initializing SQL. The second acquires/parses sources and prebuilds bounded reports; enable `analysisCache` first. Explicit source configuration is still required for online/hybrid prebuilds. See [metrics and comparisons](plugin-metrics.md) and [report cache behavior](persistent-analysis-cache.md).

## Release verification

[Actual 0.14.0 marketplace installation](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/39045608531) passed on Windows, Linux, Apple Silicon and Intel Mac with strict validation. Installed packages passed 60 LSP tests each, plus 63 MCP tests on Windows (three Unix-only skips) or 66 on Unix. The optional performance case was skipped. Automatic starter creation, preservation and concurrent startup were exercised against the installed executables; Unix also exercised read-only workspaces. Mac executable modes survived without repair. [Final evidence](evidence/automatic-settings-ci.json) records clean source, native/install jobs, dependency checks, installed-server discovery and all eight release digests. No model calls were made.
