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
