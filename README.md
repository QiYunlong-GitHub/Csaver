# Csaver Portable GUI

Csaver is a native Windows 10/11 desktop application for moving configured directories to another NTFS volume and replacing their original paths with verified directory Junctions. It provides migration previews, transactional migration and repair, audit-backed rollback, configuration editing, history import, and read-only quarantine inspection.

## Features

- Native .NET 8 WPF GUI with English and Simplified Chinese support.
- Self-contained single-file `Csaver.exe` for `win-x64`; no .NET runtime installation is required.
- Does not invoke PowerShell, `cmd`, or `robocopy`.
- Supports configuration explanation, migration preview/run, repair preview/run, rollback candidate listing, rollback preview/run, and quarantine viewing.
- Allows users to select individual tasks or application groups.
- Provides both table/form configuration editing and advanced JSON editing.
- Imports compatible historical migration manifests and journals.
- Uses transactional journals, snapshot verification, and source-side staging for migration, repair, and rollback.
- Never directly deletes the former rollback destination; it is retained in quarantine.

## Safety

- Preview commands do not modify migration paths or create migration journals or manifests.
- Configuration is validated for field types, duplicate task identities, Guard regular expressions, and path safety before saving or execution.
- Mutating operations request administrator approval and start the same executable in worker mode.
- The elevated worker reloads the configuration and independently validates paths, volume formats, reparse points, process guards, and broad-root acknowledgements.
- Only a Junction whose target exactly matches the configured destination is considered healthy.
- Before Junction cutover, the source directory is moved to a same-volume staging directory; staging is removed only after link validation succeeds.
- Destination-only files are retained and are not deleted by migration.
- Broad roots such as Tencent, Google, and kingsoft require separate acknowledgement because they may contain unrelated application or personal data.
- Quarantine cleanup is intentionally unavailable in the first release; retained data remains available for recovery and review.

## Usage

1. Extract the release ZIP to a writable folder.
2. Start `Csaver.exe`. On first launch it creates the following folders beside the executable:

   ```text
   config\\
     migration-tasks.json
   data\\
     history\\
     imports\\
   logs\\
   ```

3. Open **Configuration** and review or edit `migration-tasks.json`.
4. On **Commands**, select tasks and run **Explain Configuration** or **Preview Migration** before any mutation.
5. Close applications matched by the configured process Guards.
6. Confirm the affected paths and approve the UAC request for migration, repair, or rollback.
7. Use **History** to review source, destination, time, status, file count, size, journal, and rollback eligibility.
8. After rollback, use **Quarantine** to view the retained former destination.

If Csaver reports that its directory is not writable, move the entire Csaver folder to a user-writable location. Do not run it directly from the ZIP archive or from a protected directory such as `Program Files`.

## Configuration

The default configuration is stored at:

```text
config\\migration-tasks.json
```

Both of the following top-level formats are supported:

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

or:

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

Supported `Root` values include:

- `Local`: `%LOCALAPPDATA%`
- `Roaming`: `%APPDATA%`
- `Home`: `%USERPROFILE%`
- `Documents`: `%USERPROFILE%\\Documents`
- `Absolute`: `Rel` is used directly as an absolute source path
- An absolute path used as a literal base directory

Destination selection precedence is:

```text
Task Dest > operation destination root > JSON DestRoot > automatic non-C fixed drive > D:\\AppDataMoved
```

Csaver validates configuration before saving and creates a timestamped `.bak` backup of the existing file. If an existing configuration is malformed, Csaver does not silently replace it with the embedded default.

## History

Csaver stores new history records under `data\\history` and read-only imports compatible records from registered destination roots, including:

- `_migration_manifest.csv`
- `_repair_manifest.csv`
- `_rollback_manifest.csv`
- `_migration_manifest.legacy-adoptions.csv`
- Compatible journals under `.csaver-journal`
- Destination roots registered in `%USERPROFILE%\\.appdata-moved\\roots.json`

History supports filtering, sorting, detail inspection, and CSV export. If an old record cannot reliably provide a historical file count or size, the UI displays `Unknown` instead of presenting a current directory size as historical data.

Before exiting, the elevated worker atomically persists its terminal result under `data\worker-results\<OperationId>.json`; the GUI consumes it after restoring a disconnected result. Worker diagnostics are appended to `logs\worker-<OperationId>.log`. Neither file is part of a migration transaction.

## Build

Requirements: a .NET SDK capable of targeting .NET 8 and Windows 10/11. With SDK 10, solution-level `-r win-x64` is rejected (`NETSDK1134`); specify the RID only on the project-level publish/run commands.

```text
dotnet restore Csaver.Gui.slnx
dotnet test Csaver.Gui.slnx -c Release
dotnet publish src/Csaver.App/Csaver.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
dotnet run --project tools/Csaver.Packaging/Csaver.Packaging.csproj -c Release -- artifacts/publish artifacts/Csaver-win-x64.zip
```

The release ZIP contains:

- `Csaver.exe`
- `config/migration-tasks.json`
- `config/migration-tasks.schema.json`
- `README.md`
- `README.zh-CN.md`
- `LICENSE`
- `SHA256SUMS.txt`

The Csaver icon is embedded in `Csaver.exe` as its Win32 application icon; no separate icon file is shipped.

The GitHub folder and release ZIP must not contain any `.ps1` files. Repository design and operational details are documented under `docs/` but are not copied into the release ZIP.

## Code Signing

Local builds are unsigned by default. When the following environment variables are configured, the packaging tool can use Windows SDK SignTool for SHA-256 Authenticode signing, timestamping, and signature verification:

```text
CSAVER_SIGNTOOL_PATH
CSAVER_SIGN_CERT_THUMBPRINT
CSAVER_TIMESTAMP_URL
```

Never commit certificates, private keys, or passwords to Git. Unsigned programs may trigger a Windows SmartScreen warning. Signing identifies the publisher and detects tampering, but does not guarantee immediate SmartScreen reputation.

## Troubleshooting

- **Directory is not writable:** Move the entire extracted folder to a user-writable location.
- **UAC was cancelled:** The operation ends safely and does not continue mutating files.
- **Incomplete journal detected:** Do not delete the source, destination, or staging directory. Preserve the paths shown by Csaver and review recovery before retrying.
- **Task was skipped:** Check whether a guarded application is still running, then run a preview to see the exact reason.
- **Rollback is unavailable:** A matching committed migration record, journal, source path, and destination snapshot are required, and the destination must not have changed since migration.
- **`Unsafe reparse point encountered`:** A Junction or symbolic link exists inside the tree; copy and deletion paths refuse to follow links. If the original path is already `LinkType=Junction` with a `Target` equal to the configured destination, the task is already migrated: do not delete the application-owned link, do not delete the destination root, and do not run recovery when no incomplete journal exists. Deselect the task or set `"Disabled": true` instead. Verify with `Get-Item -LiteralPath "<original path>" -Force | Select-Object LinkType, Target` and `fsutil reparsepoint query "<original path>"`. An already-migrated task now reports `SKIP_ALREADY_LINK` instead of failing preflight; `TrustedReparsePoints` may declare in-tree links for read-only inspection only (denied by default).
- **`Elevated worker disconnected before a terminal result` / `STATE_UNKNOWN`:** The terminal frame did not arrive over the named pipe, so the GUI conservatively reports unknown state; this is not evidence that files changed. Inspect the matching `OperationId` record under `data/history` (empty `journalPath`/`manifestPath` and `rollbackEligible=false` mean preflight stopped before any copy, source deletion, link change, or manifest write), verify the source/destination link state with the commands above, and only then decide whether to retry. Current builds restore the worker's persisted result from `data/worker-results/<OperationId>.json` after validating the operation identity and nonce, so `STATE_UNKNOWN` only appears when no valid result file exists; `logs/worker-<OperationId>.log` records the disconnect reason, which is also appended to the restored result message (pipe EOF, I/O failure, startup failure, or connection timeout). Never delete the source or destination as "cleanup".

- **`Select at least one item` vs. the disabled-task prompt:** The first means nothing is checked. The second means every checked task has `"Disabled": true`; enable it in Configuration, save, and refresh before running the command. Explain Configuration still reports disabled tasks.
- **`Copy verification failed: Metadata/content differs (acl): ...`:** The copy succeeded but the effective DACL differed, usually because a cross-volume destination inherited its new parent's permissions instead of the source's. Verification is fail-closed: the source is not deleted and no Junction is created. Current builds protect the destination DACL and rebuild the source's effective access rules as explicit ACEs, so the effective rights match. The parenthesized field names the exact difference (`acl`, `lastWriteTime`, `attributes`, `content`). A failed verification leaves a `RecoveryRequired` journal under `<DestRoot>\.csaver-journal`; run Recover Incomplete Transaction for that task before previewing or migrating again.

- **Repeated failures on another PC (`FileSystemRights -536805376`, `Access ... is denied`, `RECOVERY_REFUSED`):** Application-owned DACLs can contain raw `GENERIC_*` masks that older builds could not rebuild, and a failed copy leaves read-only/restricted leftovers or locked source files. Current builds copy the raw DACL with a protected security descriptor, overwrite restricted leftovers, fail before cutover when a source file is locked, and report full paths. See `docs/user-guide.en.md` for the root causes and the step-by-step unblock procedure; never delete a journal to bypass `RECOVERY_REFUSED`.

## License

This project is licensed under the MIT License. See `LICENSE`.
