using System.Runtime.InteropServices;

namespace CleanSuite.Safety;

/// <summary>Drive types as returned by GetDriveTypeW.</summary>
public enum DriveKind { Unknown = 0, NoRootDir = 1, Removable = 2, Fixed = 3, Remote = 4, CdRom = 5, RamDisk = 6 }

/// <summary>
/// Whether a path exists, answered the same way for the registry, shortcut and uninstall scanners. Deliberate safety
/// deviations from the original, which only asks Windows once: a path counts as existing when access is denied (e.g.
/// "Program Files\WindowsApps" for a normal user) and when its drive cannot be read at all (BitLocker-locked, not ready,
/// unrecognized volume), because then nobody can tell that it is missing, and an entry that is only unreachable must
/// never be reported (and, in a scheduled run, removed) as invalid.
/// </summary>
public static class PathProbe
{
    private const int ErrorAccessDenied = 5;

    public static DriveKind DriveType(string root) => (DriveKind)GetDriveTypeW(root);

    /// <summary>FindFirstFileW (wildcards allowed, as in the original) finds something, or existence cannot be known.</summary>
    public static bool FindFirst(string path)
    {
        var h = FindFirstFileW(path, out _);
        if (h != new IntPtr(-1)) { FindClose(h); return true; }
        return Marshal.GetLastWin32Error() == ErrorAccessDenied || DriveUnreadable(path);
    }

    /// <summary>GetFileAttributesW succeeds, or existence cannot be known.</summary>
    public static bool HasAttributes(string path)
    {
        if (GetFileAttributesW(path) != uint.MaxValue) return true;
        return Marshal.GetLastWin32Error() == ErrorAccessDenied || DriveUnreadable(path);
    }

    /// <summary>
    /// <see cref="HasAttributes"/>, retried in the 32-bit twin of System32, Program Files and Common Files (paths written
    /// by 32-bit programs on a 64-bit system).
    /// </summary>
    public static bool ExistsInAnyView(string path)
    {
        if (path.Length == 0) return false;
        if (HasAttributes(path)) return true;
        foreach (var (from, to) in Wow64Redirects)
            if (path.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase) && HasAttributes(to + path[from.Length..])) return true;
        return false;
    }

    /// <summary>The drive of an absolute "X:\…" path exists but cannot be read (locked, not ready, unrecognized).</summary>
    public static bool DriveUnreadable(string path)
    {
        if (path.Length < 3 || path[1] != ':' || path[2] != '\\' || !char.IsAsciiLetter(path[0])) return false;
        var root = path[..3];
        if (DriveType(root) is DriveKind.NoRootDir or DriveKind.Unknown) return false;   // no such drive: really missing
        return GetFileAttributesW(root) == uint.MaxValue;
    }

    /// <summary>System32 → SysWOW64, Program Files → Program Files (x86), Common Files → Common Files (x86).</summary>
    public static IReadOnlyList<(string From, string To)> Wow64Redirects { get; } = BuildRedirects();

    /// <summary>The 32-bit folders a bare "name.ext" is also looked for in.</summary>
    public static IReadOnlyList<string> Wow64Folders =>
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86) }.Where(d => d.Length > 0).ToList();

    private static List<(string, string)> BuildRedirects()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var wow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        var pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var cf = Environment.GetEnvironmentVariable("CommonProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        var cf86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
        var redirects = new List<(string, string)>();
        void Map(string from, string to)
        {
            if (from.Length > 0 && to.Length > 0 && !from.Equals(to, StringComparison.OrdinalIgnoreCase)) redirects.Add((from, to));
        }
        if (sys.Length > 0) Map(sys, wow);
        Map(pf, pf86);
        Map(cf, cf86);
        return redirects;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string name, out WIN32_FIND_DATAW data);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);
}
