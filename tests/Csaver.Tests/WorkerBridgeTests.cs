using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Csaver.Application;
using Csaver.Core;
using Csaver.Windows;

namespace Csaver.Tests;

[TestClass]
public sealed class WorkerBridgeTests
{
    [TestMethod]
    public void RepeatedRequestHandoffNeverExposesPartialJson()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, ".request.json");

        for (var index = 0; index < 500; index++)
        {
            AtomicFiles.WriteJson(path, Request(index.ToString()));
            var request = JsonSerializer.Deserialize<OperationRequest>(File.ReadAllText(path, Encoding.UTF8), JsonDefaults.Options);
            Assert.IsNotNull(request);
        }
    }

    [TestMethod]
    public void ConcurrentWorkerEnvelopeWritesRemainLineDelimitedJson()
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var ipc = new WorkerPipeWriter(writer);

        Parallel.For(0, 1000, index => ipc.Write(new WorkerEnvelope
        {
            Type = "progress",
            Progress = new OperationProgress { OperationId = index.ToString(), Message = "progress" }
        }));

        writer.Flush();
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);

        Assert.AreEqual(1000, lines.Count);
        Assert.IsTrue(lines.All(line => JsonSerializer.Deserialize<WorkerEnvelope>(line, JsonDefaults.Options)?.Progress is not null));
    }

    [TestMethod]
    public void RepeatedProgressWritesAreDeliveredBeforeTerminalResult()
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var ipc = new WorkerPipeWriter(writer);

        for (var index = 0; index < 5000; index++)
        {
            Assert.IsTrue(ipc.TryWrite(new WorkerEnvelope
            {
                Type = "progress",
                Progress = new OperationProgress { OperationId = "copy", TaskId = "anaconda", Stage = "Copying", Message = $"Copied {index + 1} files" }
            }));
        }

        Assert.IsTrue(ipc.TryWriteTerminal(new WorkerEnvelope
        {
            Type = "result",
            Result = new OperationResult { OperationId = "copy", Success = true }
        }));
        writer.Flush();
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);

        Assert.AreEqual(5001, lines.Count);
        Assert.AreEqual("result", JsonSerializer.Deserialize<WorkerEnvelope>(lines[^1], JsonDefaults.Options)?.Type);
    }

    [TestMethod]
    public void ClosedWorkerPipeDoesNotEscapeProgressOrTerminalWrite()
    {
        using var stream = new FailingStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        var ipc = new WorkerPipeWriter(writer);
        var envelope = new WorkerEnvelope { Type = "progress", Progress = new OperationProgress { Message = "copying" } };

        Assert.IsTrue(ipc.TryWrite(envelope));
        stream.FailWrites = true;

        Assert.IsFalse(ipc.TryWrite(envelope));
        Assert.IsFalse(ipc.TryWriteTerminal(new WorkerEnvelope { Type = "result", Result = new OperationResult { Success = false } }));
    }

    [TestMethod]
    public void WorkerFailureResultPreservesTheActualException()
    {
        var request = Request("copy-failure");
        var result = WorkerHost.CreateFailureResult(request, new IOException("copy failed: access denied"));

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.StateUnknown);
        StringAssert.Contains(result.Message, "copy failed: access denied");
    }

    [TestMethod]
    public async Task WorkerFailureUsesTheConnectedPipeAndReturnsStructuredResult()
    {
        using var folder = new TempFolder();
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize("{}");
        var request = new OperationRequest
        {
            OperationId = "worker-failure",
            Kind = OperationKind.Migrate,
            BaseDirectory = workspace.BaseDirectory,
            ConfigurationPath = workspace.ConfigurationPath,
            ConfigurationSha256 = "wrong-hash",
            Nonce = "nonce"
        };
        var requestPath = Path.Combine(workspace.DataDirectory, ".request.json");
        AtomicFiles.WriteJson(requestPath, request);
        var pipeName = "Csaver-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var worker = WorkerHost.RunAsync(["--worker", "--request", requestPath, "--pipe", pipeName, "--nonce", "nonce"]);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, new UTF8Encoding(false));
        var line = await reader.ReadLineAsync();
        var code = await worker;
        var envelope = JsonSerializer.Deserialize<WorkerEnvelope>(line ?? throw new InvalidDataException("Worker did not return a result envelope."), JsonDefaults.Options);

        Assert.AreEqual(2, code, line);
        Assert.IsNotNull(envelope);
        Assert.AreEqual("result", envelope!.Type);
        Assert.IsNotNull(envelope.Result);
        Assert.IsFalse(envelope.Result!.Success);
        StringAssert.Contains(envelope.Result.Message, "Configuration changed after the operation was prepared");
    }

    [TestMethod]
    public async Task MalformedWorkerRequestReturnsSafeErrorEnvelope()
    {
        using var folder = new TempFolder();
        var requestPath = Path.Combine(folder.Path, ".request.json");
        File.WriteAllText(requestPath, "{\"OperationId\":\"incomplete\"");
        var pipeName = "Csaver-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var worker = WorkerHost.RunAsync(["--worker", "--request", requestPath, "--pipe", pipeName, "--nonce", "nonce"]);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, new UTF8Encoding(false));
        var line = await reader.ReadLineAsync();
        var code = await worker;
        var envelope = JsonSerializer.Deserialize<WorkerEnvelope>(line ?? throw new InvalidDataException("Worker did not return an error envelope."), JsonDefaults.Options);

        Assert.AreEqual(3, code);
        Assert.IsNotNull(envelope);
        Assert.AreEqual("error", envelope!.Type);
        StringAssert.Contains(envelope.Error, "malformed");
    }

    [TestMethod]
    public async Task WorkerRecoverySuccessReturnsTerminalResultOverOriginalConnection()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(folder.Path, "destination")).FullName;
        File.WriteAllText(Path.Combine(source, "source.txt"), "source-data");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var journalPath = WriteForwardJournal(folder.Path, source, destination, "RecoveryRequired", lastError: "Copy verification failed");
        var request = RecoveryRequest(workspace);

        var (code, envelope) = await RunWorker(request, workspace);

        Assert.AreEqual(0, code, envelope.Error);
        Assert.AreEqual("result", envelope.Type);
        Assert.IsNotNull(envelope.Result);
        Assert.IsTrue(envelope.Result!.Success, envelope.Result.Message);
        Assert.AreEqual("RECOVERED", envelope.Result.Tasks.Single().Status);
        Assert.AreEqual("Recovered", new JournalStore().Load(journalPath)?.State);
    }

    [TestMethod]
    public async Task WorkerRecoveryRefusalReturnsStructuredFailureOverOriginalConnection()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(folder.Path, "destination")).FullName;
        Directory.CreateDirectory(Path.Combine(folder.Path, "stage", "payload"));
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        WriteForwardJournal(folder.Path, source, destination, "RecoveryRequired", Path.Combine(folder.Path, "stage", "payload"));
        var request = RecoveryRequest(workspace);

        var (code, envelope) = await RunWorker(request, workspace);

        Assert.AreEqual(2, code, envelope.Error);
        Assert.AreEqual("result", envelope.Type);
        Assert.IsNotNull(envelope.Result);
        Assert.IsFalse(envelope.Result!.Success);
        StringAssert.Contains(envelope.Result.Tasks.Single().Detail, "C-stage payload exists");
    }

    [TestMethod]
    public async Task WorkerDeliversRepeatedProgressBeforeTerminalOverNamedPipe()
    {
        using var folder = new TempFolder();
        var tasks = Enumerable.Range(0, 100).Select(index => new MigrationTaskDefinition
        {
            TaskId = "task-" + index,
            Group = "Fixture",
            Source = Path.Combine(folder.Path, "source-" + index),
            Dest = Path.Combine(folder.Path, "destination-" + index)
        }).ToList();
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(JsonSerializer.Serialize(new MigrationConfiguration { DestRoot = folder.Path, Tasks = tasks }));
        var request = new OperationRequest
        {
            OperationId = "repeated-progress",
            Kind = OperationKind.ExplainConfiguration,
            BaseDirectory = workspace.BaseDirectory,
            ConfigurationPath = workspace.ConfigurationPath,
            ConfigurationSha256 = workspace.ComputeConfigurationSha256(),
            SelectedTaskIds = tasks.Select(task => task.TaskId!).ToList(),
            Nonce = "nonce"
        };

        var (code, envelopes) = await RunWorkerMessages(request, workspace);

        Assert.AreEqual(0, code);
        Assert.AreEqual(100, envelopes.Count(envelope => envelope.Progress is not null));
        Assert.AreEqual("result", envelopes[^1].Type);
        Assert.IsTrue(envelopes[^1].Result!.Success);
    }

    [TestMethod]
    public async Task WorkerClosedPipeDoesNotEscapeBeforeTerminalDelivery()
    {
        using var folder = new TempFolder();
        var requestPath = Path.Combine(folder.Path, ".request.json");
        File.WriteAllText(requestPath, "{\"OperationId\":\"incomplete\"");
        var pipeName = "Csaver-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var worker = WorkerHost.RunAsync(["--worker", "--request", requestPath, "--pipe", pipeName, "--nonce", "nonce"]);
        await server.WaitForConnectionAsync();
        server.Dispose();

        var code = await worker;
        Assert.AreEqual(3, code);
    }

    [TestMethod]
    public void DisconnectedWorkerRestoresDurableResultInsteadOfStateUnknown()
    {
        using var folder = new TempFolder();
        var workspace = new PortableWorkspace(folder.Path);
        var request = ResultRequest("restore-op");
        var expected = new OperationResult
        {
            OperationId = request.OperationId, Kind = request.Kind, Success = false,
            Message = "Migration finished: 0 committed, 1 skipped, 0 failed.",
            Tasks = [new OperationTaskResult { TaskId = "workbuddy-home", Status = "SKIP_ALREADY_LINK" }]
        };
        Assert.IsTrue(WorkerResultStore.TryWrite(workspace, request, expected));

        var restored = WorkerClient.RestoreDisconnected(workspace, request);

        Assert.IsFalse(restored.StateUnknown);
        Assert.AreEqual("SKIP_ALREADY_LINK", restored.Tasks.Single().Status);
        StringAssert.Contains(restored.Message, "restored from durable worker result");
        Assert.IsFalse(File.Exists(WorkerResultStore.PathFor(workspace, request.OperationId)));
    }

    [TestMethod]
    public void MismatchedNonceFallsBackToStateUnknown()
    {
        using var folder = new TempFolder();
        var workspace = new PortableWorkspace(folder.Path);
        var request = ResultRequest("mismatch-op");
        Assert.IsTrue(WorkerResultStore.TryWrite(workspace, request, new OperationResult { OperationId = request.OperationId, Kind = request.Kind, Success = true }));

        var unknown = WorkerClient.RestoreDisconnected(workspace, ResultRequest("mismatch-op", "different-nonce"));

        Assert.IsTrue(unknown.StateUnknown);
        Assert.AreEqual("STATE_UNKNOWN", unknown.Tasks.Single().Status);
        Assert.IsTrue(File.Exists(WorkerResultStore.PathFor(workspace, request.OperationId)));
    }

    [TestMethod]
    public void MissingDurableResultFallsBackToStateUnknown()
    {
        using var folder = new TempFolder();
        var workspace = new PortableWorkspace(folder.Path);
        var request = ResultRequest("missing-op");

        var unknown = WorkerClient.RestoreDisconnected(workspace, request);

        Assert.IsTrue(unknown.StateUnknown);
        StringAssert.Contains(unknown.Message, "Elevated worker disconnected before a terminal result");
        Assert.AreEqual("STATE_UNKNOWN", unknown.Tasks.Single().Status);
    }

    [TestMethod]
    public void RestoredResultReportsTheDisconnectReason()
    {
        using var folder = new TempFolder();
        var workspace = new PortableWorkspace(folder.Path);
        var request = ResultRequest("reason-op");
        Assert.IsTrue(WorkerResultStore.TryWrite(workspace, request,
            new OperationResult { OperationId = request.OperationId, Kind = request.Kind, Success = true }));

        var restored = WorkerClient.RestoreDisconnected(workspace, request, "IPC pipe read failed: simulated broken pipe");

        Assert.IsFalse(restored.StateUnknown);
        StringAssert.Contains(restored.Message, "restored from durable worker result");
        StringAssert.Contains(restored.Message, "IPC pipe read failed: simulated broken pipe");
    }

    [TestMethod]
    public async Task AcknowledgedWorkerConfirmsTerminalDelivery()
    {
        using var folder = new TempFolder();
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(Path.Combine(folder.Path, "source"), Path.Combine(folder.Path, "destination")));
        var request = new OperationRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Kind = OperationKind.ExplainConfiguration,
            BaseDirectory = workspace.BaseDirectory,
            ConfigurationPath = workspace.ConfigurationPath,
            ConfigurationSha256 = workspace.ComputeConfigurationSha256(),
            SelectedTaskIds = ["anaconda"],
            Nonce = "nonce"
        };

        var (code, envelopes) = await RunWorkerMessages(request, workspace);

        Assert.AreEqual(0, code);
        Assert.AreEqual("result", envelopes[^1].Type);
        var logPath = Path.Combine(folder.Path, "logs", "worker-" + request.OperationId + ".log");
        Assert.IsTrue(File.Exists(logPath));
        var log = File.ReadAllText(logPath);
        StringAssert.Contains(log, "acknowledged by GUI", log);
    }

    private static OperationRequest ResultRequest(string operationId, string nonce = "nonce") => new()
    {
        OperationId = operationId, Kind = OperationKind.Migrate,
        BaseDirectory = "C:\\Csaver", ConfigurationPath = "C:\\Csaver\\migration-tasks.json",
        ConfigurationSha256 = "hash", SelectedTaskIds = ["workbuddy-home"], Nonce = nonce
    };

    private static void SendServerAck(StreamWriter writer, OperationRequest request) =>
        writer.WriteLine(JsonSerializer.Serialize(new WorkerEnvelope { Type = "ack", OperationId = request.OperationId, Nonce = request.Nonce }, WorkerIpc.Options));

    private static async Task<(int Code, List<WorkerEnvelope> Envelopes)> RunWorkerMessages(OperationRequest request, PortableWorkspace workspace)
    {
        var requestPath = Path.Combine(workspace.DataDirectory, ".request.json");
        AtomicFiles.WriteJson(requestPath, request);
        var pipeName = "Csaver-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var worker = WorkerHost.RunAsync(["--worker", "--request", requestPath, "--pipe", pipeName, "--nonce", request.Nonce]);
        await server.WaitForConnectionAsync();
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        var envelopes = new List<WorkerEnvelope>();
        while (await reader.ReadLineAsync() is { } line)
        {
            var envelope = JsonSerializer.Deserialize<WorkerEnvelope>(line, JsonDefaults.Options) ?? throw new InvalidDataException("Worker returned an empty envelope.");
            envelopes.Add(envelope);
            if (envelope.Result is not null || !string.IsNullOrWhiteSpace(envelope.Error)) SendServerAck(writer, request);
        }

        return (await worker, envelopes);
    }

    private static async Task<(int Code, WorkerEnvelope Envelope)> RunWorker(OperationRequest request, PortableWorkspace workspace)
    {
        var requestPath = Path.Combine(workspace.DataDirectory, ".request.json");
        AtomicFiles.WriteJson(requestPath, request);
        var pipeName = "Csaver-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var worker = WorkerHost.RunAsync(["--worker", "--request", requestPath, "--pipe", pipeName, "--nonce", request.Nonce]);
        await server.WaitForConnectionAsync();
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        WorkerEnvelope? terminal = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            var envelope = JsonSerializer.Deserialize<WorkerEnvelope>(line, JsonDefaults.Options) ?? throw new InvalidDataException("Worker returned an empty envelope.");
            if (envelope.Result is not null || !string.IsNullOrWhiteSpace(envelope.Error))
            {
                terminal = envelope;
                SendServerAck(writer, request);
            }
        }

        var code = await worker;
        return (code, terminal ?? throw new InvalidDataException("Worker did not return a terminal envelope."));
    }

    private static OperationRequest RecoveryRequest(PortableWorkspace workspace) => new()
    {
        OperationId = Guid.NewGuid().ToString("N"),
        Kind = OperationKind.RecoverIncompleteMigration,
        BaseDirectory = workspace.BaseDirectory,
        ConfigurationPath = workspace.ConfigurationPath,
        ConfigurationSha256 = workspace.ComputeConfigurationSha256(),
        SelectedTaskIds = ["anaconda"],
        Nonce = "nonce"
    };

    private static string Configuration(string source, string destination) => JsonSerializer.Serialize(new MigrationConfiguration
    {
        DestRoot = Path.GetDirectoryName(destination),
        Tasks = [new MigrationTaskDefinition { TaskId = "anaconda", Group = "DevCache", Source = source, Dest = destination }]
    });

    private static string WriteForwardJournal(string root, string source, string destination, string state, string? cStage = null, string lastError = "")
    {
        var path = Path.Combine(root, ".csaver-journal", "forward-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new JournalStore().Create(path, new NativeJournal
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Kind = "ForwardMigration",
            TaskId = "anaconda",
            Group = "DevCache",
            CanonicalSource = source,
            CanonicalDest = destination,
            State = state,
            CStage = cStage ?? string.Empty,
            LastError = lastError
        });
        return path;
    }

    private static OperationRequest Request(string operationId) => new()
    {
        OperationId = operationId,
        BaseDirectory = "C:\\Csaver",
        ConfigurationPath = "C:\\Csaver\\migration-tasks.json",
        ConfigurationSha256 = "hash",
        Nonce = "nonce"
    };

    private sealed class FailingStream : MemoryStream
    {
        public bool FailWrites { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailWrites) throw new IOException("pipe closed");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (FailWrites) throw new IOException("pipe closed");
            base.Write(buffer);
        }
    }

}
