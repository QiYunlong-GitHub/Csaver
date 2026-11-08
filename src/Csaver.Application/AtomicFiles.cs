using System.Text;
using System.Text.Json;
using Csaver.Core;

namespace Csaver.Application;

public static class AtomicFiles
{
    public static void WriteText(string path, string content, bool backupExisting = false)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("File parent is unavailable.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(fullPath))
            {
                if (backupExisting)
                {
                    var backup = fullPath + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".bak";
                    File.Copy(fullPath, backup, false);
                }

                File.Move(temporary, fullPath, true);
            }
            else
            {
                File.Move(temporary, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void WriteJson<T>(string path, T value, bool backupExisting = false) =>
        WriteText(path, JsonSerializer.Serialize(value, JsonDefaults.Options), backupExisting);
}
