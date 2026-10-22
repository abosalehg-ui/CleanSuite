using System.Runtime.InteropServices;
using System.Text;

namespace CleanSuite.Safety;

/// <summary>Moves files to the Recycle Bin. Abstracted so tests never touch the user's real Recycle Bin.</summary>
public interface IRecycleBin
{
    /// <summary>Recycles the files; returns the paths that could not be recycled.</summary>
    IReadOnlyList<string> Recycle(IReadOnlyList<string> paths, bool silent);

    /// <summary>
    /// Why this path cannot go to the Recycle Bin (its drive has none, so Windows would delete it for good), or null.
    /// Callers refuse such paths instead of recycling them.
    /// </summary>
    string? WhyNotRecyclable(string path) => null;
}

/// <summary>
/// Recycle Bin via SHFileOperationW(FO_DELETE, FOF_ALLOWUNDO), the original's mechanism, with two safeguards it lacks.
/// Windows silently deletes for good what it cannot recycle (network shares, removable drives, drives whose bin is turned
/// off, files larger than the bin): such drives are detected first and their paths are never passed on, and
/// FOF_WANTNUKEWARNING makes Windows ask before it would still destroy a file instead of recycling it.
/// </summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    private const ushort FofAllowUndo = 0x0040, FofNoConfirmation = 0x0010, FofNoErrorUi = 0x0400, FofSilent = 0x0004,
        FofNoConfirmMkDir = 0x0200, FofWantNukeWarning = 0x4000;

    private readonly Dictionary<string, string?> _rootReason = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Recycle(IReadOnlyList<string> paths, bool silent)
    {
        var refused = paths.Where(p => WhyNotRecyclable(p) != null).ToList();
        var send = paths.Except(refused).ToList();
        if (send.Count == 0) return refused;
        var from = string.Join("\0", send) + "\0\0";
        ushort flags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofWantNukeWarning;
        if (silent) flags |= FofSilent | FofNoConfirmMkDir;
        var op = new SHFILEOPSTRUCTW { wFunc = 3, pFrom = from, fFlags = flags };
        SHFileOperationW(ref op);
        // Whatever the return code (or a declined warning), the items that still exist are the ones not recycled.
        return refused.Concat(send.Where(p => File.Exists(p) || Directory.Exists(p))).ToList();
    }

    public string? WhyNotRecyclable(string path)
    {
        string? root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "invalid path"; }
        if (string.IsNullOrEmpty(root)) return "invalid path";
        lock (_rootReason)
        {
            if (!_rootReason.TryGetValue(root, out var why)) _rootReason[root] = why = NoRecycleBinReason(root);
            return why;
        }
    }

    /// <summary>Null when the volume of this root has a working Recycle Bin.</summary>
    public static string? NoRecycleBinReason(string root)
    {
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return "network location without a Recycle Bin (not deleted)";
        switch (GetDriveTypeW(root))
        {
            case 2: return "removable drive without a Recycle Bin (not deleted)";       // DRIVE_REMOVABLE
            case 4: return "network drive without a Recycle Bin (not deleted)";         // DRIVE_REMOTE
            case 5: return "CD/DVD drive (not deleted)";                                 // DRIVE_CDROM
            case 0 or 1: return "drive not available (not deleted)";                     // DRIVE_UNKNOWN / DRIVE_NO_ROOT_DIR
        }
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        if (SHQueryRecycleBinW(root, ref info) != 0) return "drive without a Recycle Bin (not deleted)";
        if (RecycleBinTurnedOff(root)) return "the Recycle Bin is turned off for this drive (not deleted)";
        return null;
    }

    /// <summary>"Don't move files to the Recycle Bin" (BitBucket NukeOnDelete), per volume or for all drives; read only.</summary>
    private static bool RecycleBinTurnedOff(string root)
    {
        const string bitBucket = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";
        try
        {
            using var all = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(bitBucket);
            if (all?.GetValue("NukeOnDelete") is int g && g != 0) return true;
            var name = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPointW(root, name, name.Capacity)) return false;
            var v = name.ToString();                                         // \\?\Volume{GUID}\
            int open = v.IndexOf('{'), close = v.IndexOf('}');
            if (open < 0 || close < open) return false;
            using var vol = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(bitBucket + @"\Volume\" + v[open..(close + 1)]);
            return vol?.GetValue("NukeOnDelete") is int n && n != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string root, ref SHQUERYRBINFO info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder name, int length);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);
}
