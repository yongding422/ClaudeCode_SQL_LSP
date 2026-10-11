# Development setup

1. Install .NET SDK 10.0.401. `global.json` deliberately prevents silently selecting another SDK. Running the published server requires the .NET 10 runtime.
2. Run `dotnet restore --locked-mode`, `dotnet build --no-restore`, and `dotnet test --no-build --no-restore` at the repository root.
3. Run `dotnet publish src/SqlLsp.Server -c Release --no-restore -o claude-plugin/server`.
4. Configure an LSP client to launch `dotnet`, followed by the absolute `claude-plugin/server/sqlscriptdom-lsp.dll` path and `--stdio`. Configure language ID `sql` and `.sql` files; point the workspace root at `samples/sql-workspace`.
5. For a Claude development manifest check, run `claude plugin validate ./claude-plugin --strict`. After that, `claude --plugin-dir ./claude-plugin` is the M5 interaction verification starting point, not evidence of successful SQL requests.

PowerShell 7+ launch: `./scripts/start-server.ps1`. Unix launch: `sh ./scripts/start-server.sh`. These wrappers launch published code without build output on stdout. No database profile or credentials are needed in M1.

To run the process integration suite against the published DLL in PowerShell:

```powershell
$env:SQLLSP_TEST_SERVER_DLL = (Resolve-Path ./claude-plugin/server/sqlscriptdom-lsp.dll).Path
dotnet test tests/SqlLsp.Server.IntegrationTests -c Release --no-build --no-restore
Remove-Item Env:SQLLSP_TEST_SERVER_DLL
```

In a Unix shell:

```sh
SQLLSP_TEST_SERVER_DLL="$PWD/claude-plugin/server/sqlscriptdom-lsp.dll" \
  dotnet test tests/SqlLsp.Server.IntegrationTests -c Release --no-build --no-restore
```
