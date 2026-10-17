using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace SqlLsp.Mcp.IntegrationTests;

// Actual newline-delimited MCP JSON-RPC over the shipped stdio entry point, independent of SDK helpers.
internal sealed class McpClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly Channel<JsonElement> messages = Channel.CreateUnbounded<JsonElement>();
    private readonly Task reader;
    private readonly Task<string> stderr;
    private int id;
    private readonly bool preserveWorkspace;
    public string Root { get; } = Path.Combine(TestPaths.TempRoot, "sql-mcp 中文 " + Guid.NewGuid().ToString("N"));
    public string SourceFile => Path.Combine(Root, "modules.sql");
    private McpClient(Process process, string root, bool preserveWorkspace)
    {
        this.process = process; Root = root; this.preserveWorkspace = preserveWorkspace;
        stderr = process.StandardError.ReadToEndAsync();
        reader = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    using var json = JsonDocument.Parse(line); // Any non-protocol stdout fails the test.
                    await messages.Writer.WriteAsync(json.RootElement.Clone());
                }
                messages.Writer.TryComplete();
            }
            catch (Exception error) { messages.Writer.TryComplete(error); }
        });
    }
    public static async Task<McpClient> Start(string sql, string? configuration = null, string? cacheDirectory = null,
        string? rootDirectory = null, bool preserveWorkspace = false, string? metricsDirectory = null, bool metricsViaEnvironment = false,
        string? settingsFile = null)
    {
        var root = rootDirectory ?? Path.Combine(TestPaths.TempRoot, "sql-mcp 中文 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "modules.sql"), sql);
        var info = Command(root);
        info.ArgumentList.Add("--stdio");
        if (settingsFile != null) { info.ArgumentList.Add("--settings"); info.ArgumentList.Add(settingsFile); }
        if (cacheDirectory != null) { info.ArgumentList.Add("--cache-directory"); info.ArgumentList.Add(cacheDirectory); }
        if (metricsDirectory != null)
        {
            if (metricsViaEnvironment) info.Environment["SQLLSP_METRICS_DIR"] = metricsDirectory;
            else { info.ArgumentList.Add("--metrics-directory"); info.ArgumentList.Add(metricsDirectory); }
        }
        if (configuration != null)
        {
            var config = Path.Combine(root, "config.json");
            await File.WriteAllTextAsync(config, configuration);
            info.ArgumentList.Add("--config"); info.ArgumentList.Add(config);
        }
        var process = Process.Start(info)!;
        var client = new McpClient(process, root, preserveWorkspace);
        try
        {
            var response = await client.Call("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "independent-wire-tests", version = "1" } });
            Assert.True(response.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _));
            await client.Notify("notifications/initialized", new { });
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    internal static ProcessStartInfo Command(string root)
    {
        var exe = Environment.GetEnvironmentVariable("SQLLSP_TEST_MCP_EXE");
        var info = new ProcessStartInfo(exe ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root
        };
        if (exe == null)
        {
            var repo = Directory.GetParent(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "SqlScriptDomLsp.sln"))) repo = repo.Parent;
            Assert.NotNull(repo);
            var configurationName = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            info.ArgumentList.Add(Path.Combine(repo!.FullName, "src", "SqlLsp.Mcp", "bin", configurationName, "net10.0", "sqlscriptdom-mcp.dll"));
        }
        info.Environment.Remove("SQLLSP_MCP_CONFIG");
        info.Environment.Remove("SQLLSP_ANALYSIS_CACHE_DIR");
        info.Environment.Remove("SQLLSP_METRICS_DIR");
        return info;
    }
    public Task<JsonElement> Tool(string name, object arguments) => Call("tools/call", new { name, arguments });
    public async Task<JsonElement> Call(string method, object parameters)
    {
        var requestId = ++id;
        await Send(new { jsonrpc = "2.0", id = requestId, method, @params = parameters });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await messages.Reader.WaitToReadAsync(timeout.Token))
            while (messages.Reader.TryRead(out var message))
                if (message.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == requestId) return message;
        throw new IOException("Adapter exited before replying.");
    }
    public Task Notify(string method, object parameters) => Send(new { jsonrpc = "2.0", method, @params = parameters });
    private async Task Send(object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }
    public static JsonElement Data(JsonElement message)
    {
        var result = message.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.ToString());
        var content = result.GetProperty("content")[0].GetProperty("text").GetString();
        using var document = JsonDocument.Parse(content!);
        Assert.True(JsonElement.DeepEquals(document.RootElement, result.GetProperty("structuredContent")), "Structured and text JSON must have equal values, allowing equivalent Unicode/date escaping.");
        return document.RootElement.Clone();
    }
    public async ValueTask DisposeAsync()
    {
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        await reader;
        var error = await stderr;
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", error);
        Assert.Equal(0, process.ExitCode);
        process.Dispose();
        if (!preserveWorkspace) Directory.Delete(Root, true);
    }
}
