using System.Text.Json;
using Csaver.Core;

namespace Csaver.Application;

public sealed class QuarantineService(ITreeService trees)
{
    public List<QuarantineEntry> Load()
    {
        var output = new List<QuarantineEntry>();
        foreach (var root in DestinationRootRegistry.DiscoverRoots())
        {
            var quarantineRoot = Path.Combine(root, ".csaver-quarantine");
            if (!Directory.Exists(quarantineRoot)) continue;
            foreach (var directory in Directory.EnumerateDirectories(quarantineRoot)) output.Add(Read(directory));
        }
        return output.OrderByDescending(x => x.CreatedUtc).ToList();
    }

    private QuarantineEntry Read(string directory)
    {
        var id = Path.GetFileName(directory);
        var inventoryPath = Path.Combine(directory, "inventory.json");
        var payload = Path.Combine(directory, "payload");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(inventoryPath));
            var root = document.RootElement;
            var stored = Get(root, "StoredPath", payload);
            if (!Path.GetFullPath(stored).Equals(Path.GetFullPath(payload), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("StoredPath does not match the indexed payload path.");
            if (!Directory.Exists(payload)) throw new InvalidDataException("Quarantine payload is missing.");
            long? bytes = null;
            if (Directory.Exists(payload)) bytes = trees.GetStatistics(payload).Bytes;
            var journalPath = Get(root, "Journal", Get(root, "JournalPath", string.Empty));
            var journalState = string.Empty;
            if (File.Exists(journalPath))
            {
                try
                {
                    using var journal = JsonDocument.Parse(File.ReadAllText(journalPath));
                    journalState = Get(journal.RootElement, "State", string.Empty);
                }
                catch { journalState = "Unreadable"; }
            }
            return new QuarantineEntry
            {
                QuarantineId = Get(root, "QuarantineId", id), OperationId = Get(root, "OperationId", id),
                Source = Get(root, "Source", string.Empty), OriginalPath = Get(root, "OriginalPath", string.Empty), StoredPath = stored,
                CreatedUtc = DateTimeOffset.TryParse(Get(root, "CreatedUtc", string.Empty), out var created) ? created : null,
                SizeBytes = bytes, State = Get(root, "CleanupState", Get(root, "Status", "Retained")), JournalState = journalState,
                PayloadExists = Directory.Exists(payload)
            };
        }
        catch (Exception ex)
        {
            return new QuarantineEntry { QuarantineId = id, StoredPath = payload, PayloadExists = Directory.Exists(payload), State = "Invalid", Error = ex.Message };
        }
    }

    private static string Get(JsonElement root, string name, string fallback)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString() ?? fallback;
        return fallback;
    }
}
