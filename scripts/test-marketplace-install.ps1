# Real public marketplace install in an owned, isolated Claude configuration. No model calls.
param(
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')] [string] $Runtime = 'win-x64',
    [string] $MarketplaceSource = 'https://github.com/yongding422/ClaudeCode_SQL_LSP.git'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$fixture = Join-Path $repo ('artifacts/marketplace-install-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$previousConfig = $env:CLAUDE_CONFIG_DIR
$previousExecutable = $env:SQLLSP_TEST_SERVER_EXE
$previousMcpExecutable = $env:SQLLSP_TEST_MCP_EXE
try {
    $env:CLAUDE_CONFIG_DIR = Join-Path $fixture 'config'
    # Explicit public HTTPS avoids shorthand selecting SSH on runners without a GitHub SSH key.
    & npx --yes --package '@anthropic-ai/claude-code@2.1.224' claude plugin marketplace add $MarketplaceSource
    if ($LASTEXITCODE -ne 0) { throw 'Marketplace registration failed.' }
    & npx --yes --package '@anthropic-ai/claude-code@2.1.224' claude plugin install "sqlscriptdom-lsp-$Runtime@sqlscriptdom-lsp-marketplace"
    if ($LASTEXITCODE -ne 0) { throw 'Archive plugin installation failed.' }
    $binary = if ($Runtime.StartsWith('win-')) { 'sqlscriptdom-lsp.exe' } else { 'sqlscriptdom-lsp' }
    # The MCP publish also carries the referenced LSP project's apphost. Select the configured LSP directory.
    $executables = @(Get-ChildItem -LiteralPath $env:CLAUDE_CONFIG_DIR -File -Recurse -Force -Filter $binary |
        Where-Object { $_.Directory.Name -eq 'server' -and
            (Test-Path -LiteralPath (Join-Path $_.Directory.Parent.FullName '.claude-plugin/plugin.json')) })
    if ($executables.Count -ne 1) { throw 'Expected exactly one installed apphost in the isolated configuration.' }
    $executable = $executables[0].FullName
    $plugin = Split-Path (Split-Path $executable)
    $expectedVersion = (Get-Content -LiteralPath (Join-Path $repo 'claude-plugin/.claude-plugin/plugin.json') -Raw | ConvertFrom-Json).version
    $installedManifest = Get-Content -LiteralPath (Join-Path $plugin '.claude-plugin/plugin.json') -Raw | ConvertFrom-Json
    $installedBuild = Get-Content -LiteralPath (Join-Path $plugin 'build.json') -Raw | ConvertFrom-Json
    if ($installedManifest.version -ne $expectedVersion -or $installedBuild.version -ne $expectedVersion -or
        $installedBuild.runtime -ne $Runtime -or $installedBuild.selfContained -ne $true) {
        throw 'Installed archive identity does not match the candidate under test.'
    }
    & npx --yes --package '@anthropic-ai/claude-code@2.1.224' claude plugin validate $plugin --strict
    if ($LASTEXITCODE -ne 0) { throw 'Installed plugin validation failed.' }
    # Do not chmod the cached apphost: native tests must catch installer permission loss.
    $env:SQLLSP_TEST_SERVER_EXE = $executable
    $mcpBinary = if ($Runtime.StartsWith('win-')) { 'sqlscriptdom-mcp.exe' } else { 'sqlscriptdom-mcp' }
    $env:SQLLSP_TEST_MCP_EXE = Join-Path $plugin ('mcp/' + $mcpBinary)
    & dotnet test (Join-Path $repo 'tests/SqlLsp.Server.IntegrationTests') --no-build --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Installed apphost wire tests failed.' }
    & dotnet test (Join-Path $repo 'tests/SqlLsp.Mcp.IntegrationTests') --no-build --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Installed analysis apphost wire tests failed.' }
    Write-Output "Verified real marketplace archive install and native LSP process: $plugin"
} finally {
    $env:CLAUDE_CONFIG_DIR = $previousConfig
    $env:SQLLSP_TEST_SERVER_EXE = $previousExecutable
    $env:SQLLSP_TEST_MCP_EXE = $previousMcpExecutable
}
