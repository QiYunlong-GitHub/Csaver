# Release Process

1. Run Release tests.
2. Publish `Csaver.App` self-contained and single-file for `win-x64`; trimming remains disabled.
3. Optionally set `CSAVER_SIGNTOOL_PATH`, `CSAVER_SIGN_CERT_THUMBPRINT`, and `CSAVER_TIMESTAMP_URL`. The packaging tool signs with SHA-256, timestamps, and verifies the Authenticode signature.
4. Run `Csaver.Packaging` to create the ZIP and `SHA256SUMS.txt`.
5. Inspect the archive and verify no `.ps1` file exists.
6. Test on a clean supported Windows VM without a preinstalled .NET runtime.

Unsigned development builds may trigger Windows reputation warnings. Authenticode identifies the publisher and detects tampering, but SmartScreen reputation is not immediate or guaranteed. Never commit certificates, private keys, or passwords.

## Required clean-Windows manual checks

Automated tests cover contracts and injected failures, but these behaviors still require a clean supported Windows machine:

1. Run migration, repair, and rollback with UAC elevation; cancel during a large copy and confirm the GUI waits for a terminal result and never claims cancellation while the worker is changing files.
2. Interrupt or terminate the worker at staging, Junction cutover, and rollback quarantine boundaries. Reopen Csaver and verify the displayed recovery journal, resume behavior, original link/target, and retained quarantine payload.
3. Verify repair preserves file and directory timestamps, attributes, and ACL/SDDL for copied and overwritten entries, and that a wrong-target Junction is restored after an injected or induced cutover failure.
4. Edit a wildcard directory or dynamic Lark membership between preparation and execution and confirm the frozen task-set check refuses the operation.
5. Verify rollback rejects edited history, manifest, journal, or current Junction targets; after a successful rollback, the original migration is absent from candidates.
6. Confirm List Rollback Candidates runs with no task selected and View Quarantine remains read-only while showing malformed inventory, missing payload, and unreadable-journal entries.
7. Confirm the packaged `Csaver.exe` runs without a preinstalled .NET runtime and that paths resolve from the current user/profile rather than the build machine.
8. On an already-migrated task whose destination tree contains application-owned Junctions, run Preview Migration and Run Migration: both must report `SKIP_ALREADY_LINK` (not `PREFLIGHT_FAILED`), create no journal or manifest, and leave the existing link untouched. A preview on a task with a declared trusted link must show `PLAN` without following the link.
9. Terminate the elevated worker after its result is persisted but before the pipe frame is delivered (or otherwise force an IPC disconnect). Reopen Csaver and confirm the GUI shows the restored real result with a "restored from durable worker result" note instead of `STATE_UNKNOWN`; confirm `logs/worker-<OperationId>.log` is readable and the result file under `data/worker-results/` is consumed. With no valid result file, `STATE_UNKNOWN` must still be reported.
10. Confirm `logs/worker-<OperationId>.log` records `acknowledged by GUI` for a normal run, and that a cancel frame arrives as a single-line `Worker command frame: type=cancel; identityValid=True` entry. A `Worker command JSON invalid` entry means a producer regressed to indented JSON.
11. Migrate a real user-profile directory to another volume whose parent has a different inherited DACL. Confirm copy verification passes and the destination's effective rights match the source. Induce an ACL mismatch and confirm the failure detail names `acl`, the source is not deleted, and the journal is `RecoveryRequired`. Also check that selecting only disabled tasks shows the disabled-task prompt rather than "Select at least one item".
12. On a directory whose DACL contains raw `GENERIC_*` masks (for example Trae CN / `.trae-cn` on a clean second PC), confirm the copy does not throw `FileSystemRights -536805376` and verification passes. Leave a read-only restricted file in the destination and confirm the retry overwrites it; hold a source file open with `FileShare.None` and confirm the run fails before cutover with the full path and a recoverable journal. Confirm a journal containing snapshot evidence still produces `RECOVERY_REFUSED`.
13. Run Repair end-to-end twice: once with a real directory as the source, once with a Junction pointing at a wrong target. Both must reach `REPAIR_MERGED`, leave the repair journal in `Committed`, and create the correct Junction with no `Invalid journal transition`. Confirm repeat runs no longer accumulate new conflict archives.
14. Rewrite a source file immediately after the copy (simulating an application refreshing its own templates). Confirm `FAIL_TRANSACTION` names the changed entry, mismatch totals, the `Guard` hint, and the Recover Incomplete Transaction guidance; confirm the destination keeps the originally copied bytes, the source stays an ordinary directory, no Junction is created, and the forward journal has no snapshot evidence so recovery still succeeds.
