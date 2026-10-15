using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlLsp.Mcp;

try
{
    var launch = McpLaunchOptions.Parse(args);
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
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(launch);
    // The type-only SDK overload constructs/disposes a new target per invocation.
    // Keep one owned target so discovery and analysis share saved snapshots/watchers.
    await using var tools = new ProcedureTools(launch);
    builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "sqlscriptdom-analysis", Version = "0.11.0" })
        .WithStdioServerTransport().WithTools(tools);
    await builder.Build().RunAsync();
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or ModelContextProtocol.McpException)
{
    Console.Error.WriteLine("SQL analysis adapter could not start: invalid launch options or inaccessible workspace/configuration.");
    Environment.ExitCode = 1;
}
