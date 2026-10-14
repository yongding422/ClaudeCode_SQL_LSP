# Requires PowerShell 7 and a signed-in Claude Code CLI. Uses only copied public fixtures.
param(
    [string] $PluginPath = (Join-Path $PSScriptRoot '../claude-plugin'),
    [switch] $EnableLspTool,
    [switch] $Diagnostics,
    [switch] $Hover,
    [decimal] $MaxBudgetUsd = 1
)
$ErrorActionPreference = 'Stop'
if ($Diagnostics -and $Hover) { throw 'Choose either Diagnostics or Hover for this smoke run.' }
$repo = Split-Path $PSScriptRoot
$plugin = (Resolve-Path -LiteralPath $PluginPath).Path
$runId = [guid]::NewGuid().ToString('N')
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('sql-lsp-claude-' + $runId)
$evidence = Join-Path $repo ('artifacts/claude-smoke-' + $runId)
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
Copy-Item (Join-Path $repo 'samples/sql-workspace/payroll-*.sql') -Destination $fixture
$start = [System.Diagnostics.ProcessStartInfo]::new((Get-Command claude -ErrorAction Stop).Source)
$start.WorkingDirectory = $fixture
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
if ($EnableLspTool) { $start.Environment['ENABLE_LSP_TOOL'] = '1' }
$start.Environment['SQLLSP_TRACE_STARTUP'] = '1'
$prompt = 'Read payroll-caller.sql, then use LSP goToDefinition on payroll-caller.sql at line 3 column 15, then findReferences on payroll-procedure.sql at line 1 column 25. Both files are public synthetic SQL fixtures. Report actual tool results including failures. Do not infer locations from text. Only Read and LSP tools are permitted.'
$toolNames = 'Read,LSP,ToolSearch'
if ($Hover) {
    $prompt = 'Read payroll-caller.sql, then use the LSP hover operation on payroll-caller.sql at line 3 column 15. These are public synthetic SQL fixtures. Report the actual signature, kind and source returned by LSP. Do not infer them from file contents and do not call any other tool except Read or LSP. Report tool unavailability honestly.'
}
if ($Diagnostics) {
    $toolNames = 'Read,Edit,LSP'
    $prompt = 'Read payroll-caller.sql, then use Edit to replace exactly "EXEC dbo.ProcessPayroll @PayrollId = 42;" with "SELECT FROM;" in that public synthetic fixture. After editing, read the same file again twice in separate tool calls to allow automatic LSP diagnostics to arrive, and report them if supplied. Do not fix the intentional error. Only use Read and Edit on payroll-caller.sql. Do not infer diagnostics or run commands.'
}
$arguments = @('--plugin-dir', $plugin, '--setting-sources', '', '--settings', '{"disableAllHooks":true}',
    '--strict-mcp-config', '--mcp-config', '{"mcpServers":{}}', '--tools', $toolNames, '--allowedTools', $toolNames,
    '--permission-mode', 'dontAsk', '--disable-slash-commands', '--no-session-persistence', '--print',
    '--verbose', '--output-format', 'stream-json', '--effort', 'low', '--max-budget-usd',
    $MaxBudgetUsd.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    '--debug-file', (Join-Path $evidence 'debug.log'), $prompt)
foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
$process = [System.Diagnostics.Process]::Start($start)
$output = $process.StandardOutput.ReadToEndAsync()
$errors = $process.StandardError.ReadToEndAsync()
if (-not $process.WaitForExit(55000)) { $process.Kill($true); $process.WaitForExit() }
# Raw files remain ignored, local evidence. Never include auth status, credentials or private SQL.
$output.Result | Set-Content -LiteralPath (Join-Path $evidence 'session.jsonl')
$errors.Result | Set-Content -LiteralPath (Join-Path $evidence 'stderr.log')
$events = @($output.Result -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$initial = $events | Where-Object { $_.type -eq 'system' -and $_.subtype -eq 'init' } | Select-Object -First 1
$calls = @($events | Where-Object { $_.type -eq 'assistant' } | ForEach-Object { $_.message.content } |
    Where-Object { $_.type -eq 'tool_use' -and $_.name -eq 'LSP' })
$results = @($events | Where-Object { $_.type -eq 'user' } | ForEach-Object { $_.message.content } |
    Where-Object { $_.type -eq 'tool_result' })
$lspResults = @($results | Where-Object { $_.tool_use_id -in $calls.id })
$debugPath = Join-Path $evidence 'debug.log'
$debug = if (Test-Path -LiteralPath $debugPath) { Get-Content -LiteralPath $debugPath -Raw } else { '' }
$initialized = $debug.Contains("Received response 'initialize -")
$diagnosticsReceived = $debug -match 'Received diagnostics from plugin:sqlscriptdom-lsp:sqlscriptdom: [1-9][0-9]* diagnostic\(s\)'
$shutdown = $debug.Contains("Received response 'shutdown -")
$hoverResponse = $debug.Contains("Received response 'textDocument/hover -")
$intentionalEdit = $Diagnostics -and (Get-Content -LiteralPath (Join-Path $fixture 'payroll-caller.sql') -Raw).Contains('SELECT FROM;')
$final = $events | Where-Object { $_.type -eq 'result' } | Select-Object -Last 1
$summary = [ordered]@{
    claudeVersion = (& claude --version); exitCode = $process.ExitCode; enableLspTool = [bool]$EnableLspTool
    tools = @($initial.tools); plugins = @($initial.plugins | ForEach-Object { $_.name })
    calls = @($calls | ForEach-Object { $_.input }); results = @($lspResults | ForEach-Object { $_.content })
    initializeResponseObserved = $initialized; diagnosticsObserved = $diagnosticsReceived; shutdownResponseObserved = $shutdown
    hoverRequested = [bool]$Hover
    hoverResponseObserved = $hoverResponse
    costUsd = $final.total_cost_usd; outcome = $final.subtype; evidenceDirectory = $evidence; publicFixtureDirectory = $fixture
}
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json')
$summary | ConvertTo-Json -Depth 15
$success = if ($Diagnostics) { $initialized -and $diagnosticsReceived -and $shutdown -and $intentionalEdit } elseif ($Hover) {
    $hoverCalls = @($calls | Where-Object { $_.input.operation -eq 'hover' })
    $hoverResults = @($lspResults | Where-Object { $_.tool_use_id -in $hoverCalls.id })
    $initialized -and $hoverResponse -and $shutdown -and $hoverCalls.Count -gt 0 -and $hoverResults.Count -eq $hoverCalls.Count -and
        @($hoverResults | Where-Object { $_.is_error }).Count -eq 0 -and
        ($hoverResults.content -join '\n').Contains('Signature:') -and ($hoverResults.content -join '\n').Contains('Source:')
} else {
    $calls.Count -ge 2 -and $lspResults.Count -eq $calls.Count -and @($lspResults | Where-Object { $_.is_error }).Count -eq 0 -and
        @($calls | Where-Object { $_.input.operation -eq 'goToDefinition' }).Count -gt 0 -and
        @($calls | Where-Object { $_.input.operation -eq 'findReferences' }).Count -gt 0 -and
        ($lspResults.content -join '\n').Contains('payroll-procedure.sql')
}
if ($process.ExitCode -ne 0 -or -not $success) {
    throw 'Claude LSP smoke did not demonstrate successful calls. Inspect the local evidence; model prose is not proof.'
}
