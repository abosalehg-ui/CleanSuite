using System.Globalization;
using System.Security.Principal;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.Uninstall;

[Flags]
public enum UninstallFlags { None = 0, NoRemove = 0x1, NoModify = 0x2, NoRepair = 0x4, WindowsInstaller = 0x10 }

/// <summary>One installed program from an Uninstall key (the original's "Uninstaller Plus").</summary>
public sealed record UninstallEntry
{
    /// <summary>DisplayName, or the subkey name for nameless entries.</summary>
    public required string Name { get; init; }
    /// <summary>Subkey name (the ProductCode for Windows Installer entries).</summary>
    public required string SubKey { get; init; }
    public required RegHive Hive { get; init; }
    /// <summary>Parent key below the hive ("Software\…\Uninstall").</summary>
    public required string ParentKey { get; init; }
    public UninstallFlags Flags { get; init; }
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string ModifyPath { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string HelpLink { get; init; } = "";
    public string UrlInfoAbout { get; init; } = "";
    public string UrlUpdateInfo { get; init; } = "";
    public string DisplayIcon { get; init; } = "";
    /// <summary>EstimatedSize in KB, when known.</summary>
    public long? SizeKb { get; init; }
    public DateTime? InstalledOn { get; init; }
    /// <summary>The uninstall command is empty or points at a file that does not exist (status bar "Possible Invalids").</summary>
    public bool PossiblyInvalid { get; init; }
    /// <summary>The key is a 32-bit (Wow6432Node) entry.</summary>
    public bool Is32Bit { get; init; }

    public bool IsWindowsInstaller => (Flags & UninstallFlags.WindowsInstaller) != 0;
    public string KeyPath => ParentKey + "\\" + SubKey;
    public string KeyDisplay => RegBackupWriter.HivePath(Hive) + "\\" + KeyPath;

    /// <summary>Which commands the entry offers (NoRemove / NoModify / NoRepair flags and the commands it has).</summary>
    public bool CanUninstall => (Flags & UninstallFlags.NoRemove) == 0 && (IsWindowsInstaller || UninstallString.Length > 0 || QuietUninstallString.Length > 0);
    public bool CanModify => (Flags & UninstallFlags.NoModify) == 0 && (IsWindowsInstaller || ModifyPath.Length > 0);
    public bool CanRepair => IsWindowsInstaller && (Flags & UninstallFlags.NoRepair) == 0;
    /// <summary>The original refuses to remove the entries of Windows Installer products.</summary>
    public bool CanRemoveEntry => !IsWindowsInstaller;

    /// <summary>
    /// Full path of msiexec (a bare "msiexec" would be looked up in the program's own folder first, where a planted copy
    /// could run), quoted.
    /// </summary>
    public static string Msiexec => "\"" + Path.Combine(Environment.SystemDirectory, "msiexec.exe") + "\"";

    /// <summary>
    /// The key name is passed to msiexec only when it is a real product code ({GUID}); a key named e.g.
    /// "x /i http://host/a.msi /qn" would otherwise add its own arguments.
    /// </summary>
    public bool HasProductCode => System.Text.RegularExpressions.Regex.IsMatch(SubKey, @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");

    private bool UsesMsiexec => IsWindowsInstaller && HasProductCode;

    /// <summary>"msiexec /X {code}" for Windows Installer products, else UninstallString (QuietUninstallString if it is empty).</summary>
    public string UninstallCommand => UsesMsiexec ? Msiexec + " /X " + SubKey : UninstallString.Length > 0 ? UninstallString : QuietUninstallString;
    /// <summary>"msiexec /I {code}" for Windows Installer products, else ModifyPath.</summary>
    public string ModifyCommand => UsesMsiexec ? Msiexec + " /I " + SubKey : IsWindowsInstaller ? "" : ModifyPath;
    /// <summary>"msiexec /fv {code}" (Windows Installer only).</summary>
    public string RepairCommand => UsesMsiexec ? Msiexec + " /fv " + SubKey : "";
}

/// <summary>
/// Lists installed programs from the three Uninstall keys, in the original's order:
/// HKLM (64-bit), HKCU, HKLM\Software\Wow6432Node. Keys are opened read-only (the original asks for
/// KEY_ALL_ACCESS, which silently hides HKLM programs when it is not elevated).
/// </summary>
public sealed class UninstallScanner
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string UninstallKey32 = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Subkeys never listed (table Windows components of old systems).</summary>
    public static readonly IReadOnlySet<string> HiddenSubKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Fontcore", "AddressBook", "DirectAnimation", "DirectDrawEx", "DXM_Runtime", "IEData", "IE5BAKEX", "IE4Data", "IE40",
        "MobileOptionPack", "NetMeeting", "OutlookExpress", "PCHealth", "SchedulingAgent", "ICW", "WIC", "MPlayer2",
        "InstallShield Uninstall Information",
    };

    private readonly IRegistryRoots _roots;
    private readonly string? _userSid;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, DriveKind> _driveType;

    /// <param name="userSid">SID used for per-user Windows Installer data (default: the current user).</param>
    public UninstallScanner(IRegistryRoots? roots = null, string? userSid = null, Func<string, bool>? fileExists = null,
        Func<string, DriveKind>? driveType = null)
    {
        _roots = roots ?? WindowsRegistryRoots.Instance;
        _userSid = userSid ?? WindowsIdentity.GetCurrent().User?.Value;
        _fileExists = fileExists ?? PathExists;
        _driveType = driveType ?? PathProbe.DriveType;
    }

    /// <summary>Settings\ShowUpdates: also list entries with ParentKeyName (updates of another program).</summary>
    public bool ShowUpdates { get; init; }

    public IReadOnlyList<UninstallEntry> Scan(CancellationToken ct = default)
    {
        var list = new List<UninstallEntry>();
        var msiCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, parent, is32) in new[]
                 { (RegHive.LocalMachine, UninstallKey, false), (RegHive.CurrentUser, UninstallKey, false), (RegHive.LocalMachine, UninstallKey32, true) })
        {
            RegistryKey? root;
            try { root = _roots.Root(hive).OpenSubKey(parent, writable: false); }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { continue; }
            if (root == null) continue;
            using (root)
                foreach (var sub in SafeSubKeys(root))
                {
                    ct.ThrowIfCancellationRequested();
                    var e = Read(root, hive, parent, sub, is32);
                    if (e == null) continue;
                    if (e.IsWindowsInstaller && !msiCodes.Add(e.SubKey)) continue;          // same product in another view
                    list.Add(e);
                }
        }
        return list;
    }

    private UninstallEntry? Read(RegistryKey parentKey, RegHive hive, string parent, string sub, bool is32)
    {
        if (HiddenSubKeys.Contains(sub)) return null;
        RegistryKey? k;
        try { k = parentKey.OpenSubKey(sub, writable: false); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
        if (k == null) return null;
        using (k)
        {
            if (Dword(k, "SystemComponent") == 1) return null;
            var name = Str(k, "DisplayName");
            var uninst = Str(k, "UninstallString");
            var quiet = Str(k, "QuietUninstallString");
            var modify = Str(k, "ModifyPath");
            if (name.Length == 0 && uninst.Length == 0 && quiet.Length == 0 && modify.Length == 0) return null;
            if (!ShowUpdates && Str(k, "ParentKeyName").Length > 0) return null;
            int noRemove = Dword(k, "NoRemove"), noModify = Dword(k, "NoModify"), noRepair = Dword(k, "NoRepair");
            if (noRemove != 0 && noModify != 0) return null;
            if (uninst.Length == 0 && quiet.Length > 0) return null;                    // quiet-only entries are hidden

            var flags = UninstallFlags.None;
            if (name.Length == 0)
            {
                // Nameless keys are shown under their subkey name only when they hold almost nothing.
                if (Str(k, "").Length != 0 || SafeValueCount(k) > 1 || sub.StartsWith('{')) return null;
                name = sub;
            }
            else if (Dword(k, "WindowsInstaller") != 0) flags |= UninstallFlags.WindowsInstaller;
            if (noRemove != 0) flags |= UninstallFlags.NoRemove;
            if (noModify != 0) flags |= UninstallFlags.NoModify;
            if (noRepair != 0) flags |= UninstallFlags.NoRepair;

            var msi = (flags & UninstallFlags.WindowsInstaller) != 0 ? MsiProperties(sub) : null;
            string Prop(string n) { var v = Str(k, n); return v.Length == 0 && msi != null ? Str(msi, n) : v; }
            long? size = Dword(k, "EstimatedSize", -1) is var kb and >= 0 ? kb
                : msi != null && Dword(msi, "EstimatedSize", -1) is var mkb and >= 0 ? mkb : null;    // the original drops the MSI size; kept here

            var entry = new UninstallEntry
            {
                Name = name, SubKey = sub, Hive = hive, ParentKey = parent, Flags = flags, UninstallString = uninst,
                QuietUninstallString = quiet, ModifyPath = modify, Publisher = Prop("Publisher"), Version = Prop("DisplayVersion"),
                InstallLocation = Prop("InstallLocation"), HelpLink = Prop("HelpLink"), UrlInfoAbout = Prop("URLInfoAbout"),
                UrlUpdateInfo = Prop("URLUpdateInfo"), DisplayIcon = Prop("DisplayIcon"), SizeKb = size,
                InstalledOn = ParseDate(Prop("InstallDate")) ?? DateFromCommand(uninst), Is32Bit = is32,
                PossiblyInvalid = uninst.Length == 0 || !CommandIsValid(uninst),
            };
            msi?.Dispose();
            return entry;
        }
    }

    /// <summary>
    /// InstallProperties of a Windows Installer product: per machine (S-1-5-18) first, then the current user. The
    /// original only reads the current user's path, so machine-wide products show no publisher or date there.
    /// </summary>
    private RegistryKey? MsiProperties(string productCode)
    {
        var packed = PackGuid(productCode);
        if (packed.Length == 0) return null;
        foreach (var sid in new[] { "S-1-5-18", _userSid })
        {
            if (sid == null) continue;
            try
            {
                var k = _roots.Root(RegHive.LocalMachine).OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\{sid}\Products\{packed}\InstallProperties", false);
                if (k != null) return k;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Windows Installer "packed" GUID (groups 1–3 reversed, then each byte's nibbles swapped).</summary>
    public static string PackGuid(string guid)
    {
        var parts = guid.Trim('{', '}').Split('-');
        if (parts.Length != 5) return "";
        var sb = new System.Text.StringBuilder(32);
        for (int i = 0; i < 3; i++) sb.Append(parts[i].Reverse().ToArray());
        var rest = parts[3] + parts[4];
        for (int i = 0; i + 1 < rest.Length; i += 2) sb.Append(rest[i + 1]).Append(rest[i]);
        return sb.Length == 32 ? sb.ToString() : "";
    }

    /// <summary>
    /// "yyyyMMdd"; values longer than 8 characters with a separator are dates already. Some installers
    /// write the date in the user's Hijri calendar ("14470123" on an Arabic system); a year before 1600 is read as an
    /// Umm al-Qura date and converted (the original would show year 1447).
    /// </summary>
    internal static DateTime? ParseDate(string s)
    {
        s = s.Trim();
        if (s.Length > 8 && s.IndexOfAny(['\\', '/', '-']) >= 0)
        {
            var parts = s.Split(['\\', '/', '-', 'T', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int y)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m)
                && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int d) && parts[0].Length == 4)
                return FromParts(y, m, d);
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var any) ? any.Date : null;
        }
        if (s.Length >= 8 && int.TryParse(s[..8], NumberStyles.None, CultureInfo.InvariantCulture, out int v))
            return FromParts(v / 10000, v / 100 % 100, v % 100);
        return null;
    }

    private static DateTime? FromParts(int y, int m, int d)
    {
        try
        {
            if (y < 1600) return new UmAlQuraCalendar().ToDateTime(y, m, d, 0, 0, 0, 0);
            return new DateTime(y, m, d);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Fallback of the original for non-MSI commands: the creation date of the uninstaller file.</summary>
    private DateTime? DateFromCommand(string cmd)
    {
        var s = cmd.ToLowerInvariant();
        if (s.Length == 0 || s.Contains("msiexec", StringComparison.Ordinal)) return null;
        int cut = s.IndexOf('/');
        if (cut >= 0) s = s[..cut];
        int exe = s.IndexOf(".exe", StringComparison.Ordinal);
        if (exe >= 0) s = s[..(exe + 4)];
        s = s.Trim('"', ' ');
        try { return s.Length > 0 && File.Exists(s) ? File.GetCreationTime(s).Date : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // -------------------------------------------------------------- path check

    /// <summary>Valid when the command holds no "X:\" path, or one of its first candidates exists.</summary>
    public bool CommandIsValid(string cmd)
    {
        var u = cmd.ToUpperInvariant();
        bool tried = false;
        int pos = 0;
        for (int i = 0; i < 9; i++)
        {
            int idx = pos <= u.Length ? u.IndexOf(@":\", pos, StringComparison.Ordinal) : -1;
            if (idx < 0) return !tried;
            if (tried && idx < 5) return true;
            if (idx > 0 && u[idx - 1] is >= 'A' and <= 'Z')
            {
                if (FileCheck(u[(idx - 1)..])) return true;
                tried = true;
            }
            pos = idx + 1;
        }
        return true;
    }

    private bool FileCheck(string path)
    {
        var s = path.Trim('"', ' ');
        int cut = s.IndexOfAny(['/', '*', '?', '|', '<', '>', '"']);
        if (cut >= 0) s = s[..cut];
        if (_fileExists(s.TrimEnd())) return true;
        cut = s.IndexOfAny([';', ',', '%', '^']);
        if (cut >= 0) s = s[..cut];
        if (s.Contains("RUNDLL", StringComparison.Ordinal) || s.Contains("SHELL32", StringComparison.Ordinal) || s.Contains("REGSVR", StringComparison.Ordinal))
            return true;
        s = s.Trim('"', '\\', ' ', '\t');
        if (s.Length <= 3 || s[1] != ':') return true;
        switch (_driveType(s[..3]))
        {
            case DriveKind.Removable or DriveKind.Remote or DriveKind.CdRom: return true;
            case DriveKind.Fixed:
                if (_fileExists(s)) return true;
                foreach (var ext in new[] { ".EXE", ".DLL", ".OCX" })
                {
                    int i = s.IndexOf(ext, StringComparison.Ordinal);
                    if (i >= 0) { s = s[..(i + 4)]; break; }
                }
                return _fileExists(s) || _fileExists(s + ".lnk");
            default: return false;
        }
    }

    /// <summary>
    /// File or folder exists, the same rule as the registry and shortcut scanners (<see cref="PathProbe"/>): access denied
    /// and an unreadable drive count as existing, and the 32-bit twins of System32 and Program Files are tried too.
    /// </summary>
    internal static bool PathExists(string path) => PathProbe.ExistsInAnyView(path);

    // -------------------------------------------------------------- registry helpers

    private static string[] SafeSubKeys(RegistryKey k)
    {
        try { return k.GetSubKeyNames(); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    private static int SafeValueCount(RegistryKey k)
    {
        try { return k.ValueCount; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return 0; }
    }

    /// <summary>REG_SZ as is, REG_EXPAND_SZ expanded, other types "".</summary>
    private static string Str(RegistryKey k, string name)
    {
        try { return k.GetValue(name) as string ?? ""; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ""; }
    }

    private static int Dword(RegistryKey k, string name, int def = 0)
    {
        try { return k.GetValue(name) is int v ? v : def; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return def; }
    }
}
