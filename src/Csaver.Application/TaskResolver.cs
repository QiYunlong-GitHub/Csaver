using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Csaver.Core;

namespace Csaver.Application;

public sealed class TaskResolver
{
    private readonly Dictionary<string, string> roots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Local"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["Roaming"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ["Home"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ["Documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
    };

    public IReadOnlyDictionary<string, string> RootMap => roots;

    public List<ResolvedMigrationTask> Resolve(MigrationConfiguration configuration, string? operationDestinationRoot = null)
    {
        var destination = SelectDestinationRoot(operationDestinationRoot, configuration.DestRoot);
        var output = new List<ResolvedMigrationTask>();
        foreach (var task in configuration.Tasks)
        {
            foreach (var source in ResolveSources(task))
            {
                var target = ResolveDestination(task, source, destination.Path);
                output.Add(new ResolvedMigrationTask
                {
                    TaskId = task.TaskId ?? $"task/{task.Group}",
                    Group = task.Group,
                    Source = source,
                    Destination = target,
                    DestinationSource = string.IsNullOrWhiteSpace(task.Dest) ? destination.Source : "TaskDest",
                    DestinationRoot = destination.Path,
                    Guard = task.Guard ?? string.Empty,
                    Disabled = task.Disabled,
                    IsBroadRoot = IsBroadRoot(source),
                    TrustedReparsePoints = NormalizeTrusted(task.TrustedReparsePoints)
                });
            }
        }

        AddDynamicLark(output, destination);
        var sources = output.GroupBy(x => Normalize(x.Source), StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Any(x => x.Count() > 1)) throw new InvalidOperationException("Resolved task set contains duplicate canonical sources.");
        var destinations = output.GroupBy(x => Normalize(x.Destination), StringComparer.OrdinalIgnoreCase).ToList();
        if (destinations.Any(x => x.Count() > 1)) throw new InvalidOperationException("Resolved task set contains duplicate canonical destinations.");
        for (var i = 0; i < output.Count; i++)
            for (var j = i + 1; j < output.Count; j++)
                if (Relation(output[i].Source, output[j].Source) != "Disjoint" || Relation(output[i].Destination, output[j].Destination) != "Disjoint")
                    throw new InvalidOperationException($"Resolved task set contains overlapping paths: {output[i].TaskId} and {output[j].TaskId}.");
        return output;
    }

    public static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded.StartsWith('~')) expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        if (expanded.Contains('%')) throw new InvalidOperationException($"Unresolved environment variable in path: {path}");
        if (!Path.IsPathRooted(expanded)) throw new InvalidOperationException($"Path must be absolute: {path}");
        var full = Path.GetFullPath(expanded);
        var root = Path.GetPathRoot(full);
        return string.Equals(full.TrimEnd('\\', '/'), root?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? root!
            : full.TrimEnd('\\', '/');
    }

    public static string Relation(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return "Equal";
        if (b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase)) return "LeftAncestor";
        if (a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)) return "RightAncestor";
        return "Disjoint";
    }

    public static bool IsBroadRoot(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(x => x.Equals("Tencent", StringComparison.OrdinalIgnoreCase) ||
                      x.Equals("Google", StringComparison.OrdinalIgnoreCase) ||
                      x.Equals("kingsoft", StringComparison.OrdinalIgnoreCase));

    private IEnumerable<string> ResolveSources(MigrationTaskDefinition task)
    {
        string source;
        if (!string.IsNullOrWhiteSpace(task.Source)) source = task.Source;
        else if (string.Equals(task.Root, "Absolute", StringComparison.OrdinalIgnoreCase)) source = task.Rel ?? string.Empty;
        else
        {
            var basePath = task.Root is not null && roots.TryGetValue(task.Root, out var mapped) ? mapped : task.Root;
            if (string.IsNullOrWhiteSpace(basePath)) throw new InvalidOperationException($"Root cannot be resolved for {task.TaskId}.");
            source = Path.Combine(ExpandPath(basePath), task.Rel ?? string.Empty);
        }

        source = Environment.ExpandEnvironmentVariables(source);
        if (!source.Contains('*') && !source.Contains('?')) return [ExpandPath(source)];
        var parent = Path.GetDirectoryName(source) ?? throw new InvalidOperationException("Wildcard parent is unavailable.");
        var pattern = Path.GetFileName(source);
        if (pattern.Contains('\\') || pattern.Contains('/')) throw new InvalidOperationException("Wildcards are final-component only.");
        var matches = Directory.Exists(parent) ? Directory.EnumerateDirectories(parent, pattern, SearchOption.TopDirectoryOnly).Select(ExpandPath).ToList() : [];
        if (matches.Count == 0) throw new InvalidOperationException($"Wildcard task has no matches: {source}");
        return matches;
    }

    private string ResolveDestination(MigrationTaskDefinition task, string source, string destinationRoot)
    {
        if (!string.IsNullOrWhiteSpace(task.Dest)) return ExpandPath(task.Dest);
        var category = task.Root is not null && roots.ContainsKey(task.Root) ? task.Root : "Custom";
        if (string.Equals(task.Root, "Absolute", StringComparison.OrdinalIgnoreCase)) category = string.Empty;
        var leaf = Path.GetFileName(source.TrimEnd('\\'));
        return ExpandPath(string.IsNullOrEmpty(category) ? Path.Combine(destinationRoot, leaf) : Path.Combine(destinationRoot, category, leaf));
    }

    private static (string Path, string Source) SelectDestinationRoot(string? operationRoot, string? jsonRoot)
    {
        if (!string.IsNullOrWhiteSpace(operationRoot)) return (ExpandPath(operationRoot), "OperationDestRoot");
        if (!string.IsNullOrWhiteSpace(jsonRoot)) return (ExpandPath(jsonRoot), "JsonDestRoot");
        var candidate = DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed && !x.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.AvailableFreeSpace).FirstOrDefault();
        return candidate is null ? ("D:\\AppDataMoved", "Default") : (Path.Combine(candidate.RootDirectory.FullName, "AppDataMoved"), "AutoDiscovery");
    }

    private void AddDynamicLark(List<ResolvedMigrationTask> output, (string Path, string Source) destination)
    {
        var roaming = roots["Roaming"];
        if (!Directory.Exists(roaming)) return;
        foreach (var path in Directory.EnumerateDirectories(roaming, "LarkShell*", SearchOption.TopDirectoryOnly))
        {
            var full = ExpandPath(path);
            output.Add(new ResolvedMigrationTask
            {
                TaskId = "dynamic-lark-" + Path.GetFileName(full).ToLowerInvariant(), Group = "Feishu", Source = full,
                Destination = ExpandPath(Path.Combine(destination.Path, "Roaming", Path.GetFileName(full))),
                DestinationSource = destination.Source, DestinationRoot = destination.Path, Guard = "feishu|lark", IsDynamic = true
            });
        }
    }

    public static FrozenTask Freeze(ResolvedMigrationTask task) => new()
    {
        TaskId = task.TaskId, Group = task.Group, Source = Normalize(task.Source), Destination = Normalize(task.Destination),
        DestinationSource = task.DestinationSource, DestinationRoot = Normalize(task.DestinationRoot), Disabled = task.Disabled,
        IsDynamic = task.IsDynamic, IsBroadRoot = task.IsBroadRoot, TrustedReparsePoints = NormalizeTrusted(task.TrustedReparsePoints)
    };

    public static string ComputeResolvedTaskSetHash(IEnumerable<FrozenTask> tasks)
    {
        var canonical = tasks.OrderBy(x => x.TaskId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
            .Select(x => string.Join("|", x.TaskId, x.Group, Normalize(x.Source), Normalize(x.Destination), x.DestinationSource, Normalize(x.DestinationRoot), x.Disabled, x.IsDynamic, x.IsBroadRoot, string.Join(",", x.TrustedReparsePoints)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", canonical)))).ToLowerInvariant();
    }

    private static List<string> NormalizeTrusted(IEnumerable<string>? values)
    {
        var output = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? Array.Empty<string>())
            if (ReparsePointPaths.TryNormalizeDeclaration(value, out var normalized)) output.Add(normalized.ToLowerInvariant());
        return [.. output];
    }

    public static bool FrozenEquals(IEnumerable<ResolvedMigrationTask> live, IEnumerable<FrozenTask> frozen)
    {
        var left = live.Select(Freeze).OrderBy(x => x.TaskId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Source, StringComparer.OrdinalIgnoreCase).ToList();
        var right = frozen.OrderBy(x => x.TaskId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Source, StringComparer.OrdinalIgnoreCase).ToList();
        return ComputeResolvedTaskSetHash(left) == ComputeResolvedTaskSetHash(right) && JsonSerializer.Serialize(left, JsonDefaults.Options) == JsonSerializer.Serialize(right, JsonDefaults.Options);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}
