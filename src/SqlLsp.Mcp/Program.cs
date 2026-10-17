using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlLsp.Mcp;

try
{
    var launch = McpLaunchOptions.Parse(args);
    if (launch.EvaluationCsv != null)
    {
        var report = System.Text.Json.JsonSerializer.SerializeToElement(EvaluationMetrics.Read(launch.EvaluationCsv), ProcedureTools.JsonOptions);
        Console.WriteLine(report.GetRawText());
        if (report.GetProperty("invalidRows").GetInt32() > 0 || report.GetProperty("duplicatePairKeys").GetInt32() > 0) Environment.ExitCode = 2;
        return;
    }
    if (launch.MetricsSummary)
    {
        var report = launch.CompareMetricsDirectory == null ? MetricReports.Read(launch.MetricsDirectory!) : MetricReports.Compare(launch.MetricsDirectory!, launch.CompareMetricsDirectory);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, ProcedureTools.JsonOptions));
        using var parsed = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(report, ProcedureTools.JsonOptions));
        var summaries = launch.CompareMetricsDirectory == null ? new[] { parsed.RootElement } : new[] { parsed.RootElement.GetProperty("current"), parsed.RootElement.GetProperty("baseline") };
        if (summaries.Any(s => s.GetProperty("storageStatus").GetString() is not ("Ready" or "Empty") || s.GetProperty("windowLimited").GetBoolean())) Environment.ExitCode = 2;
        return;
    }
    if (launch.BuildCache)
    {
        await using var prewarm = new ProcedureTools(launch);
        var result = await prewarm.BuildCache(CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(result.StructuredContent, ProcedureTools.JsonOptions);
        Console.WriteLine(json);
        using var summary = System.Text.Json.JsonDocument.Parse(json);
        var data = summary.RootElement;
        if (!data.GetProperty("sourcesAvailable").GetBoolean() || data.GetProperty("truncated").GetBoolean() || data.GetProperty("sourceIssues").GetArrayLength() != 0 ||
            data.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("status").GetString() != "Resolved" || !e.GetProperty("retained").GetBoolean()))
            Environment.ExitCode = 2;
        return;
    }
    if (launch.AutoInitializeSettings) PluginSettings.EnsureStarter(launch.Workspace);
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(launch);
    // The type-only SDK overload constructs/disposes a new target per invocation.
    // Keep one owned target so discovery and analysis share saved snapshots/watchers.
    await using var tools = new ProcedureTools(launch);
    builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "sqlscriptdom-analysis", Version = PluginMetrics.Version };
            options.ServerInstructions = "Use file Read/Grep for exact literals and ad-hoc scripts. For a known SQL module, analyze its file/declaration directly; discover only unknown locations, then copy moduleRef. When the developer specifies an application folder, use its scopeUri to exclude unrelated projects, especially in SQL-only copies. Compact defaults use small expansion; expand only relevant reportRef sections and source evidence. Lists are static extracted facts, never proof that triggers, CLR, dynamic SQL or implicit engine effects are absent. MCP works independently of LSP and never executes SQL.";
        })
        .WithStdioServerTransport().WithTools(tools);
    await builder.Build().RunAsync();
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or ModelContextProtocol.McpException or Microsoft.VisualBasic.FileIO.MalformedLineException)
{
    Console.Error.WriteLine("SQL adapter command failed: invalid options/settings or unavailable sources/metrics.");
    Environment.ExitCode = 1;
}
