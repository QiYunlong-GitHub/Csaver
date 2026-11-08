# Csaver 用户指南

1. 将发布 ZIP 解压到可写目录。
2. 启动 `Csaver.exe`；无需安装程序或 .NET 运行时。
3. 检查“配置”。仅当配置不存在时，Csaver 才创建 `config/migration-tasks.json`；每次成功保存前都会备份。
4. 在“命令”中选择任务，执行修改前先运行“解释配置”或“预览迁移”。
5. 关闭配置中 Guard 匹配的程序。宽泛根目录必须单独勾选确认。
6. 运行迁移或修复并批准 UAC。进入联接切换阶段后不要强制结束进程。
7. 在“历史记录”中查看或导出结果。预览或运行回滚前，选择一个符合条件的迁移记录。
8. 回滚会恢复源目录，并把原目标保留到 `.csaver-quarantine`；此版本不会删除隔离数据。

如果启动时提示目录不可写，请把整个解压目录移动到用户可写位置。如果发现未完成 journal，请保留所列源、目标、暂存和 journal 路径以便恢复检查。

## 故障排查：重解析点与状态未知

**症状**：对已经迁移完成的目录再次运行“预览迁移”或“运行迁移”，界面出现 `PREFLIGHT_FAILED: ... | Unsafe reparse point encountered: <相对路径>`，随后可能出现 `Elevated worker disconnected before a terminal result` 与 `STATE_UNKNOWN`。

**含义**：

- 目录树中存在 Junction 或符号链接，常见的是应用自带的 vendor 链接，例如 `binaries\python\versions\3.13.12` 指向程序安装目录下的 Python。Csaver 对快照、复制和删除路径一律拒绝跟随链接，避免越界拷贝、环路或误删。
- `STATE_UNKNOWN` 只表示提权 worker 的终帧没有通过命名管道送达、界面无法确认结果，**不等于文件系统被改动**。结果未知时应先核实，再决定是否重试。
- 源路径已是 `LinkType=Junction` 且 `Target` 与配置目标一致时，说明该任务早已迁移完成，无需再次迁移，也不应再对目标树做重复扫描。

**先确认真实状态，不要立即重试**：

```powershell
Get-Item -LiteralPath "$env:USERPROFILE\.workbuddy" -Force | Select-Object FullName, Attributes, LinkType, Target
Get-Item -LiteralPath "D:\AppDataMoved\Home\.workbuddy" -Force | Select-Object FullName, LinkType, Target
fsutil reparsepoint query "$env:USERPROFILE\.workbuddy"
```

- 打开 `data/history` 中对应 `OperationId` 的记录：`status=PREFLIGHT_FAILED` 且 `journalPath`、`manifestPath` 为空、`rollbackEligible=false`，表示预检即中止，**没有复制、删源、建/改联接或写清单**。
- 检查目标根下的 `.csaver-journal`：若没有未完成 journal，**不要运行“恢复未完成迁移”**，它会因找不到匹配 journal 而拒绝。

**处置步骤**：

1. 不回滚、不恢复、不手动删除目标目录。`<DestRoot>` 是联接的实体数据，删除会让原路径失效。
2. **不要删除应用自带的 Junction**：它们通常指向程序安装目录下的运行时依赖，删除会破坏应用。
3. 后续运行只勾选尚未完成的任务，或在 `config/migration-tasks.json` 中把已完成任务设为 `"Disabled": true`（保存会生成带时间戳的 `.bak` 备份）。
4. 可信链接通过任务的 `TrustedReparsePoints` 字段按相对路径显式声明，默认拒绝，并且只对只读检查生效；复制与删除路径保持拒绝。字段与格式见 `config/migration-tasks.schema.json`。已完成的任务在预览中会显示 `SKIP_ALREADY_LINK`，无需再迁移。
5. 若应用更新或重装把联接替换成真实目录（断链），先“预览修复”再“运行修复”；**不要直接重跑迁移**，以免以 C 侧新建的小目录为准覆盖目标侧旧数据。
6. Guard 命中的程序请从托盘完全退出再重跑预览；因占用而跳过是安全行为。

**安全红线**：只有校验通过才删除源；联接创建失败必须保留目标副本，并按提示手动 `mklink /J "<源>" "<目标>"`；进入联接切换阶段后不要强制结束进程。

## 故障排查：选择提示与复制校验失败

**`Select at least one item.`**：没有任何任务被勾选。

**`所选任务均已被禁用…`**：勾选了任务，但它们全部是 `"Disabled": true`，GUI 会过滤禁用任务，所以命令没有可执行对象。请到「配置」把对应任务设为 `"Disabled": false`，保存并刷新任务列表后再勾选；提示会列出被过滤的 `TaskId`。「解释配置」不受禁用状态影响，因此会出现“解释正常、预览却提示未选择”的情况。

**`FAIL_TRANSACTION ... Copy verification failed: Metadata/content differs (acl): <相对路径>`**：

- 复制完成后，Csaver 会对源和目标分别取快照，逐项比较长度、创建/修改时间、属性、ACL 和内容 SHA256。任一不一致都不允许删源、不允许创建联接；这是 fail-closed 行为，**源目录保持原样**。
- 括号中的字段标明确切差异，例如 `acl`、`lastWriteTime`、`attributes`、`content`；不需要再靠日志猜是哪一项不同。
- 跨盘迁移时，源目录的 DACL 通常继承自 `C:\Users\<用户>`，目标目录继承自 `D:\` 等新的父目录。旧版本只把源 DACL 写到未受保护的目标对象上，目标仍会重新继承新父目录的权限，导致有效权限不同，验证因此拒绝提交。
- 当前版本会保护目标的 DACL，并把源的有效访问规则（含继承规则）重建为显式 ACE；校验按有效 DACL 比较，只忽略“继承 / 显式”这一来源差异，权限集合本身仍必须完全一致。

**重跑前的必要步骤**：验证失败会留下状态为 `RecoveryRequired` 的 journal（位于 `<DestRoot>\.csaver-journal`）。预检会拒绝带匹配未完成 journal 的预览或迁移。请先在「命令」中选择 **恢复未完成事务** 并勾选该任务，成功后重新执行「预览迁移」，再执行「运行迁移」。目标目录中上次复制的数据会被同名文件覆盖，目标独有数据仍会保留。不要在恢复前手动删除目标目录或 journal。

## 故障排查：另一台电脑反复失败（Trae CN / `.trae-cn`）

**症状**：`The value '-536805376' is not valid for this usage of the type FileSystemRights`；重跑时 `Access to the path 'D:\...\git\objects\...' is denied`；切换阶段 `Access to the path 'distro-support.json' is denied`；随后出现 `RECOVERY_REFUSED` / `REPAIR_NEEDS_REVIEW`。

**根因**：

1. 应用自建目录的 DACL 可能包含原始 `GENERIC_*` 访问掩码（例如 `0xE0010000`，转成 `FileSystemRights` 就是 `-536805376`）。旧版本通过 `FileSystemAccessRule` 逐条重建 ACE，遇到该掩码会直接抛异常并中断复制。当前版本以原始安全描述符写入受保护（`D:P`）的 DACL；校验改为比较有效权限，会把 `GENERIC_*` 位映射为文件权限，并合并同一受托人的重复 ACE。
2. 上一次失败在目标侧留下半复制数据，其中部分文件为只读或带受限 DACL，普通 `File.Copy(..., true)` 覆盖时报 `Access ... is denied`。当前版本会先清除 `ReadOnly`，必要时为当前用户临时授予写权限再重试，随后重新应用源 DACL。
3. 应用仍占用源文件句柄，导致暂存 `Directory.Move` 和暂存清理 `Directory.Delete` 失败，且旧异常只给出短文件名。当前版本在复制**之前**就检测源文件是否可独占打开，删除暂存前清除只读属性，并在异常中给出完整路径与「关闭应用」提示。
4. 一旦复制或切换已把 `SourceSnapshot` / `DestinationSnapshot` 写入 journal，恢复逻辑按 fail-closed 拒绝。`RECOVERY_REFUSED` 与 `REPAIR_NEEDS_REVIEW` 不能靠删除 journal 绕过。

**该电脑上的即时脱困步骤**：

1. 完全退出相关应用（含托盘图标与后台辅助进程）；句柄无法释放时重启电脑。
2. 动手前先记录真实状态：

   ```powershell
   Get-Item -LiteralPath "$env:APPDATA\Trae CN" -Force | Select-Object FullName, Attributes, LinkType, Target
   Get-Item -LiteralPath "D:\<DestRoot>\<task>" -Force | Select-Object FullName, Attributes, LinkType, Target
   Get-ChildItem -LiteralPath "D:\<DestRoot>\.csaver-journal" -Force
   ```

3. 使用新发布包：把新 `Csaver.exe` 覆盖到原目录（保留 `config/` 与 `data/`），或解压到全新目录后把 `config/` 复制过来。不要以「清理」为名删除历史记录或 journal。
4. 只勾选失败的任务，执行 **恢复未完成事务**。对切换前的 journal，恢复成功后重新「预览迁移」再「运行迁移」；只读或受限残留文件现在会自动覆盖。
5. 若恢复返回 `RECOVERY_REFUSED`（journal 已含复制/切换阶段的快照证据），**立即停止**。保持源、目标、暂存目录和 journal 原样，不要强制结束提权 worker；保留 `logs/worker-<OperationId>.log` 与 journal，先比对两棵树，确认数据后再人工处置切换。此状态刻意不提供自动恢复。
6. 若源文件被占用，新异常会给出完整路径；关闭对应程序后重试。现在会在切换前失败，健康的源目录不会再产生无法自动恢复的 journal。
