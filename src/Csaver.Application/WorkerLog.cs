using System.Text;

namespace Csaver.Application;

public static class WorkerLog
{
    public static void Write(string baseDirectory, string operationId, string message)
    {
        try
        {
            if (!Directory.Exists(baseDirectory)) return;
            var workspace = new PortableWorkspace(baseDirectory);
            Directory.CreateDirectory(workspace.LogsDirectory);
            var safeId = operationId.Length > 0 && operationId.All(char.IsLetterOrDigit) ? operationId : "unknown";
            var path = Path.Combine(workspace.LogsDirectory, $"worker-{safeId}.log");
            File.AppendAllText(path, $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch { }
    }
}
