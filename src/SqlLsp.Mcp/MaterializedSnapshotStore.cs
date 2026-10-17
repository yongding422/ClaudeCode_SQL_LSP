using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SqlLsp.Core;

namespace SqlLsp.Mcp;

internal sealed record MaterializedSnapshot(int Format, string Configuration, SqlMaterializedFile[] Files, string Hash);

// One immutable bounded snapshot per physical workspace; contention bypasses persistence.
internal sealed class MaterializedSnapshotStore(string directory, string workspace)
{
    private const int MaxBytes = 32 * 1024 * 1024;
    private const int MaxDecodedBytes = 128 * 1024 * 1024;
    private const long MaxTotalBytes = 128L * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetFullPath(directory), "index-v1");
    private readonly string name = "workspace-" + SqlAnalysisCacheContext.Hash(Path.GetFullPath(workspace)) + ".json.gz";
    public string Status { get; private set; } = "NotRead";
    private string PathName => Path.Combine(root, name);

    public async Task<SqlMaterializedFile[]> ReadAsync(string configuration, CancellationToken token)
    {
        try
        {
            LocalMetricStore.CheckPath(root);
            LocalMetricStore.CheckPath(PathName);
            if (!File.Exists(PathName)) { Status = "Miss"; return []; }
            await using var stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is < 1 or > MaxBytes) { Status = "Invalid"; return []; }
            using var memory = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, token)) != 0)
            {
                if (memory.Length + count > MaxBytes) { Status = "Invalid"; return []; }
                memory.Write(buffer, 0, count);
            }
            memory.Position = 0;
            using var compressed = new GZipStream(memory, CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            while ((count = await compressed.ReadAsync(buffer, token)) != 0)
            {
                if (decoded.Length + count > MaxDecodedBytes) { Status = "Invalid"; return []; }
                decoded.Write(buffer, 0, count);
            }
            var snapshot = JsonSerializer.Deserialize<MaterializedSnapshot>(decoded.GetBuffer().AsSpan(0, (int)decoded.Length), ProcedureTools.JsonOptions);
            if (snapshot is not { Format: 1 } || snapshot.Configuration != configuration || snapshot.Files.Length > 10000 ||
                snapshot.Hash != Hash(snapshot.Files) || snapshot.Files.Any(f => !f.Uri.IsFile || f.RawHash.Length != 64 || f.Declarations.IsDefault || f.Diagnostics.IsDefault ||
                    f.Semantics == null || f.Semantics.References.IsDefault || f.Semantics.Locals.IsDefault || f.Declarations.Any(d => d == null) || f.Rules == null))
            { Status = "InvalidOrIncompatible"; return []; }
            Status = "Read";
            return snapshot.Files;
        }
        catch (Exception error) when (Failure(error)) { Status = "UnavailableOrInvalid"; return []; }
    }

    public async Task WriteAsync(string configuration, SqlMaterializedFile[] files, CancellationToken token)
    {
        if (files.Length > 10000) { Status = "TooLarge"; return; }
        string? temporary = null;
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new MaterializedSnapshot(1, configuration, files, Hash(files)), ProcedureTools.JsonOptions);
            if (json.Length > MaxDecodedBytes) { Status = "TooLarge"; return; }
            using var encoded = new MemoryStream();
            using (var compressor = new GZipStream(encoded, CompressionLevel.Fastest, leaveOpen: true)) await compressor.WriteAsync(json, token);
            var bytes = encoded.ToArray();
            if (bytes.Length > MaxBytes) { Status = "TooLarge"; return; }
            LocalMetricStore.CheckPath(root);
            PrivateRoot();
            var lockPath = Path.Combine(root, "index.lock");
            LocalMetricStore.CheckPath(lockPath);
            using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            // Only owned immutable snapshots/temp names can be evicted, under the shared writer lease.
            foreach (var abandoned in new DirectoryInfo(root).EnumerateFiles("write-*.tmp").Where(f => f.Name.Length == 42 && f.Name.AsSpan(6, 32).ToString().All(char.IsAsciiHexDigit)))
            { LocalMetricStore.CheckPath(abandoned.FullName); abandoned.Delete(); }
            var retained = new DirectoryInfo(root).EnumerateFiles("workspace-*.json.gz").Where(f =>
                f.Name.Length == 82 && f.Name.AsSpan(10, 64).ToString().All(char.IsAsciiHexDigit) && f.Name != name).ToList();
            var total = retained.Sum(f => f.Length);
            var count = retained.Count;
            foreach (var old in retained.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (count < 8 && total + bytes.Length <= MaxTotalBytes) break;
                LocalMetricStore.CheckPath(old.FullName);
                total -= old.Length;
                count--;
                old.Delete();
            }
            temporary = Path.Combine(root, "write-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            LocalMetricStore.CheckPath(PathName);
            File.Move(temporary, PathName, overwrite: true);
            temporary = null;
            Status = "Stored";
        }
        catch (Exception error) when (Failure(error)) { Status = "WriteUnavailable"; }
        finally
        {
            if (temporary != null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Hash(SqlMaterializedFile[] files) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(files, ProcedureTools.JsonOptions)));
    private void PrivateRoot()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (Directory.Exists(root)) new DirectoryInfo(root).SetAccessControl(security); else new DirectoryInfo(root).Create(security);
        }
        else
        {
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
    private static bool Failure(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
        InvalidOperationException or NullReferenceException or NotSupportedException or FormatException or KeyNotFoundException or InvalidDataException;
}
