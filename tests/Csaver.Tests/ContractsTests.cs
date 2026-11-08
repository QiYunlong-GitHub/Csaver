using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Csaver.Application;
using Csaver.Core;
using Csaver.Windows;

namespace Csaver.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    public void ParsesObjectAndLegacyArrayShapes()
    {
        var service = new ConfigurationService();
        Assert.IsTrue(service.Parse("{\"DestRoot\":\"D:\\\\Moved\",\"Tasks\":[{\"Group\":\"A\",\"Root\":\"Home\",\"Rel\":\".a\"}]}").IsValid);
        Assert.IsTrue(service.Parse("[{\"Group\":\"A\",\"Source\":\"C:\\\\A\"}]").IsValid);
    }

    [TestMethod]
    public void RejectsMalformedDuplicateAndInvalidGuard()
    {
        var service = new ConfigurationService();
        Assert.IsFalse(service.Parse("{").IsValid);
        var duplicate = service.Parse("{\"Tasks\":[{\"TaskId\":\"same\",\"Group\":\"A\",\"Source\":\"C:\\\\A\"},{\"TaskId\":\"SAME\",\"Group\":\"B\",\"Source\":\"C:\\\\B\"}]}");
        Assert.IsTrue(duplicate.Issues.Any(x => x.Code == "DuplicateTaskIdentity"));
        var guard = service.Parse("[{\"Group\":\"A\",\"Source\":\"C:\\\\A\",\"Guard\":\"[\"}]");
        Assert.IsTrue(guard.Issues.Any(x => x.Code == "GuardRegexInvalid"));
    }

    [TestMethod]
    public void SaveCreatesBackupAndPreservesUtf8()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "migration-tasks.json");
        File.WriteAllText(path, "{\"Tasks\":[{\"Group\":\"旧值\",\"Source\":\"C:\\\\A\"}]}");
        var result = new ConfigurationService().ValidateAndSave(path, "{\"Tasks\":[{\"Group\":\"中文\",\"Source\":\"C:\\\\A\"}]}");
        Assert.IsTrue(result.IsValid);
        StringAssert.Contains(File.ReadAllText(path), "中文");
        Assert.AreEqual(1, Directory.GetFiles(folder.Path, "*.bak").Length);
    }

    [TestMethod]
    public void ParsesCamelCaseAndMixedCaseRootProperties()
    {
        var service = new ConfigurationService();
        var camel = service.Parse("{\"destRoot\":\"D:\\\\Moved\",\"tasks\":[{\"taskId\":\"a\",\"group\":\"A\",\"root\":\"Home\",\"rel\":\".a\"}]}");
        var mixed = service.Parse("{\"dEsTrOoT\":\"E:\\\\Moved\",\"tAsKs\":[{\"Group\":\"B\",\"Source\":\"C:\\\\B\"}]}");

        Assert.IsTrue(camel.IsValid, string.Join("; ", camel.Issues.Select(x => x.Message)));
        Assert.AreEqual("D:\\Moved", camel.Configuration?.DestRoot);
        Assert.AreEqual("a", camel.Configuration?.Tasks.Single().TaskId);
        Assert.IsTrue(mixed.IsValid, string.Join("; ", mixed.Issues.Select(x => x.Message)));
        Assert.AreEqual("E:\\Moved", mixed.Configuration?.DestRoot);
    }

    [TestMethod]
    public void RejectsAmbiguousCaseVariantRootProperties()
    {
        var service = new ConfigurationService();
        var tasks = service.Parse("{\"Tasks\":[],\"tasks\":[]}");
        var roots = service.Parse("{\"Tasks\":[{\"Group\":\"A\",\"Source\":\"C:\\\\A\"}],\"DestRoot\":\"D:\\\\A\",\"destRoot\":\"E:\\\\B\"}");

        Assert.IsTrue(tasks.Issues.Any(x => x.Code == "AmbiguousRootProperty"));
        Assert.IsTrue(roots.Issues.Any(x => x.Code == "AmbiguousRootProperty"));
    }

    [TestMethod]
    public void SavesCanonicalPascalCaseAndReloadsCamelAndLegacyInput()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "migration-tasks.json");
        File.WriteAllText(path, "{\"Tasks\":[{\"Group\":\"Old\",\"Source\":\"C:\\\\Old\"}]}");
        var service = new ConfigurationService();
        var camelResult = service.ValidateAndSave(path, "{\"destRoot\":\"D:\\\\Moved\",\"tasks\":[{\"taskId\":\"anaconda\",\"group\":\"DevCache\",\"root\":\"Home\",\"rel\":\".anaconda\",\"guard\":\"anaconda\"}]}");

        Assert.IsTrue(camelResult.IsValid, string.Join("; ", camelResult.Issues.Select(x => x.Message)));
        var saved = File.ReadAllText(path);
        StringAssert.Contains(saved, "\"DestRoot\"");
        StringAssert.Contains(saved, "\"Tasks\"");
        StringAssert.Contains(saved, "\"TaskId\"");
        StringAssert.Contains(saved, "\"Group\"");
        Assert.IsFalse(saved.Contains("\"destRoot\"", StringComparison.Ordinal));
        Assert.IsFalse(saved.Contains("\"tasks\"", StringComparison.Ordinal));
        Assert.IsTrue(service.LoadFile(path).IsValid);

        var legacyResult = service.ValidateAndSave(path, "[{\"Group\":\"Legacy\",\"Source\":\"C:\\\\Legacy\"}]");
        Assert.IsTrue(legacyResult.IsValid);
        Assert.IsTrue(service.LoadFile(path).IsValid);
        StringAssert.Contains(File.ReadAllText(path), "\"Tasks\"");
    }

    [TestMethod]
    public void InvalidSaveLeavesExistingConfigurationUnchanged()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "migration-tasks.json");
        const string original = "{\"Tasks\":[{\"Group\":\"Existing\",\"Source\":\"C:\\\\Existing\"}]}";
        File.WriteAllText(path, original);

        var result = new ConfigurationService().ValidateAndSave(path, "{\"Tasks\":");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(original, File.ReadAllText(path));
        Assert.AreEqual(0, Directory.GetFiles(folder.Path, "*.bak").Length);
    }
}

[TestClass]
public sealed class ResolverAndWorkspaceTests
{
    [TestMethod]
    public void PathRelationIsBoundaryAware()
    {
        Assert.AreEqual("LeftAncestor", TaskResolver.Relation("C:\\Data", "C:\\Data\\Child"));
        Assert.AreEqual("Disjoint", TaskResolver.Relation("C:\\Data", "C:\\Database"));
        Assert.AreEqual("Equal", TaskResolver.Relation("C:\\Data", "c:\\data\\"));
    }

    [TestMethod]
    public void BroadRootDetectionUsesPathComponents()
    {
        Assert.IsTrue(TaskResolver.IsBroadRoot("C:\\Users\\u\\AppData\\Roaming\\Tencent"));
        Assert.IsFalse(TaskResolver.IsBroadRoot("C:\\Users\\u\\TencentBackup"));
    }

    [TestMethod]
    public void FirstLaunchCreatesPortableFoldersWithoutOverwritingConfiguration()
    {
        using var folder = new TempFolder();
        var workspace = new PortableWorkspace(folder.Path);
        var content = "{\"Tasks\":[{\"Group\":\"A\",\"Source\":\"C:\\\\A\"}]}";
        workspace.Initialize(content);
        Assert.IsTrue(File.Exists(workspace.ConfigurationPath));
        Assert.IsTrue(Directory.Exists(workspace.HistoryDirectory));
        File.WriteAllText(workspace.ConfigurationPath, "custom");
        workspace.Initialize(content);
        Assert.AreEqual("custom", File.ReadAllText(workspace.ConfigurationPath));
    }
}

[TestClass]
public sealed class JournalAndFilesystemTests
{
    [TestMethod]
    public void JournalRejectsInvalidStateTransition()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "journal.json");
        var journal = new NativeJournal { OperationId = "id", Kind = "ForwardMigration", CanonicalSource = "C:\\A", CanonicalDest = "D:\\A" };
        var store = new JournalStore();
        store.Create(path, journal);
        Assert.ThrowsException<InvalidOperationException>(() => store.Transition(path, journal, "Committed"));
        store.Transition(path, journal, "Copying");
        Assert.AreEqual("Copying", store.Load(path)?.State);
    }

    [TestMethod]
    public void CopyPreservesDataAndSnapshotVerificationAllowsDestinationOnlyEntries()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(folder.Path, "destination")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "nested", "deeper"));
        File.WriteAllText(Path.Combine(source, "nested", "deeper", "a.txt"), "payload");
        File.WriteAllText(Path.Combine(destination, "retained.txt"), "keep");
        var destinationCreation = Directory.GetCreationTimeUtc(destination);
        var trees = new WindowsTreeService();

        trees.CopyPreserving(source, destination);

        var comparison = trees.Compare(trees.CaptureSnapshot(source), trees.CaptureSnapshot(destination), true);
        Assert.IsTrue(comparison.Success, string.Join("; ", comparison.Details));
        Assert.AreEqual(1, comparison.DestinationOnlyCount);
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(destination, "retained.txt")));
        Assert.AreEqual(destinationCreation, Directory.GetCreationTimeUtc(destination), "A pre-existing destination root is a container and must retain its metadata.");
    }

    [TestMethod]
    public void CompareAcceptsEquivalentInheritedAndExplicitAclsButRejectsDifferentOnes()
    {
        const string inherited = "D:(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)";
        const string explicitAcl = "D:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
        const string legacyDescriptor = "O:BAG:BAD:(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)";
        var trees = new WindowsTreeService();

        var equivalent = trees.Compare(Snapshot(inherited), Snapshot(explicitAcl), false);
        Assert.IsTrue(equivalent.Success, string.Join("; ", equivalent.Details));
        Assert.AreEqual(0, equivalent.MismatchCount);

        var legacy = trees.Compare(Snapshot(legacyDescriptor), Snapshot(explicitAcl), false);
        Assert.IsTrue(legacy.Success, string.Join("; ", legacy.Details));

        var different = trees.Compare(Snapshot(inherited), Snapshot("D:(A;OICI;FA;;;SY)"), false);
        Assert.IsFalse(different.Success);
        StringAssert.Contains(string.Join("; ", different.Details), "acl");
    }

    private static DirectorySnapshot Snapshot(string aclSddl) => new()
    {
        Root = "C:\\Root",
        Entries =
        [
            new SnapshotEntry { RelativePath = "folder", IsDirectory = true, AclSddl = aclSddl },
            new SnapshotEntry { RelativePath = "folder\\file.txt", Length = 7, AclSddl = aclSddl, ContentSha256 = "hash" }
        ]
    };

    [TestMethod]
    public void CopyPreservesEffectiveAclAcrossDifferentParentInheritance()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(Path.Combine(folder.Path, "source-parent", "source")).FullName;
        var destinationParent = Directory.CreateDirectory(Path.Combine(folder.Path, "destination-parent")).FullName;
        var destination = Path.Combine(destinationParent, "destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "a.txt"), "payload");
        var parent = new DirectoryInfo(destinationParent);
        try
        {
            var security = FileSystemAclExtensions.GetAccessControl(parent, AccessControlSections.Access);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(parent, security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            Assert.Inconclusive("This environment cannot change directory ACLs: " + ex.Message);
            return;
        }

        var trees = new WindowsTreeService();
        trees.CopyPreserving(source, destination);

        var comparison = trees.Compare(trees.CaptureSnapshot(source), trees.CaptureSnapshot(destination), true);
        Assert.IsTrue(comparison.Success, string.Join("; ", comparison.Details));
        Assert.AreEqual(0, comparison.MismatchCount);
    }

    [TestMethod]
    public void CopyPreservesAclsContainingGenericAccessMaskBits()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(Path.Combine(folder.Path, "generic-source")).FullName;
        var destination = Path.Combine(folder.Path, "generic-destination");
        File.WriteAllText(Path.Combine(source, "a.txt"), "payload");
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        // 0xE0010000 (GENERIC_READ|GENERIC_WRITE|GENERIC_EXECUTE|DELETE) becomes
        // -536805376 when read as FileSystemRights; the old FileSystemAccessRule
        // reconstruction threw ArgumentException on this exact mask.
        var genericSddl = $"D:(A;OICI;GA;;;SY)(A;OICI;0xE0010000;;;BA)(A;OICI;FA;;;{sid})";
        try
        {
            var info = new DirectoryInfo(source);
            var security = FileSystemAclExtensions.GetAccessControl(info, AccessControlSections.Access);
            security.SetSecurityDescriptorSddlForm(genericSddl, AccessControlSections.Access);
            FileSystemAclExtensions.SetAccessControl(info, security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or ArgumentException)
        {
            Assert.Inconclusive("This environment cannot apply generic-rights ACLs: " + ex.Message);
            return;
        }

        var trees = new WindowsTreeService();
        trees.CopyPreserving(source, destination);

        var comparison = trees.Compare(trees.CaptureSnapshot(source), trees.CaptureSnapshot(destination), true);
        var sourceAcl = AclOf(Path.Combine(source, "a.txt"));
        var destinationAcl = AclOf(Path.Combine(destination, "a.txt"));
        Assert.IsTrue(comparison.Success, $"{string.Join("; ", comparison.Details)} SOURCE_ACL={sourceAcl} DESTINATION_ACL={destinationAcl}");
        Assert.AreEqual(0, comparison.MismatchCount);
    }

    private static string AclOf(string path) =>
        FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    [TestMethod]
    public void CopyOverwritesReadOnlyDestinationLeftoversFromAFailedMigration()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(Path.Combine(folder.Path, "retry-source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(folder.Path, "retry-destination")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "new-data");
        var stale = Path.Combine(destination, "a.txt");
        File.WriteAllText(stale, "stale-data");
        File.SetAttributes(stale, FileAttributes.ReadOnly);

        var trees = new WindowsTreeService();
        trees.CopyPreserving(source, destination);

        Assert.AreEqual("new-data", File.ReadAllText(stale));
    }

    [TestMethod]
    public void SnapshotVerificationDetectsSameSizeContentChanges()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(folder.Path, "destination")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        var sourceFile = Path.Combine(source, "nested", "a.txt");
        var destinationFile = Path.Combine(destination, "nested", "a.txt");
        File.WriteAllText(sourceFile, "payload");
        var trees = new WindowsTreeService();

        trees.CopyPreserving(source, destination);
        var expected = trees.CaptureSnapshot(source);
        File.WriteAllText(destinationFile, "PAYLOAD");
        File.SetLastWriteTimeUtc(destinationFile, File.GetLastWriteTimeUtc(sourceFile));

        var comparison = trees.Compare(expected, trees.CaptureSnapshot(destination), true);
        Assert.IsFalse(comparison.Success);
        StringAssert.Contains(string.Join("; ", comparison.Details), "nested\\a.txt");
    }

    [TestMethod]
    public void JunctionRoundTripDoesNotDeleteTarget()
    {
        if (!OperatingSystem.IsWindows() || !IsAdministrator()) Assert.Inconclusive("Junction mutation requires an elevated Windows test process.");
        using var folder = new TempFolder();
        var target = Directory.CreateDirectory(Path.Combine(folder.Path, "target")).FullName;
        var link = Path.Combine(folder.Path, "link");
        File.WriteAllText(Path.Combine(target, "data.txt"), "safe");
        var service = new WindowsJunctionService();
        service.Create(link, target);
        Assert.IsTrue(service.Inspect(link).IsJunction);
        service.Delete(link);
        Assert.IsTrue(File.Exists(Path.Combine(target, "data.txt")));
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

[TestClass]
public sealed class HistoryLocalizationAndRepositoryTests
{
    [TestMethod]
    public void HistoryRoundTripsWithoutImportingExternalData()
    {
        using var folder = new TempFolder();
        var workspace = new PortableWorkspace(folder.Path); workspace.Initialize("{\"Tasks\":[{\"Group\":\"A\",\"Source\":\"C:\\\\A\"}]}");
        var service = new HistoryService(workspace);
        service.Save(new HistoryRecord { OperationId = "op", Kind = OperationKind.Migrate, TaskId = "a", Group = "A", Source = "C:\\A", Destination = "D:\\A", StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Status = "OK" });
        Assert.AreEqual("op", service.Load(false).Single().OperationId);
    }

    [TestMethod]
    public void LocalizationSwitchesLanguagesAndFallsBackToKey()
    {
        var service = new LocalizationService();
        Assert.AreEqual("Commands", service["Commands"]);
        service.SetCulture("zh-CN");
        Assert.AreEqual("命令", service["Commands"]);
        Assert.AreEqual("Missing", service["Missing"]);
    }

    [TestMethod]
    public void RepositoryContainsNoPowerShellScriptsOrRuntimeShellInvocations()
    {
        var root = FindRepositoryRoot();
        Assert.AreEqual(0, Directory.GetFiles(root, "*.ps1", SearchOption.AllDirectories).Length, "The GitHub folder must not contain PowerShell scripts.");
        var forbidden = new[] { "powershell" + ".exe", "pwsh" + ".exe", "cmd" + ".exe", "robocopy" + ".exe" };
        var source = string.Join('\n', Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var value in forbidden) Assert.IsFalse(source.Contains(value, StringComparison.OrdinalIgnoreCase), $"Runtime source references forbidden process {value}.");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Csaver.Gui.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

internal sealed class TempFolder : IDisposable
{
    public TempFolder() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CsaverTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
    public string Path { get; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

[TestClass]
public sealed class MigrationTransactionTests
{
    [TestMethod]
    public async Task ExplainConfigurationIsReadOnlyAndDistinctFromMigrationPreview()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = System.IO.Path.Combine(folder.Path, "destination");
        File.WriteAllText(System.IO.Path.Combine(source, "source.txt"), "source-data");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var explanation = await engine.ExecuteAsync(Request(workspace, OperationKind.ExplainConfiguration));

        Assert.IsTrue(explanation.Success, explanation.Message);
        Assert.AreEqual(OperationKind.ExplainConfiguration, explanation.Kind);
        Assert.AreEqual("CONFIGURED", explanation.Tasks.Single().Status);
        Assert.AreEqual("ReadOnly", explanation.Tasks.Single().TransactionState);
        Assert.IsNull(explanation.Tasks.Single().FileCount);
        Assert.IsNull(explanation.Tasks.Single().SizeBytes);
        StringAssert.Contains(explanation.Message, "Explained configuration");
        Assert.IsFalse(explanation.Message.Contains("Previewed", StringComparison.Ordinal));
        StringAssert.Contains(explanation.Tasks.Single().Detail, "DestinationSource=TaskDest");
        StringAssert.Contains(explanation.Tasks.Single().Detail, "PathState=Directory");

        var preview = await engine.ExecuteAsync(Request(workspace, OperationKind.PreviewMigration));

        Assert.IsTrue(preview.Success, preview.Message);
        Assert.AreEqual(OperationKind.PreviewMigration, preview.Kind);
        Assert.AreEqual("PLAN", preview.Tasks.Single().Status);
        Assert.AreEqual(1, preview.Tasks.Single().FileCount);
        Assert.AreEqual(11, preview.Tasks.Single().SizeBytes);
        StringAssert.Contains(preview.Message, "Previewed 1 task(s)");
        Assert.AreNotEqual(explanation.Message, preview.Message);
        Assert.IsTrue(Directory.Exists(source));
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task MigrationCommitsAndPreservesDestinationOnlyData()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "destination")).FullName;
        File.WriteAllText(System.IO.Path.Combine(source, "source.txt"), "source-data");
        File.WriteAllText(System.IO.Path.Combine(destination, "retained.txt"), "destination-only");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var fakeJunctions = new FakeJunctionService();
        var engine = new NativeOperationEngine(fakeJunctions, new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace));

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("OK", result.Tasks.Single().Status);
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(destination, "source.txt")));
        Assert.AreEqual("destination-only", File.ReadAllText(System.IO.Path.Combine(destination, "retained.txt")));
        Assert.IsTrue(fakeJunctions.Inspect(source).IsJunction);
        Assert.IsTrue(new HistoryService(workspace).Load(false).Single().RollbackEligible);
    }

    [TestMethod]
    public async Task JunctionFailureRestoresOriginalSource()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = System.IO.Path.Combine(folder.Path, "destination");
        File.WriteAllText(System.IO.Path.Combine(source, "important.txt"), "must-survive");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var fakeJunctions = new FakeJunctionService { ThrowOnCreate = true };
        var engine = new NativeOperationEngine(fakeJunctions, new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("FAIL_TRANSACTION", result.Tasks.Single().Status);
        Assert.AreEqual("must-survive", File.ReadAllText(System.IO.Path.Combine(source, "important.txt")));
        Assert.IsFalse(fakeJunctions.Inspect(source).IsJunction);
    }

    [TestMethod]
    public async Task MatchingRecoveryJournalBlocksPreviewWithoutWriting()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = System.IO.Path.Combine(folder.Path, "destination");
        File.WriteAllText(System.IO.Path.Combine(source, "source.txt"), "source-data");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var journalPath = WriteForwardJournal(folder.Path, source + "\\.", destination + "\\.", "RecoveryRequired");
        var before = File.ReadAllText(journalPath);
        var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace, OperationKind.PreviewMigration));

        Assert.IsFalse(result.Success, result.Message);
        Assert.AreEqual("PREFLIGHT_FAILED", result.Tasks.Single().Status);
        Assert.AreEqual("RecoveryRequired", result.Tasks.Single().TransactionState);
        StringAssert.Contains(result.Tasks.Single().Detail, journalPath);
        StringAssert.Contains(result.Tasks.Single().Detail, "Resolve or recover");
        Assert.AreEqual(before, File.ReadAllText(journalPath));
        Assert.IsTrue(Directory.Exists(source));
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task UnrelatedForwardJournalDoesNotBlockPreview()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = System.IO.Path.Combine(folder.Path, "destination");
        var unrelatedSource = System.IO.Path.Combine(folder.Path, "other-source");
        File.WriteAllText(System.IO.Path.Combine(source, "source.txt"), "source-data");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        WriteForwardJournal(folder.Path, unrelatedSource, destination, "RecoveryRequired");
        var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace, OperationKind.PreviewMigration));

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("PLAN", result.Tasks.Single().Status);
    }

    [TestMethod]
    public async Task TerminalForwardJournalsDoNotBlockPreview()
    {
        foreach (var state in new[] { "Committed", "Skipped", "Recovered" })
        {
            using var folder = new TempFolder();
            var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
            var destination = System.IO.Path.Combine(folder.Path, "destination");
            File.WriteAllText(System.IO.Path.Combine(source, "source.txt"), "source-data");
            var workspace = new PortableWorkspace(folder.Path);
            workspace.Initialize(Configuration(source, destination));
            WriteForwardJournal(folder.Path, source, destination, state);
            var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

            var result = await engine.ExecuteAsync(Request(workspace, OperationKind.PreviewMigration));

            Assert.IsTrue(result.Success, $"{state}: {result.Message}");
            Assert.AreEqual("PLAN", result.Tasks.Single().Status, state);
        }
    }

    [TestMethod]
    public async Task MigrationRefusesMatchingRecoveryJournalBeforeCreatingTransaction()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = System.IO.Path.Combine(folder.Path, "destination");
        File.WriteAllText(System.IO.Path.Combine(source, "source.txt"), "source-data");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var journalPath = WriteForwardJournal(folder.Path, source, destination, "RecoveryRequired");
        var before = File.ReadAllText(journalPath);
        var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace));

        Assert.IsFalse(result.Success, result.Message);
        Assert.AreEqual("PREFLIGHT_FAILED", result.Tasks.Single().Status);
        Assert.AreEqual(1, Directory.GetFiles(System.IO.Path.Combine(folder.Path, ".csaver-journal"), "forward-*.json").Length);
        Assert.AreEqual(before, File.ReadAllText(journalPath));
        Assert.IsTrue(Directory.Exists(source));
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task ValidPreCutoverRecoveryPreservesJournalErrorAndUnblocksPreview()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "destination")).FullName;
        var sourceFile = System.IO.Path.Combine(source, "source.txt");
        var destinationFile = System.IO.Path.Combine(destination, "partial.txt");
        File.WriteAllText(sourceFile, "source-data");
        File.WriteAllText(destinationFile, "partial-data");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var journalPath = WriteForwardJournal(folder.Path, source, destination, "RecoveryRequired", lastError: "Copy verification failed");
        var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace, OperationKind.RecoverIncompleteMigration));

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("RECOVERED", result.Tasks.Single().Status);
        StringAssert.Contains(result.Tasks.Single().Detail, journalPath);
        StringAssert.Contains(result.Tasks.Single().Detail, "no source, destination, staging data, manifest, or journal was deleted");
        var recovered = new JournalStore().Load(journalPath);
        Assert.IsNotNull(recovered);
        Assert.AreEqual("Recovered", recovered.State);
        Assert.AreEqual("Copy verification failed", recovered.LastError);
        StringAssert.Contains(recovered.RecoveryMetadata, "Pre-cutover recovery validated");
        Assert.AreEqual("source-data", File.ReadAllText(sourceFile));
        Assert.AreEqual("partial-data", File.ReadAllText(destinationFile));

        var preview = await engine.ExecuteAsync(Request(workspace, OperationKind.PreviewMigration));
        Assert.IsTrue(preview.Success, preview.Message);
        Assert.AreEqual("PLAN", preview.Tasks.Single().Status);
        Assert.AreEqual(1, Directory.GetFiles(System.IO.Path.Combine(folder.Path, ".csaver-journal"), "forward-*.json").Length);
    }

    [TestMethod]
    public async Task RecoveryRefusesJunctionSourceWithoutChangingJournalOrData()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "destination")).FullName;
        File.WriteAllText(System.IO.Path.Combine(destination, "retained.txt"), "retained");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var journalPath = WriteForwardJournal(folder.Path, source, destination, "RecoveryRequired");
        var fakeJunctions = new FakeJunctionService();
        fakeJunctions.Create(source, destination);
        var before = File.ReadAllText(journalPath);
        var engine = new NativeOperationEngine(fakeJunctions, new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace, OperationKind.RecoverIncompleteMigration));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("RECOVERY_REFUSED", result.Tasks.Single().Status);
        StringAssert.Contains(result.Tasks.Single().Detail, "ordinary directory");
        Assert.AreEqual(before, File.ReadAllText(journalPath));
        Assert.AreEqual("retained", File.ReadAllText(System.IO.Path.Combine(destination, "retained.txt")));
    }

    [TestMethod]
    public async Task RecoveryRefusesExistingStage()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "destination")).FullName;
        File.WriteAllText(System.IO.Path.Combine(source, "source.txt"), "source-data");
        var stage = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "stage", "payload")).FullName;
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var journalPath = WriteForwardJournal(folder.Path, source, destination, "RecoveryRequired", stage);
        var before = File.ReadAllText(journalPath);
        var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace, OperationKind.RecoverIncompleteMigration));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("RECOVERY_REFUSED", result.Tasks.Single().Status);
        StringAssert.Contains(result.Tasks.Single().Detail, "C-stage payload exists");
        Assert.AreEqual(before, File.ReadAllText(journalPath));
        Assert.IsTrue(Directory.Exists(stage));
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(source, "source.txt")));
    }

    [TestMethod]
    public async Task RecoveryRefusesPostCutoverSnapshotEvidence()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "destination")).FullName;
        File.WriteAllText(System.IO.Path.Combine(source, "source.txt"), "source-data");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var journalPath = WriteForwardJournal(folder.Path, source, destination, "RecoveryRequired");
        var journal = new JournalStore().Load(journalPath)!;
        journal.SourceSnapshot = new DirectorySnapshot { Root = source };
        AtomicFiles.WriteJson(journalPath, journal);
        var before = File.ReadAllText(journalPath);
        var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace, OperationKind.RecoverIncompleteMigration));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("RECOVERY_REFUSED", result.Tasks.Single().Status);
        StringAssert.Contains(result.Tasks.Single().Detail, "post-cutover");
        Assert.AreEqual(before, File.ReadAllText(journalPath));
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(source, "source.txt")));
    }

    [TestMethod]
    public async Task RecoveryRefusesUnrelatedAndTerminalJournals()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "destination")).FullName;
        var unrelated = System.IO.Path.Combine(folder.Path, "other-source");
        File.WriteAllText(System.IO.Path.Combine(source, "source.txt"), "source-data");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var unrelatedPath = WriteForwardJournal(folder.Path, unrelated, destination, "RecoveryRequired");
        var terminalPath = WriteForwardJournal(folder.Path, source, destination, "Committed");
        var beforeUnrelated = File.ReadAllText(unrelatedPath);
        var beforeTerminal = File.ReadAllText(terminalPath);
        var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace, OperationKind.RecoverIncompleteMigration));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("RECOVERY_REFUSED", result.Tasks.Single().Status);
        StringAssert.Contains(result.Tasks.Single().Detail, "none was found");
        Assert.AreEqual(beforeUnrelated, File.ReadAllText(unrelatedPath));
        Assert.AreEqual(beforeTerminal, File.ReadAllText(terminalPath));
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(source, "source.txt")));
    }

    private static string WriteForwardJournal(string destinationRoot, string source, string destination, string state, string? cStage = null, string lastError = "")
    {
        var directory = Directory.CreateDirectory(System.IO.Path.Combine(destinationRoot, ".csaver-journal"));
        var path = System.IO.Path.Combine(directory.FullName, "forward-" + Guid.NewGuid().ToString("N") + ".json");
        new JournalStore().Create(path, new NativeJournal
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Kind = "ForwardMigration",
            TaskId = "test",
            CanonicalSource = source,
            CanonicalDest = destination,
            State = state,
            CStage = cStage ?? string.Empty,
            LastError = lastError
        });
        return path;
    }

    [TestMethod]
    public async Task RepairPreviewReportsConflictsPolicyAndRecoveryLocations()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "destination")).FullName;
        File.WriteAllText(System.IO.Path.Combine(source, "same.txt"), "source");
        File.WriteAllText(System.IO.Path.Combine(destination, "same.txt"), "destination");
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());
        var request = new OperationRequest
        {
            OperationId = Guid.NewGuid().ToString("N"), Kind = OperationKind.PreviewRepair, BaseDirectory = workspace.BaseDirectory,
            ConfigurationPath = workspace.ConfigurationPath, ConfigurationSha256 = workspace.ComputeConfigurationSha256(), SelectedTaskIds = ["test"],
            RepairPolicy = RepairPolicy.SourceWins, AbortOnConflict = true, Nonce = Guid.NewGuid().ToString("N")
        };
        var result = await engine.ExecuteAsync(request);
        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("REPAIR_PLAN", result.Tasks.Single().Status);
        Assert.AreEqual(1, result.Tasks.Single().ConflictCount);
        StringAssert.Contains(result.Tasks.Single().Detail, "policy=SourceWins");
        StringAssert.Contains(result.Tasks.Single().Detail, "abortOnConflict=True");
        Assert.IsTrue(Directory.Exists(source));
        Assert.IsTrue(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task HealthyJunctionSkipsDestinationTreeScanInPreview()
    {
        using var folder = new TempFolder();
        var source = System.IO.Path.Combine(folder.Path, "source");
        var destination = System.IO.Path.Combine(folder.Path, "destination");
        Directory.CreateDirectory(destination);
        var junctions = new FakeJunctionService();
        junctions.Create(source, destination);
        var trees = new RecordingTreeService(destination);
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var engine = new NativeOperationEngine(junctions, trees, new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace, OperationKind.PreviewMigration));

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("SKIP_ALREADY_LINK", result.Tasks.Single().Status);
        Assert.AreEqual("AlreadyLinked", result.Tasks.Single().TransactionState);
        Assert.AreEqual(0, trees.DestinationScans);
        Assert.AreEqual(0, Directory.GetFiles(workspace.HistoryDirectory, "*.json").Length);
        Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(destination, ".csaver-journal")));
    }

    [TestMethod]
    public async Task HealthyJunctionMigrationSkipsWithoutJournalOrManifest()
    {
        using var folder = new TempFolder();
        var source = System.IO.Path.Combine(folder.Path, "source");
        var destination = System.IO.Path.Combine(folder.Path, "destination");
        Directory.CreateDirectory(destination);
        var junctions = new FakeJunctionService();
        junctions.Create(source, destination);
        var trees = new RecordingTreeService(destination);
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var engine = new NativeOperationEngine(junctions, trees, new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace, OperationKind.Migrate));

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("SKIP_ALREADY_LINK", result.Tasks.Single().Status);
        Assert.AreEqual(0, trees.DestinationScans);
        Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(destination, ".csaver-journal")));
        Assert.AreEqual(1, Directory.GetFiles(workspace.HistoryDirectory, "*.json").Length);
        Assert.IsFalse(File.Exists(System.IO.Path.Combine(folder.Path, "_migration_manifest.csv")));
    }

    [TestMethod]
    public async Task UndeclaredDestinationReparsePointStillBlocksPreview()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = System.IO.Path.Combine(folder.Path, "destination");
        Directory.CreateDirectory(destination);
        var trees = new RecordingTreeService(destination);
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var engine = new NativeOperationEngine(new FakeJunctionService(), trees, new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace, OperationKind.PreviewMigration));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("PREFLIGHT_FAILED", result.Tasks.Single().Status);
        StringAssert.Contains(result.Tasks.Single().Detail, "Destination scan");
        StringAssert.Contains(result.Tasks.Single().Detail, "Unsafe reparse point encountered");
        Assert.AreEqual(1, trees.DestinationScans);
    }

    [TestMethod]
    public async Task LockedSourceFailsBeforeCutoverAndRemainsRecoverable()
    {
        using var folder = new TempFolder();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "source")).FullName;
        var destination = System.IO.Path.Combine(folder.Path, "destination");
        var lockedFile = System.IO.Path.Combine(source, "locked.txt");
        File.WriteAllText(lockedFile, "data");
        using var exclusive = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var workspace = new PortableWorkspace(folder.Path);
        workspace.Initialize(Configuration(source, destination));
        var engine = new NativeOperationEngine(new FakeJunctionService(), new WindowsTreeService(), new ConfigurationService(), new TaskResolver(), new JournalStore());

        var result = await engine.ExecuteAsync(Request(workspace));

        Assert.IsFalse(result.Success, result.Message);
        Assert.AreEqual("FAIL_TRANSACTION", result.Tasks.Single().Status);
        StringAssert.Contains(result.Tasks.Single().Detail, "locked or unreadable");
        var journalPath = Directory.GetFiles(System.IO.Path.Combine(folder.Path, ".csaver-journal"), "forward-*.json").Single();
        var journalText = File.ReadAllText(journalPath);
        Assert.IsFalse(journalText.Contains("\"SourceSnapshot\": {") || journalText.Contains("\"DestinationSnapshot\": {"),
            "A pre-copy failure must not attach snapshot evidence that blocks recovery.");
    }

    private static string Configuration(string source, string destination) =>
        "{\"DestRoot\":" + System.Text.Json.JsonSerializer.Serialize(System.IO.Path.GetDirectoryName(destination)) +
        ",\"Tasks\":[{\"TaskId\":\"test\",\"Group\":\"Test\",\"Source\":" + System.Text.Json.JsonSerializer.Serialize(source) +
        ",\"Dest\":" + System.Text.Json.JsonSerializer.Serialize(destination) + ",\"Guard\":\"\"}]}";

    private static OperationRequest Request(PortableWorkspace workspace, OperationKind kind = OperationKind.Migrate) => new()
    {
        OperationId = Guid.NewGuid().ToString("N"), Kind = kind, BaseDirectory = workspace.BaseDirectory,
        ConfigurationPath = workspace.ConfigurationPath, ConfigurationSha256 = workspace.ComputeConfigurationSha256(),
        SelectedTaskIds = ["test"], Nonce = Guid.NewGuid().ToString("N")
    };
}
internal sealed class FakeJunctionService : IJunctionService
{
    private readonly Dictionary<string, string> links = new(StringComparer.OrdinalIgnoreCase);
    public bool ThrowOnCreate { get; init; }

    public JunctionInfo Inspect(string path)
    {
        var key = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        if (links.TryGetValue(key, out var target)) return new(true, true, true, true, target);
        if (Directory.Exists(path)) return new(true, true, false, false, null);
        if (File.Exists(path)) return new(true, false, false, false, null);
        return new(false, false, false, false, null);
    }

    public void Create(string path, string target)
    {
        if (ThrowOnCreate) throw new IOException("Injected Junction creation failure.");
        Directory.CreateDirectory(path);
        links[System.IO.Path.GetFullPath(path).TrimEnd('\\')] = System.IO.Path.GetFullPath(target).TrimEnd('\\');
    }

    public void Delete(string path)
    {
        var key = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        if (!links.Remove(key)) throw new IOException("Not a Junction.");
        Directory.Delete(path, false);
    }
}
