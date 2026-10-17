# Real Claude tool evidence on copied public SQL only; no global plugin/settings mutations.
param(
    [Parameter(Mandatory)] [string] $PluginPath,
    [string] $ClaudeExecutable,
    [switch] $InvokeSkill,
    [ValidateRange(5, 300)] [int] $TimeoutSeconds = 90,
    [decimal] $MaxBudgetUsd = 1
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$plugin = (Resolve-Path -LiteralPath $PluginPath).Path
$name = (Get-Content -LiteralPath (Join-Path $plugin '.claude-plugin/plugin.json') -Raw | ConvertFrom-Json).name
$runId = [guid]::NewGuid().ToString('N')
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('sql-analysis-claude-' + $runId)
$evidence = Join-Path $repo ('artifacts/claude-analysis-' + $runId)
New-Item -ItemType Directory -Path $fixture, $evidence -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'samples/understanding-evaluation/modules.sql') -Destination $fixture
$claudeCommand = if ($ClaudeExecutable) { (Resolve-Path -LiteralPath $ClaudeExecutable).Path } else { (Get-Command claude -ErrorAction Stop).Source }
$start = [System.Diagnostics.ProcessStartInfo]::new($claudeCommand)
$start.WorkingDirectory = $fixture
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['CLAUDE_CONFIG_DIR'] = Join-Path $evidence 'isolated-config'
$start.Environment['ENABLE_LSP_TOOL'] = '1'
$start.Environment.Remove('SQLLSP_MCP_CONFIG') | Out-Null
$start.Environment.Remove('SQLLSP_ANALYSIS_CACHE_DIR') | Out-Null
$start.Environment.Remove('SQLLSP_METRICS_DIR') | Out-Null
$prompt = 'Explain dbo.ProcessPayroll and how Ohio is handled. Identify direct and possible transitive table reads/writes and called procedures. Explain the Ohio guard, cite source lines, and report dependencies that cannot be verified. Do not execute SQL or run shell commands. Report tool failures honestly.'
if ($InvokeSkill) { $prompt = "Use the $($name):explain-procedure skill for this task. " + $prompt }
$tools = 'Read,Grep,Glob,LSP,Skill,ToolSearch'
$allowed = $tools + ",mcp__plugin_$($name)_sqlscriptdom-analysis__find_procedures,mcp__plugin_$($name)_sqlscriptdom-analysis__analyze_procedure"
# A fresh config plus SQL-only workspace excludes unrelated MCP sources while testing actual plugin autoload.
$arguments = @('--plugin-dir', $plugin, '--setting-sources', '', '--settings', '{"disableAllHooks":true}',
    '--tools', $tools, '--allowedTools', $allowed,
    '--permission-mode', 'dontAsk', '--no-session-persistence', '--print', '--verbose', '--output-format', 'stream-json',
    '--effort', 'low', '--max-budget-usd', $MaxBudgetUsd.ToString([System.Globalization.CultureInfo]::InvariantCulture), $prompt)
foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
$process = [System.Diagnostics.Process]::Start($start)
$output = $process.StandardOutput.ReadToEndAsync()
$errors = $process.StandardError.ReadToEndAsync()
$timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
$output.Result | Set-Content -LiteralPath (Join-Path $evidence 'session.jsonl')
$errors.Result | Set-Content -LiteralPath (Join-Path $evidence 'stderr.log')
$events = @($output.Result -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$calls = @($events | Where-Object { $_.type -eq 'assistant' } | ForEach-Object { $_.message.content } | Where-Object { $_.type -eq 'tool_use' })
$results = @($events | Where-Object { $_.type -eq 'user' } | ForEach-Object { $_.message.content } | Where-Object { $_.type -eq 'tool_result' })
$analysisCalls = @($calls | Where-Object { $_.name -match '__analyze_procedure$' })
$discoveryCalls = @($calls | Where-Object { $_.name -match '__find_procedures$' })
$analysisResults = @($results | Where-Object { $_.tool_use_id -in $analysisCalls.id })
$successful = @($analysisResults | Where-Object {
    if ($_.is_error -or $_.content -isnot [string]) { return $false }
    try {
        $data = $_.content | ConvertFrom-Json
        $entry = $data.report.modules | Where-Object { $_.identity.schema -eq 'dbo' -and $_.identity.name -eq 'ProcessPayroll' }
        $callee = $data.report.modules | Where-Object { $_.identity.schema -eq 'dbo' -and $_.identity.name -eq 'ApplyOhioRules' }
        $compact = $data.report.format -eq 'compact-v1'
        $ohio = @($entry.controlFlow.nodes | Where-Object { $_.kind -eq 'If' -and $_.predicate.preview -eq "@State = 'OH'" })
        $call = $entry.calls | Where-Object { $_.target.identity.name -eq 'ApplyOhioRules' -or ($compact -and $_.target.module -eq $callee.id) }
        $contexts = if ($compact) { $call.context } else { $call.flowContext }
        $then = @($entry.controlFlow.nodes | Where-Object { $_.kind -eq 'Then' -and $_.parentId -in $ohio.id -and $_.id -in $contexts })
        $valueCall = @($entry.valueFlow.calls | Where-Object { ($_.target.identity.name -eq 'ApplyOhioRules' -or ($compact -and $_.targetModule -eq $callee.id)) -and $_.bindingStatus -eq 'Resolved' })
        $binding = @($valueCall.bindings | Where-Object { $_.parameterName -eq '@State' })
        $where = @($callee.valueFlow.facts | Where-Object { $_.role -eq 'Where' -and @($_.inputs | Where-Object { $_.variableId -in $binding.parameterVariableId }).Count -eq 1 })
        $update = @($callee.accesses | Where-Object { $_.operation -eq 'Update' -and $_.target.identity.name -eq 'Payroll' })
        $citation = if ($compact -and $update.Count -eq 1) { $data.report.evidence.($update[0].at) } else { $null }
        return $compact -and $data.report.status -eq 'Resolved' -and
            $ohio.Count -eq 1 -and $then.Count -eq 1 -and -not $entry.controlFlow.truncated -and
            $valueCall.Count -eq 1 -and $binding.Count -eq 1 -and $where.Count -eq 1 -and
            @($entry.accesses | Where-Object { $_.operation -eq 'Insert' -and $_.target.identity.name -eq 'PayrollAudit' }).Count -eq 1 -and
            $update.Count -eq 1 -and $citation.line -eq 12 -and $citation.text -eq 'modules.sql:12' -and
            @($entry.issues | Where-Object { $_.code -eq 'call-unsupported' }).Count -eq 1
    } catch { return $false }
})
$final = $events | Where-Object { $_.type -eq 'result' } | Select-Object -Last 1
$skillCalls = @($calls | Where-Object { $_.name -eq 'Skill' })
$summary = [ordered]@{
    claudeVersion = (& $claudeCommand --version); exitCode = $process.ExitCode; pluginName = $name
    timedOut = $timedOut; timeoutSeconds = $TimeoutSeconds
    discoveryCalls = $discoveryCalls.Count; analysisCalls = $analysisCalls.Count; successfulAnalysisResults = $successful.Count
    requiredFlowEvidence = 'Exact OH predicate, Then parent and callee call context in actual MCP tool result'
    requiredValueEvidence = 'Resolved EXEC formal binding linked to the callee WHERE variable identity in actual MCP tool result'
    requiredCompactEvidence = 'Shared callee module reference and ready-to-cite UPDATE evidence modules.sql:12 in actual compact MCP result'
    skillRequested = [bool]$InvokeSkill; skillCalls = $skillCalls.Count
    toolNames = @($calls | ForEach-Object { $_.name }); failedToolAttempts = @($results | Where-Object { $_.is_error }).Count
    costUsd = $final.total_cost_usd; outcome = $final.subtype; evidenceDirectory = $evidence
}
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json')
$summary | ConvertTo-Json -Depth 12
if ($process.ExitCode -ne 0 -or $discoveryCalls.Count -lt 1 -or $analysisCalls.Count -lt 1 -or $successful.Count -lt 1 -or
    ($InvokeSkill -and $skillCalls.Count -lt 1)) { throw 'Claude smoke did not complete successfully with verified analysis tool evidence. Inspect local events and timeout metadata; model prose is not proof.' }
