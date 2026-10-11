$ErrorActionPreference = 'Stop'
$serverPath = Join-Path $PSScriptRoot '../claude-plugin/server/sqlscriptdom-lsp.dll'
if (-not (Test-Path -LiteralPath $serverPath)) {
    [Console]::Error.WriteLine('Publish the server first: dotnet publish src/SqlLsp.Server -c Release -o claude-plugin/server')
    exit 2
}
$startInfo = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.ArgumentList.Add($serverPath)
$startInfo.ArgumentList.Add('--stdio')
# Pump raw streams; keep protocol bytes out of PowerShell's text pipeline.
$serverProcess = [System.Diagnostics.Process]::Start($startInfo)
$stdinPump = [Console]::OpenStandardInput().CopyToAsync($serverProcess.StandardInput.BaseStream)
$stdoutPump = $serverProcess.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput())
$stderrPump = $serverProcess.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
$serverProcess.WaitForExit()
$stdoutPump.GetAwaiter().GetResult()
$stderrPump.GetAwaiter().GetResult()
exit $serverProcess.ExitCode
