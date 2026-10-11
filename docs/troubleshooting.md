# Troubleshooting

- A terminal showing no output is normal: the server expects framed LSP input. Use the integration suite to exercise it.
- If a client reports unframed stdout, launch the compiled/published DLL directly, never `dotnet run` or a wrapper that prints status to stdout.
- Invalid initialization options report JSON Schema paths through an error `window/showMessage` and stderr, with analysis capabilities disabled. Correct the options and restart. This avoids a verified OmniSharp initialize-callback exception path that leaves clients waiting for a response.
- Missing symbols after workspace file edits require restart in M1, or opening/editing the file through the client. Closing an overlay refreshes disk state. Excluded directories, links, oversized files and statements damaged by syntax errors may have no outlines.
- `dotnet --info` on the development host failed in the SDK workload installer; `dotnet --version`, restore, build, test and publish work. The language server has no workload dependencies.
- `--stdio` is the only transport argument. Invalid command-line arguments print usage to stderr and exit with code 2.
