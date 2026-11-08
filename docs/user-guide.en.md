# Csaver User Guide

1. Extract the release ZIP to a writable folder.
2. Start `Csaver.exe`; no installation or .NET runtime is required.
3. Review Configuration. Csaver creates `config/migration-tasks.json` only when it is absent and backs up every successful edit.
4. Select tasks on Commands and run Explain or Preview before a mutation.
5. Close guarded applications. Broad roots require the separate acknowledgement checkbox.
6. Run Migration or Repair and approve UAC. Do not terminate the elevated worker during cutover.
7. Use History to review or export records. Select an eligible migration before Preview Rollback or Run Rollback.
8. Rollback restores the source and retains the former destination under `.csaver-quarantine`; this release never deletes quarantine payloads.

If startup reports an unwritable directory, move the entire extracted folder to a user-writable location. If an incomplete journal is reported, preserve the listed source, destination, staging, and journal paths for recovery review.

## Troubleshooting: reparse points and unknown state

**Symptoms**: Running Preview Migration or Run Migration again on a directory that was already migrated produces `PREFLIGHT_FAILED: ... | Unsafe reparse point encountered: <relative path>`, possibly followed by `Elevated worker disconnected before a terminal result` and `STATE_UNKNOWN`.

**Meaning**:

- The tree contains a Junction or symbolic link, typically an application-owned vendor link such as `binaries\python\versions\3.13.12` pointing at the Python runtime in the installation directory. Csaver never follows links during snapshot capture, copying, or deletion, so an out-of-tree copy, a cycle, or a wrong deletion cannot occur.
- `STATE_UNKNOWN` only means the elevated worker's terminal frame did not arrive over the named pipe and the GUI cannot confirm the result. It is **not evidence that the filesystem changed**. Verify the state before retrying.
- When the source is already `LinkType=Junction` and its `Target` equals the configured destination, the task is already migrated. It must not be migrated again or re-scanned through the destination tree.

**Confirm the real state before any retry**:

```powershell
Get-Item -LiteralPath "$env:USERPROFILE\.workbuddy" -Force | Select-Object FullName, Attributes, LinkType, Target
Get-Item -LiteralPath "D:\AppDataMoved\Home\.workbuddy" -Force | Select-Object FullName, LinkType, Target
fsutil reparsepoint query "$env:USERPROFILE\.workbuddy"
```

- Open the record for the matching `OperationId` under `data/history`. `status=PREFLIGHT_FAILED` with empty `journalPath` and `manifestPath` and `rollbackEligible=false` means preflight stopped the operation: **no copy, no source deletion, no Junction change, and no manifest write**.
- Check `.csaver-journal` under the destination root. If there is no incomplete journal, **do not run Recover Incomplete Migration**; it will refuse because no matching journal exists.

**What to do**:

1. Do not roll back, recover, or manually delete the destination. `<DestRoot>` holds the real data behind the Junction; deleting it breaks the original path.
2. **Do not delete application-owned Junctions**: they usually point at runtime dependencies inside the installation directory and removing them breaks the application.
3. Select only unfinished tasks for later runs, or set the completed task to `"Disabled": true` in `config/migration-tasks.json` (saving keeps a timestamped `.bak` backup).
4. Trusted links are declared per task with the `TrustedReparsePoints` field as in-tree relative paths, are denied by default, and affect read-only inspection only; copy and deletion paths remain strict. See `config/migration-tasks.schema.json` for the format. An already-migrated task now shows `SKIP_ALREADY_LINK` in a preview instead of failing preflight.
5. If an application update or reinstall replaces the Junction with a real directory, use Preview Repair then Run Repair. **Do not rerun migration**, which could overwrite old destination data using the new small directory on C:.
6. Fully exit guarded applications (including the tray icon) before rerunning a preview. A skip caused by a running process is a safety feature.

**Safety rules**: delete the source only after verification; if Junction creation fails, keep the destination copy and use the suggested `mklink /J "<source>" "<target>"`; never terminate the elevated worker during Junction cutover.

## Troubleshooting: selection prompts and copy verification failures

**`Select at least one item.`**: no task is checked.

**`All selected tasks are disabled...`**: tasks are checked, but every one of them has `"Disabled": true`. The GUI filters disabled tasks, so the command has no executable target. Set the task to `"Disabled": false` in Configuration, save, refresh the task list, and check it again; the prompt lists the filtered `TaskId` values. Explain Configuration is unaffected by the disabled state, which is why explaining can succeed while previewing reports no selection.

**`FAIL_TRANSACTION ... Copy verification failed: Metadata/content differs (acl): <relative path>`**:

- After copying, Csaver captures snapshots of source and destination and compares length, creation/write times, attributes, ACL, and content SHA256 for every entry. Any difference blocks source deletion and Junction creation. This is fail-closed; **the source directory is left intact**.
- The parenthesized field names the exact difference, for example `acl`, `lastWriteTime`, `attributes`, or `content`.
- On a cross-volume migration the source DACL is usually inherited from `C:\Users\<user>` while the destination inherits from a new parent such as `D:\`. Older builds wrote the source DACL onto an unprotected destination object, which then re-inherited the new parent's permissions. The effective rights differed, so verification refused to commit.
- The current build protects the destination DACL and rebuilds the source's effective access rules (including inherited ones) as explicit ACEs. Verification compares the effective DACL and ignores only the inheritance origin; the permission set itself must still match exactly.

**Required step before retrying**: a verification failure leaves a `RecoveryRequired` journal under `<DestRoot>\.csaver-journal`. Preflight refuses a preview or migration while a matching incomplete journal exists. Select **Recover Incomplete Transaction** with the task checked first, then rerun Preview Migration and Run Migration. The previous copy in the destination is overwritten file by file, while destination-only data is retained. Do not manually delete the destination or the journal before recovery.

## Troubleshooting: repeated failures on another PC (Trae CN / `.trae-cn`)

**Symptoms**: `The value '-536805376' is not valid for this usage of the type FileSystemRights`, `Access to the path 'D:\...\git\objects\...' is denied` on a retry, `Access to the path 'distro-support.json' is denied` during cutover, then `RECOVERY_REFUSED` / `REPAIR_NEEDS_REVIEW`.

**Root causes**:

1. Application-owned DACLs can contain raw `GENERIC_*` masks (for example `0xE0010000`, which reads back as `-536805376` when cast to `FileSystemRights`). Older builds rebuilt every ACE through `FileSystemAccessRule`, which rejects that mask and aborts the copy. The current build copies the DACL as a raw security descriptor with a protected (`D:P`) DACL; verification compares effective rights, maps `GENERIC_*` bits to file rights, and merges duplicate ACEs for the same trustee.
2. A failed run leaves a half-copied destination whose files are read-only or carry a restrictive DACL. A plain `File.Copy(..., true)` then fails with `Access ... is denied`. The current build clears `ReadOnly`, grants the current user a temporary write ACE for the retry, and re-applies the source DACL afterwards.
3. The application still holds handles on source files, so `Directory.Move` (staging) and `Directory.Delete` (staging cleanup) fail with only a short file name. The current build checks that source files can be opened exclusively **before** copying, clears read-only attributes before removing staging, and reports full paths plus a "close applications" hint.
4. Once copy or cutover has written `SourceSnapshot` / `DestinationSnapshot` into the journal, recovery is intentionally fail-closed. `RECOVERY_REFUSED` must not be bypassed by deleting the journal. A `REPAIR_NEEDS_REVIEW` result always carries its own reason in the detail — on older builds it could also be the repair cutover state-machine defect described below; never delete the journal, source, destination, or staging folder to work around it.

**Immediate unblock on that PC**:

1. Fully exit the application, including its tray icon and background helpers; reboot if handles persist.
2. Record the real state before touching anything:

   ```powershell
   Get-Item -LiteralPath "$env:APPDATA\Trae CN" -Force | Select-Object FullName, Attributes, LinkType, Target
   Get-Item -LiteralPath "D:\<DestRoot>\<task>" -Force | Select-Object FullName, Attributes, LinkType, Target
   Get-ChildItem -LiteralPath "D:\<DestRoot>\.csaver-journal" -Force
   ```

3. Use the new release ZIP: replace `Csaver.exe` in the existing folder, keeping `config/` and `data/` intact, or extract to a fresh folder and copy `config/` across. Never delete history files or journals while "cleaning up".
4. Select only the failed task and run **Recover Incomplete Transaction**. For a pre-cutover journal this clears the transaction; rerun **Preview Migration** and **Run Migration**. Read-only or restricted leftovers are now overwritten automatically.
5. If recovery reports `RECOVERY_REFUSED` (the journal contains snapshot evidence of copy/cutover progress), **stop**. Keep the source, destination, staging directory, and journal intact; do not force-kill the elevated worker. Collect `logs/worker-<OperationId>.log` and the journal, compare both trees, and resolve the cutover manually with the data verified. Automatic recovery is deliberately not widened to this state.
6. If a source file is locked, the new error contains its full path. Close the owning application and retry. Csaver now fails before cutover, so a healthy source no longer produces an unrecoverable journal.

## Troubleshooting: Doubao copy verification failure and repair cutover

**Symptoms**: `FAIL_TRANSACTION ... Copy verification failed: Metadata/content differs (lastWriteTime): ...\assets\comps; Metadata/content differs (length,content): ...\comps-model-template.xlsx`, followed by two consecutive `REPAIR_NEEDS_REVIEW ... Invalid journal transition: SourceStaged -> LinkValidated` results and a `REPAIR_PLAN` that lists the same 10 conflicts every time.

**Root causes**:

1. The Doubao process rewrote its own skill template assets (`.xlsx` / `.doc`) while `CopyPreserving` was running. Csaver snapshots the source **after** the copy and compares length, timestamps, attributes, ACL, and content SHA256 with the destination; any difference stops the operation before the source is deleted. This is intentional and stays strictly fail-closed: current builds do **not** recopy changed files and do **not** ignore differences. They only make the failure actionable — changed entries, mismatch totals, the `Guard` value to close, and the correct next command.
2. A copy verification failure is raised **before** `SourceSnapshot` / `DestinationSnapshot` are written, so its journal still has no snapshot evidence and remains eligible for **Recover Incomplete Transaction**. Running **Repair** was the wrong command: Repair is for a Junction that an application update or reinstall replaced with a real directory, not for an unstable source.
3. Older builds had a defect in the repair cutover: they transitioned from `SourceStaged` straight to `LinkValidated`, skipping `JunctionCreated`, which the journal state machine forbids. Because the error happened in the non-cancellable cutover, the source was moved back and the new Junction deleted every time, while the merge had already written files into the destination and archived conflicts under `_repair_conflicts` / `_repair_backup`. Those archives are intentionally retained (data is never deleted), which is why the preview kept listing the same conflicts. Current builds use the full `Merging → Verified → SourceStaged → JunctionCreated → LinkValidated → Committed` chain and commit normally.

**What to do on that PC**:

1. Fully exit Doubao, including its tray icon and background helpers; reboot if it keeps rewriting files. An occupied application data directory must be skipped and retried after the process is closed, never forced through.
2. Replace `Csaver.exe` with the new release ZIP, keeping `config/` and `data/` intact. Do not delete history files, journals, `_repair_conflicts`, or `_repair_backup` as "cleanup".
3. Record the real state before any retry:

   ```powershell
   Get-Item -LiteralPath "$env:LOCALAPPDATA\Doubao" -Force | Select-Object FullName, Attributes, LinkType, Target
   Get-Item -LiteralPath "D:\AppDataMoved\Local\Doubao" -Force | Select-Object FullName, Attributes, LinkType, Target
   Get-ChildItem -LiteralPath "D:\AppDataMoved\.csaver-journal" -Force
   ```

4. Select only `doubao-local` and run **Recover Incomplete Transaction**, then **Preview Migration** and **Run Migration**. Use **Repair** only when the source really is a broken Junction or a real directory left behind by an application update.
5. If the failure repeats, the new error names the changed entries and the `Guard` value (`doubao`). Close that application and retry. Csaver will never recopy the changed files for you or weaken the check to make verification pass.
6. If a journal already contains snapshot evidence, automatic recovery is refused. Keep both trees and the journal intact and resolve the transaction manually as described in the recovery section above.
