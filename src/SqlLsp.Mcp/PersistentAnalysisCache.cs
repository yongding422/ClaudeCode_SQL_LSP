using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlLsp.Core;

namespace SqlLsp.Mcp;

internal sealed record AnalysisCacheUse(bool Enabled, string Status, string? File = null, DateTimeOffset? CreatedAt = null);

// Explicit opt-in, process-private data permissions, bounded JSON, atomic replacement, no AST deserialization.
internal sealed class PersistentAnalysisCache(string directory)
{
    private const int MaxEntryBytes = 8 * 1024 * 1024;
    private const long MaxTotalBytes = 128L * 1024 * 1024;
    private const int MaxEntries = 256;
    private readonly string root = Path.Combine(Path.GetFullPath(directory), "analysis-v1");

    public async Task<(SqlProcedureReport? Report, AnalysisCacheUse Use)> ReadAsync(SqlAnalysisCacheContext context,
        string key, CancellationToken token)
    {
        var path = Path.Combine(root, "report-" + key + ".json");
        try
        {
            CheckPath(root);
            if (Directory.Exists(root)) CreatePrivateRoot();
            if (!File.Exists(path)) return (null, new(true, "Miss", path));
            CheckPath(path);
            var info = new FileInfo(path);
            if (info.Length is < 1 or > MaxEntryBytes) return (null, new(true, "Invalid", path));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            // Bound the read even if another process replaces/grows a cache file after stat.
            using var memory = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, token)) != 0)
            {
                if (memory.Length + count > MaxEntryBytes) return (null, new(true, "Invalid", path));
                memory.Write(buffer, 0, count);
            }
            var envelope = JsonNode.Parse(memory.ToArray())!.AsObject();
            if (envelope["format"]?.GetValue<int>() != 1 || envelope["key"]?.GetValue<string>() != key ||
                envelope["sourceFingerprint"]?.GetValue<string>() != context.Fingerprint || envelope["report"] is not JsonObject report ||
                envelope["reportHash"]?.GetValue<string>() != SqlAnalysisCacheContext.Hash(report.ToJsonString()))
                return (null, new(true, "Invalid", path));
            var restored = context.Restore(report);
            if (restored.Status != ResolutionStatus.Resolved || restored.Modules.IsDefaultOrEmpty)
                return (null, new(true, "Invalid", path));
            var created = envelope["createdAt"]!.GetValue<DateTimeOffset>();
            // Last-write time is used only for eviction, never to establish source freshness.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return (restored, new(true, "Hit", path, created));
        }
        catch (Exception error) when (CacheFailure(error)) { return (null, new(true, "UnavailableOrInvalid", path)); }
    }

    public async Task<AnalysisCacheUse> WriteAsync(SqlAnalysisCacheContext context, string key, SqlProcedureReport report, CancellationToken token)
    {
        var path = Path.Combine(root, "report-" + key + ".json");
        if (report.Status != ResolutionStatus.Resolved || report.Modules.IsDefaultOrEmpty) return new(true, "NotStored", path);
        string? temporary = null;
        try
        {
            CheckPath(root);
            CreatePrivateRoot();
            var lockPath = Path.Combine(root, "cache.lock");
            CheckPath(lockPath);
            // Competing writers can bypass persistence, but cannot race eviction beyond the disk budget.
            using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var template = context.Normalize(report);
            var created = DateTimeOffset.UtcNow;
            var envelope = new JsonObject
            {
                ["format"] = 1,
                ["key"] = key,
                ["sourceFingerprint"] = context.Fingerprint,
                ["createdAt"] = created,
                ["reportHash"] = SqlAnalysisCacheContext.Hash(template.ToJsonString()),
                ["report"] = template
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SqlAnalysisCacheContext.JsonOptions);
            if (bytes.Length > MaxEntryBytes) return new(true, "TooLarge", path);
            Prune(bytes.Length, path, token);
            temporary = Path.Combine(root, "write-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            CheckPath(path);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return new(true, "Stored", path, created);
        }
        catch (Exception error) when (CacheFailure(error)) { return new(true, "UnavailableOrInvalid", path); }
        finally
        {
            if (temporary != null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void CreatePrivateRoot()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (Directory.Exists(root)) new DirectoryInfo(root).SetAccessControl(security);
            else new DirectoryInfo(root).Create(security);
        }
        else
        {
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void Prune(int incomingBytes, string replacing, CancellationToken token)
    {
        // The exclusive writer lease means these can only be leftovers of an interrupted earlier write.
        foreach (var abandoned in new DirectoryInfo(root).EnumerateFiles("write-*.tmp").Where(f => f.Name.Length == 42 &&
            f.Name.AsSpan(6, 32).ToString().All(char.IsAsciiHexDigit)))
        {
            token.ThrowIfCancellationRequested(); CheckPath(abandoned.FullName); File.Delete(abandoned.FullName);
        }
        var files = new DirectoryInfo(root).EnumerateFiles("report-*.json").Where(f =>
            f.Name.Length == 76 && f.Name.AsSpan(7, 64).ToString().All(c => char.IsAsciiHexDigit(c)) && f.FullName != replacing).ToArray();
        var bytes = files.Sum(f => f.Length);
        var count = files.Length;
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (count < MaxEntries && bytes + incomingBytes <= MaxTotalBytes) break;
            CheckPath(file.FullName);
            var length = file.Length;
            File.Delete(file.FullName);
            bytes -= length; count--;
        }
    }

    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked cache paths are unsupported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static bool CacheFailure(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or
        InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or NotSupportedException or NullReferenceException;
}
