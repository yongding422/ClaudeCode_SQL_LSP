using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SqlLsp.Mcp;

// Small local JSONL segments, independent of report caching. Failures never log project data.
internal sealed class LocalMetricStore(string directory)
{
    private const int MaxFiles = 32, MaxFileBytes = 512 * 1024, MaxRecordBytes = 8192, MaxReadEvents = 16384;
    private readonly string root = Path.Combine(Path.GetFullPath(directory), "metrics-v1");
    private string? currentFile;
    internal bool Append(MetricEvent metric)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metric, ProcedureTools.JsonOptions) + "\n");
            if (bytes.Length > MaxRecordBytes) return false;
            CheckPath(root); PrivateRoot();
            var lockPath = Path.Combine(root, "writer.lock"); CheckPath(lockPath);
            using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (currentFile == null || !File.Exists(currentFile) || new FileInfo(currentFile).Length + bytes.Length > MaxFileBytes)
                currentFile = Path.Combine(root, "session-" + metric.Session + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
            CheckPath(currentFile);
            var files = OwnedFiles();
            // Refuse unexpected excess rather than enumerate/delete an unbounded directory.
            if (files.Length > 128) return false;
            var count = files.Length + (File.Exists(currentFile) ? 0 : 1);
            var total = files.Sum(f => f.Length) + bytes.Length;
            foreach (var file in files.Where(f => f.FullName != currentFile).OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
            {
                if (count <= MaxFiles && total <= (long)MaxFiles * MaxFileBytes) break;
                CheckPath(file.FullName); var length = file.Length; File.Delete(file.FullName); total -= length; count--;
            }
            if (count > MaxFiles || total > (long)MaxFiles * MaxFileBytes) return false;
            using var stream = new FileStream(currentFile, FileMode.Append, FileAccess.Write, FileShare.Read);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(currentFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            stream.Write(bytes);
            return true;
        }
        catch (Exception error) when (Failure(error)) { return false; }
    }

    internal (IReadOnlyList<MetricEvent> Events, int IgnoredRecords, bool WindowLimited, string Status) Read()
    {
        var events = new Queue<MetricEvent>(); var ignored = 0; var limited = false;
        try
        {
            CheckPath(root);
            if (!Directory.Exists(root)) return ([], 0, false, "Empty");
            var files = OwnedFiles();
            limited = files.Length > MaxFiles;
            foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxFiles).Reverse())
            {
                try
                {
                    CheckPath(file.FullName);
                    if (file.Length > MaxFileBytes) { ignored++; continue; }
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    // Bound reads if a concurrent writer grows a file after stat.
                    var buffer = new byte[MaxFileBytes + 1]; var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = stream.Read(buffer.AsSpan(count)); if (read == 0) break; count += read;
                    }
                    if (count > MaxFileBytes) { ignored++; continue; }
                    var text = Encoding.UTF8.GetString(buffer, 0, count);
                    // An interrupted append may leave a final partial line; do not promote it to a valid record.
                    var lines = text.Split('\n');
                    for (var i = 0; i < lines.Length - 1; i++)
                    {
                        try
                        {
                            if (Encoding.UTF8.GetByteCount(lines[i]) > MaxRecordBytes) { ignored++; continue; }
                            var entry = JsonSerializer.Deserialize<MetricEvent>(lines[i], ProcedureTools.JsonOptions);
                            if (entry == null || !Valid(entry)) { ignored++; continue; }
                            events.Enqueue(entry);
                            while (events.Count > MaxReadEvents) { events.Dequeue(); limited = true; }
                        }
                        catch (Exception error) when (Failure(error)) { ignored++; }
                    }
                    if (lines[^1].Length != 0) ignored++;
                }
                catch (Exception error) when (Failure(error)) { ignored++; }
            }
            return (events.OrderBy(e => e.At).ToArray(), ignored, limited, ignored > 0 ? "Partial" : "Ready");
        }
        catch (Exception error) when (Failure(error)) { return ([], ignored, limited, "Unavailable"); }
    }

    private static bool Hex(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
    private static bool Valid(MetricEvent e) => e.Format == 1 && e.Version is { Length: > 0 and < 24 } && e.Version.All(c => char.IsAsciiDigit(c) || c == '.') && Hex(e.Build) && Hex(e.Session) &&
        e.Operation is "find_procedures" or "analyze_procedure" or "build_cache" && e.Detail is "full" or "compact" or "discovery" or "prebuild" or "Invalid" &&
        e.Outcome is "Success" or "Error" or "Canceled" && e.SourceMode is "offline" or "online" or "hybrid" or "NotInitialized" &&
        double.IsFinite(e.DurationMs) && e.DurationMs is >= 0 and <= 1e12 && e.IndexedDocuments is null or >= 0 && e.PayloadUtf8Bytes is null or >= 0 and <= 1L << 32 && e.ReportUtf8Bytes is null or >= 0 and <= 1L << 32 &&
        e.Modules >= 0 && e.Calls >= 0 && e.ResolvedCalls >= 0 && e.ResolvedCalls <= e.Calls && e.Accesses >= 0 && e.ResolvedAccesses >= 0 && e.ResolvedAccesses <= e.Accesses &&
        e.Issues >= 0 && e.TruncatedModules >= 0 && e.CacheEnabled is >= 0 and <= 256 &&
        (e.ResolutionStatus == null || Enum.TryParse<SqlLsp.Core.ResolutionStatus>(e.ResolutionStatus, out var status) && status.ToString() == e.ResolutionStatus) &&
        e.CacheStatuses != null && e.CacheStatuses.Count <= PluginMetrics.CacheNames.Length && e.CacheStatuses.All(p => PluginMetrics.CacheNames.Contains(p.Key, StringComparer.Ordinal) && p.Value is >= 0 and <= 256) &&
        e.Timings != null && e.Timings.Count <= PluginMetrics.PhaseNames.Length && e.Timings.All(p => PluginMetrics.PhaseNames.Contains(p.Key, StringComparer.Ordinal) && double.IsFinite(p.Value) && p.Value is >= 0 and <= 1e12);

    private FileInfo[] OwnedFiles() => new DirectoryInfo(root).EnumerateFiles("session-*.jsonl").Where(f => f.Name.Length == 79 &&
        f.Name[40] == '-' && Hex(f.Name.Substring(8, 32)) && Hex(f.Name.Substring(41, 32))).Take(129).ToArray();

    private void PrivateRoot()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
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
    internal static void CheckPath(string path)
    {
        var absolute = Path.GetFullPath(path);
        for (var current = absolute; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            try
            {
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || current != absolute && !attributes.HasFlag(FileAttributes.Directory)) throw new IOException("Unavailable metric path.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
    }
    private static bool Failure(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or
        ArgumentException or KeyNotFoundException or NotSupportedException or NullReferenceException or FormatException or OverflowException;
}
