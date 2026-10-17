# PowerShell 7, .NET SDK from global.json. Each run creates a fresh owned staging directory.
param([ValidateSet('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')] [string] $Runtime = 'win-x64', [switch] $Marketplace)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repo 'claude-plugin/.claude-plugin/plugin.json') -Raw | ConvertFrom-Json).version
$name = "sqlscriptdom-lsp-$version-$Runtime"
$output = Join-Path $repo 'artifacts/packages'
if ($Marketplace) { $output = Join-Path $output 'marketplace' }
$stage = Join-Path $output ($name + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
& dotnet publish (Join-Path $repo 'src/SqlLsp.Server') -c Release -r $Runtime --self-contained true -p:RestoreLockedMode=true `
    -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PublishAot=false -o (Join-Path $stage 'server')
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
& dotnet publish (Join-Path $repo 'src/SqlLsp.Mcp') -c Release -r $Runtime --self-contained true -p:RestoreLockedMode=true `
    -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PublishAot=false -o (Join-Path $stage 'mcp')
if ($LASTEXITCODE -ne 0) { throw 'Self-contained MCP publish failed.' }
New-Item -ItemType Directory -Path (Join-Path $stage '.claude-plugin') | Out-Null
$manifest = Get-Content -LiteralPath (Join-Path $repo 'claude-plugin/.claude-plugin/plugin.json') -Raw | ConvertFrom-Json
# Marketplace entries have distinct architecture names; entry and plugin names must match.
if ($Marketplace) { $manifest.name = 'sqlscriptdom-lsp-' + $Runtime }
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage '.claude-plugin/plugin.json') -Encoding utf8NoBOM
$binary = if ($Runtime.StartsWith('win-')) { 'sqlscriptdom-lsp.exe' } else { 'sqlscriptdom-lsp' }
$config = [ordered]@{ sqlscriptdom = [ordered]@{
    command = '${CLAUDE_PLUGIN_ROOT}/server/' + $binary; args = @('--stdio'); transport = 'stdio'
    extensionToLanguage = [ordered]@{ '.sql' = 'sql' }
} }
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage '.lsp.json') -Encoding utf8NoBOM
$mcpBinary = if ($Runtime.StartsWith('win-')) { 'sqlscriptdom-mcp.exe' } else { 'sqlscriptdom-mcp' }
$mcpConfig = [ordered]@{ mcpServers = [ordered]@{ 'sqlscriptdom-analysis' = [ordered]@{
    command = '${CLAUDE_PLUGIN_ROOT}/mcp/' + $mcpBinary; args = @('--stdio')
} } }
$mcpConfig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage '.mcp.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $repo 'claude-plugin/skills') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'samples') -Destination $stage -Recurse
New-Item -ItemType Directory -Path (Join-Path $stage 'evaluation') | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'scripts/evaluate-understanding.py') -Destination (Join-Path $stage 'evaluation')
Copy-Item -LiteralPath (Join-Path $repo 'SqlScriptDOM_LSP_Implementation_Plan.md') -Destination $stage
# Inventory bundled libraries and include the licenses supplied by their NuGet packages.
$packages = @(@('server/sqlscriptdom-lsp.deps.json', 'mcp/sqlscriptdom-mcp.deps.json') | ForEach-Object {
    $deps = Get-Content -LiteralPath (Join-Path $stage $_) -Raw | ConvertFrom-Json -AsHashtable
    $deps.libraries.GetEnumerator() | Where-Object { $_.Value.type -in @('package', 'runtimepack') } | ForEach-Object { $_.Key }
} | Sort-Object -Unique)
$packages | Set-Content -LiteralPath (Join-Path $stage 'dependencies.txt')
$cacheLine = & dotnet nuget locals global-packages --list
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate NuGet license metadata.' }
$cache = ($cacheLine -replace '^global-packages:\s*', '').Trim()
$notices = [System.Collections.Generic.List[object]]::new()
foreach ($package in $packages) {
    $identity = $package -replace '^runtimepack\.', ''
    $parts = $identity -split '/'
    $packageRoot = Join-Path (Join-Path $cache $parts[0].ToLowerInvariant()) $parts[1]
    $nuspecPath = Join-Path $packageRoot ($parts[0].ToLowerInvariant() + '.nuspec')
    if (-not (Test-Path -LiteralPath $nuspecPath)) { throw "Missing license metadata for $identity" }
    [xml] $nuspec = Get-Content -LiteralPath $nuspecPath -Raw
    $metadata = $nuspec.package.metadata
    $notices.Add([ordered]@{ package = $identity; licenseType = [string]$metadata.license.type
        license = [string]$metadata.license.InnerText; licenseUrl = [string]$metadata.licenseUrl; projectUrl = [string]$metadata.projectUrl })
    $licenseFiles = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES)' })
    if ($metadata.license.type -eq 'file') {
        $licensePath = [System.IO.Path]::GetFullPath((Join-Path $packageRoot $metadata.license.InnerText))
        $relativeLicense = [System.IO.Path]::GetRelativePath($packageRoot, $licensePath)
        if ([System.IO.Path]::IsPathRooted($relativeLicense) -or $relativeLicense.StartsWith('..')) { throw 'Invalid package license path.' }
        $licenseFiles += Get-Item -LiteralPath $licensePath
    }
    $destination = Join-Path $stage ('third-party/' + $parts[0] + '/' + $parts[1])
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $nuspecPath -Destination $destination
    foreach ($licenseFile in $licenseFiles | Sort-Object FullName -Unique) { Copy-Item -LiteralPath $licenseFile.FullName -Destination $destination }
}
$notices | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'third-party/notices.json') -Encoding utf8NoBOM
$build = [ordered]@{ version = $version; runtime = $Runtime; sdk = (& dotnet --version)
    gitCommit = (& git -C $repo rev-parse HEAD); dirtySource = [bool](& git -C $repo status --porcelain)
    selfContained = $true; trimmed = $false; packages = $packages }
$build | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'build.json') -Encoding utf8NoBOM
# ZIP entry names include dotfiles. Preserve executable Unix modes for native apphosts/libraries.
Add-Type -AssemblyName System.IO.Compression
$archive = Join-Path $output ($name + '.zip')
$stream = [System.IO.File]::Open($archive, [System.IO.FileMode]::Create)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse -Force | Sort-Object FullName) {
            $relative = [System.IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')
            $entry = $zip.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [datetimeoffset]::new(2026, 1, 1, 0, 0, 0, [timespan]::Zero)
            $mode = if ($file.Name -in @($binary, $mcpBinary) -or $file.Extension -eq '.so' -or $file.Extension -eq '.dylib') { 33261 } else { 33188 }
            $entry.ExternalAttributes = $mode -shl 16
            $source = [System.IO.File]::OpenRead($file.FullName)
            $target = $entry.Open()
            try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding utf8NoBOM
[ordered]@{ archive = $archive; sha256 = $hash; pluginDirectory = $stage; executable = (Join-Path $stage "server/$binary") } | ConvertTo-Json
