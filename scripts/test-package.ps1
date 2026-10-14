param([ValidateSet('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')] [string] $Runtime = 'win-x64', [switch] $LiveDatabase, [switch] $Marketplace)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repo 'claude-plugin/.claude-plugin/plugin.json') -Raw | ConvertFrom-Json).version
$output = Join-Path $repo 'artifacts/packages'
if ($Marketplace) { $output = Join-Path $output 'marketplace' }
$archive = Join-Path $output "sqlscriptdom-lsp-$version-$Runtime.zip"
$expected = (Get-Content -LiteralPath ($archive + '.sha256') -Raw).Split(' ')[0]
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Package checksum mismatch.' }
$destination = Join-Path $repo ('artifacts/package test 中文 ' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $destination | Out-Null
[System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $destination)
$binary = if ($Runtime.StartsWith('win-')) { 'sqlscriptdom-lsp.exe' } else { 'sqlscriptdom-lsp' }
$executable = Join-Path $destination "server/$binary"
# ZipFile extraction does not apply Unix attributes; tar/unzip do. Fix the owned extracted apphost here.
if (-not $IsWindows) {
    [System.IO.File]::SetUnixFileMode($executable, [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite -bor [System.IO.UnixFileMode]::UserExecute)
}
if (-not (Test-Path -LiteralPath (Join-Path $destination '.lsp.json'))) { throw 'Missing plugin configuration in archive.' }
$previousExe = $env:SQLLSP_TEST_SERVER_EXE
$previousLive = $env:SQLLSP_RUN_SQLSERVER_TESTS
try {
    $env:SQLLSP_TEST_SERVER_EXE = $executable
    if ($LiveDatabase) { $env:SQLLSP_RUN_SQLSERVER_TESTS = '1' } else { $env:SQLLSP_RUN_SQLSERVER_TESTS = $null }
    & dotnet test (Join-Path $repo 'tests/SqlLsp.Server.IntegrationTests') --no-build --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Packaged wire tests failed.' }
    & dotnet test (Join-Path $repo 'tests/SqlLsp.SqlServer.IntegrationTests') --no-build --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Packaged catalog/provider tests failed.' }
} finally {
    $env:SQLLSP_TEST_SERVER_EXE = $previousExe
    $env:SQLLSP_RUN_SQLSERVER_TESTS = $previousLive
}
Write-Output "Verified extracted package: $destination"
