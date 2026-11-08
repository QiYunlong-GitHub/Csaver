using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

if (args.Length != 2) throw new ArgumentException("Usage: Csaver.Packaging <publish-directory> <output-zip>");
var publishDirectory = Path.GetFullPath(args[0]);
var outputZip = Path.GetFullPath(args[1]);
var repository = FindRepository(publishDirectory);
var executable = Path.Combine(publishDirectory, "Csaver.exe");
if (!File.Exists(executable)) throw new FileNotFoundException("Published Csaver.exe not found.", executable);
if (Directory.GetFiles(repository, "*.ps1", SearchOption.AllDirectories).Length != 0) throw new InvalidOperationException("Repository contains a forbidden .ps1 file.");

SignIfConfigured(executable);
var stage = Path.Combine(Path.GetDirectoryName(outputZip)!, ".package-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(Path.Combine(stage, "config"));
    File.Copy(executable, Path.Combine(stage, "Csaver.exe"));
    File.Copy(Path.Combine(repository, "resources", "migration-tasks.json"), Path.Combine(stage, "config", "migration-tasks.json"));
    File.Copy(Path.Combine(repository, "resources", "migration-tasks.schema.json"), Path.Combine(stage, "config", "migration-tasks.schema.json"));
    File.Copy(Path.Combine(repository, "README.md"), Path.Combine(stage, "README.md"));
    File.Copy(Path.Combine(repository, "README.zh-CN.md"), Path.Combine(stage, "README.zh-CN.md"));
    File.Copy(Path.Combine(repository, "LICENSE"), Path.Combine(stage, "LICENSE"));
    var checksums = Directory.GetFiles(stage, "*", SearchOption.AllDirectories).OrderBy(x => x).Select(path =>
        $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()}  {Path.GetRelativePath(stage, path).Replace('\\', '/')}");
    File.WriteAllText(Path.Combine(stage, "SHA256SUMS.txt"), string.Join(Environment.NewLine, checksums) + Environment.NewLine, new UTF8Encoding(false));
    if (Directory.GetFiles(stage, "*.ps1", SearchOption.AllDirectories).Length != 0) throw new InvalidOperationException("Release staging contains a forbidden .ps1 file.");
    Directory.CreateDirectory(Path.GetDirectoryName(outputZip)!);
    if (File.Exists(outputZip)) File.Delete(outputZip);
    ZipFile.CreateFromDirectory(stage, outputZip, CompressionLevel.Optimal, false);
    using var archive = ZipFile.OpenRead(outputZip);
    if (archive.Entries.Any(x => x.FullName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Release ZIP contains a forbidden .ps1 file.");
    Console.WriteLine(outputZip);
}
finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }

static void SignIfConfigured(string executable)
{
    var tool = Environment.GetEnvironmentVariable("CSAVER_SIGNTOOL_PATH");
    var thumbprint = Environment.GetEnvironmentVariable("CSAVER_SIGN_CERT_THUMBPRINT");
    var timestamp = Environment.GetEnvironmentVariable("CSAVER_TIMESTAMP_URL");
    if (string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(thumbprint) || string.IsNullOrWhiteSpace(timestamp)) return;
    Run(tool, ["sign", "/sha1", thumbprint, "/fd", "SHA256", "/tr", timestamp, "/td", "SHA256", executable]);
    Run(tool, ["verify", "/pa", "/v", executable]);
}

static void Run(string file, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo { FileName = file, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start signing tool.");
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"Signing command failed with exit code {process.ExitCode}.");
}

static string FindRepository(string start)
{
    var current = new DirectoryInfo(start);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "Csaver.Gui.slnx"))) current = current.Parent;
    return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
}
