using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlLsp.Mcp;

try
{
    var launch = McpLaunchOptions.Parse(args);
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(launch);
    builder.Services.AddSingleton<ProcedureTools>();
    builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "sqlscriptdom-analysis", Version = "0.7.0" })
        .WithStdioServerTransport().WithTools<ProcedureTools>();
    await builder.Build().RunAsync();
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("SQL analysis adapter could not start: invalid launch options or inaccessible workspace/configuration.");
    Environment.ExitCode = 1;
}
