using System.Text.Json.Serialization;

namespace Csaver.Core;

public enum OperationKind
{
    ExplainConfiguration,
    PreviewMigration,
    Migrate,
    PreviewRepair,
    Repair,
    ListRollback,
    PreviewRollback,
    Rollback,
    ViewQuarantine,
    RecoverIncompleteMigration,
    Health
}

public enum RepairPolicy { KeepBoth, Newer, SourceWins, DestWins }

public enum IssueSeverity { Information, Warning, Error }

public sealed class MigrationConfiguration
{
    public string? DestRoot { get; set; }
    public List<MigrationTaskDefinition> Tasks { get; set; } = [];
}

public sealed class MigrationTaskDefinition
{
    public string? TaskId { get; set; }
    public string Group { get; set; } = string.Empty;
    public string? Root { get; set; }
    public string? Rel { get; set; }
    public string? Source { get; set; }
    public string? Dest { get; set; }
    public string? Guard { get; set; }
    public bool Disabled { get; set; }
    public List<string> TrustedReparsePoints { get; set; } = [];
}

public sealed record ValidationIssue(
    string Code,
    string Message,
    IssueSeverity Severity = IssueSeverity.Error,
    string? TaskId = null,
    bool Retryable = false);

public sealed class ConfigurationLoadResult
{
    public MigrationConfiguration? Configuration { get; init; }
    public List<ValidationIssue> Issues { get; init; } = [];
    public string RawJson { get; init; } = string.Empty;
    public bool IsValid => Configuration is not null && Issues.All(x => x.Severity != IssueSeverity.Error);
}

public sealed class ResolvedMigrationTask
{
    public string TaskId { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string DestinationSource { get; init; } = string.Empty;
    public string DestinationRoot { get; init; } = string.Empty;
    public string Guard { get; init; } = string.Empty;
    public bool Disabled { get; init; }
    public bool IsDynamic { get; init; }
    public bool IsBroadRoot { get; init; }
    public List<string> TrustedReparsePoints { get; init; } = [];
}

public sealed class FrozenTask
{
    public string TaskId { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string DestinationSource { get; init; } = string.Empty;
    public string DestinationRoot { get; init; } = string.Empty;
    public bool Disabled { get; init; }
    public bool IsDynamic { get; init; }
    public bool IsBroadRoot { get; init; }
    public List<string> TrustedReparsePoints { get; init; } = [];
}

public sealed class TaskInspection
{
    public required ResolvedMigrationTask Task { get; init; }
    public string PathState { get; init; } = "Unknown";
    public long? FileCount { get; init; }
    public long? SizeBytes { get; init; }
    public List<string> ReparsePoints { get; init; } = [];
    public List<ValidationIssue> Issues { get; init; } = [];
    public bool CanMutate => Issues.All(x => x.Severity != IssueSeverity.Error);
}

public sealed record DirectoryStatistics(long FileCount, long Bytes);

public sealed record DirectoryScanResult(
    long FileCount,
    long Bytes,
    IReadOnlyList<string> UndeclaredReparsePoints,
    IReadOnlyList<string> DeclaredReparsePoints);

public sealed class SnapshotEntry
{
    public string RelativePath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Length { get; init; }
    public long CreationUtcTicks { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public long LastAccessUtcTicks { get; init; }
    public FileAttributes Attributes { get; init; }
    public string AclSddl { get; init; } = string.Empty;
    public string ContentSha256 { get; init; } = string.Empty;
}

public sealed class DirectorySnapshot
{
    public string Root { get; init; } = string.Empty;
    public string VerificationTier { get; init; } = "StructuralMetadata";
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<SnapshotEntry> Entries { get; init; } = [];
    public List<string> ReadErrors { get; init; } = [];
    public long FileCount => Entries.LongCount(x => !x.IsDirectory);
    public long Bytes => Entries.Where(x => !x.IsDirectory).Sum(x => x.Length);
    public bool Success => ReadErrors.Count == 0;
}

public sealed class SnapshotComparison
{
    public bool Success { get; init; }
    public int MissingCount { get; init; }
    public int MismatchCount { get; init; }
    public int DestinationOnlyCount { get; init; }
    public List<string> Details { get; init; } = [];
}

public sealed class OperationRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N");
    public OperationKind Kind { get; init; }
    public string BaseDirectory { get; init; } = string.Empty;
    public string ConfigurationPath { get; init; } = string.Empty;
    public string ConfigurationSha256 { get; init; } = string.Empty;
    public List<string> SelectedTaskIds { get; init; } = [];
    public List<string> SelectedHistoryIds { get; init; } = [];
    public List<FrozenTask> FrozenTasks { get; init; } = [];
    public string ResolvedTaskSetHash { get; init; } = string.Empty;
    public bool BroadRootAcknowledged { get; init; }
    public RepairPolicy RepairPolicy { get; init; } = RepairPolicy.KeepBoth;
    public bool AbortOnConflict { get; init; }
    public string Nonce { get; init; } = string.Empty;
}

public sealed class OperationProgress
{
    public string OperationId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public double? Percent { get; init; }
    public bool CriticalSection { get; init; }
}

public sealed class OperationTaskResult
{
    public string TaskId { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string DestinationSource { get; init; } = string.Empty;
    public bool Disabled { get; init; }
    public bool IsDynamic { get; init; }
    public bool IsBroadRoot { get; init; }
    public bool BroadRootAcknowledged { get; set; }
    public string PathState { get; init; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string TransactionState { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool Retryable { get; set; }
    public long? FileCount { get; set; }
    public long? SizeBytes { get; set; }
    public string JournalPath { get; set; } = string.Empty;
    public string QuarantinePath { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public int ConflictCount { get; set; }
    public string VerificationTier { get; set; } = "StructuralMetadata";
}

public sealed class OperationResult
{
    public string OperationId { get; init; } = string.Empty;
    public OperationKind Kind { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public bool StateUnknown { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<OperationTaskResult> Tasks { get; init; } = [];
}

public sealed class WorkerEnvelope
{
    public string Type { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public OperationProgress? Progress { get; init; }
    public OperationResult? Result { get; init; }
    public string? Error { get; init; }
}

public sealed class NativeJournal
{
    public int SchemaVersion { get; init; } = 1;
    public string OperationId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string CanonicalSource { get; init; } = string.Empty;
    public string CanonicalDest { get; init; } = string.Empty;
    public string State { get; set; } = "PreflightPassed";
    public bool Retryable { get; set; } = true;
    public string LastError { get; set; } = string.Empty;
    public string CStage { get; init; } = string.Empty;
    public string QuarantinePath { get; set; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public string VerificationTier { get; init; } = "StructuralMetadata";
    public string DestinationSource { get; init; } = string.Empty;
    public bool BroadRootAcknowledged { get; init; }
    public int ConflictCount { get; set; }
    public string RecoveryPath { get; set; } = string.Empty;
    public string RecoveryMetadata { get; set; } = string.Empty;
    public DirectorySnapshot? SourceSnapshot { get; set; }
    public DirectorySnapshot? DestinationSnapshot { get; set; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HistoryRecord
{
    public int SchemaVersion { get; init; } = 1;
    public string HistoryId { get; init; } = Guid.NewGuid().ToString("N");
    public string OperationId { get; init; } = string.Empty;
    public OperationKind Kind { get; init; }
    public string TaskId { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string CanonicalSource { get; init; } = string.Empty;
    public string CanonicalDestination { get; init; } = string.Empty;
    public string DestinationSource { get; init; } = string.Empty;
    public bool BroadRootAcknowledged { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public long? FileCount { get; init; }
    public long? SizeBytes { get; init; }
    public string Status { get; init; } = string.Empty;
    public string TransactionState { get; init; } = string.Empty;
    public string VerificationTier { get; init; } = "StructuralMetadata";
    public string VerificationResult { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public bool Retryable { get; init; }
    public bool RollbackEligible { get; init; }
    public string JournalPath { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public string QuarantinePath { get; init; } = string.Empty;
    public int ConflictCount { get; init; }
    public string ResolvedTaskSetHash { get; init; } = string.Empty;
    public bool Imported { get; init; }
    [JsonIgnore]
    public TimeSpan Duration => CompletedUtc > StartedUtc ? CompletedUtc - StartedUtc : TimeSpan.Zero;
}

public sealed class QuarantineEntry
{
    public string QuarantineId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string OriginalPath { get; init; } = string.Empty;
    public string StoredPath { get; init; } = string.Empty;
    public DateTimeOffset? CreatedUtc { get; init; }
    public long? SizeBytes { get; init; }
    public string State { get; init; } = string.Empty;
    public string JournalState { get; init; } = string.Empty;
    public bool PayloadExists { get; init; }
    public string Error { get; init; } = string.Empty;
}
