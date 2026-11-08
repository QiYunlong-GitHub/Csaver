using System.Diagnostics;
using System.Text.RegularExpressions;
using Csaver.Core;

namespace Csaver.Application;

public sealed class TaskInspectionService(IJunctionService junctions, ITreeService trees)
{
    public TaskInspection Inspect(ResolvedMigrationTask task, bool mutating, bool broadRootAcknowledged,
        CancellationToken cancellationToken = default, IReadOnlyCollection<string>? trustedReparsePoints = null)
    {
        var issues = new List<ValidationIssue>();
        var reparsePoints = new List<string>();
        string source;
        string destination;
        try
        {
            source = TaskResolver.ExpandPath(task.Source);
            destination = TaskResolver.ExpandPath(task.Destination);
        }
        catch (Exception ex)
        {
            issues.Add(new("CanonicalPathUnavailable", ex.Message, TaskId: task.TaskId));
            return new TaskInspection { Task = task, PathState = "Invalid", Issues = issues };
        }

        var relation = TaskResolver.Relation(source, destination);
        if (relation != "Disjoint") issues.Add(new("SourceDestinationOverlap", $"Unsafe path relationship: {relation}.", TaskId: task.TaskId));
        InspectAncestors(source, includeFinal: false, task.TaskId, issues);
        InspectAncestors(destination, includeFinal: true, task.TaskId, issues);
        if (task.IsBroadRoot)
        {
            var severity = mutating && !broadRootAcknowledged ? IssueSeverity.Error : IssueSeverity.Warning;
            issues.Add(new("BroadRootConfirmationRequired", "Broad root may include unrelated application or personal data.", severity, task.TaskId, true));
        }

        var trusted = trustedReparsePoints ?? task.TrustedReparsePoints;
        var sourceInfo = junctions.Inspect(source);
        var state = "Absent";
        long? count = null;
        long? bytes = null;
        if (sourceInfo.Exists)
        {
            if (!sourceInfo.IsDirectory) issues.Add(new("SourceNotDirectory", "Source is not a directory.", TaskId: task.TaskId));
            else if (sourceInfo.IsReparsePoint)
            {
                if (!sourceInfo.IsJunction) issues.Add(new("UnsafeReparsePoint", "Source is a non-Junction reparse point.", TaskId: task.TaskId));
                else if (!PathsEqual(sourceInfo.Target, destination)) issues.Add(new("JunctionTargetMismatch", "Source Junction points to a different target.", TaskId: task.TaskId));
                else state = "HealthyJunction";
            }
            else
            {
                state = "Directory";
                ScanForInspection(source, trusted, "Source", task.TaskId, issues, reparsePoints, cancellationToken, out count, out bytes);
            }
        }

        var destinationInfo = junctions.Inspect(destination);
        if (destinationInfo.IsReparsePoint)
            issues.Add(new("UnsafeDestinationReparsePoint", "Destination is a reparse point.", TaskId: task.TaskId));
        else if (destinationInfo.Exists && destinationInfo.IsDirectory && state != "HealthyJunction")
            ScanForInspection(destination, trusted, "Destination", task.TaskId, issues, reparsePoints, cancellationToken, out _, out _);

        // An already-linked task performs no copy, delete, or cutover, so a running guarded application is irrelevant.
        if (mutating && state != "HealthyJunction" && !string.IsNullOrWhiteSpace(task.Guard) && IsBusy(task.Guard))
            issues.Add(new("GuardProcessRunning", "A related application is running. Close it and retry.", TaskId: task.TaskId, Retryable: true));

        if (mutating)
        {
            foreach (var path in new[] { source, destination })
            {
                try
                {
                    var root = Path.GetPathRoot(path) ?? string.Empty;
                    var drive = new DriveInfo(root);
                    if (!drive.IsReady || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                        issues.Add(new("NtfsRequired", $"Mutating Junction operations require NTFS: {root}", TaskId: task.TaskId));
                }
                catch (Exception ex) { issues.Add(new("VolumeInspectionFailed", ex.Message, TaskId: task.TaskId, Retryable: true)); }
            }
        }

        return new TaskInspection { Task = task, PathState = state, FileCount = count, SizeBytes = bytes, Issues = issues, ReparsePoints = reparsePoints };
    }

    private void ScanForInspection(string root, IReadOnlyCollection<string> trusted, string scope, string taskId,
        List<ValidationIssue> issues, List<string> reparsePoints, CancellationToken cancellationToken, out long? count, out long? bytes)
    {
        count = null;
        bytes = null;
        try
        {
            var scan = trees.GetStatisticsSkippingReparsePoints(root, trusted, cancellationToken);
            count = scan.FileCount;
            bytes = scan.Bytes;
            reparsePoints.AddRange(scan.UndeclaredReparsePoints);
            if (scan.UndeclaredReparsePoints.Count > 0)
                issues.Add(new($"{scope}ScanFailed", $"{scope} scan: {DescribeReparsePoints(scan.UndeclaredReparsePoints)}", TaskId: taskId, Retryable: true));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { issues.Add(new($"{scope}ScanFailed", $"{scope} scan failed: {ex.Message}", TaskId: taskId, Retryable: true)); }
    }

    private static string DescribeReparsePoints(IReadOnlyList<string> paths) =>
        string.Join("; ", paths.Take(20).Select(path => $"Unsafe reparse point encountered: {path}"));

    private void InspectAncestors(string path, bool includeFinal, string taskId, List<ValidationIssue> issues)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root)) return;
        var current = root;
        var components = full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            if (!includeFinal && index == components.Length - 1) break;
            var info = junctions.Inspect(current);
            if (info.Exists && info.IsReparsePoint)
            {
                issues.Add(new("UnsafeAncestorReparsePoint", $"Path traverses a reparse point: {current}", TaskId: taskId));
                return;
            }
            if (!info.Exists) return;
        }
    }

    private static bool IsBusy(string expression)
    {
        var regex = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { if (regex.IsMatch(process.ProcessName)) return true; }
                catch { }
            }
        }
        return false;
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try { return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
