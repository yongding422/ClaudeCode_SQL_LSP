#!/usr/bin/env sh
set -eu
server_dir="$(CDPATH= cd -- "$(dirname -- "$0")/../claude-plugin/server" && pwd)"
exec dotnet "$server_dir/sqlscriptdom-lsp.dll" --stdio
