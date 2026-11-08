using System.Security.AccessControl;
using System.Security.Cryptography;
using Csaver.Core;

namespace Csaver.Windows;

public sealed class WindowsTreeService : ITreeService
{
    public DirectoryStatistics GetStatistics(string root, CancellationToken cancellationToken = default)
    {
        long count = 0;
        long bytes = 0;
        Walk(root, (path, isDirectory, _) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isDirectory)
            {
                var info = new FileInfo(path);
                count++;
                bytes += info.Length;
            }
        });
        return new DirectoryStatistics(count, bytes);
    }

    public DirectoryScanResult GetStatisticsSkippingReparsePoints(string root, IReadOnlyCollection<string> trustedReparsePoints, CancellationToken cancellationToken = default)
    {
        var trusted = new HashSet<string>(
            trustedReparsePoints.Select(ReparsePointPaths.Normalize).Where(x => x.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        long count = 0;
        long bytes = 0;
        var undeclared = new List<string>();
        var declared = new List<string>();
        Walk(root, (path, isDirectory, _) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isDirectory)
            {
                var info = new FileInfo(path);
                count++;
                bytes += info.Length;
            }
        }, relative =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trusted.Contains(ReparsePointPaths.Normalize(relative)))
            {
                declared.Add(relative);
                return true;
            }
            undeclared.Add(relative);
            return false;
        });
        return new DirectoryScanResult(count, bytes, undeclared, declared);
    }

    public DirectorySnapshot CaptureSnapshot(string root, CancellationToken cancellationToken = default)
    {
        var entries = new List<SnapshotEntry>();
        var errors = new List<string>();
        var canonicalRoot = NormalizeRoot(root);
        try
        {
            AddEntry(canonicalRoot, true, string.Empty, entries);
            Walk(canonicalRoot, (path, isDirectory, relative) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { AddEntry(path, isDirectory, relative, entries); }
                catch (Exception ex) { errors.Add($"{relative}: {ex.Message}"); }
            });
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        return new DirectorySnapshot { Root = canonicalRoot, Entries = entries.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(), ReadErrors = errors };
    }

    public SnapshotComparison Compare(DirectorySnapshot expected, DirectorySnapshot actual, bool allowDestinationOnly, bool ignoreLastAccess = true)
    {
        var details = new List<string>();
        if (!expected.Success) details.AddRange(expected.ReadErrors.Select(x => "Expected: " + x));
        if (!actual.Success) details.AddRange(actual.ReadErrors.Select(x => "Actual: " + x));
        var expectedMap = expected.Entries.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var actualMap = actual.Entries.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var rootsAreDifferent = !PathsEqual(expected.Root, actual.Root);
        var missing = 0;
        var mismatch = 0;
        foreach (var item in expectedMap)
        {
            // The destination root is the existing container, not a source-relative entry.
            // Compare it only when both snapshots describe the same tree.
            if (rootsAreDifferent && item.Key.Length == 0) continue;
            if (!actualMap.TryGetValue(item.Key, out var observed))
            {
                missing++;
                details.Add("Missing: " + item.Key);
                continue;
            }
            var value = item.Value;
            var differences = new List<string>();
            if (value.IsDirectory != observed.IsDirectory) differences.Add("kind");
            if (value.Length != observed.Length) differences.Add("length");
            if (value.CreationUtcTicks != observed.CreationUtcTicks) differences.Add("creationTime");
            if (value.LastWriteUtcTicks != observed.LastWriteUtcTicks) differences.Add("lastWriteTime");
            if (!ignoreLastAccess && value.LastAccessUtcTicks != observed.LastAccessUtcTicks) differences.Add("lastAccessTime");
            if (value.Attributes != observed.Attributes) differences.Add("attributes");
            if (!AclsEqual(value.AclSddl, observed.AclSddl)) differences.Add("acl");
            if (!string.IsNullOrEmpty(value.ContentSha256) && !string.Equals(value.ContentSha256, observed.ContentSha256, StringComparison.OrdinalIgnoreCase)) differences.Add("content");
            if (differences.Count > 0)
            {
                mismatch++;
                details.Add($"Metadata/content differs ({string.Join(",", differences)}): {item.Key}");
            }
        }
        var destinationOnly = actualMap.Keys.Count(x => (x.Length != 0 || !rootsAreDifferent) && !expectedMap.ContainsKey(x));
        if (!allowDestinationOnly && destinationOnly > 0) details.Add($"Unexpected entries: {destinationOnly}");
        return new SnapshotComparison
        {
            Success = details.Count == 0 || (allowDestinationOnly && missing == 0 && mismatch == 0 && expected.Success && actual.Success),
            MissingCount = missing,
            MismatchCount = mismatch,
            DestinationOnlyCount = destinationOnly,
            Details = details
        };
    }

    public void CopyPreserving(string source, string destination, IProgress<OperationProgress>? progress = null, string operationId = "", string taskId = "", CancellationToken cancellationToken = default)
    {
        var directories = new List<(string Source, string Destination, string Relative)>();
        var files = new List<(string Source, string Destination)>();
        Directory.CreateDirectory(destination);
        long copied = 0;
        Walk(source, (path, isDirectory, relative) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, relative);
            if (isDirectory)
            {
                Directory.CreateDirectory(target);
                directories.Add((path, target, relative));
            }
            else
            {
                var parent = Path.GetDirectoryName(target);
                if (parent is not null) Directory.CreateDirectory(parent);
                File.Copy(path, target, true);
                files.Add((path, target));
                copied++;
                if (copied % 100 == 0) progress?.Report(new OperationProgress { OperationId = operationId, TaskId = taskId, Stage = "Copying", Message = $"Copied {copied:N0} files" });
            }
        });

        // Set parent ACLs first so inherited permissions settle before child ACLs are applied.
        foreach (var pair in directories.OrderBy(x => PathDepth(x.Relative)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyDirectorySecurity(pair.Source, pair.Destination);
        }
        foreach (var pair in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyFileMetadata(pair.Source, pair.Destination);
        }
        // Child writes can update parent directory times, so apply directory metadata last-to-first.
        foreach (var pair in directories.OrderByDescending(x => PathDepth(x.Relative)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyDirectoryTimesAndAttributes(pair.Source, pair.Destination);
        }
    }

    private static void Walk(string root, Action<string, bool, string> visitor, Func<string, bool>? onReparsePoint = null)
    {
        var canonicalRoot = NormalizeRoot(root);
        var pending = new Stack<string>();
        pending.Push(canonicalRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                var relative = Path.GetRelativePath(canonicalRoot, path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Snapshot, copy, and delete paths never pass a callback and therefore stay fail-closed.
                    if (onReparsePoint is null) throw new IOException($"Unsafe reparse point encountered: {relative}");
                    if (!onReparsePoint(relative)) return;
                    continue;
                }
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                visitor(path, isDirectory, relative);
                if (isDirectory) pending.Push(path);
            }
        }
    }

    private static void AddEntry(string path, bool isDirectory, string relative, List<SnapshotEntry> entries)
    {
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        info.Refresh();
        var acl = isDirectory
            ? FileSystemAclExtensions.GetAccessControl((DirectoryInfo)info, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl((FileInfo)info, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        entries.Add(new SnapshotEntry
        {
            RelativePath = relative,
            IsDirectory = isDirectory,
            Length = isDirectory ? 0 : ((FileInfo)info).Length,
            CreationUtcTicks = info.CreationTimeUtc.Ticks,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            LastAccessUtcTicks = info.LastAccessTimeUtc.Ticks,
            Attributes = info.Attributes,
            AclSddl = acl,
            ContentSha256 = isDirectory ? string.Empty : ComputeFileHash((FileInfo)info)
        });
    }

    private static void CopyFileMetadata(string source, string destination)
    {
        var sourceInfo = new FileInfo(source);
        File.SetCreationTimeUtc(destination, sourceInfo.CreationTimeUtc);
        File.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);
        File.SetLastAccessTimeUtc(destination, sourceInfo.LastAccessTimeUtc);
        File.SetAttributes(destination, sourceInfo.Attributes);
        CopyEffectiveAcl(sourceInfo, new FileInfo(destination));
    }

    private static void CopyDirectorySecurity(string source, string destination) =>
        CopyEffectiveAcl(new DirectoryInfo(source), new DirectoryInfo(destination));

    // A cross-volume destination inherits a different DACL from its new parent. Writing
    // only the source DACL onto an unprotected object leaves the inherited ACEs in place,
    // so the copied tree silently receives the destination volume's permissions. Protect
    // the DACL and re-create the source's effective rules (including inherited ones) as
    // explicit ACEs to preserve the source's effective permissions.
    private static void CopyEffectiveAcl(FileSystemInfo source, FileSystemInfo destination)
    {
        if (source is DirectoryInfo sourceDirectory && destination is DirectoryInfo destinationDirectory)
        {
            var sourceSecurity = FileSystemAclExtensions.GetAccessControl(sourceDirectory, AccessControlSections.Access);
            var destinationSecurity = FileSystemAclExtensions.GetAccessControl(destinationDirectory, AccessControlSections.Access);
            RewriteAcl(sourceSecurity, destinationSecurity);
            FileSystemAclExtensions.SetAccessControl(destinationDirectory, destinationSecurity);
            return;
        }
        var sourceFile = (FileInfo)source;
        var destinationFile = (FileInfo)destination;
        var sourceFileSecurity = FileSystemAclExtensions.GetAccessControl(sourceFile, AccessControlSections.Access);
        var destinationFileSecurity = FileSystemAclExtensions.GetAccessControl(destinationFile, AccessControlSections.Access);
        RewriteAcl(sourceFileSecurity, destinationFileSecurity);
        FileSystemAclExtensions.SetAccessControl(destinationFile, destinationFileSecurity);
    }

    private static void RewriteAcl(FileSystemSecurity sourceSecurity, FileSystemSecurity destinationSecurity)
    {
        var effectiveRules = sourceSecurity.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Select(x => new FileSystemAccessRule(
                (System.Security.Principal.SecurityIdentifier)x.IdentityReference,
                x.FileSystemRights, x.InheritanceFlags, x.PropagationFlags, x.AccessControlType))
            .ToList();
        destinationSecurity.SetAccessRuleProtection(true, false);
        foreach (var rule in destinationSecurity.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                     .OfType<FileSystemAccessRule>().ToList())
            destinationSecurity.RemoveAccessRuleSpecific(rule);
        foreach (var rule in effectiveRules) destinationSecurity.AddAccessRule(rule);
    }

    private static void CopyDirectoryTimesAndAttributes(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        Directory.SetCreationTimeUtc(destination, sourceInfo.CreationTimeUtc);
        Directory.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);
        Directory.SetLastAccessTimeUtc(destination, sourceInfo.LastAccessTimeUtc);
        File.SetAttributes(destination, sourceInfo.Attributes);
    }

    private static string ComputeFileHash(FileInfo info)
    {
        using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool AclsEqual(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return true;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        try { return NormalizedAceSet(expected).SequenceEqual(NormalizedAceSet(actual), StringComparer.Ordinal); }
        catch (ArgumentException) { return false; }
    }

    // An inherited ACE and the equivalent explicit ACE grant the same effective rights.
    // A cross-volume copy necessarily changes the inheritance origin, so compare the
    // effective DACL while ignoring only the Inherited flag. This also accepts older
    // journals that stored a complete security descriptor instead of a DACL-only SDDL.
    private static List<string> NormalizedAceSet(string sddl)
    {
        var result = new List<string>();
        var acl = new RawSecurityDescriptor(sddl).DiscretionaryAcl;
        if (acl is null) return result;
        foreach (GenericAce ace in acl)
        {
            var bytes = new byte[ace.BinaryLength];
            ace.GetBinaryForm(bytes, 0);
            bytes[1] = (byte)(bytes[1] & ~(byte)AceFlags.Inherited);
            result.Add(Convert.ToHexString(bytes));
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static int PathDepth(string relative) => relative.Length == 0 ? 0 : relative.Count(x => x is '\\' or '/');

    private static string NormalizeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd('\\', '/');
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizeRoot(left), NormalizeRoot(right), StringComparison.OrdinalIgnoreCase);
}
