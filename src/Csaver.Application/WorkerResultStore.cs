using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Csaver.Core;

namespace Csaver.Application;

public sealed class WorkerResultRecord
{
    public int SchemaVersion { get; init; } = 1;
    public string OperationId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string NonceSha256 { get; init; } = string.Empty;
    public OperationResult Result { get; init; } = new();
    public DateTimeOffset WrittenUtc { get; init; } = DateTimeOffset.UtcNow;
}

public static class WorkerResultStore
{
    private const int Schema = 1;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static string PathFor(PortableWorkspace workspace, string operationId) =>
        Path.Combine(workspace.WorkerResultsDirectory, operationId + ".json");

    public static bool TryWrite(PortableWorkspace workspace, OperationRequest request, OperationResult result)
    {
        try
        {
            var record = new WorkerResultRecord
            {
                SchemaVersion = Schema,
                OperationId = request.OperationId,
                Kind = request.Kind.ToString(),
                NonceSha256 = NonceHash(request.Nonce),
                Result = result,
                WrittenUtc = DateTimeOffset.UtcNow
            };
            AtomicFiles.WriteJson(PathFor(workspace, request.OperationId), record);
            return true;
        }
        catch { return false; }
    }

    public static OperationResult? TryRead(PortableWorkspace workspace, OperationRequest request)
    {
        var path = PathFor(workspace, request.OperationId);
        try
        {
            if (!File.Exists(path)) return null;
            var record = JsonSerializer.Deserialize<WorkerResultRecord>(File.ReadAllText(path), JsonDefaults.Options);
            if (record is null || record.SchemaVersion != Schema) return null;
            if (!string.Equals(record.OperationId, request.OperationId, StringComparison.Ordinal)) return null;
            if (!string.Equals(record.Kind, request.Kind.ToString(), StringComparison.Ordinal)) return null;
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(record.NonceSha256), Encoding.UTF8.GetBytes(NonceHash(request.Nonce)))) return null;
            Consume(path);
            return record.Result;
        }
        catch { return null; }
    }

    public static void Cleanup(PortableWorkspace workspace)
    {
        try
        {
            if (!Directory.Exists(workspace.WorkerResultsDirectory)) return;
            foreach (var path in Directory.EnumerateFiles(workspace.WorkerResultsDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try { if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow - Retention) File.Delete(path); }
                catch { }
            }
        }
        catch { }
    }

    private static void Consume(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string NonceHash(string nonce) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce ?? string.Empty))).ToLowerInvariant();
}
