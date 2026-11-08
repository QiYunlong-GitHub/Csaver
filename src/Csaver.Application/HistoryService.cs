using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Csaver.Core;

namespace Csaver.Application;

public sealed class HistoryService(PortableWorkspace workspace)
{
    public void Save(HistoryRecord record)
    {
        var name = $"{record.StartedUtc:yyyyMMdd-HHmmssfff}-{Safe(record.OperationId)}-{Safe(record.TaskId)}.json";
        AtomicFiles.WriteJson(Path.Combine(workspace.HistoryDirectory, name), record);
    }

    public void MarkRollbackConsumed(HistoryRecord record)
    {
        Save(new HistoryRecord
        {
            HistoryId = record.HistoryId, OperationId = record.OperationId, Kind = record.Kind, TaskId = record.TaskId, Group = record.Group,
            Source = record.Source, Destination = record.Destination, CanonicalSource = record.CanonicalSource, CanonicalDestination = record.CanonicalDestination,
            DestinationSource = record.DestinationSource, BroadRootAcknowledged = record.BroadRootAcknowledged, StartedUtc = record.StartedUtc, CompletedUtc = DateTimeOffset.UtcNow,
            FileCount = record.FileCount, SizeBytes = record.SizeBytes, Status = record.Status, TransactionState = record.TransactionState,
            VerificationTier = record.VerificationTier, VerificationResult = record.VerificationResult, Detail = record.Detail + "; rollback consumed", Retryable = false,
            RollbackEligible = false, JournalPath = record.JournalPath, ManifestPath = record.ManifestPath, QuarantinePath = record.QuarantinePath,
            ConflictCount = record.ConflictCount, ResolvedTaskSetHash = record.ResolvedTaskSetHash, Imported = record.Imported
        });
    }
    public List<HistoryRecord> Load(bool importExisting = true)
    {
        if (importExisting) ImportExisting();
        var output = new List<HistoryRecord>();
        foreach (var file in Directory.EnumerateFiles(workspace.HistoryDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var record = JsonSerializer.Deserialize<HistoryRecord>(File.ReadAllText(file), JsonDefaults.Options);
                if (record is not null) output.Add(record);
            }
            catch { }
        }
        return output.GroupBy(Key, StringComparer.OrdinalIgnoreCase).Select(x => x.OrderBy(y => y.Imported).First())
            .OrderByDescending(x => x.StartedUtc).ToList();
    }

    public void ImportExisting()
    {
        foreach (var root in DestinationRootRegistry.DiscoverRoots())
        {
            ImportManifest(root, "_migration_manifest.csv", OperationKind.Migrate);
            ImportManifest(root, "_repair_manifest.csv", OperationKind.Repair);
            ImportManifest(root, "_rollback_manifest.csv", OperationKind.Rollback);
            ImportManifest(root, "_migration_manifest.legacy-adoptions.csv", OperationKind.Migrate);
        }
    }

    public void ExportCsv(string path, IEnumerable<HistoryRecord> records)
    {
        var lines = new List<string> { "OperationId,Kind,Group,TaskId,Source,Destination,StartedUtc,CompletedUtc,FileCount,SizeBytes,Status,TransactionState,Detail,RollbackEligible,Imported" };
        lines.AddRange(records.Select(x => string.Join(',',
            Csv(x.OperationId), Csv(x.Kind.ToString()), Csv(x.Group), Csv(x.TaskId), Csv(x.Source), Csv(x.Destination),
            Csv(x.StartedUtc.ToString("O")), Csv(x.CompletedUtc.ToString("O")), Csv(x.FileCount?.ToString() ?? string.Empty),
            Csv(x.SizeBytes?.ToString() ?? string.Empty), Csv(x.Status), Csv(x.TransactionState), Csv(x.Detail),
            Csv(x.RollbackEligible.ToString()), Csv(x.Imported.ToString()))));
        AtomicFiles.WriteText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private void ImportManifest(string root, string fileName, OperationKind kind)
    {
        var path = Path.Combine(root, fileName);
        if (!File.Exists(path)) return;
        List<Dictionary<string, string>> rows;
        try { rows = ParseCsv(File.ReadAllText(path, Encoding.UTF8)); }
        catch { return; }
        foreach (var row in rows)
        {
            var source = Get(row, "Source");
            var destination = Get(row, "Dest");
            if (string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(destination)) continue;
            var operationId = Get(row, "OperationId");
            if (string.IsNullOrWhiteSpace(operationId)) operationId = StableId(path + "|" + source + "|" + destination + "|" + Get(row, "Time"));
            var journal = Get(row, "JournalPath");
            if (!string.IsNullOrWhiteSpace(journal) && !Path.IsPathRooted(journal)) journal = Path.Combine(root, journal);
            var status = Get(row, "Status");
            var state = Get(row, "TransactionState");
            var time = ParseDate(Get(row, "Time"));
            var (files, bytes) = ReadSnapshotTotals(journal);
            var rollbackEligible = kind == OperationKind.Migrate && status.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
                                   (string.IsNullOrWhiteSpace(state) || state.Equals("Committed", StringComparison.OrdinalIgnoreCase)) && File.Exists(journal);
            var record = new HistoryRecord
            {
                HistoryId = "import-" + StableId(path + "|" + operationId + "|" + source), OperationId = operationId, Kind = kind,
                TaskId = Get(row, "TaskId"), Group = Get(row, "Group"), Source = source, Destination = destination,
                CanonicalSource = Default(Get(row, "CanonicalSource"), source), CanonicalDestination = Default(Get(row, "CanonicalDest"), destination), DestinationSource = Get(row, "DestRootSource"),
                BroadRootAcknowledged = IsTrue(Get(row, "BroadRootAcknowledged")),
                StartedUtc = time, CompletedUtc = time, FileCount = files, SizeBytes = bytes, Status = status,
                TransactionState = state, VerificationTier = Default(Get(row, "VerificationTier"), "StructuralMetadata"),
                VerificationResult = Get(row, "Detail"), Detail = Get(row, "Detail"), Retryable = IsTrue(Get(row, "Retryable")),
                RollbackEligible = rollbackEligible, JournalPath = journal, ManifestPath = path,
                QuarantinePath = Get(row, "QuarantinePath"), ConflictCount = int.TryParse(Get(row, "ConflictCount"), out var conflicts) ? conflicts : 0, Imported = true
            };
            var localPath = Path.Combine(workspace.HistoryDirectory, record.HistoryId + ".json");
            if (!File.Exists(localPath)) AtomicFiles.WriteJson(localPath, record);
        }
    }

    private static (long? Files, long? Bytes) ReadSnapshotTotals(string journalPath)
    {
        if (string.IsNullOrWhiteSpace(journalPath) || !File.Exists(journalPath)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(journalPath));
            if (!TryProperty(document.RootElement, "DestinationSnapshot", out var snapshot)) return (null, null);
            if (TryProperty(snapshot, "FileCount", out var count) && count.TryGetInt64(out var directCount) &&
                TryProperty(snapshot, "Bytes", out var bytes) && bytes.TryGetInt64(out var directBytes)) return (directCount, directBytes);
            if (!TryProperty(snapshot, "FileEntries", out var entries) || entries.ValueKind != JsonValueKind.Array) return (null, null);
            long total = 0;
            long files = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                files++;
                if (TryProperty(entry, "Length", out var length) && length.TryGetInt64(out var value)) total += value;
            }
            return (files, total);
        }
        catch { return (null, null); }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static List<Dictionary<string, string>> ParseCsv(string text)
    {
        var rows = ParseCsvRows(text);
        if (rows.Count == 0) return [];
        var headers = rows[0];
        return rows.Skip(1).Where(x => x.Any(y => !string.IsNullOrWhiteSpace(y))).Select(values =>
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++) row[headers[i]] = i < values.Count ? values[i] : string.Empty;
            return row;
        }).ToList();
    }

    private static List<List<string>> ParseCsvRows(string text)
    {
        var output = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted && c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { row.Add(field.ToString()); field.Clear(); }
            else if ((c == '\r' || c == '\n') && !quoted)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear(); output.Add(row); row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); output.Add(row); }
        return output;
    }

    private static string Key(HistoryRecord x) => $"{x.Kind}|{x.OperationId}|{x.Source}|{x.Destination}";
    private static string Get(Dictionary<string, string> row, string name) => row.TryGetValue(name, out var value) ? value : string.Empty;
    private static string Default(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;
    private static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
    private static string Safe(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
