# Architecture

`Csaver.App` is the WPF shell and also hosts the hidden elevated worker entry point. `Csaver.Core` contains versioned contracts, `Csaver.Application` owns configuration, resolution, journals, history, and portable storage, and `Csaver.Windows` implements Windows filesystem, Junction, operation, UAC, and IPC behavior.

Read-only commands execute in the normal process. Mutating commands serialize a digest-bound request and launch the same binary with `--worker`; the worker independently validates the request and configuration before using the transactional engine. Progress and results are streamed over a current-user named pipe and durable state is written to destination journals plus portable history records.

The project intentionally has no dependency on the legacy PowerShell implementation and forbids `.ps1` files in source and release artifacts.

## Worker result durability and diagnostics

Every IPC frame (progress, result, error, cancel, ack) is one line of JSON. All producers and consumers use the shared `WorkerIpc.Options`, which clones `JsonDefaults.Options` with `WriteIndented = false`; indented JSON would split one frame into several lines and silently break command parsing.

Before sending its terminal frame, the elevated worker atomically persists the `OperationResult` under `data/worker-results/<OperationId>.json` together with the operation identity, kind, and a nonce digest. The GUI acknowledges a received terminal frame over the pipe. The worker keeps the pipe open until it receives that acknowledgement, detects the peer closing the pipe, or reaches a short upper limit; stream wrappers on both sides use `leaveOpen`, so the pipe is closed only once, after the final flush. Exit-code semantics and the current-user pipe/UAC boundary are unchanged, and the worker logs which of the three outcomes occurred.

On pipe EOF, I/O failure, startup failure, or connection timeout, the GUI restores the persisted result only after validating all three identity fields, then consumes the file; stale files are cleaned up on the next operation. `STATE_UNKNOWN` is reported only when no valid persisted result exists. The concrete disconnect reason is appended to the restored result message. The worker also appends phase and exception records to `logs/worker-<OperationId>.log` without recording the nonce or the full request, making a disconnect diagnosable after the fact.
