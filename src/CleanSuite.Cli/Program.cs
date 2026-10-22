using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CleanSuite.Compat;
using CleanSuite.Junk;
using CleanSuite.RegistryClean;
using CleanSuite.Safety;
using CleanSuite.Shortcuts;

// CleanSuite command line. Read-only by design: nothing here deletes or modifies files or the registry.
return Cli.Run(args);

static class Cli
{
    // Exit codes (also in README): 0 done, 1 unknown command (usage printed), 2 invalid arguments,
    // 3 plug-in entries that could not be decoded, 4 a file or folder could not be read or written.
    public const int Ok = 0, UnknownCommand = 1, BadArguments = 2, DecodeFailures = 3, IoError = 4;

    // The options each command accepts: those that take a value, then plain switches. Anything else is refused.
    private static readonly string[] IgnoreValues = ["--ignore-dir"], IgnoreFlags = ["--no-default-ignore", "--import-ace"];
    private static readonly Dictionary<string, (string[] Values, string[] Flags)> Options = new()
    {
        ["junk scan"] = (["--root", "--temp", "--json", .. IgnoreValues], ["--system-temp", "--expert", "--temp-all-ignores", .. IgnoreFlags]),
        ["plugins decode"] = (["--ini", "--out"], []),
        ["shortcuts scan"] = (["--root", "--drives", .. IgnoreValues], ["--empty-folders", "--include-removable", .. IgnoreFlags]),
        ["uninstall list"] = (["--report"], ["--updates", "--invalid", "--tab"]),
        ["registry scan"] = (["--category", "--json", .. IgnoreValues], ["--thorough", "--include-removable", "--include-cd", "--show-ignored", .. IgnoreFlags]),
        ["duplicates scan"] = (["--root", "--content", "--min-size", "--profile", "--json", .. IgnoreValues], ["--no-name", "--date", .. IgnoreFlags]),
    };

    public static int Run(string[] args)
    {
        try
        {
            return args switch
            {
                ["--help" or "-h" or "/?" or "help"] => Usage(Ok),
                ["junk", "scan", .. var rest] => JunkScan(Checked("junk scan", rest)),
                ["junk", "settings"] => JunkSettings(),
                ["junk", "backup", "list", var zip] => BackupList(zip),
                ["plugins", "decode", .. var rest] => PluginsDecode(Checked("plugins decode", rest)),
                ["shortcuts", "scan", .. var rest] => ShortcutsScan(Checked("shortcuts scan", rest)),
                ["uninstall", "list", .. var rest] => UninstallList(Checked("uninstall list", rest)),
                ["registry", "scan", .. var rest] => RegistryScan(Checked("registry scan", rest)),
                ["duplicates", "scan", .. var rest] => DuplicatesScan(Checked("duplicates scan", rest)),
                _ => Usage(UnknownCommand),
            };
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or RegexMatchTimeoutException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return BadArguments;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return IoError;
        }
    }

    /// <summary>Refuses an option the command does not know, and a value option given without its value.</summary>
    internal static string[] Checked(string command, string[] a)
    {
        var (values, flags) = Options[command];
        for (int i = 0; i < a.Length; i++)
        {
            if (values.Contains(a[i]))
            {
                if (i + 1 >= a.Length || values.Contains(a[i + 1]) || flags.Contains(a[i + 1]))
                    throw new ArgumentException($"{a[i]} needs a value");
                i++;
            }
            else if (!flags.Contains(a[i]))
                throw new ArgumentException(a[i].StartsWith('-') ? $"unknown option for '{command}': {a[i]}" : $"unexpected argument for '{command}': {a[i]}");
        }
        return a;
    }

    private static int Usage(int exitCode)
    {
        var w = exitCode == Ok ? Console.Out : Console.Error;
        w.WriteLine("""
            CleanSuite command line (read-only)

              junk, shortcuts, registry and duplicates scans apply the same ignore lists as their tabs (the original
              program's Data\Ignore_* folders when it is installed, and CleanSuite's own Ignore_Junk and Ignore_Reg in
              %LocalAppData%\CleanSuite); --no-default-ignore turns that off, --ignore-dir adds folders. The registry
              scan's built-in protection of Windows areas (servicing, installers, packages, services) always applies.

              junk scan --root <path> [--root <path>...] [--temp <path>...] [--system-temp]
                        [--import-ace] [--ignore-dir <dir>] [--expert] [--temp-all-ignores] [--json <file>]
                  Lists junk files under the given roots. Never deletes anything.
                  --temp-all-ignores  apply every ignore-list line inside Temp folders, not only "/T" lines
                  --import-ace  use settings read (read-only) from HKCU\Software\Acelogix\Ace Utilities
                  --system-temp also scan the system temp folders (otherwise only --temp paths)

              junk settings
                  Shows the junk settings imported from the original program.

              junk backup list <zip>
                  Lists a CleanSuite or Ace Utilities junk backup and where each file would be restored.

              plugins decode --ini <applications.ini> --out <file.json>
                  Converts the original plug-in database to open JSON.

              uninstall list [--updates] [--invalid] [--report <file.txt>] [--tab]
                  Lists installed programs (HKLM, HKCU, HKLM 32-bit Uninstall keys). Read-only.
                  --invalid  only entries whose uninstaller is missing ("possible invalid")

              shortcuts scan [--root <path>...] [--drives C:,D:] [--empty-folders] [--include-removable]
                             [--import-ace] [--ignore-dir <dir>]
                  Lists shortcuts whose target is missing (default: Start menus, desktops, AppData, Recent).
                  --drives      scan whole drives instead of the Start menu and desktop folders

              registry scan [--category <hex mask>] [--thorough] [--include-removable] [--include-cd]
                            [--import-ace] [--ignore-dir <dir>] [--show-ignored] [--json <file>]
                  Lists invalid registry entries (Clean System Registry). Opens keys read-only; changes nothing.
                  --import-ace  settings and Data\Ignore_Reg of the original program, read only

              duplicates scan --root <path> [--root ...] [--content md5|crc|none] [--no-name] [--date]
                              [--min-size <bytes>] [--profile <name>] [--import-ace] [--ignore-dir <dir>] [--json <file>]
                  Lists duplicate files (Find Duplicate Files). Read-only; nothing is removed.

              Exit codes: 0 done, 1 unknown command, 2 invalid arguments, 3 plug-in entries not decoded,
                          4 a file or folder could not be read or written.
            """);
        return exitCode;
    }

    private static List<string> Values(string[] a, string name)
    {
        var r = new List<string>();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) r.Add(a[i + 1]);
        return r;
    }

    private static bool Flag(string[] a, string name) => a.Contains(name);

    /// <summary>The tab's default ignore folders (unless --no-default-ignore), then every --ignore-dir.</summary>
    private static List<string> IgnoreFolders(string[] a, IEnumerable<string> defaults) =>
        (Flag(a, "--no-default-ignore") ? [] : defaults).Concat(Values(a, "--ignore-dir")).ToList();

    private static IEnumerable<string> OneFolder(string? folder) => folder != null ? [folder] : [];

    private static int JunkSettings()
    {
        var s = AceJunkSettings.Load(new AceRegistryReader());
        Console.WriteLine($"found in registry      : {s.FoundInRegistry}");
        Console.WriteLine($"SettingsJnk            : 0x{s.SettingsJnk:X} -> {Describe(s.ToOptions())}");
        Console.WriteLine($"ExcludeModifiedLately  : {s.ExcludeFilesModifiedLately}");
        Console.WriteLine($"Removal (DelJunkToBin) : {s.DelJunkToBin}");
        Console.WriteLine($"Drives (DriveJunkFiles): {(s.DriveJunkFiles is uint m ? string.Join(" ", AceJunkSettings.DrivesFromMask(m)) : "(default: fixed drives)")}");
        Console.WriteLine($"Active profile         : {s.ActiveProfile} ({s.ToPatternSet().Patterns.Count} patterns)");
        Console.WriteLine($"Exclusions             : {s.Exclusions.Count}");
        return 0;
    }

    private static int BackupList(string zip)
    {
        if (!File.Exists(zip)) throw new ArgumentException($"file not found: {zip}");
        IReadOnlyList<JunkBackupArchive.BackupEntry> entries;
        try { entries = JunkBackupArchive.List(zip); }
        catch (InvalidDataException e) { throw new ArgumentException(e.Message); }
        Console.WriteLine($"format: {(JunkBackupArchive.IsLegacyBackup(zip) ? "Ace Utilities (legacy)" : "CleanSuite")}");
        foreach (var e in entries)
            Console.WriteLine($"{e.Size,12:N0}  {e.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}  " +
                              (e.OriginalPath ?? $"[not restorable: {e.Problem}] {e.EntryName}"));
        Console.WriteLine($"files: {entries.Count}, restorable: {entries.Count(e => e.OriginalPath != null)}");
        return 0;
    }

    private static JunkOptions WithAllIgnoreListsInTemp(JunkOptions o, bool on) => new()
    {
        CleanTempFolders = o.CleanTempFolders, FindZeroLength = o.FindZeroLength, UseIgnoreList = o.UseIgnoreList,
        DeleteEmptyTempFolders = o.DeleteEmptyTempFolders, ExcludeModifiedWithinWeek = o.ExcludeModifiedWithinWeek,
        ExpertMode = o.ExpertMode, ApplyAllIgnoreListsInTemp = on,
    };

    private static string Describe(JunkOptions o) =>
        $"temp={o.CleanTempFolders} zero={o.FindZeroLength} ignore={o.UseIgnoreList} emptyTemp={o.DeleteEmptyTempFolders}";

    private static int JunkScan(string[] a)
    {
        var roots = Values(a, "--root");
        if (roots.Count == 0) throw new ArgumentException("at least one --root is required (the command line never scans a drive it was not given)");
        foreach (var r in roots)
            if (!Directory.Exists(r.Length == 2 && r[1] == ':' ? r + "\\" : r)) throw new ArgumentException($"root not found: {r}");

        AceJunkSettings settings = Flag(a, "--import-ace") ? AceJunkSettings.Load(new AceRegistryReader()) : new AceJunkSettings();
        var temps = new List<string>(Values(a, "--temp"));
        if (Flag(a, "--system-temp")) temps.AddRange(TempFolderLocator.Locate(new WindowsEnvironment()));

        var req = new JunkScanRequest
        {
            Roots = roots,
            Patterns = settings.ToPatternSet(),
            Exclusions = settings.Exclusions,
            Ignore = IgnoreList.LoadFolders(IgnoreFolders(a, AceInstallation.DefaultJunkIgnoreFolders())),
            Options = WithAllIgnoreListsInTemp(settings.ToOptions(Flag(a, "--expert")), Flag(a, "--temp-all-ignores")),
            TempFolders = temps,
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = new JunkScanner().Scan(req);
        sw.Stop();

        foreach (var i in res.Items)
            Console.WriteLine($"[{i.Group,4}] {i.SizeKb,8} KB  0x{(int)i.Flags:X3}  {i.Category,-6} {i.FullPath}");
        Console.WriteLine($"Files searched: {res.FilesSearched}, junk: {res.Items.Count} ({res.TotalSize:N0} bytes), ignored: {res.IgnoredCount}, locked: {res.SkippedLocked.Count}, time: {sw.ElapsedMilliseconds} ms");

        var json = Values(a, "--json").FirstOrDefault();
        if (json != null)
            File.WriteAllText(json, JsonSerializer.Serialize(res, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }

    private static int UninstallList(string[] a)
    {
        var all = new CleanSuite.Uninstall.UninstallScanner { ShowUpdates = Flag(a, "--updates") }.Scan();
        var list = Flag(a, "--invalid") ? all.Where(e => e.PossiblyInvalid).ToList() : all.ToList();
        foreach (var e in list)
            Console.WriteLine($"{(e.PossiblyInvalid ? "!" : " ")} {e.Name,-50} {e.Publisher,-30} {e.Version,-16} " +
                              $"{e.InstalledOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "",-10} " +
                              $"{(e.SizeKb is { } kb ? CleanSuite.Uninstall.UninstallManager.FormatSize(kb) : ""),9}  {e.KeyDisplay}");
        if (Values(a, "--report").FirstOrDefault() is { } report)
            CleanSuite.Uninstall.UninstallManager.WriteReport(report, list, Flag(a, "--tab"), ["Application Name", "Publisher", "Installed On", "Size", "Version"]);
        Console.WriteLine($"Total: {all.Count}; possible invalids: {all.Count(e => e.PossiblyInvalid)}. Nothing was changed.");
        return 0;
    }

    private static int ShortcutsScan(string[] a)
    {
        var o = (Flag(a, "--import-ace") ? AceShortcutSettings.Read() : null) ?? new ShortcutScanOptions();
        var roots = Values(a, "--root");
        var drives = Values(a, "--drives").SelectMany(d => d.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
        o = o with
        {
            StartMenuMode = drives.Count == 0 && o.StartMenuMode,
            Drives = drives.Count > 0 ? drives : o.Drives,
            EmptyStartMenuFolders = o.EmptyStartMenuFolders || Flag(a, "--empty-folders"),
            IncludeRemovable = Flag(a, "--include-removable"),
            IgnoreFolders = o.IgnoreFolders.Concat(IgnoreFolders(a, AceShortcutSettings.Read()?.IgnoreFolders ?? [])).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Roots = roots.Count > 0 ? roots.Select(r => (r, Flag(a, "--empty-folders"))).ToList() : null,
        };
        var scanner = new ShortcutScanner(o);
        var items = scanner.Scan();
        foreach (var it in items)
            Console.WriteLine($"{(it.Kind == ShortcutItemKind.EmptyFolder ? "[empty folder]" : it.Reason.ToString()),-20} {it.FullPath}  ->  {it.Target}");
        Console.WriteLine($"{items.Count} item(s); {scanner.ShortcutsScanned} shortcut(s) in {scanner.FilesScanned} file(s) scanned. Nothing was changed.");
        return 0;
    }

    private static int RegistryScan(string[] a)
    {
        var o = Flag(a, "--import-ace")
            ? AceRegistrySettings.Read().ToOptions(AceRegistrySettings.IgnoreFolder(AceInstallation.Locate()))
            : new RegScanOptions();
        var mask = Values(a, "--category").FirstOrDefault();
        o = o with
        {
            Categories = mask != null ? (RegCategory)ParseMask(mask) : o.Categories,
            Thorough = o.Thorough || Flag(a, "--thorough"),
            IncludeRemovable = o.IncludeRemovable || Flag(a, "--include-removable"),
            IncludeCd = o.IncludeCd || Flag(a, "--include-cd"),
            IgnoreFolders = o.IgnoreFolders.Concat(IgnoreFolders(a, IgnoreWords.DefaultFolders(AceRegistrySettings.IgnoreFolder(AceInstallation.Locate()))))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
        var res = new RegistryScanner(CleanSuite.Safety.WindowsRegistryRoots.Instance, o).Scan();
        foreach (var f in res.Findings)
            Console.WriteLine($"{f.Category,-18} {f.Reason,-26} {f.KeyDisplay}{(f.Target == RegTargetKind.Value ? " :: " + (f.ValueName.Length == 0 ? "(default)" : f.ValueName) : "")}  [{f.Data}]");
        if (Flag(a, "--show-ignored"))
            foreach (var f in res.Ignored) Console.WriteLine($"(ignored: {f.IgnoredBy}) {f.KeyDisplay} :: {f.ValueName}");
        Console.WriteLine($"{res.Findings.Count} problem(s); {res.Ignored.Count} entr(y/ies) with ignore words; {res.Unreadable.Count} key(s) not readable. Nothing was changed.");
        if (Values(a, "--json").FirstOrDefault() is { } json)
            File.WriteAllText(json, JsonSerializer.Serialize(res, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }

    private static int DuplicatesScan(string[] a)
    {
        var o = Flag(a, "--import-ace")
            ? CleanSuite.Duplicates.AceDupSettings.Read().ToOptions(CleanSuite.Duplicates.AceDupSettings.IgnoreFolder(AceInstallation.Locate()))
            : new CleanSuite.Duplicates.DupOptions();
        var roots = Values(a, "--root");
        var content = Values(a, "--content").FirstOrDefault()?.ToLowerInvariant();
        var min = Values(a, "--min-size").FirstOrDefault();
        var profile = Values(a, "--profile").FirstOrDefault();
        if (content is not (null or "md5" or "crc" or "none")) throw new ArgumentException($"--content must be md5, crc or none, not '{content}'");
        if (profile != null && !CleanSuite.Duplicates.DupProfiles.BuiltIn.ContainsKey(profile))
            throw new ArgumentException($"unknown --profile '{profile}'; known: {string.Join(", ", CleanSuite.Duplicates.DupProfiles.BuiltIn.Keys)}");
        o = o with
        {
            Roots = roots.Count > 0 ? roots : o.Roots,
            SameName = !Flag(a, "--no-name") && o.SameName,
            SameDate = o.SameDate || Flag(a, "--date"),
            Content = content switch
            {
                "md5" => CleanSuite.Duplicates.ContentCompare.Md5,
                "crc" => CleanSuite.Duplicates.ContentCompare.Crc32,
                "none" => CleanSuite.Duplicates.ContentCompare.None,
                _ => o.Content,
            },
            MinSize = min != null ? ParseSize(min) : o.MinSize,
            Patterns = profile != null && CleanSuite.Duplicates.DupProfiles.BuiltIn.TryGetValue(profile, out var p) ? p : o.Patterns,
            Ignore = o.Ignore.Concat(IgnoreFolders(a, OneFolder(CleanSuite.Duplicates.AceDupSettings.IgnoreFolder(AceInstallation.Locate())))
                .SelectMany(CleanSuite.Duplicates.IgnoreFile.Load)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
        if (o.Roots.Count == 0) throw new ArgumentException("--root is required (or --import-ace with scan paths)");
        var res = new CleanSuite.Duplicates.DuplicateScanner(o).Scan();
        foreach (var g in res.Groups)
        {
            Console.WriteLine($"Group {g.Number}: {g.Files.Count} files, {g.TotalSize:N0} bytes ({g.Reclaimable:N0} reclaimable)");
            foreach (var f in g.Files) Console.WriteLine($"    {f.FullPath}");
        }
        Console.WriteLine($"{res.Groups.Count} group(s) in {res.FilesSearched} file(s) searched; content {(res.ContentVerified ? "compared" : "NOT compared")}; " +
                          $"{res.Unreadable.Count} unreadable. Nothing was changed.");
        if (Values(a, "--json").FirstOrDefault() is { } json)
            File.WriteAllText(json, JsonSerializer.Serialize(res, new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }

    /// <summary>A hexadecimal category mask, with or without "0x".</summary>
    internal static uint ParseMask(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var m)
            ? m : throw new ArgumentException($"--category must be a hexadecimal mask such as 0x1FF, not '{text}'");
    }

    internal static long ParseSize(string text) =>
        long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : throw new ArgumentException($"--min-size must be a whole number of bytes, not '{text}'");

    private static int PluginsDecode(string[] a)
    {
        var ini = Values(a, "--ini").FirstOrDefault() ?? throw new ArgumentException("--ini is required");
        var outFile = Values(a, "--out").FirstOrDefault() ?? throw new ArgumentException("--out is required");
        var text = File.ReadAllText(ini, System.Text.Encoding.Latin1);
        var db = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        int failures = 0;
        // A time limit: a crafted file must not keep the pattern busy forever.
        foreach (Match m in Regex.Matches(text, @"(?m)^\[([^\]]+)\]\r?\n((?:(?!^\[).*\r?\n?)*)", RegexOptions.None, TimeSpan.FromSeconds(10)))
        {
            var section = m.Groups[1].Value;
            if (section.StartsWith("_General_", StringComparison.Ordinal)) continue;
            var entry = new Dictionary<string, string>();
            foreach (var line in m.Groups[2].Value.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();
                if (LegacyPluginDecoder.TryGetIndex(k, out var idx) && v.Length > 0 && v.Length % 16 == 0 && Regex.IsMatch(v, "^[0-9A-F]+$"))
                {
                    try { v = LegacyPluginDecoder.Decode(v, section, idx); }
                    catch (FormatException) { failures++; }
                }
                entry[k] = v;
            }
            db[section] = entry;
        }
        File.WriteAllText(outFile, JsonSerializer.Serialize(db, new JsonSerializerOptions
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine($"plug-ins: {db.Count}, decode failures: {failures} -> {outFile}");
        return failures == 0 ? Ok : DecodeFailures;
    }
}
