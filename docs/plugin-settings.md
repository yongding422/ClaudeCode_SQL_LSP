# Configure local metrics and saved reports

Starting with 0.13.0, create **`.sqlscriptdom.json` in the SQL project folder where you start Claude Code**:

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

Start a fresh Claude session from that folder. This works with the marketplace plugin on Windows, macOS and Linux, without setting shell variables or editing the installed plugin. Ask Claude to analyze a procedure, then ask it to call `get_metrics` with `includePreviousSessions=true`.

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

Existing directory flags/environment variables continue to require absolute paths and explicitly enable their feature, even if the file says `enabled=false`. Blank inherited environment variables are ignored. Settings are validated even when storage paths are overridden. Unknown/duplicate fields, unsupported versions, malformed JSON, invalid types, inaccessible files and files over 64 KiB fail startup with exit 1 and a generic stderr message; values are not echoed. A missing automatically discovered file is allowed. An explicitly requested missing file is an error. UTF-8 with or without a BOM is supported; comments/trailing commas are not.

This file configures **MCP local storage only**. SQL parsing, resolution and approved database profiles retain their existing explicit `--config` / `SQLLSP_MCP_CONFIG` [source options](configuration.schema.json). They cannot be supplied in `.sqlscriptdom.json`. The LSP process still receives its own client initialization options.

## Standalone commands

Summary and prebuild commands use the same project settings:

```sh
"$SQL_PLUGIN_ROOT/mcp/sqlscriptdom-mcp" --workspace /path/to/sql-project --metrics-summary
"$SQL_PLUGIN_ROOT/mcp/sqlscriptdom-mcp" --workspace /path/to/sql-project --build-cache
```

Use `mcp/sqlscriptdom-mcp.exe` with PowerShell's call operator on Windows. Set `SQL_PLUGIN_ROOT` to the installed/extracted plugin folder. The first command reads retained metrics without initializing SQL. The second acquires/parses sources and prebuilds bounded reports; enable `analysisCache` first. Explicit source configuration is still required for online/hybrid prebuilds. See [metrics and comparisons](plugin-metrics.md) and [report cache behavior](persistent-analysis-cache.md).

## Release verification

[Actual 0.13.0 marketplace installation](https://github.com/yongding422/ClaudeCode_SQL_LSP/actions/runs/39001122394) passed on Windows, Linux, Apple Silicon and Intel Mac with strict validation. Installed packages passed 60 LSP tests each, plus 57 MCP tests on Windows (two Unix-link skips) or 59 on Unix, including 21 new project-configuration cases. The optional performance case was skipped. Mac executable modes survived without repair. [Final evidence](evidence/plugin-settings-ci.json) records source, native/installation jobs, dependency checks, the corrected initial SSH failure and all eight release digests. No model calls were made.
