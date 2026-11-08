using System.Text.Json;
using Csaver.Core;

namespace Csaver.Application;

public sealed record DiscoveredJournal(string Path, NativeJournal Journal);

public sealed class JournalStore
{
    private static readonly Dictionary<string, HashSet<string>> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PreflightPassed"] = new(StringComparer.OrdinalIgnoreCase) { "Copying", "Merging", "RestoreStaging", "Skipped", "RecoveryRequired" },
        ["Copying"] = new(StringComparer.OrdinalIgnoreCase) { "Copied", "RecoveryRequired" },
        ["Copied"] = new(StringComparer.OrdinalIgnoreCase) { "Verified", "RecoveryRequired" },
        ["Merging"] = new(StringComparer.OrdinalIgnoreCase) { "Verified", "ConflictAborted", "RecoveryRequired" },
        ["Verified"] = new(StringComparer.OrdinalIgnoreCase) { "SourceVerified", "SourceStaged", "RestoreStaged", "RecoveryRequired" },
        ["SourceVerified"] = new(StringComparer.OrdinalIgnoreCase) { "SourceStaged", "RecoveryRequired" },
        ["SourceStaged"] = new(StringComparer.OrdinalIgnoreCase) { "JunctionCreated", "Recovered", "RecoveryRequired" },
        ["JunctionCreated"] = new(StringComparer.OrdinalIgnoreCase) { "LinkValidated", "Recovered", "RecoveryRequired" },
        ["LinkValidated"] = new(StringComparer.OrdinalIgnoreCase) { "Committed", "CommittedWithStaging", "RecoveryRequired" },
        ["RestoreStaging"] = new(StringComparer.OrdinalIgnoreCase) { "RestoreStaged", "RecoveryRequired" },
        ["RestoreStaged"] = new(StringComparer.OrdinalIgnoreCase) { "SourceReplaced", "RecoveryRequired" },
        ["SourceReplaced"] = new(StringComparer.OrdinalIgnoreCase) { "DestinationQuarantined", "RollbackIncomplete" },
        ["DestinationQuarantined"] = new(StringComparer.OrdinalIgnoreCase) { "COMPLETED", "RollbackIncomplete" },
        ["RecoveryRequired"] = new(StringComparer.OrdinalIgnoreCase) { "Recovered", "Copying", "Merging", "RestoreStaging" },
        ["RollbackIncomplete"] = new(StringComparer.OrdinalIgnoreCase) { "RestoreStaging", "SourceReplaced", "DestinationQuarantined", "COMPLETED" }
    };

    public void Create(string path, NativeJournal journal)
    {
        if (File.Exists(path)) throw new IOException($"Journal already exists: {path}");
        AtomicFiles.WriteJson(path, journal);
    }

    public void Transition(string path, NativeJournal journal, string next, string error = "", bool retryable = true)
    {
        if (!Allowed.TryGetValue(journal.State, out var nextStates) || !nextStates.Contains(next))
            throw new InvalidOperationException($"Invalid journal transition: {journal.State} -> {next}");
        journal.State = next;
        journal.LastError = error;
        journal.Retryable = retryable;
        journal.UpdatedUtc = DateTimeOffset.UtcNow;
        AtomicFiles.WriteJson(path, journal);
    }

    public NativeJournal? Load(string path)
    {
        try { return JsonSerializer.Deserialize<NativeJournal>(File.ReadAllText(path), JsonDefaults.Options); }
        catch { return null; }
    }

    public NativeJournal RecoverPreCutover(string path, string operationId, string metadata)
    {
        var current = Load(path) ?? throw new InvalidDataException($"Journal could not be loaded: {path}");
        if (!string.Equals(current.Kind, "ForwardMigration", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected journal identity changed; recovery is refused.");
        if (!string.Equals(current.State, "RecoveryRequired", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Only a RecoveryRequired journal can be recovered; current state is {current.State}.");

        current.RecoveryMetadata = metadata;
        Transition(path, current, "Recovered", current.LastError, retryable: false);
        return Load(path) ?? throw new IOException($"Recovered journal could not be reloaded: {path}");
    }

    public IEnumerable<DiscoveredJournal> FindIncompleteForwardJournals(string journalDirectory, string source, string destination)
    {
        if (!Directory.Exists(journalDirectory)) yield break;

        string canonicalSource;
        string canonicalDestination;
        try
        {
            canonicalSource = CanonicalPath(source);
            canonicalDestination = CanonicalPath(destination);
        }
        catch
        {
            yield break;
        }

        string[] paths;
        try { paths = Directory.GetFiles(journalDirectory, "*.json", SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var path in paths)
        {
            var journal = Load(path);
            if (journal is null || !string.Equals(journal.Kind, "ForwardMigration", StringComparison.OrdinalIgnoreCase) || IsTerminalForwardState(journal.State)) continue;
            if (PathsEqual(journal.CanonicalSource, canonicalSource) && PathsEqual(journal.CanonicalDest, canonicalDestination))
                yield return new DiscoveredJournal(path, journal);
        }
    }

    public static bool IsTerminalForwardState(string? state) => state is not null &&
        (state.Equals("Committed", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("Skipped", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("Recovered", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase));

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try { return CanonicalPath(left).Equals(right, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string CanonicalPath(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}
