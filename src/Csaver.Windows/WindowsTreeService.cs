using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Csaver.Core;

namespace Csaver.Windows;

public sealed class WindowsTreeService : ITreeService
{
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;

    [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurity(string fileName, uint securityInformation, byte[] securityDescriptor);

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
                CopyFileOverwriting(path, target);
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

    private static void CopyFileOverwriting(string source, string destination)
    {
        try
        {
            if (File.Exists(destination))
            {
                var attributes = File.GetAttributes(destination);
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
            }
            File.Copy(source, destination, true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                // A previous failed migration can leave files whose DACL refuses writes.
                // Grant a temporary write ACE; CopyPreserving re-applies the source DACL afterwards.
                GrantTemporaryWriteAccess(destination);
                File.Copy(source, destination, true);
                return;
            }
            catch (Exception retry)
            {
                throw new IOException($"Cannot copy '{source}' to '{destination}': {ex.Message}. A previous failed migration may have left read-only or restricted files in the destination; close applications using the destination and retry. Retry failed: {retry.Message}", retry);
            }
        }
    }

    private static void GrantTemporaryWriteAccess(string path)
    {
        var info = new FileInfo(path);
        var security = FileSystemAclExtensions.GetAccessControl(info, AccessControlSections.Access);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(info, security);
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
    // the DACL and copy the source's effective ACEs onto the destination.
    private static void CopyEffectiveAcl(FileSystemInfo source, FileSystemInfo destination)
    {
        var sddl = ProtectedAclSddl(source);
        // Managed FileSystemSecurity rebuilds ACEs through FileSystemAccessRule: it
        // rejects GENERIC_* masks and silently rewrites or merges other raw masks.
        // Write the self-relative descriptor directly with SetFileSecurity instead;
        // PROTECTED_DACL_SECURITY_INFORMATION also stops Windows from recomputing an
        // auto-inherited DACL from the destination parent.
        var descriptor = new RawSecurityDescriptor(sddl);
        var binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        if (!SetFileSecurity(destination.FullName, DaclSecurityInformation | ProtectedDaclSecurityInformation, binary))
            throw new IOException($"Cannot apply the source DACL to '{destination.FullName}': {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    // Copy the effective DACL at the ACE level. Rebuilding rules through
    // FileSystemAccessRule rejects access masks that contain GENERIC_* bits
    // (0xE0010000 becomes -536805376 as FileSystemRights), which is common in
    // application-owned directories. SDDL/RawSecurityDescriptor keeps every 32-bit
    // mask, ACE type, and inheritance flag; only Inherited is cleared because the
    // destination DACL is protected rather than inherited from its new parent.
    private static string ProtectedAclSddl(FileSystemInfo source)
    {
        FileSystemSecurity sourceSecurity = source is DirectoryInfo sourceDirectory
            ? FileSystemAclExtensions.GetAccessControl(sourceDirectory, AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl((FileInfo)source, AccessControlSections.Access);
        var descriptor = new RawSecurityDescriptor(sourceSecurity.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        if (descriptor.DiscretionaryAcl is not null)
            foreach (GenericAce ace in descriptor.DiscretionaryAcl)
                ace.AceFlags &= ~AceFlags.Inherited;
        // ControlFlags is read-only on a parsed descriptor, so normalize the SDDL
        // prefix to D:P (DACL present + protected, no auto-inheritance).
        var sddl = descriptor.GetSddlForm(AccessControlSections.Access);
        var aclStart = sddl.IndexOf('(');
        if (aclStart >= 0 && sddl.StartsWith("D:", StringComparison.Ordinal))
            sddl = "D:P" + sddl[aclStart..];
        return sddl;
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

    // An inherited ACE and the equivalent explicit ACE grant the same effective rights,
    // and a cross-volume copy necessarily changes the inheritance origin. Windows also
    // maps GENERIC_* bits to file-specific rights and merges duplicate SID ACEs when a
    // DACL is applied, so compare effective masks per (ACE type, flags, SID) instead of
    // raw ACE bytes. This also accepts legacy journals with full security descriptors.
    private static List<string> NormalizedAceSet(string sddl)
    {
        var effective = new Dictionary<string, uint>(StringComparer.Ordinal);
        var opaque = new List<string>();
        var acl = new RawSecurityDescriptor(sddl).DiscretionaryAcl;
        if (acl is null) return opaque;
        foreach (GenericAce ace in acl)
        {
            var bytes = new byte[ace.BinaryLength];
            ace.GetBinaryForm(bytes, 0);
            var flags = (byte)(bytes[1] & ~(byte)AceFlags.Inherited);
            if (ace is CommonAce common && bytes.Length >= 8 && bytes[0] <= 1)
            {
                var key = $"M:{bytes[0]}|{flags}|{common.SecurityIdentifier.Value}";
                var rights = MapGenericFileRights((uint)common.AccessMask);
                effective[key] = effective.TryGetValue(key, out var current) ? current | rights : rights;
            }
            else
            {
                bytes[1] = flags;
                opaque.Add("O:" + Convert.ToHexString(bytes));
            }
        }
        var result = effective.Select(x => $"{x.Key}|{x.Value:X8}").Concat(opaque).ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static uint MapGenericFileRights(uint mask)
    {
        var result = mask & 0x0FFFFFFFu;
        if ((mask & 0x80000000u) != 0) result |= 0x00120089u;
        if ((mask & 0x40000000u) != 0) result |= 0x00120116u;
        if ((mask & 0x20000000u) != 0) result |= 0x001200A0u;
        if ((mask & 0x10000000u) != 0) result |= 0x001F01FFu;
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
