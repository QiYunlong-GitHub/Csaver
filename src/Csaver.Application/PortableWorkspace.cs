using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Csaver.Application;

public sealed class PortableWorkspace
{
    public PortableWorkspace(string baseDirectory)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory);
        ConfigurationDirectory = Path.Combine(BaseDirectory, "config");
        ConfigurationPath = Path.Combine(ConfigurationDirectory, "migration-tasks.json");
        DataDirectory = Path.Combine(BaseDirectory, "data");
        HistoryDirectory = Path.Combine(DataDirectory, "history");
        ImportsDirectory = Path.Combine(DataDirectory, "imports");
        WorkerResultsDirectory = Path.Combine(DataDirectory, "worker-results");
        LogsDirectory = Path.Combine(BaseDirectory, "logs");
    }

    public string BaseDirectory { get; }
    public string ConfigurationDirectory { get; }
    public string ConfigurationPath { get; }
    public string DataDirectory { get; }
    public string HistoryDirectory { get; }
    public string ImportsDirectory { get; }
    public string WorkerResultsDirectory { get; }
    public string LogsDirectory { get; }

    public void Initialize(string? defaultConfiguration = null)
    {
        EnsureWritableDirectory(BaseDirectory);
        Directory.CreateDirectory(ConfigurationDirectory);
        Directory.CreateDirectory(HistoryDirectory);
        Directory.CreateDirectory(ImportsDirectory);
        Directory.CreateDirectory(WorkerResultsDirectory);
        Directory.CreateDirectory(LogsDirectory);

        if (!File.Exists(ConfigurationPath))
        {
            defaultConfiguration ??= ReadEmbeddedDefault();
            AtomicFiles.WriteText(ConfigurationPath, defaultConfiguration);
        }
    }

    public string ComputeConfigurationSha256()
    {
        using var stream = File.OpenRead(ConfigurationPath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public IEnumerable<string> FindIncompleteJournals()
    {
        var journalStore = new JournalStore();
        foreach (var root in DestinationRootRegistry.DiscoverRoots())
        {
            var journalDirectory = Path.Combine(root, ".csaver-journal");
            if (!Directory.Exists(journalDirectory)) continue;
            foreach (var path in Directory.EnumerateFiles(journalDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var journal = journalStore.Load(path);
                if (journal is null || !JournalStore.IsTerminalForwardState(journal.State)) yield return path;
            }
        }
    }

    public IEnumerable<DiscoveredJournal> FindIncompleteForwardJournals(string source, string destination, string destinationRoot)
    {
        var journalStore = new JournalStore();
        foreach (var root in DestinationRootRegistry.DiscoverRoots([destinationRoot]))
        {
            var journalDirectory = Path.Combine(root, ".csaver-journal");
            foreach (var journal in journalStore.FindIncompleteForwardJournals(journalDirectory, source, destination))
                yield return journal;
        }
    }

    private static string ReadEmbeddedDefault()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Csaver.DefaultMigrationTasks.json")
            ?? throw new InvalidOperationException("Embedded default migration configuration is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void EnsureWritableDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".csaver-write-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "write-test", Encoding.ASCII);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new IOException($"Csaver must be extracted to a writable folder. Move it out of protected locations such as Program Files. ({ex.Message})", ex);
        }
    }
}

public static class DestinationRootRegistry
{
    public static IEnumerable<string> DiscoverRoots(IEnumerable<string>? additional = null)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (additional is not null)
        {
            foreach (var root in additional) Add(root);
        }

        var registry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".appdata-moved", "roots.json");
        if (File.Exists(registry))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(registry));
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("Root", out var root) && root.ValueKind == System.Text.Json.JsonValueKind.String) Add(root.GetString());
                    }
                }
            }
            catch { }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady && !string.Equals(x.Name, "C:\\", StringComparison.OrdinalIgnoreCase)))
        {
            Add(Path.Combine(drive.RootDirectory.FullName, "AppDataMoved"));
        }

        return values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value)).TrimEnd(Path.DirectorySeparatorChar);
                if (Directory.Exists(full)) values.Add(full);
            }
            catch { }
        }
    }
}
