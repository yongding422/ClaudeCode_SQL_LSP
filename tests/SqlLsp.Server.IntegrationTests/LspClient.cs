using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SqlLsp.Server.IntegrationTests;

// Independent wire client: no OmniSharp types or in-process server shortcuts.
internal sealed class LspClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly Task reader;
    private readonly SemaphoreSlim writer = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> requests = new();
    private readonly Channel<JsonElement> notifications = Channel.CreateUnbounded<JsonElement>();
    private int nextId;

    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SqlScriptDomLsp.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    public LspClient(params string[] args)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryRoot
        };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot, "src", "SqlLsp.Server", "bin", configuration,
            "net10.0", "sqlscriptdom-lsp.dll"));
        foreach (var argument in args.Length == 0 ? new[] { "--stdio" } : args) start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new InvalidOperationException("Server launch failed.");
        stderr = process.StandardError.ReadToEndAsync();
        reader = ReadLoopAsync();
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, bool fragmented = false)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests[id] = completion;
        await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, fragmented);
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    public Task NotifyAsync(string method, object? parameters) =>
        SendAsync(new { jsonrpc = "2.0", method, @params = parameters });

    public async Task<JsonElement> DiagnosticsAsync(string uri, int? version = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var notification = await notifications.Reader.ReadAsync(timeout.Token);
            if (notification.GetProperty("method").GetString() != "textDocument/publishDiagnostics") continue;
            var parameters = notification.GetProperty("params");
            if (parameters.GetProperty("uri").GetString() == uri &&
                (version == null || parameters.GetProperty("version").GetInt32() == version)) return parameters;
        }
    }

    public async Task<JsonElement> InitializeAsync(string? rootUri = null, object? initializationOptions = null,
        bool hierarchical = true) => await RequestAsync("initialize", new
        {
            processId = (int?)null,
            rootUri,
            capabilities = new
            {
                general = new { positionEncodings = new[] { "utf-8", "utf-16" } },
                textDocument = new { documentSymbol = new { hierarchicalDocumentSymbolSupport = hierarchical } }
            },
            initializationOptions
        }, fragmented: true);

    public async Task StopAsync()
    {
        var response = await RequestAsync("shutdown", null);
        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
        await NotifyAsync("exit", null);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await reader;
        Assert.Equal(0, process.ExitCode);
    }

    private async Task SendAsync(object message, bool fragmented = false)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var frame = header.Concat(body).ToArray();
        await writer.WaitAsync();
        try
        {
            var stream = process.StandardInput.BaseStream;
            if (fragmented)
            {
                for (var offset = 0; offset < frame.Length; offset += 7)
                {
                    await stream.WriteAsync(frame.AsMemory(offset, Math.Min(7, frame.Length - offset)));
                    await stream.FlushAsync();
                }
            }
            else { await stream.WriteAsync(frame); await stream.FlushAsync(); }
        }
        finally { writer.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var stream = process.StandardOutput.BaseStream;
            var one = new byte[1];
            while (true)
            {
                var header = new List<byte>();
                while (true)
                {
                    if (await stream.ReadAsync(one) == 0)
                    {
                        if (header.Count != 0) throw new EndOfStreamException("Truncated LSP header.");
                        return;
                    }
                    header.Add(one[0]);
                    if (header.Count > 8192) throw new InvalidDataException("Unframed stdout.");
                    if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var lengthLine = lines.Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var body = new byte[int.Parse(lengthLine.Split(':')[1].Trim())];
                await stream.ReadExactlyAsync(body);
                using var document = JsonDocument.Parse(body);
                var message = document.RootElement.Clone();
                Assert.Equal("2.0", message.GetProperty("jsonrpc").GetString());
                if (message.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) &&
                    requests.TryRemove(number, out var completion)) completion.SetResult(message);
                else await notifications.Writer.WriteAsync(message);
            }
        }
        catch (Exception error)
        {
            foreach (var pending in requests.Values) pending.TrySetException(error);
            notifications.Writer.TryComplete(error);
            throw;
        }
        finally
        {
            foreach (var pending in requests.Values) pending.TrySetException(new EndOfStreamException("Server stopped."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        var logs = await stderr;
        try { await reader; }
        catch (Exception error) { throw new InvalidOperationException($"LSP read failed. stderr: {logs}", error); }
        process.Dispose();
        writer.Dispose();
    }
}
