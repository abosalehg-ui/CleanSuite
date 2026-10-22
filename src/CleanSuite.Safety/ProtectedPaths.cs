using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CleanSuite.Safety;

/// <summary>
/// Last-line guard applied to every delete, independent of scan rules (not present in the original;
/// the original only protects ?:\Windows and System32 for "/E" rules).
/// </summary>
public sealed class ProtectedPaths
{
    private readonly List<string> _trees = new();   // nothing inside may be deleted
    private readonly List<string> _roots = new();   // the folder itself may not be deleted
    private readonly List<string> _allowed = new(); // explicit exceptions inside protected trees (exact rules only)

    public static ProtectedPaths ForCurrentSystem()
    {
        var p = new ProtectedPaths();
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (win.Length > 0)
        {
            foreach (var sub in new[] { "System32", "SysWOW64", "WinSxS", "servicing", "Boot", "Fonts", "assembly", "Installer", "SystemApps" })
                p.AddTree(Path.Combine(win, sub));
            p.AddRoot(win);
        }
        foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                                  Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData,
                                  Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                                  Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop })
        {
            var path = Environment.GetFolderPath(f);
            if (path.Length > 0) p.AddRoot(path);
        }
        p.AddTree(AppContext.BaseDirectory);
        return p;
    }

    public ProtectedPaths AddTree(string path) { _trees.Add(Normalize(path)); return this; }
    public ProtectedPaths AddRoot(string path) { _roots.Add(Normalize(path)); return this; }

    /// <summary>
    /// Explicit exception: files inside <paramref name="path"/> (or that exact file) may be deleted even if it lies in a
    /// protected tree. The folder itself stays protected. Used only for fixed, reviewed History rules (e.g. System32\spool\PRINTERS).
    /// </summary>
    public ProtectedPaths Allow(string path) { _allowed.Add(Normalize(path)); return this; }

    /// <summary>Returns a reason when deleting <paramref name="path"/> is forbidden, otherwise null.</summary>
    public string? Check(string path, bool isDirectory = false)
    {
        string n;
        try { n = Normalize(path); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "invalid path"; }
        if (n.Length <= 3) return "drive root";
        foreach (var a in _allowed)
            if (n.Equals(a, StringComparison.OrdinalIgnoreCase) && !isDirectory
                || n.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase))
                return null;
        foreach (var t in _trees)
            if (n.Equals(t, StringComparison.OrdinalIgnoreCase) || n.StartsWith(t + "\\", StringComparison.OrdinalIgnoreCase))
                return "protected system folder: " + t;
        foreach (var r in _roots)
            if (n.Equals(r, StringComparison.OrdinalIgnoreCase))
                return "protected folder: " + r;
        // A folder that contains a protected folder (C:\Users, \AppData) would take it along.
        if (isDirectory)
            foreach (var p in _trees.Concat(_roots))
                if (p.StartsWith(n + "\\", StringComparison.OrdinalIgnoreCase))
                    return "contains a protected folder: " + p;
        return null;
    }

    /// <summary>
    /// <see cref="Check"/> on the path as written and on the path Windows would really act on. The real path follows every
    /// junction, symbolic link and subst drive on the way (a folder replaced by a link to C:\Windows after the scan) and expands
    /// short 8.3 names (C:\PROGRA~1); the item itself is not followed (deleting a link removes the link). An item that cannot be
    /// opened (gone, access denied) is judged by its written path; the caller's existence check deals with the rest.
    /// </summary>
    public string? CheckResolved(string path, bool isDirectory = false)
    {
        if (Check(path, isDirectory) is { } why) return why;
        var real = RealPath(path);
        if (real == null) return null;
        if (Check(real, isDirectory) is not null) return "protected location reached through a link or short name: " + real;
        return null;
    }

    /// <summary>The final path of an existing file or folder (the item itself not followed), or null when it cannot be opened.</summary>
    public static string? RealPath(string path)
    {
        try
        {
            using var h = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7 /* share all */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0 /* FILE_NAME_NORMALIZED | VOLUME_NAME_DOS */);
            if (n == 0) return null;
            if (n >= sb.Capacity)
            {
                sb = new StringBuilder((int)n + 1);
                if (GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0) == 0) return null;
            }
            return sb.ToString();
        }
        catch (Exception e) when (e is ArgumentException or IOException) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    /// <summary>Full path without trailing separators and without the \\?\ / \\?\UNC\ long-path prefixes.</summary>
    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) path = path[4..];
        return Path.GetFullPath(path).TrimEnd('\\');
    }
}
