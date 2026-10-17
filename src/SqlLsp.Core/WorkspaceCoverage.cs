namespace SqlLsp.Core;

// Detail is explicitly paged; aggregate telemetry never contains SQL, paths or exception messages.
public sealed record WorkspaceFileCoverage(Uri Uri, string Status, long Bytes = 0, string? Encoding = null,
    int ParseErrors = 0, string? FailureKind = null);
public sealed record WorkspaceCoverage(int Discovered, int Indexed, int ParseErrorFiles, int ParseErrors,
    int ExcludedSize, int ExcludedPolicy, int Unreadable, int Failed, int Deleted, int Pending,
    long ReadBytes, long ParseAttempts, double EnumerationMs, double ReadMs, double ParseMs,
    string EnumerationLimit = "Excluded/linked directories are not traversed; inaccessible directory contents cannot be counted.");
