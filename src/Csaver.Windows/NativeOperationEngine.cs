using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Csaver.Application;
using Csaver.Core;

namespace Csaver.Windows;

public sealed class NativeOperationEngine : IOperationEngine
{
    private readonly IJunctionService junctions;
    private readonly ITreeService trees;
    private readonly ConfigurationService configurations;
    private readonly TaskResolver resolver;
    private readonly JournalStore journals;

    // A 350+ MiB application profile can take minutes to copy. Refuse to start while the
    // source is still changing, and re-check periodically during the copy. Both are
    // early-abort guards only: the final snapshot comparison stays the authoritative check.
    private const int StabilityMaxSamples = 3;
    private static readonly TimeSpan StabilityPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CopyStabilityCheckInterval = TimeSpan.FromSeconds(5);

    public NativeOperationEngine(IJunctionService junctions, ITreeService trees, ConfigurationService configurations, TaskResolver resolver, JournalStore journals)
    {
        this.junctions = junctions;
        this.trees = trees;
        this.configurations = configurations;
        this.resolver = resolver;
        this.journals = journals;
    }

    public Task<OperationResult> ExecuteAsync(OperationRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Execute(request, progress, cancellationToken), cancellationToken);

    private OperationResult Execute(OperationRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var result = new OperationResult { OperationId = request.OperationId, Kind = request.Kind, StartedUtc = started };
        try
        {
            ValidateRequest(request);
            if (request.Kind == OperationKind.Health)
            {
                result.Success = true;
                result.Message = "Elevated worker is healthy.";
                return Complete(result);
            }

            var workspace = new PortableWorkspace(request.BaseDirectory);
            var history = new HistoryService(workspace);
            if (request.Kind is OperationKind.ListRollback or OperationKind.PreviewRollback or OperationKind.Rollback)
                ExecuteRollbackCommand(request, result, history, progress, cancellationToken);
            else if (request.Kind == OperationKind.ViewQuarantine)
            {
                result.Success = true;
                result.Message = $"{new QuarantineService(trees).Load().Count} quarantine entries found.";
            }
            else
            {
                var loaded = configurations.LoadFile(request.ConfigurationPath);
                if (!loaded.IsValid || loaded.Configuration is null) throw new InvalidDataException(string.Join(Environment.NewLine, loaded.Issues.Select(x => x.Message)));
                var selected = resolver.Resolve(loaded.Configuration).Where(x => request.SelectedTaskIds.Contains(x.TaskId, StringComparer.OrdinalIgnoreCase)).ToList();
                if (!string.IsNullOrWhiteSpace(request.ResolvedTaskSetHash))
                {
                    if (request.FrozenTasks.Count == 0 || !TaskResolver.FrozenEquals(selected, request.FrozenTasks) || !request.ResolvedTaskSetHash.Equals(TaskResolver.ComputeResolvedTaskSetHash(request.FrozenTasks), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Resolved task set changed after the operation was prepared; refusing to continue.");
                }
                if (selected.Count == 0) throw new InvalidOperationException("No selected tasks were resolved from the current configuration.");
                if (request.Kind == OperationKind.ExplainConfiguration)
                    ExecuteExplainConfiguration(request, selected, result, progress, cancellationToken);
                else if (request.Kind == OperationKind.PreviewRepair)
                    ExecuteRepairPreview(request, selected, result, progress, cancellationToken);
                else if (request.Kind == OperationKind.PreviewMigration)
                    ExecutePreview(request, selected, result, progress, cancellationToken);
                else if (request.Kind == OperationKind.RecoverIncompleteMigration)
                    ExecuteRecovery(request, selected, result, progress, cancellationToken);
                else if (request.Kind == OperationKind.Migrate)
                    ExecuteMigration(request, selected, result, history, progress, cancellationToken);
                else if (request.Kind == OperationKind.Repair)
                    ExecuteRepair(request, selected, result, history, progress, cancellationToken);
                else throw new InvalidOperationException("Unsupported operation.");
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Success = false;
            result.Message = "Operation cancelled before the critical cutover section.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = ex.Message;
        }
        return Complete(result);
    }

    private void ValidateRequest(OperationRequest request)
    {
        if (request.SchemaVersion != 1) throw new InvalidDataException("Unsupported request schema.");
        if (string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.Nonce)) throw new InvalidDataException("Request identity is incomplete.");
        var baseDirectory = Path.GetFullPath(request.BaseDirectory).TrimEnd('\\');
        var expectedConfiguration = Path.Combine(baseDirectory, "config", "migration-tasks.json");
        if (!Path.GetFullPath(request.ConfigurationPath).Equals(expectedConfiguration, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Configuration path is outside the portable workspace.");
        if (!File.Exists(expectedConfiguration)) throw new FileNotFoundException("Configuration does not exist.", expectedConfiguration);
        using var stream = File.OpenRead(expectedConfiguration);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!hash.Equals(request.ConfigurationSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Configuration changed after the operation was prepared.");
        if (!Enum.IsDefined(request.Kind)) throw new InvalidDataException("Operation is not allowlisted.");
    }

    private void ExecuteExplainConfiguration(OperationRequest request, List<ResolvedMigrationTask> tasks, OperationResult result, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pathState = CurrentPathState(task);
            progress?.Report(Progress(request, task, "Explaining", "Resolving configuration without inspecting files or changing paths."));
            result.Tasks.Add(new OperationTaskResult
            {
                TaskId = task.TaskId,
                Group = task.Group,
                Source = task.Source,
                Destination = task.Destination,
                DestinationSource = task.DestinationSource,
                Disabled = task.Disabled,
                IsDynamic = task.IsDynamic,
                IsBroadRoot = task.IsBroadRoot,
                BroadRootAcknowledged = task.IsBroadRoot && request.BroadRootAcknowledged,
                PathState = pathState,
                Status = task.Disabled ? "DISABLED" : "CONFIGURED",
                TransactionState = "ReadOnly",
                Detail = $"TaskId={task.TaskId}; Group={task.Group}; Source={task.Source}; Destination={task.Destination}; DestinationSource={task.DestinationSource}; State={(task.Disabled ? "Disabled" : "Enabled")}; Dynamic={(task.IsDynamic ? "Yes" : "No")}; BroadRootWarning={(task.IsBroadRoot ? "Yes" : "No")}; BroadRootAcknowledged={(task.IsBroadRoot && request.BroadRootAcknowledged ? "Yes" : "No")}; PathState={pathState}"
            });
        }
        result.Success = true;
        result.Message = $"Explained configuration for {result.Tasks.Count} task(s); no paths, files, or audit artifacts were changed.";
    }

    private string CurrentPathState(ResolvedMigrationTask task)
    {
        var info = junctions.Inspect(task.Source);
        if (!info.Exists) return "Absent";
        if (!info.IsDirectory) return "File";
        if (!info.IsReparsePoint) return "Directory";
        if (!info.IsJunction) return "UnsafeReparse";
        return PathsEqual(info.Target, task.Destination) ? "HealthyJunction" : "WrongJunction";
    }

    private void ExecutePreview(OperationRequest request, List<ResolvedMigrationTask> tasks, OperationResult result, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var inspector = new TaskInspectionService(junctions, trees);
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var incomplete = FindIncompleteForwardJournals(request, task);
            if (incomplete.Count > 0)
            {
                result.Tasks.Add(RecoveryBlockedTask(task, incomplete));
                continue;
            }
            progress?.Report(Progress(request, task, "Inspecting", "Inspecting task safety and size."));
            var inspection = inspector.Inspect(task, mutating: false, request.BroadRootAcknowledged, cancellationToken);
            var alreadyLinked = inspection.CanMutate && inspection.PathState == "HealthyJunction";
            result.Tasks.Add(new OperationTaskResult
            {
                TaskId = task.TaskId, Group = task.Group, Source = task.Source, Destination = task.Destination,
                Status = !inspection.CanMutate ? "PREFLIGHT_FAILED" : alreadyLinked ? "SKIP_ALREADY_LINK" : "PLAN",
                TransactionState = alreadyLinked ? "AlreadyLinked" : "Preview",
                Detail = alreadyLinked ? "Healthy Junction is already present; no changes required."
                    : inspection.Issues.Count == 0 ? $"{inspection.PathState}; warnings=none" : string.Join("; ", inspection.Issues.Select(x => x.Message)),
                Retryable = inspection.Issues.Any(x => x.Retryable), FileCount = inspection.FileCount, SizeBytes = inspection.SizeBytes,
                DestinationSource = task.DestinationSource, IsBroadRoot = task.IsBroadRoot, BroadRootAcknowledged = task.IsBroadRoot && request.BroadRootAcknowledged, VerificationTier = "StructuralMetadata"
            });
        }
        result.Success = result.Tasks.All(x => x.Status is "PLAN" or "SKIP_ALREADY_LINK");
        var blocked = result.Tasks.Count(x => x.Status == "PREFLIGHT_FAILED");
        var linked = result.Tasks.Count(x => x.Status == "SKIP_ALREADY_LINK");
        result.Message = $"Previewed {result.Tasks.Count} task(s); blocked={blocked}; alreadyLinked={linked}; no migration paths or audit artifacts were changed.";
    }

    private void ExecuteRepairPreview(OperationRequest request, List<ResolvedMigrationTask> tasks, OperationResult result, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(Progress(request, task, "Inspecting", "Building a read-only repair plan."));
            var row = NewTaskResult(task);
            row.BroadRootAcknowledged = task.IsBroadRoot && request.BroadRootAcknowledged;
            var sourceInfo = junctions.Inspect(task.Source);
            var mergeSource = sourceInfo.IsJunction ? sourceInfo.Target : task.Source;
            var conflicts = new List<string>();
            if (!string.IsNullOrWhiteSpace(mergeSource) && Directory.Exists(mergeSource) && Directory.Exists(task.Destination))
            {
                foreach (var file in Directory.EnumerateFiles(mergeSource, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = Path.Combine(task.Destination, Path.GetRelativePath(mergeSource, file));
                    if (File.Exists(target) && !FilesEquivalent(file, target)) conflicts.Add(Path.GetRelativePath(mergeSource, file));
                }
            }
            row.Status = sourceInfo.IsJunction && PathsEqual(sourceInfo.Target, task.Destination) ? "REPAIR_PLAN_HEALTHY" : "REPAIR_PLAN";
            row.TransactionState = "Preview"; row.ConflictCount = conflicts.Count; row.VerificationTier = "StructuralMetadata";
            var archive = Path.Combine(FindDestinationRoot(task.Destination), "_repair_conflicts", "<operation>", Path.GetFileName(task.Source));
            var backup = Path.Combine(FindDestinationRoot(task.Destination), "_repair_backup", "<operation>", Path.GetFileName(task.Source));
            row.Detail = $"SourceState={CurrentPathState(task)}; conflicts={conflicts.Count}; policy={request.RepairPolicy}; archive={archive}; backup={backup}; abortOnConflict={request.AbortOnConflict}; conflictPaths={(conflicts.Count == 0 ? "none" : string.Join("|", conflicts.Take(20)))}";
            result.Tasks.Add(row);
        }
        result.Success = true;
        result.Message = $"Repair previewed {result.Tasks.Count} task(s); no paths or audit artifacts were changed.";
    }

    private void ExecuteRecovery(OperationRequest request, List<ResolvedMigrationTask> tasks, OperationResult result, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(Progress(request, task, "Recovering", "Validating the pre-cutover journal without deleting data."));
            var row = NewTaskResult(task);
            var matches = FindIncompleteForwardJournals(request, task);
            if (matches.Count != 1)
            {
                row.Status = "RECOVERY_REFUSED";
                row.TransactionState = matches.Count == 0 ? "RecoveryRequired" : "Ambiguous";
                row.Retryable = true;
                row.Detail = matches.Count == 0
                    ? "Exactly one matching incomplete ForwardMigration journal is required; none was found."
                    : $"Exactly one matching incomplete ForwardMigration journal is required; found {matches.Count}. Recovery was refused without changing data.";
                result.Tasks.Add(row);
                continue;
            }

            var match = matches[0];
            row.JournalPath = match.Path;
            var journal = match.Journal;
            try
            {
                if (!string.Equals(journal.State, "RecoveryRequired", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Only a RecoveryRequired journal can be recovered; current state is {journal.State}.");
                if (!string.Equals(journal.Kind, "ForwardMigration", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only a ForwardMigration journal can be recovered.");
                if (!PathsEqual(journal.CanonicalSource, task.Source) || !PathsEqual(journal.CanonicalDest, task.Destination))
                    throw new InvalidOperationException("Journal source or destination does not match the selected task.");
                if (journal.SourceSnapshot is not null || journal.DestinationSnapshot is not null)
                    throw new InvalidOperationException("Journal snapshot evidence indicates copying or cutover progressed; ambiguous/post-cutover recovery is refused.");
                if (PathsEqual(task.Source, task.Destination))
                    throw new InvalidOperationException("Source and destination must be different.");

                var sourceInfo = junctions.Inspect(task.Source);
                if (!sourceInfo.Exists || !sourceInfo.IsDirectory || sourceInfo.IsReparsePoint)
                    throw new IOException("Source must remain an accessible ordinary directory; Junctions and reparse points are refused.");
                EnsureDirectoryAccessible(task.Source, "Source");

                var destinationInfo = junctions.Inspect(task.Destination);
                if (destinationInfo.Exists)
                {
                    if (!destinationInfo.IsDirectory || destinationInfo.IsReparsePoint)
                        throw new IOException("Destination must be an accessible ordinary directory or safely absent; files and reparse points are refused.");
                    EnsureDirectoryAccessible(task.Destination, "Destination");
                }
                else if (File.Exists(task.Destination))
                {
                    throw new IOException("Destination is an existing file; recovery is refused.");
                }

                if (!string.IsNullOrWhiteSpace(journal.CStage) && (Directory.Exists(journal.CStage) || File.Exists(journal.CStage)))
                    throw new IOException($"C-stage payload exists at {journal.CStage}; post-copy recovery is refused.");

                const string metadata = "Pre-cutover recovery validated; source and destination retained; no data deleted.";
                journals.RecoverPreCutover(match.Path, journal.OperationId, metadata);
                row.Status = "RECOVERED";
                row.TransactionState = "Recovered";
                row.Retryable = false;
                row.Detail = $"Journal={match.Path}; Source={task.Source}; Destination={task.Destination}; State=Recovered; LastError preserved; no source, destination, staging data, manifest, or journal was deleted. A fresh migration may now be prepared.";
            }
            catch (Exception ex)
            {
                row.Status = "RECOVERY_REFUSED";
                row.TransactionState = "RecoveryRequired";
                row.Retryable = true;
                row.Detail = $"Journal={match.Path}; Source={task.Source}; Destination={task.Destination}; {ex.Message} No data was deleted.";
            }
            result.Tasks.Add(row);
        }

        result.Success = result.Tasks.Count > 0 && result.Tasks.All(x => x.Status == "RECOVERED");
        result.Message = result.Success
            ? $"Recovered {result.Tasks.Count} incomplete transaction(s); no source, destination, staging data, manifest, or journal was deleted."
            : $"Recovery refused for {result.Tasks.Count(x => x.Status == "RECOVERY_REFUSED")} transaction(s); no data was deleted.";
    }

    private static void EnsureDirectoryAccessible(string path, string label)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(path);
        }
        catch (Exception ex)
        {
            throw new IOException($"{label} directory is not accessible: {ex.Message}", ex);
        }
    }

    // Snapshot verification stays strictly fail-closed: no difference is ignored and no
    // file is recopied. This only makes the failure actionable, including the correct
    // command to run next (recovery, not repair) and whether recovery is still possible.
    private static string BuildInstabilityMessage(ResolvedMigrationTask task, SnapshotComparison comparison, string prefix, bool sourceStillChanging, bool snapshotEvidence)
    {
        var differences = string.Join("; ", comparison.Details.Take(5));
        var remaining = comparison.Details.Count > 5 ? $" (+{comparison.Details.Count - 5} more differences)" : string.Empty;
        var changedEntries = string.Join(", ", comparison.Details
            .Select(x => x.Contains(':') ? x[(x.LastIndexOf(':') + 1)..].Trim() : x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5));
        var cause = sourceStillChanging
            ? "The source directory is still being modified; a second source snapshot changed again."
            : "The source directory changed during the copying window and is now stable again, so the destination no longer matches it.";
        var guidance = snapshotEvidence
            ? "This journal already contains snapshot evidence, so automatic recovery will be refused. Keep the source, destination, and journal intact and follow the user guide to resolve the transaction manually."
            : UnstableSourceGuidance(task);
        return $"{prefix}: {differences}{remaining} | MismatchCount={comparison.MismatchCount}; MissingCount={comparison.MissingCount}; changedEntries={changedEntries}. {cause} {guidance}";
    }

    private static string UnstableSourceGuidance(ResolvedMigrationTask task)
    {
        var guardHint = string.IsNullOrWhiteSpace(task.Guard)
            ? "Close every application writing to the source directory, including tray and background helpers."
            : $"Close the application matching Guard '{task.Guard}', including its tray and background helpers.";
        return $"{guardHint} If this keeps recurring, migrate in Windows Safe Mode, where third-party startup apps and background services are not loaded; the user guide gives detailed steps to enter and exit Safe Mode. This transaction has no snapshot evidence yet. Run Recover Incomplete Transaction, then Preview Migration and Run Migration again. Do not use Repair for a copy verification failure; Repair is for a broken Junction. The source was left intact and no Junction was created.";
    }

    private static string BuildCopyTimeInstabilityMessage(ResolvedMigrationTask task, string prefix, string cause) =>
        $"{prefix}: {cause} Copying stopped before the cutover; no files were recopied and no difference was ignored. {UnstableSourceGuidance(task)}";

    // Runs while the journal is in Copying, before any file is copied and before any
    // snapshot is attached. Consecutive identical source snapshots are required.
    private void WaitForStableSource(OperationRequest request, ResolvedMigrationTask task, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var baseline = trees.CaptureSnapshot(task.Source, cancellationToken);
        SnapshotComparison? lastComparison = null;
        for (var sample = 1; sample <= StabilityMaxSamples; sample++)
        {
            cancellationToken.WaitHandle.WaitOne(StabilityPollInterval);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(Progress(request, task, "Stabilizing", $"Waiting for the source to stop changing (sample {sample}/{StabilityMaxSamples})."));
            var current = trees.CaptureSnapshot(task.Source, cancellationToken);
            lastComparison = trees.Compare(baseline, current, allowDestinationOnly: false);
            if (lastComparison.Success) return;
            baseline = current;
        }
        throw new IOException(BuildInstabilityMessage(task, lastComparison!, "Source is not stable before copying", sourceStillChanging: true, snapshotEvidence: false));
    }

    private string? DetectCopyTimeInstability(ResolvedMigrationTask task, DirectoryScanResult baseline, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(task.Guard) && TaskInspectionService.IsBusy(task.Guard))
                return BuildCopyTimeInstabilityMessage(task, "Guard process appeared during copying",
                    $"Guard '{task.Guard}' is running while Csaver is copying.");
        }
        catch (ArgumentException) { /* GuardRegexInvalid is already reported by configuration validation. */ }
        try
        {
            var current = trees.GetStatisticsSkippingReparsePoints(task.Source, task.TrustedReparsePoints, cancellationToken);
            if (current.FileCount != baseline.FileCount || current.Bytes != baseline.Bytes)
                return BuildCopyTimeInstabilityMessage(task, "Source changed during copying",
                    $"Source statistics changed during copying (files {baseline.FileCount} -> {current.FileCount}, bytes {baseline.Bytes} -> {current.Bytes}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A transient scan failure is not evidence of an unstable source; the final
            // snapshot comparison remains the authoritative fail-closed check.
        }
        return null;
    }

    // CopyPreserving reports progress every 100 files. Wrapping that callback gives a
    // cheap periodic hook without changing the ITreeService signature or its three
    // implementations. Throwing IOException here leaves the journal in Copying, so it
    // becomes RecoveryRequired with no snapshot evidence and stays recoverable.
    private sealed class StabilityCheckProgress(IProgress<OperationProgress>? inner, Func<string?> check) : IProgress<OperationProgress>
    {
        private DateTime lastCheckUtc = DateTime.MinValue;

        public void Report(OperationProgress value)
        {
            inner?.Report(value);
            var now = DateTime.UtcNow;
            if (now - lastCheckUtc < CopyStabilityCheckInterval) return;
            lastCheckUtc = now;
            var instability = check();
            if (instability is not null) throw new IOException(instability);
        }
    }

    private static void EnsureSourceMovable(string source, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Unsafe reparse point in source tree '{path}'; declare it under TrustedReparsePoints or remove it before migrating.");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                FileStream? handle = null;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try { handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (attempt == 2)
                            throw new IOException($"Source file '{path}' is locked or unreadable: {ex.Message}. Close the application using this directory (for example Trae CN) before migrating.", ex);
                        Thread.Sleep(100);
                    }
                }
                handle?.Dispose();
            }
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private void ExecuteMigration(OperationRequest request, List<ResolvedMigrationTask> tasks, OperationResult result, HistoryService history, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var inspector = new TaskInspectionService(junctions, trees);
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var taskStarted = DateTimeOffset.UtcNow;
            var row = NewTaskResult(task);
            row.BroadRootAcknowledged = task.IsBroadRoot && request.BroadRootAcknowledged;
            var incomplete = FindIncompleteForwardJournals(request, task);
            if (incomplete.Count > 0)
            {
                var blocked = RecoveryBlockedTask(task, incomplete);
                result.Tasks.Add(blocked);
                SaveHistory(history, request, blocked, taskStarted);
                break;
            }
            var inspection = inspector.Inspect(task, mutating: true, request.BroadRootAcknowledged, cancellationToken);
            row.FileCount = inspection.FileCount;
            row.SizeBytes = inspection.SizeBytes;
            if (!inspection.CanMutate)
            {
                row.Status = "PREFLIGHT_FAILED"; row.TransactionState = "PreflightFailed"; row.Retryable = inspection.Issues.Any(x => x.Retryable);
                row.Detail = string.Join("; ", inspection.Issues.Select(x => x.Message)); result.Tasks.Add(row); SaveHistory(history, request, row, taskStarted); break;
            }
            if (inspection.PathState == "Absent")
            {
                row.Status = "SKIP_ABSENT"; row.TransactionState = "Skipped"; row.Detail = "Source does not exist."; result.Tasks.Add(row); SaveHistory(history, request, row, taskStarted); continue;
            }
            if (inspection.PathState == "HealthyJunction")
            {
                row.Status = "SKIP_ALREADY_LINK"; row.TransactionState = "Skipped"; row.Detail = "Healthy Junction is already present."; result.Tasks.Add(row); SaveHistory(history, request, row, taskStarted); continue;
            }

            var id = Guid.NewGuid().ToString("N");
            var destinationRoot = FindDestinationRoot(task.Destination);
            var journalDirectory = Path.Combine(destinationRoot, ".csaver-journal");
            Directory.CreateDirectory(journalDirectory);
            var journalPath = Path.Combine(journalDirectory, "forward-" + id + ".json");
            row.JournalPath = journalPath;
            var stageParent = Path.Combine(Path.GetDirectoryName(task.Source)!, ".csaver-forward-stage", id);
            var stagePayload = Path.Combine(stageParent, "payload");
            var journal = new NativeJournal { OperationId = id, Kind = "ForwardMigration", TaskId = task.TaskId, Group = task.Group, CanonicalSource = task.Source, CanonicalDest = task.Destination, DestinationSource = task.DestinationSource, BroadRootAcknowledged = request.BroadRootAcknowledged, CStage = stagePayload, ManifestPath = Path.Combine(destinationRoot, "_migration_manifest.csv") };
            try
            {
                journals.Create(journalPath, journal);
                journals.Transition(journalPath, journal, "Copying");
                // Fail before copying and before attaching snapshots to the journal, so a
                // locked source stays recoverable instead of becoming a post-cutover refusal.
                EnsureSourceMovable(task.Source, cancellationToken);
                progress?.Report(Progress(request, task, "Stabilizing", "Waiting for the source to stop changing before copying."));
                WaitForStableSource(request, task, progress, cancellationToken);
                var baselineStats = trees.GetStatisticsSkippingReparsePoints(task.Source, task.TrustedReparsePoints, cancellationToken);
                var stabilityProgress = new StabilityCheckProgress(progress, () => DetectCopyTimeInstability(task, baselineStats, cancellationToken));
                progress?.Report(Progress(request, task, "Copying", "Copying without deleting destination-only data."));
                trees.CopyPreserving(task.Source, task.Destination, stabilityProgress, request.OperationId, task.TaskId, cancellationToken);
                journals.Transition(journalPath, journal, "Copied");
                var sourceSnapshot = trees.CaptureSnapshot(task.Source, cancellationToken);
                var destinationSnapshot = trees.CaptureSnapshot(task.Destination, cancellationToken);
                var verification = trees.Compare(sourceSnapshot, destinationSnapshot, allowDestinationOnly: true);
                if (!verification.Success)
                {
                    // A mismatch here is usually the guarded application rewriting its own data
                    // mid-copy. Re-snapshot the source to confirm, then fail closed with an
                    // actionable message. This stays before the journal's snapshot assignments,
                    // so the transaction remains eligible for Recover Incomplete Transaction.
                    var stillChanging = false;
                    try
                    {
                        var rescan = trees.CaptureSnapshot(task.Source, cancellationToken);
                        stillChanging = !trees.Compare(sourceSnapshot, rescan, allowDestinationOnly: false).Success;
                    }
                    catch { /* diagnostic only; never mask the verification failure */ }
                    throw new IOException(BuildInstabilityMessage(task, verification, "Copy verification failed", stillChanging, snapshotEvidence: false));
                }
                journal.SourceSnapshot = sourceSnapshot;
                journal.DestinationSnapshot = destinationSnapshot;
                journals.Transition(journalPath, journal, "Verified");
                var fresh = trees.CaptureSnapshot(task.Source, cancellationToken);
                var stable = trees.Compare(sourceSnapshot, fresh, allowDestinationOnly: false);
                if (!stable.Success)
                    throw new IOException(BuildInstabilityMessage(task, stable, "Source changed during migration", sourceStillChanging: true, snapshotEvidence: true));
                journals.Transition(journalPath, journal, "SourceVerified");

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(Progress(request, task, "Cutover", "Entering non-cancellable Junction cutover.", true));
                Directory.CreateDirectory(stageParent);
                try { Directory.Move(task.Source, stagePayload); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { throw new IOException($"Cannot stage source '{task.Source}' to '{stagePayload}': {ex.Message}. Close applications using the source directory before retrying.", ex); }
                journals.Transition(journalPath, journal, "SourceStaged");
                junctions.Create(task.Source, task.Destination);
                journals.Transition(journalPath, journal, "JunctionCreated");
                var link = junctions.Inspect(task.Source);
                if (!link.IsJunction || !PathsEqual(link.Target, task.Destination)) throw new IOException("Final Junction validation failed.");
                journals.Transition(journalPath, journal, "LinkValidated");
                // Read-only files would make the recursive staging cleanup fail with only a
                // file name in the exception; clear them and report the full path if it still fails.
                ClearReadOnlyAttributes(stagePayload);
                try { Directory.Delete(stagePayload, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { throw new IOException($"Cannot remove staged copy '{stagePayload}': {ex.Message}. The Junction was created and validated; close the locking application and remove this staging folder manually.", ex); }
                if (Directory.Exists(stageParent)) Directory.Delete(stageParent, true);
                journals.Transition(journalPath, journal, "Committed", retryable: false);
                row.Status = "OK"; row.TransactionState = "Committed"; row.Retryable = false;
                row.FileCount = sourceSnapshot.FileCount; row.SizeBytes = sourceSnapshot.Bytes;
                row.Detail = $"StructuralMetadata verified; destination-only entries={verification.DestinationOnlyCount}; LinkValidated";
                AppendManifest(journal.ManifestPath, row, id, "StructuralMetadata");
            }
            catch (Exception ex)
            {
                RecoverSource(task.Source, stagePayload);
                TryTransition(journalPath, journal, "RecoveryRequired", ex.Message);
                row.Status = "FAIL_TRANSACTION"; row.TransactionState = "RecoveryRequired"; row.Retryable = true; row.Detail = ex.Message;
            }
            result.Tasks.Add(row);
            SaveHistory(history, request, row, taskStarted, id);
            if (row.Status.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase)) break;
        }
        result.Success = result.Tasks.All(x => !x.Status.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) && x.Status != "PREFLIGHT_FAILED");
        result.Message = $"Migration finished: {result.Tasks.Count(x => x.Status == "OK")} committed, {result.Tasks.Count(x => x.Status.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase))} skipped, {result.Tasks.Count(x => x.Status.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) || x.Status == "PREFLIGHT_FAILED")} failed.";
    }

    private void ExecuteRepair(OperationRequest request, List<ResolvedMigrationTask> tasks, OperationResult result, HistoryService history, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var inspector = new TaskInspectionService(junctions, trees);
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = DateTimeOffset.UtcNow;
            var row = NewTaskResult(task);
            row.BroadRootAcknowledged = task.IsBroadRoot && request.BroadRootAcknowledged;
            var sourceInfo = junctions.Inspect(task.Source);
            if (!sourceInfo.Exists) { row.Status = "REPAIR_SKIP_ABSENT"; row.TransactionState = "Skipped"; row.Detail = "Source absent."; result.Tasks.Add(row); SaveHistory(history, request, row, started); continue; }
            if (sourceInfo.IsJunction && PathsEqual(sourceInfo.Target, task.Destination)) { row.Status = "REPAIR_OK_HEALTHY"; row.TransactionState = "Skipped"; row.Detail = "Healthy Junction."; result.Tasks.Add(row); SaveHistory(history, request, row, started); continue; }
            if (sourceInfo.IsReparsePoint && !sourceInfo.IsJunction) { row.Status = "REPAIR_NEEDS_REVIEW"; row.TransactionState = "PreflightFailed"; row.Detail = "Unsafe non-Junction reparse point."; result.Tasks.Add(row); SaveHistory(history, request, row, started); continue; }
            var inspection = inspector.Inspect(task, true, request.BroadRootAcknowledged, cancellationToken);
            var blocking = inspection.Issues.Where(x => x.Code is not "JunctionTargetMismatch").ToList();
            if (blocking.Any(x => x.Severity == IssueSeverity.Error)) { row.Status = "REPAIR_NEEDS_REVIEW"; row.TransactionState = "PreflightFailed"; row.Detail = string.Join("; ", blocking.Select(x => x.Message)); result.Tasks.Add(row); SaveHistory(history, request, row, started); continue; }

            var mergeSource = sourceInfo.IsJunction ? sourceInfo.Target! : task.Source;
            var originalJunctionTarget = sourceInfo.IsJunction ? sourceInfo.Target : null;
            if (!Directory.Exists(mergeSource)) { row.Status = "REPAIR_NEEDS_REVIEW"; row.TransactionState = "PreflightFailed"; row.Detail = "Repair source is unavailable."; result.Tasks.Add(row); SaveHistory(history, request, row, started); continue; }
            var id = Guid.NewGuid().ToString("N");
            var destinationRoot = FindDestinationRoot(task.Destination);
            var journalPath = Path.Combine(destinationRoot, ".csaver-journal", "repair-" + id + ".json");
            var stagePayload = Path.Combine(Path.GetDirectoryName(task.Source)!, ".csaver-repair-stage", id, "payload");
            var conflictRoot = Path.Combine(destinationRoot, "_repair_conflicts", id, Path.GetFileName(task.Source));
            var backupRoot = Path.Combine(destinationRoot, "_repair_backup", id, Path.GetFileName(task.Source));
            var journal = new NativeJournal { OperationId = id, Kind = "Repair", TaskId = task.TaskId, Group = task.Group, CanonicalSource = task.Source, CanonicalDest = task.Destination, DestinationSource = task.DestinationSource, BroadRootAcknowledged = request.BroadRootAcknowledged, CStage = stagePayload, QuarantinePath = conflictRoot, ManifestPath = Path.Combine(destinationRoot, "_repair_manifest.csv") };
            row.JournalPath = journalPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
                journals.Create(journalPath, journal);
                journals.Transition(journalPath, journal, "Merging");
                var merge = Merge(mergeSource, task.Destination, conflictRoot, backupRoot, request.RepairPolicy, request.AbortOnConflict, cancellationToken);
                journal.ConflictCount = merge.Conflicts;
                if (merge.Aborted) { journals.Transition(journalPath, journal, "ConflictAborted"); row.Status = "REPAIR_ABORTED"; row.TransactionState = "ConflictAborted"; row.Detail = "Conflict encountered; no cutover performed."; }
                else
                {
                    journal.DestinationSnapshot = trees.CaptureSnapshot(task.Destination, cancellationToken);
                    journals.Transition(journalPath, journal, "Verified");
                    progress?.Report(Progress(request, task, "Cutover", "Entering non-cancellable repair cutover.", true));
                    if (sourceInfo.IsJunction) junctions.Delete(task.Source);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(stagePayload)!); Directory.Move(task.Source, stagePayload); }
                    // Both source shapes share the same auditable state chain; writing journal.State
                    // directly here used to bypass JournalStore and skip the JunctionCreated step.
                    journals.Transition(journalPath, journal, "SourceStaged");
                    junctions.Create(task.Source, task.Destination);
                    journals.Transition(journalPath, journal, "JunctionCreated");
                    var finalLink = junctions.Inspect(task.Source);
                    if (!finalLink.IsJunction || !PathsEqual(finalLink.Target, task.Destination)) throw new IOException("Final repair Junction validation failed.");
                    journals.Transition(journalPath, journal, "LinkValidated");
                    if (Directory.Exists(stagePayload))
                    {
                        ClearReadOnlyAttributes(stagePayload);
                        Directory.Delete(stagePayload, true);
                    }
                    journals.Transition(journalPath, journal, "Committed", retryable: false);
                    row.Status = "REPAIR_MERGED"; row.TransactionState = "Committed"; row.Retryable = false;
                    row.FileCount = journal.DestinationSnapshot.FileCount; row.SizeBytes = journal.DestinationSnapshot.Bytes;
                    row.Detail = $"copied={merge.Copied}; conflicts={merge.Conflicts}; archived={merge.Archived}; overwritten={merge.Overwritten}; LinkValidated";
                    row.ConflictCount = merge.Conflicts;
                    row.QuarantinePath = conflictRoot;
                    AppendManifest(journal.ManifestPath, row, id, "StructuralMetadata");
                }
            }
            catch (Exception ex)
            {
                RecoverRepair(task.Source, originalJunctionTarget, stagePayload);
                journal.RecoveryPath = originalJunctionTarget ?? stagePayload;
                TryTransition(journalPath, journal, "RecoveryRequired", ex.Message);
                row.Status = "REPAIR_NEEDS_REVIEW"; row.TransactionState = "RecoveryRequired"; row.Retryable = true; row.Detail = ex.Message;
            }
            result.Tasks.Add(row); SaveHistory(history, request, row, started, id);
        }
        result.Success = result.Tasks.All(x => x.Status is "REPAIR_MERGED" or "REPAIR_OK_HEALTHY" or "REPAIR_SKIP_ABSENT");
        result.Message = $"Repair finished for {result.Tasks.Count} task(s).";
    }

    private void ExecuteRollbackCommand(OperationRequest request, OperationResult result, HistoryService history, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var records = history.Load(false).Where(x => x.Kind == OperationKind.Migrate && x.RollbackEligible).ToList();
        if (request.Kind == OperationKind.ListRollback)
        {
            foreach (var item in records)
            {
                var row = FromHistory(item, "ROLLBACK_CANDIDATE");
                var candidateJournal = journals.Load(item.JournalPath);
                if (!ValidateRollbackAuthorization(item, candidateJournal, out var reason)) { row.Status = "ROLLBACK_BLOCKED"; row.Detail = reason; }
                else if (!junctions.Inspect(item.Source).IsJunction || !PathsEqual(junctions.Inspect(item.Source).Target, item.Destination)) { row.Status = "ROLLBACK_BLOCKED"; row.Detail = "Current Junction target does not match the authorized destination."; }
                result.Tasks.Add(row);
            }
            result.Success = true; result.Message = $"{records.Count} rollback candidate(s) listed; blocked records are shown with reasons."; return;
        }
        var selected = records.Where(x => request.SelectedHistoryIds.Contains(x.HistoryId, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("No eligible rollback records were selected.");
        foreach (var record in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = FromHistory(record, "PLAN");
            var journal = journals.Load(record.JournalPath);
            if (!ValidateRollbackAuthorization(record, journal, out var authorizationError)) { row.Status = "ROLLBACK_BLOCKED"; row.Detail = authorizationError; result.Tasks.Add(row); continue; }
            var link = junctions.Inspect(record.Source);
            if (!link.IsJunction || !PathsEqual(link.Target, record.Destination)) { row.Status = "FAILED_AMBIGUOUS"; row.Detail = "Authorized Junction target mismatch."; result.Tasks.Add(row); continue; }
            var current = trees.CaptureSnapshot(record.Destination, cancellationToken);
            var unchanged = trees.Compare(journal!.DestinationSnapshot!, current, allowDestinationOnly: false);
            if (!unchanged.Success) { row.Status = "FAILED_INCOMPLETE"; row.Detail = "Destination changed from the committed migration snapshot."; result.Tasks.Add(row); continue; }
            if (request.Kind == OperationKind.PreviewRollback) { row.Detail = "Rollback eligible; no changes made."; result.Tasks.Add(row); continue; }
            ExecuteRollback(request, record, journal, row, history, progress, cancellationToken);
            result.Tasks.Add(row);
        }
        result.Success = result.Tasks.All(x => x.Status is "PLAN" or "COMPLETED");
        result.Message = request.Kind == OperationKind.PreviewRollback ? "Rollback preview complete; no changes made." : "Rollback execution complete.";
    }

    private bool ValidateRollbackAuthorization(HistoryRecord record, NativeJournal? journal, out string error)
    {
        error = string.Empty;
        if (journal is null || journal.State != "Committed" || journal.Retryable || journal.DestinationSnapshot is null) { error = "A committed, non-retryable migration journal with a snapshot is required."; return false; }
        if (!string.Equals(journal.OperationId, record.OperationId, StringComparison.OrdinalIgnoreCase) || !string.Equals(journal.TaskId, record.TaskId, StringComparison.OrdinalIgnoreCase) || !PathsEqual(journal.CanonicalSource, record.Source) || !PathsEqual(journal.CanonicalDest, record.Destination) || !string.Equals(journal.Group, record.Group, StringComparison.OrdinalIgnoreCase))
        { error = "History and journal identity does not match; rollback is blocked."; return false; }
        if (string.IsNullOrWhiteSpace(record.ManifestPath) || !File.Exists(record.ManifestPath)) { error = "The committed migration manifest is missing."; return false; }
        var manifest = File.ReadAllText(record.ManifestPath, Encoding.UTF8);
        if (!manifest.Contains(record.OperationId, StringComparison.OrdinalIgnoreCase) || !manifest.Contains(record.Source, StringComparison.OrdinalIgnoreCase) || !manifest.Contains(record.Destination, StringComparison.OrdinalIgnoreCase) || !manifest.Contains("\"OK\"", StringComparison.OrdinalIgnoreCase))
        { error = "The migration manifest does not authorize this exact rollback."; return false; }
        return true;
    }
    private void ExecuteRollback(OperationRequest request, HistoryRecord record, NativeJournal authorization, OperationTaskResult row, HistoryService history, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var id = record.OperationId;
        var root = FindDestinationRoot(record.Destination);
        var rollbackJournalPath = Path.Combine(root, ".csaver-journal", "rollback-" + id + ".json");
        var stage = Path.Combine(Path.GetDirectoryName(record.Source)!, ".csaver-rollback-stage", id);
        var stagePayload = Path.Combine(stage, "payload");
        var quarantineRoot = Path.Combine(root, ".csaver-quarantine", id);
        var quarantinePayload = Path.Combine(quarantineRoot, "payload");
        var journal = new NativeJournal { OperationId = id, Kind = "Rollback", TaskId = record.TaskId, Group = record.Group, CanonicalSource = record.Source, CanonicalDest = record.Destination, CStage = stagePayload, QuarantinePath = quarantinePayload, ManifestPath = Path.Combine(root, "_rollback_manifest.csv"), DestinationSnapshot = authorization.DestinationSnapshot, State = "PreflightPassed" };
        try
        {
            var existing = journals.Load(rollbackJournalPath);
            if (existing is not null)
            {
                if (!string.Equals(existing.OperationId, id, StringComparison.OrdinalIgnoreCase) || !string.Equals(existing.TaskId, record.TaskId, StringComparison.OrdinalIgnoreCase) || !PathsEqual(existing.CanonicalSource, record.Source) || !PathsEqual(existing.CanonicalDest, record.Destination)) throw new UnauthorizedAccessException("Existing rollback journal identity does not match the authorized migration.");
                journal = existing;
            }
            else
            {
                journals.Create(rollbackJournalPath, journal);
                journals.Transition(rollbackJournalPath, journal, "RestoreStaging");
            }
            if (journal.State is "PreflightPassed" or "RestoreStaging")
            {
                progress?.Report(new OperationProgress { OperationId = request.OperationId, TaskId = record.TaskId, Stage = "RestoreStaging", Message = "Copying destination to source-side staging." });
                if (!Directory.Exists(stagePayload)) trees.CopyPreserving(record.Destination, stagePayload, progress, request.OperationId, record.TaskId, cancellationToken);
                var staged = trees.CaptureSnapshot(stagePayload, cancellationToken);
                var verified = trees.Compare(authorization.DestinationSnapshot!, staged, allowDestinationOnly: false);
                if (!verified.Success) throw new IOException("Rollback staging verification failed.");
                if (journal.State == "RestoreStaging") journals.Transition(rollbackJournalPath, journal, "RestoreStaged");
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress { OperationId = request.OperationId, TaskId = record.TaskId, Stage = "Cutover", Message = "Entering non-cancellable rollback cutover.", CriticalSection = true });
            if (journal.State == "RestoreStaged")
            {
                if (junctions.Inspect(record.Source).IsJunction) junctions.Delete(record.Source);
                if (!Directory.Exists(record.Source)) Directory.Move(stagePayload, record.Source);
                journals.Transition(rollbackJournalPath, journal, "SourceReplaced");
            }
            if (journal.State == "SourceReplaced")
            {
                Directory.CreateDirectory(quarantineRoot);
                if (!Directory.Exists(quarantinePayload) && Directory.Exists(record.Destination)) Directory.Move(record.Destination, quarantinePayload);
                journals.Transition(rollbackJournalPath, journal, "DestinationQuarantined");
            }
            AtomicFiles.WriteJson(Path.Combine(quarantineRoot, "inventory.json"), new
            {
                QuarantineId = id, Kind = "RollbackDestination", OriginalPath = record.Destination, StoredPath = quarantinePayload,
                Source = record.Source, Manifest = record.ManifestPath, Journal = rollbackJournalPath, OperationId = id,
                CreatedUtc = DateTimeOffset.UtcNow, VerificationTier = "StructuralMetadata", ActiveReferenceCount = 1,
                Eligible = false, CleanupState = "Retained", PayloadExists = true, Status = "RollbackDestinationQuarantined"
            });
            journals.Transition(rollbackJournalPath, journal, "COMPLETED", retryable: false);
            row.Status = "COMPLETED"; row.TransactionState = "COMPLETED"; row.Retryable = false; row.JournalPath = rollbackJournalPath; row.QuarantinePath = quarantinePayload; row.Detail = "Source restored; former destination retained in quarantine.";
            AppendManifest(journal.ManifestPath, row, id, "StructuralMetadata");
            SaveHistory(history, request, row, DateTimeOffset.UtcNow, id);
            history.MarkRollbackConsumed(record);
        }
        catch (Exception ex)
        {
            if (!Directory.Exists(record.Source) && Directory.Exists(record.Destination)) { try { junctions.Create(record.Source, record.Destination); } catch { } }
            TryTransition(rollbackJournalPath, journal, "RollbackIncomplete", ex.Message);
            row.Status = "FAILED_INCOMPLETE"; row.TransactionState = "RollbackIncomplete"; row.Retryable = true; row.Detail = ex.Message;
        }
    }

    private MergeResult Merge(string source, string destination, string conflictRoot, string backupRoot, RepairPolicy policy, bool abortOnConflict, CancellationToken cancellationToken)
    {
        var result = new MergeResult();
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe reparse point in repair source.");
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            CopyDirectoryMetadata(directory, Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe reparse point in repair source.");
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) { CopyFilePreserving(file, target, false); result.Copied++; continue; }
            if (FilesEquivalent(file, target)) continue;
            result.Conflicts++;
            if (abortOnConflict) { result.Aborted = true; return result; }
            var overwrite = policy == RepairPolicy.SourceWins || (policy == RepairPolicy.Newer && File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(target));
            if (overwrite)
            {
                CopyConflict(target, Path.Combine(backupRoot, relative));
                CopyFilePreserving(file, target, true); result.Overwritten++;
            }
            else
            {
                CopyConflict(file, Path.Combine(conflictRoot, relative)); result.Archived++;
            }
        }
        return result;
    }

    private static void CopyDirectoryMetadata(string source, string destination)
    {
        var info = new DirectoryInfo(source);
        Directory.SetCreationTimeUtc(destination, info.CreationTimeUtc);
        Directory.SetLastWriteTimeUtc(destination, info.LastWriteTimeUtc);
        Directory.SetLastAccessTimeUtc(destination, info.LastAccessTimeUtc);
        File.SetAttributes(destination, info.Attributes);
        try { FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(destination), FileSystemAclExtensions.GetAccessControl(info)); } catch (PlatformNotSupportedException) { }
    }
    private static bool FilesEquivalent(string left, string right)
    {
        var a = new FileInfo(left); var b = new FileInfo(right);
        if (a.Length != b.Length) return false;
        using var one = File.OpenRead(left); using var two = File.OpenRead(right);
        return SHA256.HashData(one).AsSpan().SequenceEqual(SHA256.HashData(two));
    }

    private static void CopyFilePreserving(string source, string destination, bool overwrite)
    {
        File.Copy(source, destination, overwrite);
        var sourceInfo = new FileInfo(source);
        File.SetCreationTimeUtc(destination, sourceInfo.CreationTimeUtc);
        File.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);
        File.SetLastAccessTimeUtc(destination, sourceInfo.LastAccessTimeUtc);
        File.SetAttributes(destination, sourceInfo.Attributes);
        try { FileSystemAclExtensions.SetAccessControl(new FileInfo(destination), FileSystemAclExtensions.GetAccessControl(sourceInfo)); } catch (PlatformNotSupportedException) { }
    }
    private static void CopyConflict(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, true);
        File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    private void RecoverRepair(string source, string? originalTarget, string stagePayload)
    {
        try
        {
            var current = junctions.Inspect(source);
            if (current.IsJunction) junctions.Delete(source);
            if (!string.IsNullOrWhiteSpace(originalTarget))
            {
                if (!Directory.Exists(source)) junctions.Create(source, originalTarget);
            }
            else if (!Directory.Exists(source) && Directory.Exists(stagePayload)) Directory.Move(stagePayload, source);
        }
        catch { }
    }
    private void RecoverSource(string source, string stagePayload)
    {
        try
        {
            var current = junctions.Inspect(source);
            if (current.IsJunction) junctions.Delete(source);
            if (!Directory.Exists(source) && Directory.Exists(stagePayload)) Directory.Move(stagePayload, source);
        }
        catch { }
    }

    private void TryTransition(string path, NativeJournal journal, string state, string error)
    {
        try { journals.Transition(path, journal, state, error, true); }
        catch
        {
            journal.State = state; journal.LastError = error; journal.Retryable = true;
            try { AtomicFiles.WriteJson(path, journal); } catch { }
        }
    }

    private static void AppendManifest(string path, OperationTaskResult row, string operationId, string verificationTier)
    {
        row.ManifestPath = path; row.VerificationTier = verificationTier;
        var header = "Time,Group,TaskId,Source,Dest,Status,Detail,OperationId,CanonicalSource,CanonicalDest,DestRootSource,VerificationTier,HashAlgorithm,ConflictCount,TransactionState,QuarantinePath,Retryable,BroadRootAcknowledged,JournalPath";
        var line = string.Join(',', Csv(DateTimeOffset.UtcNow.ToString("O")), Csv(row.Group), Csv(row.TaskId), Csv(row.Source), Csv(row.Destination), Csv(row.Status), Csv(row.Detail), Csv(operationId), Csv(Path.GetFullPath(row.Source)), Csv(Path.GetFullPath(row.Destination)), Csv(row.DestinationSource), Csv(verificationTier), Csv("SHA256"), Csv(row.ConflictCount.ToString()), Csv(row.TransactionState), Csv(row.QuarantinePath), Csv(row.Retryable.ToString()), Csv(row.BroadRootAcknowledged.ToString()), Csv(row.JournalPath));
        var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : header + Environment.NewLine;
        AtomicFiles.WriteText(path, existing + line + Environment.NewLine);
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private List<DiscoveredJournal> FindIncompleteForwardJournals(OperationRequest request, ResolvedMigrationTask task)
    {
        var workspace = new PortableWorkspace(request.BaseDirectory);
        return workspace.FindIncompleteForwardJournals(task.Source, task.Destination, FindDestinationRoot(task.Destination)).ToList();
    }

    private static OperationTaskResult RecoveryBlockedTask(ResolvedMigrationTask task, IReadOnlyList<DiscoveredJournal> journals) => new()
    {
        TaskId = task.TaskId,
        Group = task.Group,
        Source = task.Source,
        Destination = task.Destination,
        DestinationSource = task.DestinationSource,
        IsBroadRoot = task.IsBroadRoot,
        Status = "PREFLIGHT_FAILED",
        TransactionState = "RecoveryRequired",
        Retryable = true,
        JournalPath = journals[0].Path,
        VerificationTier = "StructuralMetadata",
        Detail = $"Blocked by incomplete forward migration journal(s): {string.Join("; ", journals.Select(x => $"{x.Path} (state={x.Journal.State})"))}. Resolve or recover the journal before retrying; no new migration transaction was started."
    };

    private static string FindDestinationRoot(string destination)
    {
        var full = Path.GetFullPath(destination);
        var marker = full.IndexOf("\\AppDataMoved", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0 && (full.Length == marker + "\\AppDataMoved".Length || full[marker + "\\AppDataMoved".Length] == '\\')) return full[..(marker + "\\AppDataMoved".Length)];
        return Path.GetDirectoryName(full) ?? Path.GetPathRoot(full)!;
    }
    private static bool PathsEqual(string? left, string right) => !string.IsNullOrWhiteSpace(left) && string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static OperationTaskResult NewTaskResult(ResolvedMigrationTask task) => new()
    {
        TaskId = task.TaskId, Group = task.Group, Source = task.Source, Destination = task.Destination,
        DestinationSource = task.DestinationSource, IsBroadRoot = task.IsBroadRoot, BroadRootAcknowledged = false,
        VerificationTier = "StructuralMetadata"
    };
    private static OperationProgress Progress(OperationRequest request, ResolvedMigrationTask task, string stage, string message, bool critical = false) => new() { OperationId = request.OperationId, TaskId = task.TaskId, Stage = stage, Message = message, CriticalSection = critical };
    private static OperationResult Complete(OperationResult result) { result.CompletedUtc = DateTimeOffset.UtcNow; return result; }
    private static OperationTaskResult FromHistory(HistoryRecord item, string status) => new() { TaskId = item.HistoryId, Group = item.Group, Source = item.Source, Destination = item.Destination, DestinationSource = item.DestinationSource, IsBroadRoot = item.BroadRootAcknowledged, BroadRootAcknowledged = item.BroadRootAcknowledged, Status = status, TransactionState = item.TransactionState, Detail = item.Detail, Retryable = item.Retryable, FileCount = item.FileCount, SizeBytes = item.SizeBytes, JournalPath = item.JournalPath, ManifestPath = item.ManifestPath, QuarantinePath = item.QuarantinePath, ConflictCount = item.ConflictCount, VerificationTier = item.VerificationTier };
    private static void SaveHistory(HistoryService history, OperationRequest request, OperationTaskResult row, DateTimeOffset started, string? operationId = null) => history.Save(new HistoryRecord
    {
        OperationId = operationId ?? request.OperationId, Kind = request.Kind, TaskId = row.TaskId, Group = row.Group, Source = row.Source, Destination = row.Destination,
        CanonicalSource = Path.GetFullPath(row.Source), CanonicalDestination = Path.GetFullPath(row.Destination), DestinationSource = row.DestinationSource, BroadRootAcknowledged = row.BroadRootAcknowledged,
        StartedUtc = started, CompletedUtc = DateTimeOffset.UtcNow, FileCount = row.FileCount, SizeBytes = row.SizeBytes, Status = row.Status,
        TransactionState = row.TransactionState, VerificationTier = row.VerificationTier, VerificationResult = row.Detail, Detail = row.Detail, Retryable = row.Retryable,
        ConflictCount = row.ConflictCount, ResolvedTaskSetHash = request.ResolvedTaskSetHash,
        RollbackEligible = request.Kind == OperationKind.Migrate && row.Status == "OK" && row.TransactionState == "Committed", JournalPath = row.JournalPath, ManifestPath = row.ManifestPath, QuarantinePath = row.QuarantinePath
    });

    private sealed class MergeResult { public int Copied { get; set; } public int Conflicts { get; set; } public int Archived { get; set; } public int Overwritten { get; set; } public bool Aborted { get; set; } }
}
