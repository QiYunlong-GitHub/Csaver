using System.Globalization;

namespace Csaver.Application;

public sealed class LocalizationService
{
    private static readonly IReadOnlyDictionary<string, (string English, string Chinese)> Text =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = ("Csaver Portable Migration", "Csaver 便携迁移工具"),
            ["Commands"] = ("Commands", "命令"), ["Configuration"] = ("Configuration", "配置"),
            ["History"] = ("History", "历史记录"), ["Quarantine"] = ("Quarantine", "隔离区"),
            ["Logs"] = ("Logs", "日志"), ["Settings"] = ("Settings", "设置"), ["About"] = ("About", "关于"),
            ["Refresh"] = ("Refresh", "刷新"), ["Save"] = ("Validate and Save", "验证并保存"),
            ["Run"] = ("Run selected command", "运行所选命令"), ["SelectAll"] = ("Select all", "全选"),
            ["SelectNone"] = ("Select none", "全不选"), ["Export"] = ("Export CSV", "导出 CSV"),
            ["BroadAck"] = ("I understand broad roots may contain unrelated data.", "我了解宽泛根目录可能包含无关数据。"),
            ["NoSelection"] = ("Select at least one item.", "请至少勾选一项。"),
            ["AllSelectedDisabled"] = ("All selected tasks are disabled. Enable them in Configuration, save, then refresh the task list.", "所选任务均已被禁用。请在「配置」中启用它们，保存后刷新任务列表。"),
            ["ExplainConfiguration"] = ("Resolve task paths and warnings without changing files.", "解析任务路径和警告，不修改文件。"),
            ["PreviewMigration"] = ("Inspect selected tasks and calculate sizes without mutation.", "检查所选任务并计算大小，不执行修改。"),
            ["Migrate"] = ("Copy, verify, stage, and replace selected folders with Junctions.", "复制、验证、暂存并将所选目录替换为联接。"),
            ["PreviewRepair"] = ("Inspect changed or missing Junctions and show the repair plan.", "检查已变化或丢失的联接并显示修复计划。"),
            ["Repair"] = ("Merge retained data and safely restore expected Junctions.", "合并保留的数据并安全恢复预期联接。"),
            ["ListRollback"] = ("List audit-backed migrations that can be rolled back.", "列出具有审计记录、可回滚的迁移。"),
            ["PreviewRollback"] = ("Verify rollback identity, Junction, and snapshot without mutation.", "验证回滚身份、联接和快照，不执行修改。"),
            ["Rollback"] = ("Restore the source and retain the former destination in quarantine.", "恢复源目录并将原目标保留在隔离区。"),
            ["ViewQuarantine"] = ("View retained rollback payloads; cleanup is not available in this release.", "查看保留的回滚数据；此版本不提供清理功能。"),
            ["RecoverIncompleteMigration"] = ("Validate a pre-cutover journal and mark it Recovered without deleting data.", "验证切换前日志并标记为已恢复，不删除任何数据。")
        };

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public bool IsChinese => Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public void SetCulture(string name) => Culture = CultureInfo.GetCultureInfo(name);
    public string this[string key] => Text.TryGetValue(key, out var value) ? (IsChinese ? value.Chinese : value.English) : key;
}
