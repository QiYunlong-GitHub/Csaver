# Safety Model

Csaver treats paths, configuration, imported history, journals, and elevated-worker messages as untrusted input.

## Preflight invariants
- Paths must canonicalize to absolute local paths.
- Source and destination must be boundary-aware disjoint paths.
- Nested or ancestor reparse points are rejected; an existing source Junction is accepted only when its target exactly matches the task destination.
- Junction mutation requires NTFS and broad roots require explicit acknowledgement.
- Guarded running processes block mutation.

## Reparse point policy
- **Deny by default.** Any reparse point encountered by snapshot capture, copying, source staging, deletion, cutover, or rollback fails the operation closed. Csaver never follows a link to read, hash, copy, or delete data outside the declared tree.
- Read-only inspection may record a reparse point **without descending into it**, so statistics cannot recurse through links or double-count a linked tree. Undeclared links still produce an error-level preflight issue; only explicitly declared, in-tree relative paths may be trusted for read-only checks. A trust declaration never relaxes snapshot capture, copying, staging, deletion, or rollback.
- A healthy source Junction whose target exactly equals the configured destination is an already-migrated task. Preflight short-circuits the destination tree walk, and migration reports an already-linked skip without creating a journal, copying data, or touching the link again.

## Transactions
Migration copies without deleting destination-only entries, captures structural snapshots, rechecks source stability, stages the real source, creates a mount-point Junction, and removes staging only after exact target validation. Repair archives or backs up conflicts before cutover. Rollback requires committed audit identity and snapshot agreement, restores through source-side staging, and moves the former destination to indexed quarantine.

Atomic JSON journals record each durable phase. Invalid state transitions are rejected. Cancellation is honored before cutover and disabled during source replacement. A failed cutover attempts to restore the staged source and marks the journal recovery-required. The repair cutover uses the same auditable chain as migration — `Merging → Verified → SourceStaged → JunctionCreated → LinkValidated → Committed` — for both an ordinary directory source and a Junction pointing at the wrong target. The success path never writes `State` directly; only the failure-path fallback may do so, ensuring the error is always persisted.

## Copy verification
Verification is strictly fail-closed: any difference in length, content, timestamps, attributes, or effective DACL aborts before the source is staged. Csaver never recopies changed files and never ignores a difference to make verification pass; the failure message only reports the changed entries, mismatch totals, and the `Guard` process to close, then directs the operator to Recover Incomplete Transaction. A copy verification failure is raised before any snapshot is attached to the journal, so recovery remains available. Repair is not a substitute for recovery: it addresses a broken Junction, not an unstable source.

## Permission preservation
A cross-volume copy must not silently adopt the destination parent's inherited permissions. Each copied directory and file receives a protected DACL written as a raw security descriptor from the source's effective access rules, including rules that were inherited on the source side, so the destination parent cannot inject its own ACEs and raw `GENERIC_*` masks are never passed through `FileSystemAccessRule`. Verification compares effective rights per (ACE type, inheritance flags, trustee): `GENERIC_*` bits are mapped to file rights, duplicate ACEs for the same trustee are merged by mask union, and only the `Inherited` flag is ignored. A different trustee, effective right, allow/deny type, or inheritance scope still fails verification. A verification failure leaves the source intact, never creates the Junction, and marks the journal recovery-required. The pre-existing destination root stays a container: its own metadata and DACL are deliberately not overwritten, and the two roots are not compared as source-relative entries.

## Privilege boundary
The normal GUI prepares a one-time request. The same executable is launched with UAC in worker mode, connects over a current-user named pipe, verifies the nonce and configuration digest, reloads configuration, and repeats safety validation. Worker commands are allowlisted and there is no arbitrary deletion or quarantine-cleanup command.

## Unknown worker results
A lost terminal IPC frame is not evidence of a filesystem change. The worker persists its terminal result atomically before exiting; the GUI restores that result only after validating operation identity, kind, and nonce. `STATE_UNKNOWN` is reported only when no valid persisted result exists. Users must then verify source/destination link state, history records, and journals before retrying, and must never delete the source or destination as "cleanup". A preflight failure recorded with empty journal and manifest paths and no rollback eligibility means no copy, source deletion, Junction mutation, or manifest write occurred.
