using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using SqlLsp.Core;
using SqlLsp.Server;

namespace SqlLsp.Mcp;

public sealed record McpLaunchOptions(string Workspace, string? ConfigurationFile)
{
    public static McpLaunchOptions Parse(string[] args)
    {
        var workspace = Directory.GetCurrentDirectory();
        var config = Environment.GetEnvironmentVariable("SQLLSP_MCP_CONFIG");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--stdio") continue;
            if (args[i] == "--workspace" && i + 1 < args.Length) workspace = args[++i];
            else if (args[i] == "--config" && i + 1 < args.Length) config = args[++i];
            else throw new ArgumentException("Unknown/missing argument.");
        }
        workspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(workspace)) throw new ArgumentException("Workspace must exist.");
        return new(workspace, string.IsNullOrWhiteSpace(config) ? null : Path.GetFullPath(config));
    }
}

[McpServerToolType]
public sealed class ProcedureTools(McpLaunchOptions launch) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private ServerState? state;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool(Name = "find_procedures", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Find indexed T-SQL procedures, functions and views by name. Returns exact source URI, identity, revision and zero-based UTF-16 selection position. Multiple candidates remain visible; choose the correct source/database/schema before analyzing. Does not execute SQL.")]
    public async Task<CallToolResult> FindProcedures([Description("Name substring; empty lists indexed modules.")] string query = "",
        [Description("Maximum results, 1..100.")] int maxResults = 30, CancellationToken cancellationToken = default)
    {
        if (query.Length > 256 || maxResults is < 1 or > 100) throw new McpException("Query/limit outside supported bounds.");
        return await WithIndex(async index =>
        {
            var symbols = index.Search(query, cancellationToken).Where(s => s.Identity.Kind is SqlObjectKind.Procedure or SqlObjectKind.Function or SqlObjectKind.View)
                .OrderBy(s => s.Target.Uri.AbsoluteUri, StringComparer.Ordinal).ThenBy(s => s.Target.Range.Start.Line).ToArray();
            var modules = symbols.Take(maxResults).Select(symbol =>
            {
                var report = index.AnalyzeProcedure(symbol.Target.Uri, symbol.Target.Range.Start, cancellationToken, 0, 1);
                return new
                {
                    symbol.Identity,
                    symbol.Target,
                    selection = symbol.Target.Range.Start,
                    revision = report.Modules.FirstOrDefault()?.Revision,
                    status = report.Status,
                    signature = symbol.Declaration.Documentation?.Signature
                };
            }).ToArray();
            return await Task.FromResult(Result(new
            {
                source = "Saved workspace/catalog snapshots; this process does not receive LSP unsaved buffers.",
                indexedDocuments = index.DocumentCount,
                sourceStatuses = state!.SourceStatuses,
                sourceIssues = index.SourceParseIssues(cancellationToken),
                modules,
                totalMatches = symbols.Length,
                truncated = symbols.Length > maxResults
            }));
        }, cancellationToken);
    }

    [McpServerTool(Name = "analyze_procedure", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Analyze a verified T-SQL procedure/function/view definition or body. Returns direct table operations, static calls, parameters, source spans and explicit unknowns, with bounded callee/view expansion. This is static possible-effect analysis, not execution or complete runtime impact. No analyzed SQL is executed. Source positions are zero-based UTF-16; use find_procedures first.")]
    public async Task<CallToolResult> AnalyzeProcedure(string documentUri, int line, int character,
        [Description("Call/view expansion levels, 0..4; default 2.")] int maxDepth = 2,
        [Description("Maximum modules including entry point, 1..40; default 20.")] int maxModules = 20,
        [Description("Optional revision from discovery; reject changed source instead of analyzing silently.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(documentUri, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            line < 0 || character < 0 || maxDepth is < 0 or > 4 || maxModules is < 1 or > 40)
            throw new McpException("Expected an indexed file URI, zero-based UTF-16 position and bounded expansion limits.");
        return await WithIndex(index => Task.FromResult(Result(new
        {
            source = "Saved workspace/catalog snapshots; this process does not receive LSP unsaved buffers.",
            indexedDocuments = index.DocumentCount,
            sourceStatuses = state!.SourceStatuses,
            report = index.AnalyzeProcedure(uri, new(line, character), cancellationToken, maxDepth, maxModules, expectedRevision),
            drift = index.Drifts.Where(d => d.Local.Uri == uri).ToArray()
        })), cancellationToken);
    }

    private async Task<CallToolResult> WithIndex(Func<SqlNavigationIndex, Task<CallToolResult>> action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (state == null)
            {
                var candidate = new ServerState();
                try
                {
                    JObject? options = null;
                    if (launch.ConfigurationFile != null)
                    {
                        if (new FileInfo(launch.ConfigurationFile).Length > 1_048_576) throw new McpException("Configuration exceeds size limit.");
                        options = JObject.Parse(await File.ReadAllTextAsync(launch.ConfigurationFile, token));
                    }
                    var config = ServerConfiguration.Parse(options);
                    var rootPath = launch.Workspace.EndsWith(Path.DirectorySeparatorChar) ? launch.Workspace : launch.Workspace + Path.DirectorySeparatorChar;
                    await candidate.InitializeSourcesAsync(config, [new Uri(rootPath)], token);
                    if ((config.Mode != "online" || config.ScanInOnline) && candidate.Files?.Roots.Count != 1)
                        throw new McpException("Workspace is linked or inaccessible; use its physical directory.");
                    state = candidate;
                }
                catch { await candidate.DisposeAsync(); throw; }
            }
            return await action(await state.NavigationAsync(token));
        }
        catch (OperationCanceledException) { throw; }
        catch (McpException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Newtonsoft.Json.JsonException or OmniSharp.Extensions.JsonRpc.RpcErrorException)
        {
            throw new McpException("Analysis sources/configuration are unavailable or invalid; details suppressed.");
        }
        finally { gate.Release(); }
    }

    private static CallToolResult Result(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        using var document = JsonDocument.Parse(json);
        return new() { StructuredContent = document.RootElement.Clone(), Content = [new TextContentBlock { Text = json }] };
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (state != null) await state.DisposeAsync(); }
        finally { gate.Release(); gate.Dispose(); }
    }
}
