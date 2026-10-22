using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.Duplicates;

/// <summary>
/// The original's Find Duplicate Files settings,
/// read with read access only from HKCU\Software\Acelogix\Ace Utilities\Dup. Nothing is ever written there.
/// </summary>
public sealed class AceDupSettings
{
    public const string Root = @"Software\Acelogix\Ace Utilities\Dup";

    public uint SettingsDup { get; init; } = 0x2B;
    public uint MatchCrc { get; init; } = 2;
    public uint IgnoreSmallSize { get; init; } = 1;
    /// <summary>mb_or_kb: 1 = KB, anything else = MB.</summary>
    public uint MbOrKb { get; init; }
    public IReadOnlyList<string> ScanPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>? Excludes { get; init; }
    public string Profile { get; init; } = "Default";
    public IReadOnlyDictionary<string, string> Profiles { get; init; } = new Dictionary<string, string>();
    /// <summary>DelDupToBin: 2 = zip backup, anything else = Recycle Bin (the original never deletes permanently).</summary>
    public uint RemovalMode { get; init; } = 1;
    public string BackupFolder { get; init; } = "";
    public bool FoundInRegistry { get; init; }

    public static AceDupSettings Read(IRegistryRoots? roots = null)
    {
        var hkcu = (roots ?? WindowsRegistryRoots.Instance).Root(RegHive.CurrentUser);
        try
        {
            using var d = hkcu.OpenSubKey(Root, writable: false);
            if (d == null) return new AceDupSettings();
            uint U(string n, uint def) => d.GetValue(n) is int i ? unchecked((uint)i) : def;

            var paths = new List<string>();
            using (var k = d.OpenSubKey("DupScanPaths", writable: false))
                if (k != null)
                    foreach (var n in k.GetValueNames())
                        if (n.EndsWith('\\') && k.GetValue(n) is int v && v != 0) paths.Add(n.TrimEnd('\\'));
            List<string>? excludes = null;
            using (var k = d.OpenSubKey("ExcludeDup", writable: false))
                if (k != null)
                {
                    excludes = new List<string>();
                    foreach (var n in k.GetValueNames())
                        if (n.Length > 0 && !n.StartsWith('\\') && k.GetValue(n) is int v && v != 0) excludes.Add(n);
                }
            var profiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string profile = "Default";
            using (var k = d.OpenSubKey("Profiles", writable: false))
                if (k != null)
                    foreach (var n in k.GetValueNames())
                    {
                        if (k.GetValue(n) is not string s) continue;
                        if (n.Length == 0) profile = s.Length > 0 ? s : "Default";
                        else profiles[n] = s;
                    }
            return new AceDupSettings
            {
                SettingsDup = U("SettingsDup", 0x2B), MatchCrc = U("matchcrc", 2), IgnoreSmallSize = Math.Max(1, U("ignoresmallfiles_size", 1)),
                MbOrKb = U("mb_or_kb", 0), ScanPaths = paths, Excludes = excludes, Profile = profile, Profiles = profiles,
                RemovalMode = U("DelDupToBin", 1), BackupFolder = d.GetValue("dupbakFolder") as string ?? "", FoundInRegistry = true,
            };
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new AceDupSettings();
        }
    }

    /// <summary>Patterns of the selected profile: a built-in category or a stored "|" list (unknown names fall back to "Default").</summary>
    public IReadOnlyList<string> ProfilePatterns() =>
        Profiles.TryGetValue(Profile, out var list) && list.Length > 0 ? DupProfiles.Parse(list)
        : DupProfiles.BuiltIn.TryGetValue(Profile, out var b) ? b : DupProfiles.Default;

    /// <summary>Scan options from these settings (with the normalisation of the original).</summary>
    public DupOptions ToOptions(string? ignoreFolder = null) => new DupOptions
    {
        Roots = ScanPaths,
        SameName = (SettingsDup & 1) != 0,
        SameSize = (SettingsDup & 2) != 0,
        SameDate = (SettingsDup & 4) != 0,
        MinSize = (SettingsDup & 8) != 0 ? (long)IgnoreSmallSize << (MbOrKb == 1 ? 10 : 20) : 0,
        Content = (SettingsDup & 0x20) == 0 ? ContentCompare.None : MatchCrc == 1 ? ContentCompare.Crc32 : ContentCompare.Md5,
        Patterns = ProfilePatterns(),
        Excludes = Excludes ?? DupProfiles.DefaultExcludes,
        Ignore = ignoreFolder != null ? IgnoreFile.Load(ignoreFolder) : Array.Empty<string>(),
    }.Normalized();

    public static string? IgnoreFolder(string? installDir)
    {
        if (installDir == null) return null;
        var p = Path.Combine(installDir, "Data", "Ignore_Dup");
        return Directory.Exists(p) ? p : null;
    }
}

/// <summary>
/// Data\Ignore_Dup: every file of the folder, one file-name pattern per line, "//" starts a comment. Read
/// with the same decoder as the junk ignore lists (UTF-16 or UTF-8 with a byte order mark, else the system code page), so
/// Arabic file names in an ignore list match.
/// </summary>
public static class IgnoreFile
{
    public static IReadOnlyList<string> Load(string folder)
    {
        var list = new List<string>();
        if (!Directory.Exists(folder)) return list;
        foreach (var f in Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            List<string> lines;
            try { lines = TextFiles.ReadLines(f).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var raw in lines)
            {
                var l = raw.Trim().ToUpperInvariant();
                int c = l.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0) l = l[..c];
                l = l.Trim();
                if (l.Length > 0) list.Add(l);
            }
        }
        return list;
    }
}
