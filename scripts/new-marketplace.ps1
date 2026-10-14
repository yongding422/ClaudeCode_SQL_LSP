# Build a catalog from the exact, already-tested release ZIPs. Does not publish anything.
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository = 'yongding422/ClaudeCode_SQL_LSP',
    [string] $PackageDirectory = (Join-Path $PSScriptRoot '../artifacts/packages/marketplace'),
    [string] $OutputPath = (Join-Path $PSScriptRoot '../.claude-plugin/marketplace.json')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repo 'claude-plugin/.claude-plugin/plugin.json') -Raw | ConvertFrom-Json).version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must be numeric major.minor.patch.' }
Add-Type -AssemblyName System.IO.Compression
$entries = foreach ($runtime in @('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')) {
    $name = 'sqlscriptdom-lsp-' + $runtime
    $fileName = "sqlscriptdom-lsp-$version-$runtime.zip"
    $archive = Join-Path $PackageDirectory $fileName
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Missing package: $fileName" }
    $expected = (Get-Content -LiteralPath ($archive + '.sha256') -Raw).Trim().Split(' ')[0]
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -notmatch '^[a-fA-F0-9]{64}$' -or $actual -ne $expected) { throw "Checksum mismatch: $fileName" }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $manifestEntry = $zip.GetEntry('.claude-plugin/plugin.json')
        $buildEntry = $zip.GetEntry('build.json')
        if ($null -eq $manifestEntry -or $null -eq $buildEntry -or $null -eq $zip.GetEntry('.lsp.json')) { throw "Incomplete plugin: $fileName" }
        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        $reader = [System.IO.StreamReader]::new($buildEntry.Open())
        try { $build = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.name -ne $name -or $manifest.version -ne $version -or $build.runtime -ne $runtime -or
            $build.version -ne $version -or $build.selfContained -ne $true) { throw "Package identity mismatch: $fileName. Build with -Marketplace." }
        $binary = if ($runtime.StartsWith('win-')) { 'sqlscriptdom-lsp.exe' } else { 'sqlscriptdom-lsp' }
        if ($null -eq $zip.GetEntry('server/' + $binary)) { throw "Missing apphost: $fileName" }
    } finally { $zip.Dispose() }
    [ordered]@{
        name = $name
        version = $version
        description = "T-SQL code search and navigation for $runtime; includes .NET runtime. Install only your platform's entry."
        source = [ordered]@{ source = 'archive'; url = "https://github.com/$Repository/releases/download/v$version/$fileName"; sha256 = $actual }
    }
}
$marketplace = [ordered]@{
    name = 'sqlscriptdom-lsp-marketplace'
    owner = [ordered]@{ name = $Repository.Split('/')[0] }
    metadata = [ordered]@{ description = 'SQL code search and navigation for Claude Code. Choose one platform package. Archive installs require Claude Code 2.1.224 or later.' }
    plugins = @($entries)
}
$absoluteOutput = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path $absoluteOutput) -Force | Out-Null
$marketplace | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $absoluteOutput -Encoding utf8NoBOM
Write-Output "Generated checksum-pinned marketplace: $absoluteOutput"
