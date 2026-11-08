using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Csaver.Application;
using Csaver.Core;
using Csaver.Windows;

namespace Csaver.App;

public partial class MainWindow : Window
{
    private readonly PortableWorkspace workspace;
    private readonly ConfigurationService configurationService = new();
    private readonly TaskResolver resolver = new();
    private readonly WindowsJunctionService junctions = new();
    private readonly WindowsTreeService trees = new();
    private readonly NativeOperationEngine engine;
    private readonly HistoryService history;
    private readonly LocalizationService localizer = new();
    private readonly ObservableCollection<SelectableTask> taskRows = [];
    private readonly ObservableCollection<MigrationTaskDefinition> configurationRows = [];
    private List<HistoryRecord> historyRows = [];
    private CancellationTokenSource? operationCancellation;
    private bool criticalSection;

    public MainWindow()
    {
        InitializeComponent();
        workspace = new PortableWorkspace(AppContext.BaseDirectory);
        workspace.Initialize();
        engine = new NativeOperationEngine(junctions, trees, configurationService, resolver, new JournalStore());
        history = new HistoryService(workspace);
        TasksGrid.ItemsSource = taskRows;
        LoadConfiguration();
        BuildCommands();
        RefreshTasks();
        RefreshHistory();
        RefreshQuarantine();
        Closing += OnClosing;
        var incomplete = workspace.FindIncompleteJournals().Take(10).ToList();
        if (incomplete.Count > 0) AppendOutput("Recovery-required journals detected:\n" + string.Join("\n", incomplete));
    }

    private void BuildCommands(OperationKind? selected = null)
    {
        selected ??= (CommandBox.SelectedItem as CommandChoice)?.Kind ?? OperationKind.ExplainConfiguration;
        var choices = new[]
        {
            Choice(OperationKind.ExplainConfiguration, "Explain Configuration", "ExplainConfiguration", false),
            Choice(OperationKind.PreviewMigration, "Preview Migration", "PreviewMigration", false),
            Choice(OperationKind.RecoverIncompleteMigration, "Recover Incomplete Transaction", "RecoverIncompleteMigration", true),
            Choice(OperationKind.Migrate, "Run Migration", "Migrate", true),
            Choice(OperationKind.PreviewRepair, "Preview Repair", "PreviewRepair", false),
            Choice(OperationKind.Repair, "Run Repair", "Repair", true),
            Choice(OperationKind.ListRollback, "List Rollback Candidates", "ListRollback", false, false),
            Choice(OperationKind.PreviewRollback, "Preview Rollback", "PreviewRollback", false, false),
            Choice(OperationKind.Rollback, "Run Rollback", "Rollback", true, false),
            Choice(OperationKind.ViewQuarantine, "View Quarantine", "ViewQuarantine", false, false)
        };
        CommandBox.ItemsSource = choices;
        CommandBox.SelectedItem = choices.First(x => x.Kind == selected);
    }

    private CommandChoice Choice(OperationKind kind, string name, string key, bool mutating, bool usesTasks = true) =>
        new(kind, localizer.IsChinese ? TranslateCommandName(kind) : name, localizer[key], mutating, usesTasks);

    private static string TranslateCommandName(OperationKind kind) => kind switch
    {
        OperationKind.ExplainConfiguration => "解释配置", OperationKind.PreviewMigration => "预览迁移", OperationKind.Migrate => "运行迁移",
        OperationKind.PreviewRepair => "预览修复", OperationKind.Repair => "运行修复", OperationKind.ListRollback => "列出回滚候选项",
        OperationKind.PreviewRollback => "预览回滚", OperationKind.Rollback => "运行回滚", OperationKind.ViewQuarantine => "查看隔离区", OperationKind.RecoverIncompleteMigration => "恢复未完成事务", _ => kind.ToString()
    };

    private void LoadConfiguration()
    {
        ConfigurationEditor.Text = File.ReadAllText(workspace.ConfigurationPath, Encoding.UTF8);
        var loaded = configurationService.Parse(ConfigurationEditor.Text);
        configurationRows.Clear();
        if (loaded.Configuration is not null)
        {
            DestinationRootEditor.Text = loaded.Configuration.DestRoot ?? string.Empty;
            foreach (var task in loaded.Configuration.Tasks) configurationRows.Add(task);
        }
        ConfigTasksGrid.ItemsSource = configurationRows;
    }

    private void RefreshTasks()
    {
        taskRows.Clear();
        var command = CommandBox.SelectedItem as CommandChoice;
        if (command is not null && !command.UsesTasks)
        {
            if (command.Kind == OperationKind.ViewQuarantine) { RefreshQuarantine(); MainTabs.SelectedItem = QuarantineTab; return; }
            foreach (var item in history.Load().Where(x => x.Kind == OperationKind.Migrate && x.RollbackEligible))
                taskRows.Add(new SelectableTask { Id = item.HistoryId, Group = item.Group, Source = item.Source, Destination = item.Destination, State = item.Status, SizeBytes = item.SizeBytes, IsSelected = false });
            return;
        }
        var loaded = configurationService.LoadFile(workspace.ConfigurationPath);
        if (!loaded.IsValid || loaded.Configuration is null) { AppendOutput(string.Join(Environment.NewLine, loaded.Issues.Select(x => x.Message))); return; }
        foreach (var task in resolver.Resolve(loaded.Configuration))
        {
            var info = junctions.Inspect(task.Source);
            var state = !info.Exists ? "Absent" : info.IsJunction ? (PathsEqual(info.Target, task.Destination) ? "HealthyJunction" : "WrongJunction") : info.IsReparsePoint ? "UnsafeReparse" : "Directory";
            taskRows.Add(new SelectableTask { Id = task.TaskId, Group = task.Group, Source = task.Source, Destination = task.Destination, Guard = task.Guard, State = state, Enabled = !task.Disabled, IsSelected = command?.Kind == OperationKind.ExplainConfiguration || !task.Disabled });
        }
    }

    private async void RunButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (CommandBox.SelectedItem is not CommandChoice command) return;
        if (command.Kind == OperationKind.ViewQuarantine) { RefreshQuarantine(); MainTabs.SelectedItem = QuarantineTab; return; }
        var checkedRows = taskRows.Where(x => x.IsSelected).ToList();
        var selected = checkedRows.Where(x => command.Kind == OperationKind.ExplainConfiguration || x.Enabled).ToList();
        if (selected.Count == 0 && command.Kind != OperationKind.ListRollback)
        {
            if (checkedRows.Count > 0)
            {
                var disabledIds = string.Join(", ", checkedRows.Where(x => !x.Enabled).Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase));
                MessageBox.Show(localizer["AllSelectedDisabled"] + (disabledIds.Length > 0 ? "\n\n" + disabledIds : string.Empty), "Csaver", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else MessageBox.Show(localizer["NoSelection"], "Csaver", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (command.Mutating && selected.Any(x => TaskResolver.IsBroadRoot(x.Source)) && BroadRootCheck.IsChecked != true)
        {
            MessageBox.Show(localizer["BroadAck"], "Csaver", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        if (command.Mutating && MessageBox.Show(command.Kind == OperationKind.RecoverIncompleteMigration
            ? command.Description + "\n\n" + string.Join("\n", selected.Select(x => x.Source + "  →  " + x.Destination)) + "\n\nThe matching RecoveryRequired journal will be validated and atomically marked Recovered. No source, destination, staging data, manifest, or journal will be deleted."
            : command.Description + "\n\n" + string.Join("\n", selected.Select(x => x.Source + "  →  " + x.Destination)), "Confirm", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        operationCancellation = new CancellationTokenSource();
        SetRunning(true);
        var frozen = new List<FrozenTask>();
        if (command.UsesTasks)
        {
            var loaded = configurationService.LoadFile(workspace.ConfigurationPath);
            if (!loaded.IsValid || loaded.Configuration is null) throw new InvalidDataException("Configuration is invalid.");
            frozen = resolver.Resolve(loaded.Configuration).Where(x => selected.Select(s => s.Id).Contains(x.TaskId, StringComparer.OrdinalIgnoreCase)).Select(TaskResolver.Freeze).ToList();
        }
        var request = new OperationRequest
        {
            OperationId = Guid.NewGuid().ToString("N"), Kind = command.Kind, BaseDirectory = workspace.BaseDirectory,
            ConfigurationPath = workspace.ConfigurationPath, ConfigurationSha256 = workspace.ComputeConfigurationSha256(),
            SelectedTaskIds = command.UsesTasks ? selected.Select(x => x.Id).ToList() : [],
            FrozenTasks = frozen, ResolvedTaskSetHash = command.UsesTasks ? TaskResolver.ComputeResolvedTaskSetHash(frozen) : string.Empty,
            SelectedHistoryIds = command.UsesTasks ? [] : selected.Select(x => x.Id).ToList(),
            BroadRootAcknowledged = BroadRootCheck.IsChecked == true, RepairPolicy = SelectedRepairPolicy(),
            AbortOnConflict = AbortConflictCheck.IsChecked == true, Nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
        };
        var progress = new Progress<OperationProgress>(item => { criticalSection = item.CriticalSection; AppendOutput($"[{item.Stage}] [{item.TaskId}] {item.Message}"); });
        try
        {
            OperationResult result;
            if (command.Mutating)
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
                result = await new WorkerClient(executable, workspace).RunElevatedAsync(request, progress, operationCancellation.Token);
            }
            else result = await engine.ExecuteAsync(request, progress, operationCancellation.Token);
            AppendOutput(result.Message);
            foreach (var item in result.Tasks) AppendOutput($"{item.Status}: {item.Source} → {item.Destination} | {item.Detail}");
            RefreshTasks(); RefreshHistory(); RefreshQuarantine();
        }
        catch (OperationCanceledException) { AppendOutput("Cancelled."); }
        catch (Exception ex) { AppendOutput("ERROR: " + ex.Message); MessageBox.Show(ex.Message, "Csaver", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { criticalSection = false; SetRunning(false); operationCancellation.Dispose(); operationCancellation = null; }
    }

    private RepairPolicy SelectedRepairPolicy() => Enum.TryParse<RepairPolicy>((RepairPolicyBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var value) ? value : RepairPolicy.KeepBoth;
    private void SetRunning(bool running) { RunButton.IsEnabled = !running; CancelButton.IsEnabled = running && !criticalSection; CommandBox.IsEnabled = !running; }
    private void CancelButton_OnClick(object sender, RoutedEventArgs e) { if (!criticalSection) operationCancellation?.Cancel(); }

    private void SaveConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        SaveConfigurationJson(ConfigurationEditor.Text);
    }

    private void SaveTableConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ConfigTasksGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ConfigTasksGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            ConfigStatus.Text = "Finish or correct the active table cell before saving the configuration.";
            return;
        }

        var model = new MigrationConfiguration { DestRoot = DestinationRootEditor.Text.Trim(), Tasks = configurationRows.ToList() };
        SaveConfigurationJson(configurationService.SerializeCanonical(model));
    }

    private void AddConfigTaskButton_OnClick(object sender, RoutedEventArgs e)
    {
        var item = new MigrationTaskDefinition { TaskId = "task-" + Guid.NewGuid().ToString("N")[..8], Group = "NewGroup", Root = "Home", Rel = ".new-folder", Guard = string.Empty };
        configurationRows.Add(item);
        ConfigTasksGrid.SelectedItem = item;
        ConfigTasksGrid.ScrollIntoView(item);
    }

    private void RemoveConfigTaskButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ConfigTasksGrid.SelectedItem is MigrationTaskDefinition item) configurationRows.Remove(item);
    }

    private void SaveConfigurationJson(string json)
    {
        try
        {
            var result = configurationService.ValidateAndSave(workspace.ConfigurationPath, json);
            ConfigStatus.Text = result.IsValid ? "Configuration saved; a timestamped backup was retained." : string.Join(" | ", result.Issues.Select(x => x.Message));
            if (result.IsValid) { LoadConfiguration(); RefreshTasks(); }
        }
        catch (Exception ex) { ConfigStatus.Text = ex.Message; }
    }

    private void RefreshHistory()
    {
        historyRows = history.Load();
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var filter = HistoryFilter.Text?.Trim() ?? string.Empty;
        HistoryGrid.ItemsSource = string.IsNullOrWhiteSpace(filter) ? historyRows : historyRows.Where(x =>
            x.Group.Contains(filter, StringComparison.OrdinalIgnoreCase) || x.Source.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            x.Destination.Contains(filter, StringComparison.OrdinalIgnoreCase) || x.Status.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void RefreshQuarantine() => QuarantineGrid.ItemsSource = new QuarantineService(trees).Load();
    private void AppendOutput(string text) { OutputBox.AppendText($"[{DateTime.Now:T}] {text}{Environment.NewLine}"); OutputBox.ScrollToEnd(); LogsBox.Text = OutputBox.Text; }

    private void ApplyLanguage()
    {
        Title = TitleText.Text = localizer["Title"];
        CommandsTab.Header = localizer["Commands"]; ConfigurationTab.Header = localizer["Configuration"]; HistoryTab.Header = localizer["History"];
        QuarantineTab.Header = localizer["Quarantine"]; LogsTab.Header = localizer["Logs"]; SettingsTab.Header = localizer["Settings"]; AboutTab.Header = localizer["About"];
        RefreshTasksButton.Content = RefreshHistoryButton.Content = RefreshQuarantineButton.Content = localizer["Refresh"];
        SelectAllButton.Content = localizer["SelectAll"]; SelectNoneButton.Content = localizer["SelectNone"]; SaveConfigButton.Content = localizer["Save"];
        ExportHistoryButton.Content = localizer["Export"]; RunButton.Content = localizer["Run"]; BroadRootCheck.Content = localizer["BroadAck"];
        BuildCommands();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (operationCancellation is null) return;
        e.Cancel = true;
        MessageBox.Show(criticalSection ? "Csaver is in a critical cutover. Keep this window open." : "Cancel the operation before closing Csaver.", "Csaver", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CommandBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) { if (CommandBox.SelectedItem is CommandChoice choice) { CommandDescription.Text = choice.Description; RepairPolicyBox.Visibility = AbortConflictCheck.Visibility = choice.Kind is OperationKind.Repair or OperationKind.PreviewRepair ? Visibility.Visible : Visibility.Collapsed; RefreshTasks(); } }
    private void LanguageBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) { if (!IsLoaded || LanguageBox.SelectedItem is not ComboBoxItem item) return; localizer.SetCulture(item.Tag?.ToString() ?? "en-US"); ApplyLanguage(); }
    private void RefreshTasksButton_OnClick(object sender, RoutedEventArgs e) => RefreshTasks();
    private void SelectAllButton_OnClick(object sender, RoutedEventArgs e) { foreach (var item in taskRows.Where(x => x.Enabled)) item.IsSelected = true; }
    private void SelectNoneButton_OnClick(object sender, RoutedEventArgs e) { foreach (var item in taskRows) item.IsSelected = false; }
    private void RefreshHistoryButton_OnClick(object sender, RoutedEventArgs e) => RefreshHistory();
    private void HistoryFilter_OnTextChanged(object sender, TextChangedEventArgs e) { if (HistoryGrid is not null) ApplyHistoryFilter(); }
    private void RefreshQuarantineButton_OnClick(object sender, RoutedEventArgs e) => RefreshQuarantine();
    private void ExportHistoryButton_OnClick(object sender, RoutedEventArgs e) { var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "csaver-history.csv" }; if (dialog.ShowDialog(this) == true) history.ExportCsv(dialog.FileName, historyRows); }
    private void OpenQuarantineButton_OnClick(object sender, RoutedEventArgs e) { if (QuarantineGrid.SelectedItem is QuarantineEntry entry && Directory.Exists(entry.StoredPath)) Process.Start(new ProcessStartInfo { FileName = entry.StoredPath, UseShellExecute = true }); }
    private static bool PathsEqual(string? left, string right) { try { return !string.IsNullOrWhiteSpace(left) && Path.GetFullPath(left).TrimEnd('\\').Equals(Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); } catch { return false; } }
}
