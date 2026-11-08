# Csaver 便携版图形界面

Csaver 是一款原生 Windows 10/11 桌面应用，用于将配置的目录迁移到其他 NTFS 卷，并在原路径创建经过验证的目录联接（Junction）。它提供迁移预览、事务式迁移与修复、基于审计记录的回滚、配置编辑、历史记录导入以及只读隔离区查看功能。

## 主要特性

- 原生 .NET 8 WPF 图形界面，支持 English 和简体中文。
- 面向 `win-x64` 的自包含单文件 `Csaver.exe`，无需安装 .NET 运行时。
- 不调用 PowerShell、`cmd` 或 `robocopy`。
- 支持解释配置、预览/运行迁移、预览/运行修复、回滚候选项列表、预览/运行回滚以及查看隔离区。
- 支持选择单个任务或应用程序分组。
- 提供表格/表单配置编辑和高级 JSON 编辑。
- 支持导入兼容的历史迁移清单和 journal。
- 迁移、修复和回滚使用事务 journal、快照验证和源目录暂存机制。
- 不会直接删除回滚前的原目标目录，而是将其保留在隔离区。

## 安全说明

- 预览命令不会修改迁移路径，也不会创建迁移 journal 或 manifest。
- 保存或执行前，会检查配置字段类型、重复任务标识、Guard 正则表达式和路径安全性。
- 运行修改操作时，Csaver 会请求管理员权限，并启动同一个 EXE 的 worker 模式。
- 提权 worker 会重新加载配置，并独立检查路径、卷格式、重解析点、进程占用和宽泛根目录确认状态。
- 只有目标与配置目标完全匹配的 Junction 才会被视为健康联接。
- 创建 Junction 前，源目录会先移动到同卷暂存目录；只有联接验证成功后才会清理暂存目录。
- 目标目录中原有、但源目录没有的文件会被保留，迁移过程不会删除它们。
- Tencent、Google 和 kingsoft 等宽泛根目录需要单独确认，因为其中可能包含无关应用或个人数据。
- 首个版本不提供隔离区清理功能，保留的数据可用于恢复和审查。

## 使用方法

1. 将发布 ZIP 解压到可写目录。
2. 启动 `Csaver.exe`。首次运行会在 EXE 同级创建以下目录：

   ```text
   config\\
     migration-tasks.json
   data\\
     history\\
     imports\\
   logs\\
   ```

3. 打开“配置”页面，检查或编辑 `migration-tasks.json`。
4. 在“命令”页面选择任务，并先运行“解释配置”或“预览迁移”。
5. 关闭配置中 Guard 匹配的相关应用程序。
6. 对迁移、修复或回滚操作确认影响路径并批准 UAC 请求。
7. 在“历史记录”页面检查源目录、目标目录、时间、状态、文件数量、大小、journal 和回滚资格。
8. 回滚完成后，可在“隔离区”页面查看保留的原目标目录。

如果提示目录不可写，请将整个 Csaver 文件夹移动到用户可写的位置。不要直接从 ZIP 压缩包内运行，也不要将它放在 `Program Files` 等受保护目录中。

## 配置文件

默认配置位于：

```text
config\\migration-tasks.json
```

支持以下两种顶层格式：

```json
[
  {
    "TaskId": "example",
    "Group": "Example",
    "Root": "Home",
    "Rel": ".example",
    "Guard": "example"
  }
]
```

或者：

```json
{
  "DestRoot": "D:\\AppDataMoved",
  "Tasks": [
    {
      "TaskId": "example",
      "Group": "Example",
      "Root": "Home",
      "Rel": ".example",
      "Guard": "example",
      "Disabled": false
    }
  ]
}
```

支持的 `Root` 值包括：

- `Local`：`%LOCALAPPDATA%`
- `Roaming`：`%APPDATA%`
- `Home`：`%USERPROFILE%`
- `Documents`：`%USERPROFILE%\\Documents`
- `Absolute`：`Rel` 直接作为绝对源路径
- 将绝对路径作为字面基目录

目标目录选择优先级为：

```text
单条任务 Dest > 操作目标根 > JSON DestRoot > 自动选择非 C 固定磁盘 > D:\\AppDataMoved
```

保存配置时，Csaver 会先进行验证，并为已有配置创建带时间戳的 `.bak` 备份。如果已有配置格式错误，Csaver 不会自动使用内置默认配置覆盖它。

## 历史记录

Csaver 会在 `data\\history` 中保存新的历史记录，并以只读方式从已注册目标根导入兼容记录，包括：

- `_migration_manifest.csv`
- `_repair_manifest.csv`
- `_rollback_manifest.csv`
- `_migration_manifest.legacy-adoptions.csv`
- `.csaver-journal` 下的兼容 journal
- `%USERPROFILE%\\.appdata-moved\\roots.json` 中登记的目标根

历史记录支持筛选、排序、详情查看和 CSV 导出。如果旧记录无法可靠提供历史文件数量或大小，界面会显示 `Unknown`，不会用当前目录大小冒充历史数据。

提权 worker 退出前会把终态结果原子写入 `data\worker-results\<OperationId>.json`，界面在断连时用它还原真实结果并消费该文件；工作端诊断日志追加到 `logs\worker-<OperationId>.log`。两者都不属于迁移事务状态。

## 构建

需要能够针对 .NET 8 和 Windows 10/11 构建的 .NET SDK。在 SDK 10 下，解决方案级的 `-r win-x64` 会被拒绝（`NETSDK1134`），只在项目级的 publish/run 命令中指定 RID。

```text
dotnet restore Csaver.Gui.slnx
dotnet test Csaver.Gui.slnx -c Release
dotnet publish src/Csaver.App/Csaver.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
dotnet run --project tools/Csaver.Packaging/Csaver.Packaging.csproj -c Release -- artifacts/publish artifacts/Csaver-win-x64.zip
```

发布 ZIP 包含：

- `Csaver.exe`
- `config/migration-tasks.json`
- `config/migration-tasks.schema.json`
- `README.md`
- `README.zh-CN.md`
- `LICENSE`
- `SHA256SUMS.txt`

Csaver 图标已作为 Win32 应用图标嵌入 `Csaver.exe`，发布包不再附带独立的图标文件。

GitHub 文件夹和发布 ZIP 都不得包含任何 `.ps1` 文件。仓库的设计和运维细节位于 `docs/`，但不会复制到发布 ZIP 中。

## 代码签名

本地构建默认不签名。若配置以下环境变量，打包工具可以使用 Windows SDK SignTool 进行 SHA-256 Authenticode 签名、时间戳处理和签名验证：

```text
CSAVER_SIGNTOOL_PATH
CSAVER_SIGN_CERT_THUMBPRINT
CSAVER_TIMESTAMP_URL
```

不要将证书、私钥或密码提交到 Git。未签名程序可能触发 Windows SmartScreen 提示；签名可以证明发布者身份并检测篡改，但不能保证立即获得 SmartScreen 声誉。

## 故障处理

- **目录不可写**：将整个解压目录移动到用户可写位置。
- **UAC 被取消**：本次操作会安全结束，不会继续修改文件。
- **发现未完成 journal**：不要删除源目录、目标目录或暂存目录；保留 Csaver 显示的路径，并在重试前先进行恢复检查。
- **任务被跳过**：检查 Guard 匹配的应用程序是否仍在运行，然后运行预览命令查看具体原因。
- **回滚不可用**：必须存在匹配的已提交迁移记录、journal、源路径和目标快照，并且目标目录自迁移后不能发生变化。
- **提示 `Unsafe reparse point encountered`**：目录树中存在 Junction 或符号链接，复制与删除路径会拒绝跟随。若原路径已是 `LinkType=Junction` 且 `Target` 与配置目标一致，说明该任务已完成：不要删除应用自带的联接，不要手动删除目标根，在无未完成 journal 时也不要运行恢复命令；改为取消勾选该任务或设为 `"Disabled": true`。复核命令：`Get-Item -LiteralPath "<原路径>" -Force | Select-Object LinkType, Target` 与 `fsutil reparsepoint query "<原路径>"`。已完成任务现在会显示 `SKIP_ALREADY_LINK` 而非预检失败；`TrustedReparsePoints` 可为只读检查声明树内链接（默认拒绝）。
- **提示 `Elevated worker disconnected before a terminal result` / `STATE_UNKNOWN`**：提权 worker 的终帧未通过命名管道送达，界面保守标记"状态未知"，并不代表数据被改动。先查看 `data/history` 中对应 `OperationId` 的记录（`journalPath`、`manifestPath` 为空且 `rollbackEligible=false` 表示预检即中止，没有复制、删源、建/改联接或写清单），再用上面的命令复核源与目标的链接状态，确认后才决定是否重试；不要为了"清理"而删除源或目标目录。当前版本会在校验操作标识与 nonce 后，从 `data/worker-results/<OperationId>.json` 还原 worker 落盘的真实结果，因此只有无有效结果文件时才会显示 `STATE_UNKNOWN`；断连原因见 `logs/worker-<OperationId>.log`，并会附在还原后的结果消息里（管道 EOF、IO 失败、启动失败或连接超时）。

- **“请至少勾选一项”与“所选任务均已被禁用”**：前者表示没有任何任务被勾选；后者表示勾选的任务全部为 `"Disabled": true`，被 GUI 过滤。请在「配置」中启用对应任务、保存并刷新后再执行；「解释配置」仍会显示禁用任务。
- **提示 `Copy verification failed: Metadata/content differs (acl): ...`**：复制已完成，但有效 ACL 与源不一致，通常是跨盘迁移时目标继承了新父目录的权限，而不是源目录的权限。校验是 fail-closed：不会删源、不会创建联接。当前版本会保护目标 DACL，并把源的有效访问规则重建为显式 ACE，使有效权限一致；括号中的字段会标明确切差异（`acl`、`lastWriteTime`、`attributes`、`content`）。验证失败会在 `<DestRoot>\.csaver-journal` 留下 `RecoveryRequired` journal，请先对该任务执行「恢复未完成事务」，再重新预览与迁移。

- **另一台电脑反复失败（`FileSystemRights -536805376`、`Access ... is denied`、`RECOVERY_REFUSED`）**：应用自建目录的 DACL 可能含旧版本无法重建的原始 `GENERIC_*` 掩码；失败后还会留下只读／受限残留文件，或源文件仍被应用占用。当前版本以受保护的原始安全描述符复制 DACL、自动覆盖受限残留、在被占用时提前于切换阶段失败，并给出完整路径。根因与逐步脱困步骤见 `docs/user-guide.zh-CN.md`；不要通过删除 journal 绕过 `RECOVERY_REFUSED`。

## 许可证

本项目使用 MIT License，详见 `LICENSE`。
