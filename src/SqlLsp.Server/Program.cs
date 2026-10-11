using OmniSharp.Extensions.LanguageServer.Server;

if (args.Length != 0 && !args.SequenceEqual(new[] { "--stdio" }))
{
    Console.Error.WriteLine("Usage: sqlscriptdom-lsp [--stdio]");
    return 2;
}

using var server = await LanguageServer.From(options => options
    .WithInput(Console.OpenStandardInput())
    .WithOutput(Console.OpenStandardOutput()));
await server.WaitForExit;
return 0;
