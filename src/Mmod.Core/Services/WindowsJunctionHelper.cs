using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mmod.Core.Services;

/// <summary>
/// 通过 Win32 FSCTL_SET_REPARSE_POINT 创建 NTFS 目录链接（junction），供 mklink 失败时回退。
/// </summary>
internal static class WindowsJunctionHelper
{
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x900A4;
    private const uint FsctlGetReparsePoint = 0x900A8;
    private const string NonInterpretedPathPrefix = @"\??\";
    private const int ReparseHeaderSize = 8;
    private const int MountPointHeaderSize = 8;

    [Flags]
    private enum FileAccessRights : uint
    {
        GenericRead = 0x80000000,
        GenericWrite = 0x40000000,
    }

    [Flags]
    private enum FileFlags : uint
    {
        BackupSemantics = 0x02000000,
        OpenReparsePoint = 0x00200000,
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        FileAccessRights dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        FileFlags dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? inBuffer,
        int inBufferSize,
        byte[] outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    /// <summary>
    /// 读取目录 junction 的目标路径（适用于 mklink /J，<see cref="Directory.ResolveLinkTarget"/> 常失败）。
    /// </summary>
    public static string? TryGetMountPointTarget(string junctionPath)
    {
        var junction = PathSanitizer.GetFullPath(junctionPath);
        var buffer = new byte[16 * 1024];

        using var handle = CreateFile(
            junction,
            FileAccessRights.GenericRead,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlags.BackupSemantics | FileFlags.OpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return null;

        if (!DeviceIoControl(handle, FsctlGetReparsePoint, [], 0, buffer, buffer.Length, out var returned, IntPtr.Zero) ||
            returned < ReparseHeaderSize + MountPointHeaderSize)
        {
            return null;
        }

        var tag = BitConverter.ToUInt32(buffer, 0);
        if (tag != IoReparseTagMountPoint)
            return null;

        var substituteOffset = BitConverter.ToUInt16(buffer, ReparseHeaderSize);
        var substituteLength = BitConverter.ToUInt16(buffer, ReparseHeaderSize + 2);
        if (substituteLength <= 0)
            return null;

        var nameStart = ReparseHeaderSize + substituteOffset;
        if (nameStart + substituteLength > returned)
            return null;

        var raw = DecodeUnicodePath(buffer, nameStart, substituteLength);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (raw.StartsWith(NonInterpretedPathPrefix, StringComparison.Ordinal))
            raw = raw[NonInterpretedPathPrefix.Length..];

        return PathSanitizer.GetFullPath(raw.TrimEnd('\\'));
    }

    private static string DecodeUnicodePath(byte[] buffer, int nameStart, int maxBytes)
    {
        var end = Math.Min(nameStart + maxBytes, buffer.Length);
        for (var i = nameStart; i + 1 < end; i += 2)
        {
            if (buffer[i] == 0 && buffer[i + 1] == 0)
            {
                end = i;
                break;
            }
        }

        var length = end - nameStart;
        if (length <= 0)
            return string.Empty;

        var raw = Encoding.Unicode.GetString(buffer, nameStart, length);
        return PathSanitizer.Clean(raw);
    }

    public static void Create(string junctionPath, string targetDirectory)
    {
        var junction = PathSanitizer.GetFullPath(junctionPath);
        var target = PathSanitizer.GetFullPath(targetDirectory).TrimEnd('\\');

        if (Directory.Exists(junction))
            throw new IOException($"链接路径已存在：{junction}");

        Directory.CreateDirectory(junction);

        var substituteName = NonInterpretedPathPrefix + target;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName + '\0');
        var printNameBytes = Encoding.Unicode.GetBytes("\0");

        var reparseDataLength = MountPointHeaderSize + substituteBytes.Length + printNameBytes.Length;
        var inBuffer = new byte[ReparseHeaderSize + reparseDataLength];
        var offset = 0;

        WriteUInt32(inBuffer, ref offset, IoReparseTagMountPoint);
        WriteUInt16(inBuffer, ref offset, (ushort)reparseDataLength);
        WriteUInt16(inBuffer, ref offset, 0);
        WriteUInt16(inBuffer, ref offset, 0);
        WriteUInt16(inBuffer, ref offset, (ushort)substituteBytes.Length);
        WriteUInt16(inBuffer, ref offset, (ushort)(MountPointHeaderSize + substituteBytes.Length));
        WriteUInt16(inBuffer, ref offset, 0);

        Buffer.BlockCopy(substituteBytes, 0, inBuffer, offset, substituteBytes.Length);
        offset += substituteBytes.Length;
        Buffer.BlockCopy(printNameBytes, 0, inBuffer, offset, printNameBytes.Length);

        using var handle = CreateFile(
            junction,
            FileAccessRights.GenericWrite,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlags.BackupSemantics | FileFlags.OpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            TryDeleteEmptyDirectory(junction);
            throw new IOException($"无法打开链接点：{junction}", new Win32Exception(err));
        }

        if (!DeviceIoControl(handle, FsctlSetReparsePoint, inBuffer, inBuffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            TryDeleteEmptyDirectory(junction);
            throw new IOException($"无法创建目录链接：{junction}", new Win32Exception(err));
        }
    }

    private static void WriteUInt32(byte[] buffer, ref int offset, uint value)
    {
        buffer[offset++] = (byte)value;
        buffer[offset++] = (byte)(value >> 8);
        buffer[offset++] = (byte)(value >> 16);
        buffer[offset++] = (byte)(value >> 24);
    }

    private static void WriteUInt16(byte[] buffer, ref int offset, ushort value)
    {
        buffer[offset++] = (byte)value;
        buffer[offset++] = (byte)(value >> 8);
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: false);
        }
        catch
        {
            // ignore cleanup failure
        }
    }
}
