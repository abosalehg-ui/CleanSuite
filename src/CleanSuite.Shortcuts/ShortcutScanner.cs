using System.Runtime.InteropServices;
using System.Security.Principal;
using CleanSuite.Matching;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.Shortcuts;

public enum ShortcutItemKind { InvalidShortcut = 0, EmptyFolder = 1 }

/// <summary>Why a shortcut was reported (the original shows no reason; CleanSuite records it for review).</summary>
public enum ShortcutReason { None, TargetMissing, ArgumentPathMissing, EmptyFolder }

/// <summary>One reported item: a .lnk whose target is missing, or an empty Start-menu folder.</summary>
public sealed record ShortcutItem
{
    public required ShortcutItemKind Kind { get; init; }
    /// <summary>File or folder name.</summary>
    public required string Name { get; init; }
    /// <summary>Containing folder.</summary>
    public required string Folder { get; init; }
    /// <summary>The missing target (or the missing path in the arguments); empty for folders.</summary>
    public required string Target { get; init; }
    public required ShortcutReason Reason { get; init; }
    /// <summary>Last write time when scanned; a shortcut that changed afterwards is not deleted.</summary>
    public DateTime LastWriteUtc { get; init; }

    public string FullPath => Path.Combine(Folder, Name);
}


/// <summary>Scan settings (defaults = the original's: Start menu &amp; Desktop mode, no empty folders, fixed drives).</summary>
public sealed record ShortcutScanOptions
{
    /// <summary>Settings\ScanOnlyStartMenu (1): scan the Start menus, desktops, AppData and Recent; otherwise <see cref="Drives"/>.</summary>
    public bool StartMenuMode { get; init; } = true;
    /// <summary>Settings\ScanEmptyStartMenuFolders (0): also report folders of the two Start menus that contain no files.</summary>
    public bool EmptyStartMenuFolders { get; init; }
    /// <summary>Drive roots ("C:") for drive mode (Settings\DriveShortCuts; default all fixed drives).</summary>
    public IReadOnlyList<string> Drives { get; init; } = FixedDrives();
    /// <summary>IncludeFloppyShort (0): check targets on removable drives instead of assuming they are valid.</summary>
    public bool IncludeRemovable { get; init; }
    /// <summary>Folders with ignore lists (the original's Data\Ignore_Lnk); every file in them is read. Read only.</summary>
    public IReadOnlyList<string> IgnoreFolders { get; init; } = Array.Empty<string>();
    /// <summary>Explicit roots instead of the system folders (tests and the CLI's --root).</summary>
    public IReadOnlyList<(string Path, bool EmptyFolders)>? Roots { get; init; }

    public static IReadOnlyList<string> FixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == System.IO.DriveType.Fixed).Select(d => d.Name[..2].ToUpperInvariant()).ToList();
}

/// <summary>User profiles and account names used to find shortcuts whose profile folder moved.</summary>
public sealed record ProfileInfo(IReadOnlyList<string> ProfileRoots, IReadOnlyList<string> UserNames)
{
    public static ProfileInfo Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// Every folder in the profiles folder ("C:\Users\…"), and the current user name followed by the other accounts of
    /// ProfileList (Flags = 0, not the system accounts S-1-5-18/19/20). Read only.
    /// </summary>
    public static ProfileInfo Current()
    {
        var roots = new List<string>();
        try
        {
            var profiles = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (profiles != null && Directory.Exists(profiles))
                roots.AddRange(Directory.GetDirectories(profiles));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var names = new List<string> { Environment.UserName.Trim() };
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", false);
            foreach (var sid in list?.GetSubKeyNames() ?? [])
            {
                if (sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") continue;
                using var k = list!.OpenSubKey(sid, false);
                if (k?.GetValue("Flags") is int flags ? flags != 0 : true) continue;
                try
                {
                    var account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
                    var name = account[(account.LastIndexOf('\\') + 1)..];
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                }
                catch (Exception e) when (e is IdentityNotMappedException or ArgumentException or SystemException) { }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return new ProfileInfo(roots, names);
    }
}

/// <summary>
/// Fix Invalid Shortcuts: finds .lnk files whose target no longer exists.
/// Read-only: shortcuts are loaded without IShellLink::Resolve, so nothing is searched for or changed.
/// </summary>
public sealed class ShortcutScanner
{
    private readonly ShortcutScanOptions _o;
    private readonly ProfileInfo _profiles;
    private readonly Func<string, DriveKind> _driveType;
    private readonly Func<string, LinkTarget?> _read;
    private readonly List<string> _ignore = new();
    private readonly List<ShortcutItem> _items = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private string _currentDrive = "";
    private CancellationToken _ct;

    public int FilesScanned { get; private set; }
    public int ShortcutsScanned { get; private set; }
    public IReadOnlyList<string> IgnorePatterns => _ignore;

    public ShortcutScanner(ShortcutScanOptions options, ProfileInfo? profiles = null, Func<string, DriveKind>? driveType = null,
        Func<string, LinkTarget?>? readLink = null)
    {
        _o = options;
        _profiles = profiles ?? ProfileInfo.Current();
        _driveType = driveType ?? PathProbe.DriveType;
        _read = readLink ?? ShellLinkFile.Read;
        LoadIgnoreList();
    }

    // ------------------------------------------------------------------ ignore list

    /// <summary>Always ignored, with or without the original's lists: the component store keeps old shortcuts on purpose.</summary>
    public static readonly IReadOnlyList<string> BuiltInIgnore = [@"*\WINSXS\*"];

    /// <summary>The built-in patterns, then every file of every ignore folder: one wildcard pattern per line, "//" starts a comment.</summary>
    private void LoadIgnoreList()
    {
        _ignore.AddRange(BuiltInIgnore);
        foreach (var folder in _o.IgnoreFolders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var f in Directory.GetFiles(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string[] lines;
                try { lines = File.ReadAllLines(f, System.Text.Encoding.Latin1); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var line in lines)
                {
                    var s = line;
                    int c = s.IndexOf("//", StringComparison.Ordinal);
                    if (c >= 0) s = s[..c];
                    s = s.Trim().ToUpperInvariant();
                    if (s.Length > 0) _ignore.Add(s);
                }
            }
        }
    }

    /// <summary>A pattern with "\" is matched against the full path, otherwise against the name.</summary>
    internal bool IsIgnored(string name, string fullPath) =>
        _ignore.Any(p => AceWildcard.IsMatch(p.Contains('\\') ? fullPath : name, p));

    // ------------------------------------------------------------------ roots

    /// <summary>The folders scanned in Start-menu mode, in the original's order (CSIDL 0x16, 0x0B, 0x10, 0x19, 0x1A, 0x08).</summary>
    public static IReadOnlyList<(string Path, bool StartMenu)> StartMenuRoots() =>
    [
        (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), true),
        (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), true),
        (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
        (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
        (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), false),
        (Environment.GetFolderPath(Environment.SpecialFolder.Recent), false),
    ];

    public IReadOnlyList<ShortcutItem> Scan(CancellationToken ct = default)
    {
        _ct = ct;
        _items.Clear(); _seen.Clear();
        FilesScanned = ShortcutsScanned = 0;
        if (_o.Roots != null)
        {
            foreach (var (path, empty) in _o.Roots)
            {
                _currentDrive = _o.StartMenuMode ? "" : path.Length >= 2 ? path[..2].ToUpperInvariant() : "";
                ScanFolder(path.TrimEnd('\\'), empty);
            }
        }
        else if (_o.StartMenuMode)
        {
            _currentDrive = "";
            foreach (var (path, startMenu) in StartMenuRoots())
                if (path.Length > 0) ScanFolder(path.TrimEnd('\\'), startMenu && _o.EmptyStartMenuFolders);
        }
        else
        {
            foreach (var d in _o.Drives)
            {
                _currentDrive = d.TrimEnd('\\').ToUpperInvariant();
                ScanFolder(_currentDrive, false);
            }
        }
        return _items.ToList();
    }

    // ------------------------------------------------------------------ folder walk

    private const int MaxPath = SafeWalk.MaxPath;

    /// <summary>
    /// Checks every shortcut below <paramref name="root"/> (the shared <see cref="SafeWalk"/>: links never followed). Ignored
    /// folders, hidden system folders, "Send To" and Administrative Tools are not entered; an empty Start-menu folder is reported
    /// (when asked) instead of being entered.
    /// </summary>
    private void ScanFolder(string root, bool emptyFolders)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SafeWalk.Walk(root, dir => !skip.Contains(dir), (path, entries) =>
        {
            foreach (var e in entries)
            {
                _ct.ThrowIfCancellationRequested();
                var full = path + "\\" + e.Name;
                if (e is FileInfo fi)
                {
                    if (IsIgnored(e.Name, full)) continue;
                    FilesScanned++;
                    if (!e.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                    ShortcutsScanned++;
                    if (e.Name.Equals("Windows Catalog.lnk", StringComparison.OrdinalIgnoreCase)) continue;
                    CheckShortcut(fi, path);
                    continue;
                }
                var attr = e.Attributes;
                if (IsIgnored(e.Name, full)
                    || (attr & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)
                    || (attr & FileAttributes.ReparsePoint) != 0
                    || AceWildcard.IsMatch(full, @"*\sendto*ba*") || AceWildcard.IsMatch(full, @"*\Administrative Tools"))
                {
                    skip.Add(full);
                    continue;
                }
                if (emptyFolders && HasNoFiles(full))
                {
                    skip.Add(full);
                    if (!e.Name.Equals("startup", StringComparison.OrdinalIgnoreCase) && !e.Name.Equals("startup-", StringComparison.OrdinalIgnoreCase)
                        && !e.Name.Equals("System Tools", StringComparison.OrdinalIgnoreCase))
                        _items.Add(new ShortcutItem
                        {
                            Kind = ShortcutItemKind.EmptyFolder, Name = e.Name, Folder = path, Target = "", Reason = ShortcutReason.EmptyFolder,
                            LastWriteUtc = e.LastWriteTimeUtc,
                        });
                }
            }
        }, ct: _ct);
    }

    /// <summary>No file anywhere below; a reparse-point subfolder counts as content.</summary>
    internal static bool HasNoFiles(string folder)
    {
        if (folder.Length >= MaxPath) return false;
        try
        {
            foreach (var e in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                if (e is FileInfo) return false;
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (!HasNoFiles(e.FullName)) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ------------------------------------------------------------------ one shortcut (§3 of the spec)

    private void CheckShortcut(FileInfo lnk, string folder)
    {
        var link = _read(lnk.FullName);
        if (link == null || link.Target.Length == 0) return;                 // unreadable or shell-only shortcut: skipped
        var target = link.Target;

        var fn = Path.GetFileName(target.Trim('"').TrimEnd('\\'));
        if (fn.Length > 0 && Exists(Path.Combine(folder, fn))) return;        // target sits next to the shortcut

        ShortcutReason reason;
        string reported;
        if (TargetPathOk(target, link.EnvironmentExpanded))
        {
            if (Acceptable(link.Arguments, link.EnvironmentExpanded)) return;
            reason = ShortcutReason.ArgumentPathMissing;
            reported = link.Arguments;
        }
        else
        {
            if (Relocated(target, lnk.FullName)) return;
            reason = ShortcutReason.TargetMissing;
            reported = target;
        }
        if (!_seen.Add(lnk.FullName)) return;
        _items.Add(new ShortcutItem
        {
            Kind = ShortcutItemKind.InvalidShortcut, Name = lnk.Name, Folder = folder, Target = reported, Reason = reason,
            LastWriteUtc = lnk.LastWriteTimeUtc,
        });
    }

    /// <summary>Valid when the text holds no "X:\" path or one of its first candidates is acceptable.</summary>
    internal bool TargetPathOk(string text, bool envExpanded = false)
    {
        var u = text.ToUpperInvariant();
        bool tried = false;
        int pos = 0;
        for (int i = 0; i < 9; i++)
        {
            int idx = pos <= u.Length ? u.IndexOf(@":\", pos, StringComparison.Ordinal) : -1;
            if (idx < 0) return !tried;
            if (tried && idx < 5) return true;
            if (idx > 0 && u[idx - 1] is >= 'A' and <= 'Z')
            {
                if (Acceptable(u[(idx - 1)..], envExpanded)) return true;
                tried = true;
            }
            pos = idx + 1;
        }
        return true;
    }

    /// <summary>
    /// True unless the text is a path on a fixed drive that does not exist after the original's
    /// trimming. Network, CD, unknown drives and (by default) removable drives are never reported.
    /// </summary>
    internal bool Acceptable(string text, bool envExpanded = false)
    {
        var s = text.ToUpperInvariant().Trim('"');
        if (s.StartsWith(@"A:\", StringComparison.Ordinal) && _driveType(@"A:\") == DriveKind.Removable && !_o.IncludeRemovable) return true;
        if (Exists(s)) return true;
        if (_currentDrive.Length > 0 && s.Length > 3)
        {
            if (Exists(_currentDrive + "\\" + s[3..])) return true;                  // same path on the scanned drive
            if (envExpanded && !s.StartsWith(_currentDrive, StringComparison.Ordinal)) return true;
        }
        int cut = s.IndexOfAny(['/', '*', '?', '|', '<', '>', '"']);
        if (cut >= 0) s = s[..cut];
        if (Exists(s)) return true;
        if (s.Length < 4 || s.Substring(1, 2) != @":\") return true;

        switch (_driveType(s[..3]))
        {
            case DriveKind.Removable: return !_o.IncludeRemovable;
            case DriveKind.Fixed:
                cut = s.IndexOfAny([';', ',', '%', '^']);
                if (cut >= 0) s = s[..cut];
                if (s.Contains("RUNDLL", StringComparison.Ordinal) || s.Contains("SHELL32", StringComparison.Ordinal)
                    || s.Contains("REGSVR", StringComparison.Ordinal)) return true;
                s = s.TrimStart('"', ' ', '\t').TrimEnd('"', '\\', ' ', '\t');
                if (s.Length == 0 || Exists(s)) return true;
                foreach (var ext in new[] { ".EXE", ".DLL", ".CHM" })
                {
                    int i = s.IndexOf(ext, StringComparison.Ordinal);
                    if (i >= 0) { s = s[..(i + 4)]; break; }
                }
                return Exists(s + ".exe") || Exists(s + ".url") || Exists(s);
            default:
                return true;
        }
    }

    /// <summary>
    /// Relocation tries: the same path below another profile folder, or with another account name (shortcuts copied
    /// between profiles). The original also searches other Windows installations for profile folders with a hard-coded
    /// "d:" drive; CleanSuite applies that search to the shortcut's and the target's own drives.
    /// </summary>
    private bool Relocated(string target, string lnkPath)
    {
        foreach (var p in _profiles.ProfileRoots)
        {
            if (!target.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = target[p.Length..];
            foreach (var q in _profiles.ProfileRoots)
            {
                var cand = q + rest;
                if (!cand.Equals(target, StringComparison.OrdinalIgnoreCase) && Exists(cand)) return true;
            }
        }
        if (_profiles.UserNames.Count == 0) return false;
        var user0 = _profiles.UserNames[0];
        if (!target.Contains(user0, StringComparison.OrdinalIgnoreCase)) return false;
        var marker = "\\" + user0 + "\\";
        int pos = target.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (pos >= 0)
        {
            foreach (var x in new[] { lnkPath, target })
                foreach (var d in ProfileDirsOnDrive(x))
                    if (Exists(d + "\\" + target[(pos + marker.Length)..])) return true;
        }
        if (pos >= 0 && !SameDrive(lnkPath, target))
            for (int i = 1; i < _profiles.UserNames.Count; i++)
                if (Exists(target[..pos] + "\\" + _profiles.UserNames[i] + "\\" + target[(pos + marker.Length)..])) return true;
        return false;
    }

    private static bool SameDrive(string a, string b) => a.Length >= 2 && b.Length >= 2 && char.ToUpperInvariant(a[0]) == char.ToUpperInvariant(b[0]) && a[1] == ':' && b[1] == ':';

    private readonly Dictionary<char, List<string>> _profileCache = new();

    /// <summary>Profile folders (with ntuser.dat) of a Windows installation on the path's drive.</summary>
    private IReadOnlyList<string> ProfileDirsOnDrive(string path)
    {
        if (path.Length < 2 || path[1] != ':') return Array.Empty<string>();
        var drive = char.ToUpperInvariant(path[0]);
        if (_profileCache.TryGetValue(drive, out var cached)) return cached;
        var list = new List<string>();
        var root = drive + @":\";
        try
        {
            if (Directory.Exists(root + "Windows"))
                foreach (var u in Directory.EnumerateDirectories(root, "U*"))
                    foreach (var d in Directory.EnumerateDirectories(u))
                        if (File.Exists(Path.Combine(d, "ntuser.dat"))) list.Add(d);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _profileCache[drive] = list;
        return list;
    }

    /// <summary>
    /// A file or folder exists (FindFirstFile semantics; bare "name.ext" also via SearchPath). Deliberate
    /// safety deviations, shared with the registry and uninstall scanners (<see cref="PathProbe"/>): access denied and an
    /// unreadable drive count as existing, and a path under System32 or Program Files is retried in its 32-bit twin.
    /// </summary>
    internal static bool Exists(string path)
    {
        var p = path.TrimEnd('\\', ' ');
        if (p.Length == 0) return false;
        if (PathProbe.FindFirst(p)) return true;
        if (p.Contains(@":\", StringComparison.Ordinal)) return PathProbe.ExistsInAnyView(p);
        var name = p[(p.LastIndexOf('\\') + 1)..];
        if (!name.Contains('.')) return false;
        var buf = new char[260];
        return SearchPathW(null, name, null, (uint)buf.Length, buf, IntPtr.Zero) != 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SearchPathW(string? path, string fileName, string? ext, uint bufLen, [Out] char[] buffer, IntPtr filePart);
}
