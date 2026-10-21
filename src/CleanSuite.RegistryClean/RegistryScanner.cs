using System.Globalization;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.RegistryClean;

/// <summary>
/// Clean System Registry: finds invalid entries per category. The scan
/// opens keys with read access only (the original asks for KEY_ALL_ACCESS) and never changes anything.
/// </summary>
public sealed partial class RegistryScanner
{
    private readonly IRegistryRoots _roots;
    private readonly RegScanOptions _o;
    private readonly IgnoreWords _words;
    private readonly List<RegFinding> _findings = new();
    private readonly List<RegFinding> _ignored = new();
    private readonly List<string> _unreadable = new();
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private CancellationToken _ct;

    public PathValidator Paths { get; }

    public RegistryScanner(IRegistryRoots roots, RegScanOptions options, PathValidator? paths = null, IgnoreWords? words = null)
    {
        _roots = roots;
        _o = options;
        Paths = paths ?? new PathValidator(new PathValidatorOptions
        {
            IncludeRemovable = options.IncludeRemovable, IncludeCd = options.IncludeCd, Thorough = options.Thorough, Expert = options.Expert,
        }, folders: SystemFolderInfo.Current, knownCommands: null);
        _words = words ?? IgnoreWords.Load(options.IgnoreFolders, options.Thorough, n => !options.DisabledDatLists.Contains(n));
    }

    /// <summary>Runs every selected category in the original's order.</summary>
    public RegScanResult Scan(CancellationToken ct = default)
    {
        _ct = ct;
        _findings.Clear(); _ignored.Clear(); _unreadable.Clear(); _reported.Clear();
        Paths.SetKnownCommands(KnownCommandNames());
        var c = _o.Categories & ~RegScanOptions.Unsupported;
        foreach (var (cat, run) in Steps())
        {
            ct.ThrowIfCancellationRequested();
            if ((c & cat) != 0) run(cat);
        }
        return new RegScanResult { Findings = _findings.ToList(), Ignored = _ignored.ToList(), Unreadable = _unreadable.ToList() };
    }

    /// <summary>The categories in the original's execution order (System Entries runs on a parallel thread there; here in sequence).</summary>
    private IEnumerable<(RegCategory, Action<RegCategory>)> Steps()
    {
        yield return (RegCategory.SystemEntries, ScanSystemEntries);
        yield return (RegCategory.UserEntries, ScanUserEntries);
        yield return (RegCategory.AllUsersEntries, ScanAllUsersEntries);
        yield return (RegCategory.StartMenu, ScanStartMenu);
        yield return (RegCategory.ObsoleteSoftware, ScanObsoleteSoftware);
        yield return (RegCategory.FileExtensions, ScanFileExtensions);
        yield return (RegCategory.FileAssociations, ScanFileAssociations);
        yield return (RegCategory.ActiveXCom, ScanActiveX);
        yield return (RegCategory.Fonts, ScanFonts);
        yield return (RegCategory.HelpFiles, ScanHelpFiles);
        yield return (RegCategory.ApplicationPaths, ScanApplicationPaths);
        yield return (RegCategory.AddRemoveEntries, ScanAddRemove);
        yield return (RegCategory.Sounds, ScanSounds);
        yield return (RegCategory.SharedDlls, ScanSharedDlls);
        yield return (RegCategory.HistoryLists, ScanHistoryLists);
        yield return (RegCategory.SharedFolders, ScanSharedFolders);
    }

    // ---------------------------------------------------------------- reporting

    internal const int InvalidPathId = 0xA172, InvalidEntryId = 0xA173;

    /// <summary>
    /// Reports one finding, applying the ignore words as the original does: words are applied when
    /// <paramref name="applyIgnore"/> is set, except for MRU/History keys when <see cref="RegScanOptions.NoIgnoreForMru"/>
    /// is on; a few areas (<see cref="IgnoreWords.AlwaysChecked"/>) are always checked. A matching finding is dropped
    /// (kept in <see cref="RegScanResult.Ignored"/>), except in expert mode with "Use ignore list" off, where it is listed
    /// but marked (the original shows it unchecked).
    /// </summary>
    internal void Report(RegCategory cat, RegHive hive, string key, string valueName, string data, RegTargetKind target,
        string reason, string reasonText, int reasonId = 0, bool applyIgnore = true, bool verifyKey = false, object? raw = null)
    {
        if (verifyKey && !KeyExists(hive, key)) return;
        var id = $"{(int)hive}|{key}|{(target == RegTargetKind.Key ? "\u0001" : valueName)}";
        if (!_reported.Add(id)) return;                                       // one finding per key/value

        var composite = string.Join('\\', RegBackupWriter.HivePath(hive), key, valueName, data, reasonText).ToUpperInvariant();
        bool apply = applyIgnore;
        if (apply && _o.NoIgnoreForMru)
        {
            var keyU = key.ToUpperInvariant();
            var nameU = valueName.ToUpperInvariant();
            if (IgnoreWords.MruKeys.Any(m => keyU.Contains(m, StringComparison.Ordinal))
                || nameU.Contains("MRU", StringComparison.Ordinal) || nameU.Contains("RECENTFILE", StringComparison.Ordinal))
                apply = false;
        }
        if (!apply && IgnoreWords.AlwaysChecked.Any(m => composite.Contains(m, StringComparison.Ordinal))) apply = true;
        var word = apply ? _words.Match(composite) : null;
        var protect = IgnoreWords.Protective.FirstOrDefault(p => composite.Contains(p, StringComparison.Ordinal));

        var f = new RegFinding
        {
            Category = cat, Hive = hive, Key = key, ValueName = valueName, Data = data, Target = target, Reason = reason,
            ReasonText = reasonText, ReasonId = reasonId, IgnoredBy = word, RawValue = raw,
            KeyStamp = target == RegTargetKind.Key ? CleanSuite.RegistryClean.KeyStamp.Of(_roots.Root(hive), key) : null,
        };
        if (protect != null) _ignored.Add(f with { IgnoredBy = protect });
        else if (word != null && (_o.UseIgnoreList || !_o.Expert)) _ignored.Add(f);
        else _findings.Add(f);
    }

    // ---------------------------------------------------------------- registry helpers (read-only)

    private RegistryKey? Open(RegHive hive, string path)
    {
        try { return path.Length == 0 ? _roots.Root(hive) : _roots.Root(hive).OpenSubKey(path, writable: false); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _unreadable.Add(RegBackupWriter.HivePath(hive) + "\\" + path);
            return null;
        }
    }

    /// <summary>Opens a key; the caller must dispose unless it is a hive root.</summary>
    private sealed class KeyLease(RegistryKey? key, bool owned) : IDisposable
    {
        public RegistryKey? Key { get; } = key;
        public void Dispose() { if (owned) Key?.Dispose(); }
    }

    private KeyLease Lease(RegHive hive, string path) => new(Open(hive, path), path.Length != 0);

    internal bool KeyExists(RegHive hive, string path)
    {
        using var l = Lease(hive, path);
        return l.Key != null;
    }

    private static string[] SubKeys(RegistryKey k)
    {
        try { return k.GetSubKeyNames(); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    private static string[] ValueNames(RegistryKey k)
    {
        try { return k.GetValueNames(); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "\\" + b;

    /// <summary>A string value (REG_SZ / REG_EXPAND_SZ, unexpanded), or null.</summary>
    internal string? ReadString(RegHive hive, string key, string value)
    {
        using var l = Lease(hive, key);
        return l.Key == null ? null : ReadString(l.Key, value);
    }

    private static string? ReadString(RegistryKey k, string value)
    {
        try { return k.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>"%lu" of a DWORD; a DWORD with a wrong data length (returned as bytes) shows its first four bytes, as the original's buffer does.</summary>
    internal static string DwordText(object raw) => raw switch
    {
        int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
        byte[] b => BitConverter.ToUInt32([.. b.Take(4), .. new byte[Math.Max(0, 4 - b.Length)]]).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Expand(string s) => Environment.ExpandEnvironmentVariables(s);

    private static string Unexpanded(RegistryKey k, string name) => ReadString(k, name) ?? "";

    // ---------------------------------------------------------------- generic scanner

    /// <summary>Data longer than the original's 0x40E-byte buffer, or names longer than 0x205 characters, are skipped.</summary>
    private const int MaxDataBytes = 0x40E, MaxNameChars = 0x205, MaxExpandedChars = 0x206;

    /// <summary>
    /// Recursive walk below <paramref name="start"/>: subkeys first, then the values of the key. With
    /// <paramref name="skipRoots"/> the class roots and Perflib are skipped; "MMC\SnapIns" is skipped anywhere, as are
    /// "VirtualStore" keys and Office ClickToRun.
    /// </summary>
    internal void GenericScan(RegCategory cat, RegHive hive, string start, bool skipRoots, bool expand)
    {
        void Walk(RegistryKey key, string full, string name)
        {
            _ct.ThrowIfCancellationRequested();
            if (skipRoots && full.Length > 0 && (full.Equals(@"SOFTWARE\CLASSES", StringComparison.OrdinalIgnoreCase)
                || full.Equals(@"SOFTWARE\Wow6432Node\CLASSES", StringComparison.OrdinalIgnoreCase)
                || full.Equals(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib", StringComparison.OrdinalIgnoreCase)))
                return;
            if (full.Contains(@"MMC\SnapIns", StringComparison.Ordinal)) return;
            if (name.Equals("VirtualStore", StringComparison.OrdinalIgnoreCase)
                || full.Equals(@"SOFTWARE\Microsoft\Office\ClickToRun", StringComparison.OrdinalIgnoreCase)) return;

            foreach (var sub in SubKeys(key))
            {
                RegistryKey? child;
                try { child = key.OpenSubKey(sub, writable: false); }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    _unreadable.Add(RegBackupWriter.HivePath(hive) + "\\" + Join(full, sub));
                    continue;
                }
                if (child == null) continue;
                using (child) Walk(child, Join(full, sub), sub);
            }
            ScanValues(cat, hive, key, full, expand);
        }

        using var l = Lease(hive, start);
        if (l.Key != null) Walk(l.Key, start, start[(start.LastIndexOf('\\') + 1)..]);
    }

    /// <summary>REG_SZ and (when expanding) REG_EXPAND_SZ data are checked for paths; Thorough also checks value names.</summary>
    private void ScanValues(RegCategory cat, RegHive hive, RegistryKey key, string full, bool expand)
    {
        foreach (var name in ValueNames(key))
        {
            if (name.Length > MaxNameChars) continue;
            RegistryValueKind kind;
            object? raw;
            try
            {
                kind = key.GetValueKind(name);
                raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { continue; }
            if (raw == null) continue;

            string data;
            int byteLength;
            switch (kind)
            {
                case RegistryValueKind.String:
                    if (raw is not string str) continue;                         // malformed data
                    data = str;
                    byteLength = (data.Length + 1) * 2;
                    if (byteLength > MaxDataBytes) continue;
                    break;
                case RegistryValueKind.ExpandString:
                    if (!expand) continue;
                    if (raw is not string s) continue;
                    byteLength = (s.Length + 1) * 2;
                    if (byteLength > MaxDataBytes) continue;
                    data = Expand(s);
                    if (data.Length > MaxExpandedChars) continue;
                    break;
                case RegistryValueKind.DWord:
                    data = DwordText(raw);
                    byteLength = 4;
                    break;
                case RegistryValueKind.Binary:
                    if (name.StartsWith("HRZR_", StringComparison.OrdinalIgnoreCase)) return;   // UserAssist: stop this key
                    data = "<BINARY>";
                    byteLength = raw is byte[] bin ? bin.Length : 0;
                    if (byteLength > MaxDataBytes) continue;
                    break;
                default:
                    continue;                                                  // multi-string, QWORD, none…
            }

            bool flagged = false;
            if (byteLength != 0 && kind is RegistryValueKind.String or RegistryValueKind.ExpandString && !Paths.DataIsValid(data))
            {
                Report(cat, hive, full, name, data, RegTargetKind.Value, "InvalidPath", "Invalid Path: %1", InvalidPathId, raw: raw);
                flagged = true;
            }
            if (_o.Thorough && !flagged && !Paths.DataIsValid(name))
                Report(cat, hive, full, name, data, RegTargetKind.Value, "InvalidEntry", "Invalid Entry: %2", InvalidEntryId, raw: raw);
        }
    }

    /// <summary>
    /// Standard mode only (Thorough already checks names): value names that are paths
    /// (MUICache, AppCompatFlags) must exist.
    /// </summary>
    internal void ScanValueNamesAsPaths(RegCategory cat, RegHive hive, string key)
    {
        if (_o.Thorough) return;
        using var l = Lease(hive, key);
        if (l.Key == null) return;
        bool mui = key.Contains("MuiCache", StringComparison.Ordinal);
        foreach (var name in ValueNames(l.Key))
        {
            if (name.Length > MaxNameChars) continue;
            var path = name.Trim('"');
            if (mui) path = path.Replace(".ApplicationCompany", "", StringComparison.Ordinal).Replace(".FriendlyAppName", "", StringComparison.Ordinal);
            if (!Paths.DataIsValid(path))
                Report(cat, hive, key, name, "", RegTargetKind.Value, "InvalidEntry", "Invalid Entry: %2", InvalidEntryId, raw: RawOf(l.Key, name));
        }
    }

    private static object? RawOf(RegistryKey k, string name)
    {
        try { return k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
