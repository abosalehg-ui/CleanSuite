using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CleanSuite.Safety;

/// <summary>
/// The physical identity of a file (volume serial number and file index). Two different paths name the same file when
/// they reach it through a subst drive, a network share of a local folder, a hard link or nested scan roots; comparing
/// identities instead of path text prevents treating a file as a copy of itself.
/// </summary>
public readonly record struct FileIdentity(uint Volume, ulong Index)
{
    /// <summary>Null when the file cannot be opened for its attributes.</summary>
    public static FileIdentity? Of(string path)
    {
        using var h = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7 /* share read, write, delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
        return new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    /// <summary>True when both paths open the same file. Unknown identities fall back to comparing the full paths.</summary>
    public static bool Same(string a, string b)
    {
        if (string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase)) return true;
        var ia = Of(a);
        var ib = Of(b);
        return ia != null && ia == ib;
    }

    /// <summary>Byte-for-byte comparison of two files; false when either cannot be read.</summary>
    public static bool SameContent(string a, string b)
    {
        try
        {
            using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            if (fa.Length != fb.Length) return false;
            var ba = new byte[1 << 20];
            var bb = new byte[1 << 20];
            while (true)
            {
                int na = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
                int nb = fb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
                if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
                if (na == 0) return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
}
