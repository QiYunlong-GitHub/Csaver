using System.Text.Json;
using System.Text.RegularExpressions;
using Csaver.Core;

namespace Csaver.Application;

public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions ConfigurationWriteOptions = CreateConfigurationWriteOptions();

    public ConfigurationLoadResult LoadFile(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            return new ConfigurationLoadResult { Issues = [new("ConfigurationReadFailed", ex.Message)] };
        }
    }

    public ConfigurationLoadResult Parse(string json)
    {
        var issues = new List<ValidationIssue>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            string? destinationRoot = null;
            JsonElement tasksElement;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                tasksElement = document.RootElement;
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (!TryGetUniqueProperty(document.RootElement, "Tasks", out tasksElement, out var tasksAmbiguous))
                {
                    return tasksAmbiguous
                        ? Invalid(json, "AmbiguousRootProperty", "Configuration contains multiple Tasks properties that differ only by letter case.")
                        : Invalid(json, "TopLevelShapeInvalid", "Configuration must be an array or an object containing Tasks.");
                }

                if (TryGetUniqueProperty(document.RootElement, "DestRoot", out var destination, out var destinationAmbiguous))
                {
                    if (destination.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
                        issues.Add(new("DestRootTypeInvalid", "DestRoot must be a string."));
                    else destinationRoot = destination.GetString();
                }
                else if (destinationAmbiguous)
                {
                    issues.Add(new("AmbiguousRootProperty", "Configuration contains multiple DestRoot properties that differ only by letter case."));
                }
            }
            else
            {
                return Invalid(json, "TopLevelShapeInvalid", "Configuration must be an array or an object containing Tasks.");
            }

            if (tasksElement.ValueKind != JsonValueKind.Array)
                return Invalid(json, "TasksMustBeArray", "Tasks must be a JSON array.");

            var tasks = new List<MigrationTaskDefinition>();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var knownProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "TaskId", "Group", "Root", "Rel", "Source", "Dest", "Guard", "Disabled", "TrustedReparsePoints"
            };
            var index = 0;
            foreach (var element in tasksElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new("TaskShapeInvalid", $"Task {index} must be an object.", TaskId: $"task[{index}]"));
                    index++;
                    continue;
                }

                MigrationTaskDefinition? task;
                try { task = element.Deserialize<MigrationTaskDefinition>(JsonDefaults.Options); }
                catch (Exception ex)
                {
                    issues.Add(new("TaskTypeInvalid", $"Task {index}: {ex.Message}", TaskId: $"task[{index}]"));
                    index++;
                    continue;
                }

                task ??= new MigrationTaskDefinition();
                task.TaskId = string.IsNullOrWhiteSpace(task.TaskId) ? $"task[{index}]/{task.Group}" : task.TaskId.Trim();
                foreach (var property in element.EnumerateObject())
                    if (!knownProperties.Contains(property.Name))
                        issues.Add(new("UnknownTaskProperty", $"Task {index}: unknown property '{property.Name}' is ignored.", IssueSeverity.Warning, TaskId: task.TaskId));
                ValidateTask(task, index, identities, issues);
                tasks.Add(task);
                index++;
            }

            if (tasks.Count == 0) issues.Add(new("TasksEmpty", "At least one task is required."));
            var configuration = new MigrationConfiguration { DestRoot = destinationRoot, Tasks = tasks };
            return new ConfigurationLoadResult { Configuration = configuration, Issues = issues, RawJson = json };
        }
        catch (JsonException ex)
        {
            return Invalid(json, "JsonMalformed", $"Invalid JSON at line {ex.LineNumber}, byte {ex.BytePositionInLine}: {ex.Message}");
        }
    }

    public ConfigurationLoadResult ValidateAndSave(string path, string json)
    {
        var result = Parse(json);
        if (!result.IsValid) return result;
        var normalized = SerializeCanonical(result.Configuration!);
        AtomicFiles.WriteText(path, normalized, backupExisting: true);
        var persisted = LoadFile(path);
        if (!persisted.IsValid)
            throw new InvalidDataException("The saved configuration could not be reloaded: " + string.Join("; ", persisted.Issues.Select(x => x.Message)));
        return new ConfigurationLoadResult { Configuration = persisted.Configuration, Issues = persisted.Issues, RawJson = normalized };
    }

    public string SerializeCanonical(MigrationConfiguration configuration) =>
        JsonSerializer.Serialize(configuration, ConfigurationWriteOptions);

    private static JsonSerializerOptions CreateConfigurationWriteOptions()
    {
        return new JsonSerializerOptions(JsonDefaults.Options)
        {
            PropertyNamingPolicy = null
        };
    }

    private static bool TryGetUniqueProperty(JsonElement element, string name, out JsonElement value, out bool ambiguous)
    {
        value = default;
        ambiguous = false;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found)
            {
                ambiguous = true;
                value = default;
                return false;
            }

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static void ValidateTask(MigrationTaskDefinition task, int index, HashSet<string> identities, List<ValidationIssue> issues)
    {
        var id = task.TaskId ?? $"task[{index}]";
        if (string.IsNullOrWhiteSpace(task.Group)) issues.Add(new("GroupRequired", $"Task {index} requires a Group.", TaskId: id));
        if (!identities.Add(id)) issues.Add(new("DuplicateTaskIdentity", $"Duplicate task identity: {id}.", TaskId: id));
        if (string.IsNullOrWhiteSpace(task.Source) && (string.IsNullOrWhiteSpace(task.Root) || string.IsNullOrWhiteSpace(task.Rel)))
            issues.Add(new("SourceDefinitionMissing", "Task requires Source or both Root and Rel.", TaskId: id));
        if (!string.IsNullOrWhiteSpace(task.Source) && task.Source.IndexOf('\0') >= 0)
            issues.Add(new("SourceContainsNull", "Source contains a null character.", TaskId: id));
        if (!string.IsNullOrWhiteSpace(task.Guard))
        {
            try { _ = new Regex(task.Guard, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (ArgumentException ex) { issues.Add(new("GuardRegexInvalid", ex.Message, TaskId: id)); }
        }

        var wildcardText = task.Source ?? task.Rel ?? string.Empty;
        var firstWildcard = wildcardText.IndexOfAny(['*', '?']);
        if (firstWildcard >= 0 && wildcardText[(firstWildcard + 1)..].IndexOfAny(['\\', '/']) >= 0)
            issues.Add(new("WildcardShapeInvalid", "Wildcards are allowed only in the final path component.", TaskId: id));

        var normalized = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in task.TrustedReparsePoints ?? new List<string>())
        {
            if (!ReparsePointPaths.TryNormalizeDeclaration(value, out var path))
                issues.Add(new("TrustedReparsePointInvalid", $"Task {index}: '{value}' is not an in-tree relative path.", TaskId: id));
            else normalized.Add(path);
        }
        task.TrustedReparsePoints = [.. normalized];
    }

    private static ConfigurationLoadResult Invalid(string raw, string code, string message) =>
        new() { RawJson = raw, Issues = [new(code, message)] };
}
