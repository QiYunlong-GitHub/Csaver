using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Csaver.Application;
using Csaver.Core;
using Csaver.Windows;

namespace Csaver.Tests;

internal sealed class RecordingTreeService(string destination) : ITreeService
{
    private const string Link = "binaries\\python\\versions\\3.13.12";

    public int DestinationScans { get; private set; }
    public int SourceScans { get; private set; }
    public IReadOnlyCollection<string> LastTrusted { get; private set; } = Array.Empty<string>();

    private bool IsDestination(string root) => string.Equals(
        Path.GetFullPath(root).TrimEnd('\\'), Path.GetFullPath(destination).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public DirectoryStatistics GetStatistics(string root, CancellationToken cancellationToken = default) => new(0, 0);

    public DirectoryScanResult GetStatisticsSkippingReparsePoints(string root, IReadOnlyCollection<string> trustedReparsePoints, CancellationToken cancellationToken = default)
    {
        LastTrusted = trustedReparsePoints;
        if (!IsDestination(root)) { SourceScans++; return new DirectoryScanResult(0, 0, [], []); }
        DestinationScans++;
        var declared = trustedReparsePoints.Any(x => string.Equals(x, Link, StringComparison.OrdinalIgnoreCase));
        return declared ? new DirectoryScanResult(0, 0, [], [Link]) : new DirectoryScanResult(0, 0, [Link], []);
    }

    public DirectorySnapshot CaptureSnapshot(string root, CancellationToken cancellationToken = default) => new() { Root = root };

    public SnapshotComparison Compare(DirectorySnapshot expected, DirectorySnapshot actual, bool allowDestinationOnly, bool ignoreLastAccess = true) =>
        new() { Success = true };

    public void CopyPreserving(string source, string destinationPath, IProgress<OperationProgress>? progress = null,
        string operationId = "", string taskId = "", CancellationToken cancellationToken = default) { }
}

[TestClass]
public sealed class ReparsePointPolicyTests
{
    private const string Link = "binaries\\python\\versions\\3.13.12";

    [TestMethod]
    public void ReadOnlyScanRecordsButDoesNotFollowJunction()
    {
        using var folder = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(folder.Path, "tree")).FullName;
        var external = Directory.CreateDirectory(Path.Combine(folder.Path, "external")).FullName;
        File.WriteAllText(Path.Combine(root, "a.txt"), "12345");
        File.WriteAllText(Path.Combine(external, "outside.txt"), "0123456789");
        var link = Path.Combine(root, "vendor-link");
        var junctions = new WindowsJunctionService();
        try { junctions.Create(link, external); }
        catch (Exception ex) { Assert.Inconclusive("Junction creation is unavailable: " + ex.Message); }

        try
        {
            var trees = new WindowsTreeService();
            var blocked = trees.GetStatisticsSkippingReparsePoints(root, Array.Empty<string>());
            Assert.AreEqual(1, blocked.UndeclaredReparsePoints.Count);
            Assert.AreEqual("vendor-link", blocked.UndeclaredReparsePoints.Single());
            Assert.IsTrue(blocked.Bytes <= 5, "The scan must not count files behind the Junction.");

            var allowed = trees.GetStatisticsSkippingReparsePoints(root, ["vendor-link"]);
            Assert.AreEqual(0, allowed.UndeclaredReparsePoints.Count);
            Assert.AreEqual("vendor-link", allowed.DeclaredReparsePoints.Single());
            Assert.IsTrue(allowed.Bytes <= 5);

            var snapshot = trees.CaptureSnapshot(root);
            Assert.IsFalse(snapshot.Success);
            StringAssert.Contains(string.Join(";", snapshot.ReadErrors), "Unsafe reparse point encountered");
            Assert.ThrowsException<IOException>(() => trees.CopyPreserving(root, Path.Combine(folder.Path, "copy")));
        }
        finally
        {
            try { junctions.Delete(link); } catch { }
        }
    }

    [TestMethod]
    public void DeclaredReparsePointIsPassedToReadOnlyInspection()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(folder.Path, "destination")).FullName;
        var trees = new RecordingTreeService(destination);
        var inspector = new TaskInspectionService(new FakeJunctionService(), trees);

        var inspection = inspector.Inspect(ResolvedTask([Link], source, destination), false, false);

        Assert.IsTrue(inspection.CanMutate, string.Join("; ", inspection.Issues.Select(x => x.Message)));
        Assert.AreEqual(1, trees.DestinationScans);
        Assert.IsTrue(trees.LastTrusted.Contains(Link));
    }

    [TestMethod]
    public void UndeclaredReparsePointRemainsAnError()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(folder.Path, "destination")).FullName;
        var inspector = new TaskInspectionService(new FakeJunctionService(), new RecordingTreeService(destination));

        var inspection = inspector.Inspect(ResolvedTask([], source, destination), false, false);

        Assert.IsFalse(inspection.CanMutate);
        Assert.IsTrue(inspection.Issues.Any(x => x.Code == "DestinationScanFailed"));
        Assert.AreEqual(Link, inspection.ReparsePoints.Single());
    }

    [TestMethod]
    public void IllegalTrustedReparsePointDeclarationsAreRejected()
    {
        var service = new ConfigurationService();
        foreach (var illegal in new[] { "C:\\absolute", "..\\escape", "a\\..\\b", "a/*", "D:drive" })
        {
            var json = "{\"Tasks\":[{\"Group\":\"A\",\"Source\":\"C:\\\\A\",\"TrustedReparsePoints\":[" + JsonSerializer.Serialize(illegal) + "]}]}";
            var result = service.Parse(json);
            Assert.IsFalse(result.IsValid, illegal);
            Assert.IsTrue(result.Issues.Any(x => x.Code == "TrustedReparsePointInvalid"), illegal);
        }

        var valid = service.Parse("{\"Tasks\":[{\"Group\":\"A\",\"Source\":\"C:\\\\A\",\"TrustedReparsePoints\":[\"binaries/python/versions/3.13.12\"]}]}");
        Assert.IsTrue(valid.IsValid, string.Join("; ", valid.Issues.Select(x => x.Message)));
        Assert.AreEqual(Link, valid.Configuration!.Tasks.Single().TrustedReparsePoints.Single());
    }

    [TestMethod]
    public void UnknownTaskPropertyProducesWarningOnly()
    {
        var result = new ConfigurationService().Parse("{\"Tasks\":[{\"Group\":\"A\",\"Source\":\"C:\\\\A\",\"Exclude\":true}]}");

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.Issues.Any(x => x.Code == "UnknownTaskProperty" && x.Severity == IssueSeverity.Warning));
    }

    [TestMethod]
    public void FrozenTaskSetHashIncludesNormalizedTrustedDeclarations()
    {
        var baseline = TaskResolver.ComputeResolvedTaskSetHash([TaskResolver.Freeze(ResolvedTask([]))]);
        var declared = TaskResolver.ComputeResolvedTaskSetHash([TaskResolver.Freeze(ResolvedTask([Link]))]);
        Assert.AreNotEqual(baseline, declared);

        var reordered = TaskResolver.ComputeResolvedTaskSetHash([TaskResolver.Freeze(ResolvedTask(["OTHER", Link]))]);
        var equivalent = TaskResolver.ComputeResolvedTaskSetHash([TaskResolver.Freeze(ResolvedTask([Link, "other"]))]);
        Assert.AreEqual(reordered, equivalent);
    }

    private static ResolvedMigrationTask ResolvedTask(IEnumerable<string> trusted, string? source = null, string? destination = null) => new()
    {
        TaskId = "test", Group = "Test", Source = source ?? "C:\\A", Destination = destination ?? "D:\\B",
        DestinationRoot = "D:\\", TrustedReparsePoints = [.. trusted]
    };
}
