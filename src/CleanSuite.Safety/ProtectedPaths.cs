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

    /// <summary>Full path without trailing separators and without the \\?\ / \\?\UNC\ long-path prefixes.</summary>
    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) path = path[4..];
        return Path.GetFullPath(path).TrimEnd('\\');
    }
}
