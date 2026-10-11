# Troubleshooting

- A terminal showing no output is normal: the server expects framed LSP input. Use the integration suite to exercise it.
- If a client reports unframed stdout, launch the compiled/published DLL directly, never `dotnet run` or a wrapper that prints status to stdout.
- Invalid initialization options report JSON Schema paths through an error `window/showMessage` and stderr, with analysis capabilities disabled. Correct the options and restart. This avoids a verified OmniSharp initialize-callback exception path that leaves clients waiting for a response.
- Workspace file edits refresh after watcher debounce. If native watching is unavailable, send watched-file notifications, re-open files, or restart. Open buffers continue to win over disk changes. Excluded directories, links, oversized files and statements damaged by syntax errors may have no outlines.
- Empty definition/reference results can mean unresolved, ambiguous or unsupported targets. Check schema/database qualification and `resolution` options. Each workspace root is a separate project; source context, duplicate declarations and case rules can prevent a jump. Dynamic SQL and numbered/remote targets are not bound.
- `dotnet --info` on the development host failed in the SDK workload installer; `dotnet --version`, restore, build, test and publish work. The language server has no workload dependencies.
- `--stdio` is the only transport argument. Invalid command-line arguments print usage to stderr and exit with code 2.
