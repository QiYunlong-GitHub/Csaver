namespace Csaver.Core;

public interface IJunctionService
{
    JunctionInfo Inspect(string path);
    void Create(string path, string target);
    void Delete(string path);
}

public sealed record JunctionInfo(bool Exists, bool IsDirectory, bool IsReparsePoint, bool IsJunction, string? Target, string? Error = null);

public interface ITreeService
{
    DirectoryStatistics GetStatistics(string root, CancellationToken cancellationToken = default);
    /// <summary>Read-only scan that never descends into reparse points and stops at the first undeclared link.</summary>
    DirectoryScanResult GetStatisticsSkippingReparsePoints(string root, IReadOnlyCollection<string> trustedReparsePoints, CancellationToken cancellationToken = default);
    DirectorySnapshot CaptureSnapshot(string root, CancellationToken cancellationToken = default);
    SnapshotComparison Compare(DirectorySnapshot expected, DirectorySnapshot actual, bool allowDestinationOnly, bool ignoreLastAccess = true);
    void CopyPreserving(string source, string destination, IProgress<OperationProgress>? progress = null, string operationId = "", string taskId = "", CancellationToken cancellationToken = default);
}

public interface IOperationEngine
{
    Task<OperationResult> ExecuteAsync(OperationRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
}
