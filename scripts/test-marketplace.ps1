# Deterministic release-integrity tests; no network, GitHub settings or Claude usage.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repo 'claude-plugin/.claude-plugin/plugin.json') -Raw | ConvertFrom-Json).version
$fixture = Join-Path $repo ('artifacts/marketplace-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$output = Join-Path $fixture 'catalog/.claude-plugin/marketplace.json'
Add-Type -AssemblyName System.IO.Compression
function Write-FixtureArchive([string] $Runtime, [string] $Name, [bool] $SelfContained = $true) {
    $path = Join-Path $fixture "sqlscriptdom-lsp-$version-$Runtime.zip"
    $zip = [System.IO.Compression.ZipArchive]::new([System.IO.File]::Open($path, [System.IO.FileMode]::Create), [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $binary = if ($Runtime.StartsWith('win-')) { 'sqlscriptdom-lsp.exe' } else { 'sqlscriptdom-lsp' }
        $mcpBinary = if ($Runtime.StartsWith('win-')) { 'sqlscriptdom-mcp.exe' } else { 'sqlscriptdom-mcp' }
        $files = @{
            '.claude-plugin/plugin.json' = (@{ name = $Name; version = $version } | ConvertTo-Json)
            'build.json' = (@{ version = $version; runtime = $Runtime; selfContained = $SelfContained } | ConvertTo-Json)
            '.lsp.json' = '{}'
            "server/$binary" = 'synthetic archive validation fixture, not an executable'
            '.mcp.json' = '{}'
            "mcp/$mcpBinary" = 'synthetic MCP archive validation fixture, not an executable'
            'skills/explain-procedure/SKILL.md' = 'synthetic skill fixture'
        }
        foreach ($file in $files.GetEnumerator()) {
            $writer = [System.IO.StreamWriter]::new($zip.CreateEntry($file.Key).Open())
            try { $writer.Write($file.Value) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($path))" | Set-Content -LiteralPath ($path + '.sha256')
}
function Assert-Rejected([string] $ExpectedMessage) {
    $before = (Get-FileHash -LiteralPath $output).Hash
    $caught = $null
    try { & (Join-Path $PSScriptRoot 'new-marketplace.ps1') -PackageDirectory $fixture -OutputPath $output | Out-Null }
    catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or -not $caught.Contains($ExpectedMessage)) { throw "Expected rejection: $ExpectedMessage; got: $caught" }
    if ((Get-FileHash -LiteralPath $output).Hash -ne $before) { throw 'Failed generation overwrote the previous catalog.' }
}
foreach ($runtime in @('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')) { Write-FixtureArchive $runtime ('sqlscriptdom-lsp-' + $runtime) }
& (Join-Path $PSScriptRoot 'new-marketplace.ps1') -PackageDirectory $fixture -OutputPath $output | Out-Null
$catalog = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
if ($catalog.plugins.Count -ne 4) { throw 'Missing platform entries.' }
foreach ($entry in $catalog.plugins) {
    if ($entry.source.source -ne 'archive' -or $entry.source.sha256 -notmatch '^[a-f0-9]{64}$' -or
        $entry.source.url -notmatch '^https://github.com/yongding422/ClaudeCode_SQL_LSP/releases/download/v\d+\.\d+\.\d+/sqlscriptdom-lsp-') { throw 'Invalid release source.' }
}
$intel = Join-Path $fixture "sqlscriptdom-lsp-$version-osx-x64.zip"
$sidecar = Get-Content -LiteralPath ($intel + '.sha256') -Raw
('0' * 64) | Set-Content -LiteralPath ($intel + '.sha256')
Assert-Rejected 'Checksum mismatch'
$sidecar | Set-Content -LiteralPath ($intel + '.sha256')
Write-FixtureArchive 'osx-x64' 'wrong-name'
Assert-Rejected 'Package identity mismatch'
Write-FixtureArchive 'osx-x64' 'sqlscriptdom-lsp-osx-x64' $false
Assert-Rejected 'Package identity mismatch'
Write-FixtureArchive 'osx-x64' 'sqlscriptdom-lsp-osx-x64'
Move-Item -LiteralPath $intel -Destination ($intel + '.missing')
Assert-Rejected 'Missing package'
Move-Item -LiteralPath ($intel + '.missing') -Destination $intel
Write-Output 'Marketplace integrity checks passed: four-platform catalog, checksum rejection, name rejection, runtime independence rejection, missing package rejection, and preservation on failure.'
