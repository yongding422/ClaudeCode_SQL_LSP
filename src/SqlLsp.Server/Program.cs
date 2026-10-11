using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using OmniSharp.Extensions.LanguageServer.Server;
using SqlLsp.Server;

if (args.Length != 0 && !args.SequenceEqual(new[] { "--stdio" }))
{
    Console.Error.WriteLine("Usage: sqlscriptdom-lsp [--stdio]");
    return 2;
}

await using var state = new ServerState();
using var server = await LanguageServer.From(options => options
    .WithInput(Console.OpenStandardInput())
    .WithOutput(Console.OpenStandardOutput())
    .WithServices(services => services.AddSingleton(state))
    .WithHandler<SqlTextSyncHandler>()
    .WithHandler<SqlDocumentSymbolHandler>()
    .OnInitialize(async (facade, request, token) =>
    {
        try { await state.InitializeAsync(facade, request, token); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Initialization failed: {error.GetType().Name}: {error.InnerException?.Message ?? error.Message}");
            throw;
        }
    })
    .OnInitialized((facade, request, response, token) =>
    {
        response.Capabilities.PositionEncoding = PositionEncodingKind.UTF16;
        if (state.ConfigurationError != null)
        {
            response.Capabilities.DocumentSymbolProvider = false;
            response.Capabilities.TextDocumentSync = null;
        }
        return Task.CompletedTask;
    })
    .OnStarted((facade, token) =>
    {
        if (state.ConfigurationError != null)
            facade.Window.ShowMessage(new ShowMessageParams { Type = MessageType.Error, Message = state.ConfigurationError });
        return Task.CompletedTask;
    }));
await server.WaitForExit;
return 0;
