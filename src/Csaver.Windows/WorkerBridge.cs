using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Csaver.Application;
using Csaver.Core;

namespace Csaver.Windows;

internal static class WorkerIpc
{
    // IPC frames are line-delimited JSON; indented JSON would split one frame into several lines.
    public static readonly JsonSerializerOptions Options = new(JsonDefaults.Options) { WriteIndented = false };
}

internal sealed class WorkerPipeWriter(StreamWriter writer)
{
    private readonly object sync = new();
    private bool terminalSent;
    private bool disconnected;

    public bool IsDisconnected { get { lock (sync) return disconnected; } }

    public void Write(WorkerEnvelope envelope)
    {
        if (!TryWrite(envelope)) throw new IOException("The worker IPC pipe is unavailable.");
    }

    public bool TryWrite(WorkerEnvelope envelope) => TryWrite(envelope, terminal: false);

    public bool TryWriteTerminal(WorkerEnvelope envelope) => TryWrite(envelope, terminal: true);

    private bool TryWrite(WorkerEnvelope envelope, bool terminal)
    {
        lock (sync)
        {
            if (terminalSent || disconnected) return false;
            try
            {
                var line = JsonSerializer.Serialize(envelope, WorkerIpc.Options);
                writer.WriteLine(line);
                writer.Flush();
                if (terminal) terminalSent = true;
                return true;
            }
            catch (IOException)
            {
                disconnected = true;
                if (terminal) terminalSent = true;
                return false;
            }
            catch (ObjectDisposedException)
            {
                disconnected = true;
                if (terminal) terminalSent = true;
                return false;
            }
            catch (InvalidOperationException)
            {
                disconnected = true;
                if (terminal) terminalSent = true;
                return false;
            }
            catch (JsonException)
            {
                disconnected = true;
                if (terminal) terminalSent = true;
                return false;
            }
        }
    }
}

internal sealed class WorkerProgress(WorkerPipeWriter ipc) : IProgress<OperationProgress>
{
    public void Report(OperationProgress value) => ipc.TryWrite(new WorkerEnvelope { Type = "progress", Progress = value });
}

public sealed class WorkerClient(string executablePath, PortableWorkspace workspace)
{
    public async Task<OperationResult> RunElevatedAsync(OperationRequest request, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var pipeName = "Csaver-" + request.OperationId + "-" + Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(workspace.DataDirectory, ".request-" + request.OperationId + ".json");
        AtomicFiles.WriteJson(requestPath, request);
        WorkerResultStore.Cleanup(workspace);
        try
        {
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = workspace.BaseDirectory
            };
            start.ArgumentList.Add("--worker");
            start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath);
            start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
            start.ArgumentList.Add("--nonce"); start.ArgumentList.Add(request.Nonce);
            try { Process.Start(start); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new OperationResult { OperationId = request.OperationId, Kind = request.Kind, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Cancelled = true, Message = "UAC elevation was cancelled." };
            }
            catch (Exception ex)
            {
                return RestoreDisconnected(workspace, request, $"Elevated worker could not be started: {ex.Message}");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                // Both wrappers share the duplex pipe; neither may close it before the outer using does.
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                using var cancelRegistration = cancellationToken.Register(() =>
                {
                    try
                    {
                        lock (writer) writer.WriteLine(JsonSerializer.Serialize(new WorkerEnvelope { Type = "cancel", OperationId = request.OperationId, Nonce = request.Nonce }, WorkerIpc.Options));
                    }
                    catch { }
                });
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    WorkerEnvelope? envelope;
                    try
                    {
                        envelope = JsonSerializer.Deserialize<WorkerEnvelope>(line, WorkerIpc.Options);
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidDataException("Elevated worker returned malformed IPC JSON.", ex);
                    }

                    if (envelope is null) throw new InvalidDataException("Elevated worker returned an empty IPC message.");
                    if (envelope.Progress is not null) progress?.Report(envelope.Progress);
                    if (envelope.Result is not null) { SendAck(writer, request); return envelope.Result; }
                    if (!string.IsNullOrWhiteSpace(envelope.Error)) { SendAck(writer, request); return WorkerErrorResult(request, envelope.Error); }
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (IOException ex)
            {
                return RestoreDisconnected(workspace, request, $"IPC pipe read failed: {ex.Message}");
            }
            catch (ObjectDisposedException ex)
            {
                return RestoreDisconnected(workspace, request, $"IPC pipe was disposed while waiting for the terminal frame: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                return RestoreDisconnected(workspace, request, "Timed out waiting for the elevated worker to connect.");
            }
            return RestoreDisconnected(workspace, request, "The elevated worker closed the pipe before a terminal frame was read.");
        }
        finally
        {
            try { if (File.Exists(requestPath)) File.Delete(requestPath); } catch { }
        }
    }

    private static void SendAck(StreamWriter writer, OperationRequest request)
    {
        try
        {
            lock (writer) writer.WriteLine(JsonSerializer.Serialize(new WorkerEnvelope { Type = "ack", OperationId = request.OperationId, Nonce = request.Nonce }, WorkerIpc.Options));
        }
        catch { }
    }

    public static OperationResult RestoreDisconnected(PortableWorkspace workspace, OperationRequest request, string? reason = null)
    {
        var restored = WorkerResultStore.TryRead(workspace, request);
        if (restored is null) return DisconnectedResult(request, reason);
        var detail = string.IsNullOrWhiteSpace(reason) ? "the terminal IPC frame was not delivered" : reason;
        restored.Message += $" (restored from durable worker result; {detail}.)";
        return restored;
    }
    private static OperationResult WorkerErrorResult(OperationRequest request, string error) => new()
    {
        OperationId = request.OperationId,
        Kind = request.Kind,
        StartedUtc = DateTimeOffset.UtcNow,
        CompletedUtc = DateTimeOffset.UtcNow,
        StateUnknown = true,
        Message = $"Elevated worker returned an error. {error} {RequestContext(request)}",
        Tasks = RequestContextTasks(request)
    };

    private static OperationResult DisconnectedResult(OperationRequest request, string? reason = null) => new()
    {
        OperationId = request.OperationId,
        Kind = request.Kind,
        StartedUtc = DateTimeOffset.UtcNow,
        CompletedUtc = DateTimeOffset.UtcNow,
        StateUnknown = true,
        Message = $"{reason ?? "Elevated worker disconnected before a terminal result; filesystem state is unknown."} {RequestContext(request)}",
        Tasks = RequestContextTasks(request)
    };

    internal static string RequestContext(OperationRequest request)
    {
        var selected = request.SelectedTaskIds.Count == 0 ? "<none>" : string.Join(",", request.SelectedTaskIds);
        var journal = request.Kind == OperationKind.RecoverIncompleteMigration
            ? "matching incomplete ForwardMigration journal under the selected destination .csaver-journal directory"
            : "not applicable";
        return $"OperationId={request.OperationId}; Kind={request.Kind}; BaseDirectory={request.BaseDirectory}; ConfigurationPath={request.ConfigurationPath}; SelectedTaskIds={selected}; RecoveryJournalContext={journal}.";
    }

    internal static List<OperationTaskResult> RequestContextTasks(OperationRequest request) =>
        request.FrozenTasks.Count > 0
            ? request.FrozenTasks.Select(task => new OperationTaskResult
            {
                TaskId = task.TaskId,
                Group = task.Group,
                Source = task.Source,
                Destination = task.Destination,
                DestinationSource = task.DestinationSource,
                Disabled = task.Disabled,
                IsDynamic = task.IsDynamic,
                Status = "STATE_UNKNOWN",
                TransactionState = "Unknown",
                Retryable = true,
                Detail = "No terminal worker result was delivered; filesystem state is unknown."
            }).ToList()
            : request.SelectedTaskIds.Select(taskId => new OperationTaskResult
            {
                TaskId = taskId,
                Status = "STATE_UNKNOWN",
                TransactionState = "Unknown",
                Retryable = true,
                Detail = "No terminal worker result was delivered; filesystem state is unknown."
            }).ToList();
}

public static class WorkerHost
{
    private static readonly TimeSpan AcknowledgementLimit = TimeSpan.FromSeconds(2);

    public static bool IsWorker(string[] args) => args.Contains("--worker", StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        OperationRequest? request = null;
        NamedPipeClientStream? pipe = null;
        StreamWriter? writer = null;
        StreamReader? commandReader = null;
        CancellationTokenSource? workerCancellation = null;
        WorkerPipeWriter? ipc = null;
        TaskCompletionSource<bool>? acknowledgement = null;
        try
        {
            var requestPath = Value(args, "--request");
            var pipeName = Value(args, "--pipe");
            var nonce = Value(args, "--nonce");

            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(30_000).ConfigureAwait(false);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            ipc = new WorkerPipeWriter(writer);

            try
            {
                request = JsonSerializer.Deserialize<OperationRequest>(await File.ReadAllTextAsync(requestPath).ConfigureAwait(false), JsonDefaults.Options)
                    ?? throw new InvalidDataException("Worker request JSON is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Worker request JSON is malformed.", ex);
            }

            if (string.IsNullOrWhiteSpace(request.Nonce)) throw new InvalidDataException("Worker request nonce is missing.");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Nonce), Encoding.UTF8.GetBytes(nonce)))
                throw new UnauthorizedAccessException("Worker nonce mismatch.");

            WorkerLog.Write(request.BaseDirectory, request.OperationId, "Worker connected and validated the request.");
            commandReader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            workerCancellation = new CancellationTokenSource();
            acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var commandStream = commandReader!;
            var cancellation = workerCancellation!;
            var ackSource = acknowledgement!;
            var currentRequest = request!;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await commandStream.ReadLineAsync().ConfigureAwait(false) is { } command)
                    {
                        WorkerEnvelope? envelope;
                        try { envelope = JsonSerializer.Deserialize<WorkerEnvelope>(command, WorkerIpc.Options); }
                        catch (JsonException ex)
                        {
                            WorkerLog.Write(currentRequest.BaseDirectory, currentRequest.OperationId, "Worker command JSON invalid: " + ex.Message);
                            continue;
                        }
                        if (envelope is null) continue;
                        var identityValid = string.Equals(envelope.OperationId, currentRequest.OperationId, StringComparison.Ordinal) &&
                            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(envelope.Nonce), Encoding.UTF8.GetBytes(currentRequest.Nonce));
                        WorkerLog.Write(currentRequest.BaseDirectory, currentRequest.OperationId,
                            $"Worker command frame: type={envelope.Type}; identityValid={identityValid}.");
                        if (!identityValid) continue;
                        if (envelope.Type == "cancel") cancellation.Cancel();
                        else if (envelope.Type == "ack") ackSource.TrySetResult(true);
                    }
                }
                catch { }
                finally { ackSource.TrySetResult(false); }
            });
            var progress = new WorkerProgress(ipc);
            var engine = CreateEngine();
            var result = await engine.ExecuteAsync(request, progress, workerCancellation.Token).ConfigureAwait(false);
            var persisted = Persist(request, result);
            WorkerLog.Write(request.BaseDirectory, request.OperationId, $"Terminal result persisted={persisted}; success={result.Success}.");
            var sent = ipc.TryWriteTerminal(new WorkerEnvelope { Type = "result", OperationId = request.OperationId, Result = result });
            WorkerLog.Write(request.BaseDirectory, request.OperationId, $"Terminal frame sent={sent}; pipeDisconnected={ipc.IsDisconnected}.");
            if (sent && acknowledgement is not null)
            {
                var completed = await Task.WhenAny(acknowledgement.Task, Task.Delay(AcknowledgementLimit)).ConfigureAwait(false);
                string outcome;
                if (completed != acknowledgement.Task) outcome = "acknowledgement limit reached";
                else outcome = await acknowledgement.Task.ConfigureAwait(false) ? "acknowledged by GUI" : "pipe closed before acknowledgement";
                WorkerLog.Write(request.BaseDirectory, request.OperationId, "Terminal delivery outcome: " + outcome + ".");
            }
            return sent ? result.Success ? 0 : 2 : 3;
        }
        catch (Exception ex)
        {
            WorkerLog.Write(request?.BaseDirectory ?? AppContext.BaseDirectory, request?.OperationId ?? OperationIdFromArgs(args),
                "Worker failed before a terminal result: " + ex.Message);
            if (ipc is null) return 3;
            if (request is null)
            {
                ipc.TryWriteTerminal(new WorkerEnvelope { Type = "error", Error = ex.Message });
                return 3;
            }
            var failure = CreateFailureResult(request, ex);
            Persist(request, failure);
            ipc.TryWriteTerminal(new WorkerEnvelope { Type = "result", OperationId = request.OperationId, Result = failure });
            return 3;
        }
        finally
        {
            try { workerCancellation?.Dispose(); } catch { }
            try { writer?.Flush(); } catch { }
            try { writer?.Dispose(); } catch { }
            try { commandReader?.Dispose(); } catch { }
            try { pipe?.Dispose(); } catch { }
        }
    }

    public static string OperationIdFromArgs(string[] args)
    {
        try
        {
            var parts = Value(args, "--pipe").Split('-');
            return parts.Length >= 2 ? parts[1] : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static bool Persist(OperationRequest request, OperationResult result)
    {
        try
        {
            if (!Directory.Exists(request.BaseDirectory)) return false;
            return WorkerResultStore.TryWrite(new PortableWorkspace(request.BaseDirectory), request, result);
        }
        catch { return false; }
    }

    internal static OperationResult CreateFailureResult(OperationRequest request, Exception exception) => new()
    {
        OperationId = request.OperationId,
        Kind = request.Kind,
        StartedUtc = DateTimeOffset.UtcNow,
        CompletedUtc = DateTimeOffset.UtcNow,
        Success = false,
        Cancelled = exception is OperationCanceledException,
        StateUnknown = true,
        Message = $"Elevated worker failed: {exception.Message} {WorkerClient.RequestContext(request)}",
        Tasks = WorkerClient.RequestContextTasks(request)
    };

    public static NativeOperationEngine CreateEngine()
    {
        var junctions = new WindowsJunctionService();
        var trees = new WindowsTreeService();
        return new NativeOperationEngine(junctions, trees, new ConfigurationService(), new TaskResolver(), new JournalStore());
    }

    private static string Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])) throw new ArgumentException($"Missing worker argument: {name}");
        return args[index + 1];
    }
}
