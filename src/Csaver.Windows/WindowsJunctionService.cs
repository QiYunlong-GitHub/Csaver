using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Csaver.Core;

namespace Csaver.Windows;

public sealed class WindowsJunctionService : IJunctionService
{
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public JunctionInfo Inspect(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return new(false, false, false, false, null);
            var attributes = File.GetAttributes(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isReparse = attributes.HasFlag(FileAttributes.ReparsePoint);
            if (!isReparse) return new(true, isDirectory, false, false, null);
            if (!isDirectory) return new(true, false, true, false, null);
            var target = ReadTarget(path, out var tag);
            return new(true, true, true, tag == IoReparseTagMountPoint, target);
        }
        catch (Exception ex)
        {
            return new(true, false, false, false, null, ex.Message);
        }
    }

    public void Create(string path, string target)
    {
        var source = Path.GetFullPath(path).TrimEnd('\\');
        var destination = Path.GetFullPath(target).TrimEnd('\\');
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException($"Junction target does not exist: {destination}");
        if (File.Exists(source)) throw new IOException("Junction path is an existing file.");
        if (Directory.Exists(source))
        {
            var info = Inspect(source);
            if (info.IsReparsePoint || Directory.EnumerateFileSystemEntries(source).Any())
                throw new IOException("Junction path must not exist or must be an empty ordinary directory.");
        }
        else Directory.CreateDirectory(source);

        var substitute = "\\??\\" + destination;
        var print = destination;
        var substituteBytes = System.Text.Encoding.Unicode.GetBytes(substitute);
        var printBytes = System.Text.Encoding.Unicode.GetBytes(print);
        var pathBytes = new byte[substituteBytes.Length + 2 + printBytes.Length + 2];
        Buffer.BlockCopy(substituteBytes, 0, pathBytes, 0, substituteBytes.Length);
        Buffer.BlockCopy(printBytes, 0, pathBytes, substituteBytes.Length + 2, printBytes.Length);

        var reparseDataLength = checked((ushort)(8 + pathBytes.Length));
        var buffer = new byte[8 + reparseDataLength];
        BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes(reparseDataLength).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes((ushort)substituteBytes.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substituteBytes.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, 14);
        Buffer.BlockCopy(pathBytes, 0, buffer, 16, pathBytes.Length);

        try
        {
            using var handle = OpenDirectory(source, GenericWrite);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create Junction reparse point.");

            var observed = Inspect(source);
            if (!observed.IsJunction || !PathsEqual(observed.Target, destination))
                throw new IOException("Junction creation did not produce the expected mount-point target.");
        }
        catch
        {
            try
            {
                var observed = Inspect(source);
                if (!observed.IsReparsePoint && Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
            }
            catch { }
            throw;
        }
    }

    public void Delete(string path)
    {
        var info = Inspect(path);
        if (!info.Exists) return;
        if (!info.IsJunction) throw new IOException("Refusing to delete a path that is not a Junction.");
        Directory.Delete(path, false);
    }

    private static string? ReadTarget(string path, out uint tag)
    {
        using var handle = OpenDirectory(path, 0);
        var buffer = new byte[16 * 1024];
        if (!DeviceIoControl(handle, FsctlGetReparsePoint, null, 0, buffer, buffer.Length, out var returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to inspect reparse point.");
        if (returned < 16) throw new IOException("Invalid reparse data.");
        tag = BitConverter.ToUInt32(buffer, 0);
        if (tag != IoReparseTagMountPoint) return null;
        var offset = BitConverter.ToUInt16(buffer, 8);
        var length = BitConverter.ToUInt16(buffer, 10);
        var target = System.Text.Encoding.Unicode.GetString(buffer, 16 + offset, length);
        if (target.StartsWith("\\??\\", StringComparison.Ordinal)) target = target[4..];
        return target;
    }

    private static SafeFileHandle OpenDirectory(string path, uint access)
    {
        var handle = CreateFile(path, access, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to open path: {path}");
        return handle;
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[]? inBuffer, int inBufferSize,
        byte[]? outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
