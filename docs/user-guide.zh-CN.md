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
4. 一旦复制或切换已把 `SourceSnapshot` / `DestinationSnapshot` 写入 journal，恢复逻辑按 fail-closed 拒绝，`RECOVERY_REFUSED` 不能靠删除 journal 绕过。`REPAIR_NEEDS_REVIEW` 的具体原因总是写在详情里——旧版本还可能是下文所述的修复切换状态机缺陷；无论哪种情况都不要删除 journal、源、目标或暂存目录来绕过。

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

## 故障排查：豆包复制校验失败与修复切换报错

**症状**：`FAIL_TRANSACTION ... Copy verification failed: Metadata/content differs (lastWriteTime): ...\assets\comps; Metadata/content differs (length,content): ...\comps-model-template.xlsx`；随后连续两次「运行修复」都返回 `REPAIR_NEEDS_REVIEW ... Invalid journal transition: SourceStaged -> LinkValidated`，而修复预览每次都列出同样的 10 个冲突文件。

**根因**：

1. 豆包进程在 `CopyPreserving` 执行期间重写了自己的 skills 模板资产（`.xlsx` / `.doc`）。Csaver 在复制**之后**才对源取快照，逐项与目标比较长度、时间、属性、ACL 和内容 SHA256；任一差异都会在删源之前中止。这是刻意的 fail-closed 行为，当前版本**不自动重拷**变化文件、也**不忽略**任何差异，只是把失败变得可操作：给出变化条目、差异计数、需要关闭的 `Guard` 值，以及正确的下一步命令。
2. 复制校验失败的异常在写 `SourceSnapshot` / `DestinationSnapshot` **之前**抛出，因此该 journal 没有快照证据，仍然可以执行 **恢复未完成事务**。改用「运行修复」是命令选错：修复面向的是应用更新或重装把联接替换成真实目录的断链场景，而不是源目录不稳定。
3. 旧版本的修复切换存在缺陷：从 `SourceStaged` 直接跳到 `LinkValidated`，漏掉了状态机要求的 `JunctionCreated`。异常发生在「不可取消」阶段，于是每次都把源移回、删掉刚建的联接，而 merge 已经把文件写入目标、把冲突归档到 `_repair_conflicts` / `_repair_backup`。这些归档按「不删除数据」原则保留，所以预览会反复列出同样的冲突。当前版本按 `Merging → Verified → SourceStaged → JunctionCreated → LinkValidated → Committed` 完整链路正常提交。

**该电脑上的处置步骤**：

1. 完全退出豆包（含托盘图标与后台辅助进程）；若仍在写文件则重启。被占用的应用数据目录应当关闭程序后重跑，而不是强行通过。
2. 用新发布包替换 `Csaver.exe`，保留 `config/` 与 `data/`。不要以「清理」为名删除历史记录、journal、`_repair_conflicts` 或 `_repair_backup`。
3. 重试前先记录真实状态：

   ```powershell
   Get-Item -LiteralPath "$env:LOCALAPPDATA\Doubao" -Force | Select-Object FullName, Attributes, LinkType, Target
   Get-Item -LiteralPath "D:\AppDataMoved\Local\Doubao" -Force | Select-Object FullName, Attributes, LinkType, Target
   Get-ChildItem -LiteralPath "D:\AppDataMoved\.csaver-journal" -Force
   ```

4. 只勾选 `doubao-local`，执行 **恢复未完成事务**，再执行「预览迁移」与「运行迁移」。只有当源确实是断链联接、或应用更新后留下的真实目录时，才使用「运行修复」。
5. 若再次失败，新错误会列出变化条目和 `Guard` 值（`doubao`）；关闭该应用后重试。Csaver 不会替你重拷变化文件，也不会为了让校验通过而放宽检查。
6. 若 journal 已含快照证据，自动恢复会被拒绝。请保持两棵树和 journal 原样，按上文的恢复章节人工处置。

## 豆包失败为何反复出现，以及当前版本的处理

当前版本在复制前与复制期间新增了两道**提前中止**防线，两者都不放宽校验：

- `Stabilizing`（稳定等待）阶段：在复制任何文件之前，Csaver 要求连续两次源目录结构快照完全一致。源若持续变化，会以 `Source is not stable before copying` 中止——不复制任何文件、不创建暂存目录。
- 复制期复检：周期性（随复制进度上报触发，最多每 5 秒一次）重新检查 `Guard` 进程以及源目录的文件数/总字节数。应用出现或统计变化时，会在切换前以 `Guard process appeared during copying` 或 `Source changed during copying` 中止。
- 复制后的最终快照对比完全不变，仍严格 fail-closed：不忽略任何差异、不自动重拷。所有提前中止都发生在 journal 仍处于 `Copying`、尚未写入快照证据时，因此 **恢复未完成事务** 依然可用。

如果彻底退出应用仍挡不住后台辅助进程改写 `.skills` 模板，请使用 Windows 安全模式。

## 在 Windows 安全模式下迁移（详细步骤）

安全模式只加载最小驱动与服务集：第三方自启动程序、后台更新服务以及多数杀毒过滤驱动都不会加载，源目录因此能保持静止。Csaver 是自包含单文件 exe，无需预装 .NET 运行时；NTFS 目录联接与 UAC 提权在安全模式下均可用。

### 开始前

1. 使用当前发布包。让 `config/` 与 `data/` 和 `Csaver.exe` 放在一起；不要删除 journal、`_repair_conflicts` 或 `_repair_backup`。
2. 记下 `DestRoot` 所在盘（例如 `D:`）。固定的 NTFS 数据盘在安全模式下仍会挂载；可移动盘或被 BitLocker 锁定的盘可能不可见。
3. `RecoveryRequired` journal 会阻止新迁移。进入安全模式前（或进入后立即）先对该任务执行 **恢复未完成事务**。
4. 纯本地迁移用普通安全模式即可；只有需要网络时才选「带网络的安全模式」。

### 进入安全模式（Windows 10 / 11 启动菜单，一次性）

1. 打开 **设置 → 系统 → 恢复**（Windows 10 为 **更新和安全 → 恢复**）。
2. 在 **高级启动** 下点击 **立即重新启动**，电脑会进入 Windows 恢复环境。
3. 依次选择 **疑难解答 → 高级选项 → 启动设置 → 重启**。
4. 再次重启后按 `4` / `F4` 选择 **启用安全模式**，或按 `5` / `F5` 选择 **启用带网络的安全模式**。
5. 用管理员账户登录，屏幕四角会显示「安全模式」。

### 备选：msconfig（会一直保持安全模式，直到手动取消）

1. 按 `Win + R`，输入 `msconfig`，回车。
2. 在 **引导** 选项卡勾选 **安全引导**，选择 **最小**（需要网络则选 **网络**），点 **确定** 后 **重启**。

### 备选：管理员命令行

```powershell
bcdedit /set {current} safeboot minimal
shutdown /r /t 0
```

### 执行迁移

1. 把 `Csaver.exe` 连同 `config/`、`data/` 复制到固定本地磁盘上的目录。
2. 右键 `Csaver.exe` → **以管理员身份运行**，接受 UAC 提示。
3. 只勾选受影响的任务（例如 `doubao-local`）；若 journal 仍待处理先执行 **恢复未完成事务**，再执行「预览迁移」与「运行迁移」。上述三道检查会覆盖整个复制过程。
4. 退出安全模式前先验证联接：

   ```powershell
   Get-Item -LiteralPath "$env:LOCALAPPDATA\Doubao" -Force | Select-Object FullName, Attributes, LinkType, Target
   ```

   `LinkType` 必须是 `Junction`，`Target` 必须是 `DestRoot` 下的目标路径。

### 退出安全模式（必须执行）

- **启动菜单（`4`/`F4`/`5`/`F5`）**：该方式是一次性的，正常重启即可。

  ```powershell
  shutdown /r /t 0
  ```

- **msconfig**：`Win + R` → `msconfig` → **引导** 选项卡 → 取消勾选 **安全引导** → **确定** → **重启**。
- **bcdedit**：在管理员终端执行：

  ```powershell
  bcdedit /deletevalue {current} safeboot
  shutdown /r /t 0
  ```

- 确认 Windows 已正常启动（四角不再显示「安全模式」），再启动应用。应用仍按原路径读写，底层已由联接指向新位置。

### 安全模式常见问题

- **反复进入安全模式**：进入 Windows 恢复环境 → **疑难解答 → 高级选项 → 命令提示符**，执行 `bcdedit /deletevalue {default} safeboot` 后重启。
- **看不到 `D:` 盘**：把工作目录和 `DestRoot` 换到固定的 NTFS 卷，或先解锁 BitLocker。
- **UAC 提权失败**：改用本地管理员账户登录；安全模式不会免除创建联接所需的管理员权限。
