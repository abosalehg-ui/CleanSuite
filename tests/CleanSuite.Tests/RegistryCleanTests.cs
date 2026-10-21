using CleanSuite.RegistryClean;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.Tests;

/// <summary>Clean System Registry: path judgement, ignore words, scanners and cleaning, on a private hive file and temp files only.</summary>
public partial class RegistryCleanTests
{
    /// <summary>The real file system, with chosen drive letters reported as removable/CD.</summary>
    private sealed class Probe(Dictionary<char, DriveKind>? drives = null) : IFileProbe
    {
        public bool FindFirst(string path) => WindowsFileProbe.Instance.FindFirst(path);
        public bool HasAttributes(string path) => WindowsFileProbe.Instance.HasAttributes(path);
        public bool SearchPath(string fileName) => WindowsFileProbe.Instance.SearchPath(fileName);
        public DriveKind DriveType(string root) =>
            drives != null && drives.TryGetValue(char.ToUpperInvariant(root[0]), out var k) ? k : WindowsFileProbe.Instance.DriveType(root);
    }

    private sealed class Env : IDisposable
    {
        public Sandbox Sb { get; } = new();
        public AppHiveRegistryRoots Roots { get; }
        public Env() => Roots = new AppHiveRegistryRoots(Path.Combine(Sb.Root, "test.hiv"));
        public RegistryKey Root(RegHive h) => Roots.Root(h);
        public static readonly Guid KnownFolderId = new("6D809377-6AF0-444B-8957-A3773F02200E");
        public string Existing => Sb.File(@"app\prog.exe");
        public string Missing => Path.Combine(Sb.Root, @"gone\prog.exe");

        public RegScanResult Scan(RegCategory cats, RegScanOptions? o = null, IgnoreWords? words = null)
        {
            var opts = (o ?? new RegScanOptions()) with { Categories = cats };
            var pv = new PathValidator(new PathValidatorOptions { IncludeRemovable = opts.IncludeRemovable, IncludeCd = opts.IncludeCd, Thorough = opts.Thorough },
                new Probe());
            return new RegistryScanner(Roots, opts, pv, words ?? IgnoreWords.Load(Array.Empty<string>(), opts.Thorough))
            {
                SpecialFolder = f => f switch
                {
                    Environment.SpecialFolder.Programs => Path.Combine(Sb.Root, "Programs"),
                    Environment.SpecialFolder.CommonPrograms => Path.Combine(Sb.Root, "CommonPrograms"),
                    Environment.SpecialFolder.Fonts => Directory.CreateDirectory(Path.Combine(Sb.Root, "Fonts")).FullName,
                    _ => "",
                },
                WindowsFolder = Directory.CreateDirectory(Path.Combine(Sb.Root, "Windows")).FullName,
                KnownFolderPath = id => id == KnownFolderId ? Path.Combine(Sb.Root, "Known") : null,
            }.Scan();
        }

        public RegistryCleaner Cleaner() => new(Roots, Path.Combine(Sb.Root, "_journal"), Path.Combine(Sb.Root, "_bak"));

        public void Dispose()
        {
            Roots.Dispose();
            Sb.Dispose();
        }
    }

    private static PathValidator Validator(PathValidatorOptions? o = null, Dictionary<char, DriveKind>? drives = null) =>
        new(o ?? new PathValidatorOptions(), new Probe(drives));

    [Fact]
    public void Text_without_drive_paths_is_valid_and_missing_paths_are_invalid()
    {
        using var sb = new Sandbox();
        var file = sb.File(@"x\tool.exe");
        var v = Validator();
        Assert.True(v.DataIsValid("just text, no path"));
        Assert.True(v.DataIsValid(file));
        Assert.True(v.DataIsValid("\"" + file + "\" /arg %1"));
        Assert.False(v.DataIsValid(Path.Combine(sb.Root, @"none\tool.exe")));
        // one existing candidate among several is enough
        Assert.True(v.DataIsValid(Path.Combine(sb.Root, @"none\a.exe") + ";" + file));
    }

    [Fact]
    public void Exists_applies_the_original_trimming_rules()
    {
        using var sb = new Sandbox();
        var file = sb.File(@"x\tool.exe");
        var dll = sb.File(@"x\lib.dll");
        var v = Validator();
        Assert.True(v.Exists(file + ",0"));                         // icon index
        Assert.True(v.Exists("@" + dll + ",-101", commandMode: true)); // resource reference
        Assert.True(v.Exists(file + " -background"));               // switch after the program
        Assert.True(v.Exists(file + "|extra"));
        Assert.True(v.Exists(Path.ChangeExtension(file, null)));      // ".exe" appended
        Assert.True(v.Exists(@"C:\\double\\backslash"));              // ':\\' is accepted as is
        Assert.True(v.Exists(@"C:\Nowhere\rundll32-stub"));           // RUNDLL/SHELL32/REGSVR are accepted
        Assert.False(v.Exists(Path.Combine(sb.Root, @"none\tool.exe")));
    }

    [Fact]
    public void Removable_and_cd_paths_are_valid_unless_the_options_include_them()
    {
        var drives = new Dictionary<char, DriveKind> { ['R'] = DriveKind.Removable, ['Q'] = DriveKind.CdRom, ['N'] = DriveKind.Remote };
        Assert.True(Validator(drives: drives).Exists(@"R:\setup\x.exe"));
        Assert.True(Validator(drives: drives).Exists(@"Q:\setup\x.exe"));
        Assert.True(Validator(drives: drives).Exists(@"N:\share\x.exe"));
        Assert.False(Validator(new PathValidatorOptions { IncludeRemovable = true }, drives).Exists(@"R:\setup\x.exe"));
        Assert.False(Validator(new PathValidatorOptions { IncludeCd = true }, drives).Exists(@"Q:\setup\x.exe"));
        Assert.True(Validator(new PathValidatorOptions { IncludeCd = true }, drives).Exists(@"R:\setup\x.exe"));
    }

    [Fact]
    public void Ignore_words_load_plain_lists_and_enabled_dat_lists()
    {
        using var sb = new Sandbox();
        File.WriteAllText(Path.Combine(sb.Root, "mine.ini"), "//comment\r\n\\MyVendor\\ // trailing comment\r\n\r\n");
        File.WriteAllText(Path.Combine(sb.Root, "StandardScanOnly.ini"), "standardword\r\n");
        File.WriteAllText(Path.Combine(sb.Root, "Vendor.dat"), "[settings]\r\nindex=0 1\r\n\r\n[data]\r\n0=Vendor\\Key\r\n1=Other\r\n");
        File.WriteAllText(Path.Combine(sb.Root, "Off.dat"), "[settings]\r\nindex=0\r\n\r\n[data]\r\n0=disabledword\r\n");

        var std = IgnoreWords.Load([sb.Root], thorough: false, n => n != "Off");
        Assert.Contains(@"\MYVENDOR\", std.Words);
        Assert.Contains("STANDARDWORD", std.Words);
        Assert.Contains(@"VENDOR\KEY", std.Words);
        Assert.Contains("OTHER", std.Words);
        Assert.Contains("LOGFILE", std.Words);
        Assert.Contains(".HTA", std.Words);
        Assert.Equal(IgnoreWords.BuiltIn.Count + 1 + 4, std.Words.Count);
        Assert.DoesNotContain("DISABLEDWORD", std.Words);

        var th = IgnoreWords.Load([sb.Root], thorough: true);
        Assert.DoesNotContain("STANDARDWORD", th.Words);
        Assert.DoesNotContain("LOGFILE", th.Words);
        Assert.Contains("DISABLEDWORD", th.Words);
    }

    [Fact]
    public void Generic_scan_reports_missing_paths_with_the_original_skips()
    {
        using var env = new Env();
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\Vendor\App"))
        {
            k.SetValue("Good", env.Existing);
            k.SetValue("Bad", env.Missing);
            k.SetValue("Count", 5, RegistryValueKind.DWord);
            k.SetValue("Text", "plain");
            k.SetValue("Multi", new[] { env.Missing }, RegistryValueKind.MultiString);          // not checked
            k.SetValue("Long", env.Missing + new string('x', 600));                          // longer than the buffer
        }
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\Microsoft\MMC\SnapIns\X"))
            k.SetValue("Path", env.Missing);
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\Vendor\UserAssist"))
        {
            k.SetValue("HRZR_first", new byte[] { 1 }, RegistryValueKind.Binary);
            k.SetValue("After", env.Missing);                                                // after HRZR_: key abandoned
        }

        var r = env.Scan(RegCategory.UserEntries);
        var f = Assert.Single(r.Findings);
        Assert.Equal(@"Software\Vendor\App", f.Key);
        Assert.Equal("Bad", f.ValueName);
        Assert.Equal(RegTargetKind.Value, f.Target);
        Assert.Equal(0xA172, f.ReasonId);
    }

    [Fact]
    public void Thorough_mode_also_checks_value_names()
    {
        using var env = new Env();
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\Vendor\Files"))
        {
            k.SetValue(env.Missing, 1, RegistryValueKind.DWord);
            k.SetValue(env.Existing, 1, RegistryValueKind.DWord);
        }
        Assert.DoesNotContain(env.Scan(RegCategory.UserEntries).Findings, x => x.Key.EndsWith("Files"));
        var f = Assert.Single(env.Scan(RegCategory.UserEntries, new RegScanOptions { Thorough = true }).Findings);
        Assert.Equal(env.Missing, f.ValueName);
        Assert.Equal("1", f.Data);
        Assert.Equal(0xA173, f.ReasonId);
    }

    [Fact]
    public void Mui_cache_names_are_checked_in_standard_mode()
    {
        using var env = new Env();
        const string mui = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(mui))
        {
            k.SetValue(env.Missing + ".FriendlyAppName", "Gone");
            k.SetValue(env.Existing + ".FriendlyAppName", "Here");
        }
        var r = env.Scan(RegCategory.UserEntries, new RegScanOptions { NoIgnoreForMru = false });
        var f = Assert.Single(r.Findings);
        Assert.Equal(env.Missing + ".FriendlyAppName", f.ValueName);
        Assert.Equal(mui, f.Key);
    }

    [Fact]
    public void Startup_approved_entries_without_a_run_value_are_reported()
    {
        using var env = new Env();
        const string sa = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(sa))
        {
            k.SetValue("Kept", new byte[12], RegistryValueKind.Binary);
            k.SetValue("Orphan", new byte[12], RegistryValueKind.Binary);
        }
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
            k.SetValue("Kept", "\"" + env.Existing + "\"");
        var f = Assert.Single(env.Scan(RegCategory.UserEntries).Findings);
        Assert.Equal("Orphan", f.ValueName);
        Assert.Equal("InvalidStartupReference", f.Reason);
    }

    [Fact]
    public void Ignore_words_filter_findings_but_not_mru_lists()
    {
        using var env = new Env();
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\Vendor\App"))
            k.SetValue("Bad", env.Missing);
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\Vendor\Recent"))
            k.SetValue("MRU0", env.Missing);
        var words = new IgnoreWords();
        words.Add(@"\Vendor\");

        var r = env.Scan(RegCategory.UserEntries, words: words);
        Assert.Equal("MRU0", Assert.Single(r.Findings).ValueName);           // MRU lists are not filtered (bit3)
        var ignored = Assert.Single(r.Ignored);
        Assert.Equal(@"\VENDOR\", ignored.IgnoredBy);

        var nonExpert = env.Scan(RegCategory.UserEntries, new RegScanOptions { UseIgnoreList = false }, words);
        Assert.Single(nonExpert.Findings);                                    // outside expert mode a hit is always dropped
        var off = env.Scan(RegCategory.UserEntries, new RegScanOptions { UseIgnoreList = false, Expert = true }, words);
        Assert.Equal(2, off.Findings.Count);                                  // listed, but marked
        Assert.Contains(off.Findings, x => x.IgnoredBy == @"\VENDOR\");
    }

    [Fact]
    public void Cleaning_backs_up_first_skips_changed_entries_and_restores()
    {
        using var env = new Env();
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\Vendor\App"))
        {
            k.SetValue("Bad", env.Missing);
            k.SetValue("Changed", env.Missing.Replace("prog", "other"));
        }
        var r = env.Scan(RegCategory.UserEntries);
        Assert.Equal(2, r.Findings.Count);
        using (var k = env.Root(RegHive.CurrentUser).OpenSubKey(@"Software\Vendor\App", writable: true)!)
            k.SetValue("Changed", "now something else");

        var cleaner = env.Cleaner();
        var res = cleaner.Clean(r.Findings);
        Assert.Equal(1, res.Removed);
        Assert.Equal("Changed", Assert.Single(res.Skipped).ValueName);
        Assert.Empty(res.Failed);
        using (var k = env.Root(RegHive.CurrentUser).OpenSubKey(@"Software\Vendor\App")!)
            Assert.DoesNotContain("Bad", k.GetValueNames());
        var reg = File.ReadAllText(res.BackupPath!);
        Assert.Contains(@"[HKEY_CURRENT_USER\Software\Vendor\App]", reg);
        Assert.Contains("\"Bad\"=", reg);
        Assert.DoesNotContain("\"Changed\"=", reg);
        Assert.Contains(OperationJournal.Read(res.JournalPath), e => e.Action == "reg-delete-value" && e.Success);

        Assert.Equal(1, cleaner.Restore(res.BackupPath!));
        using (var k = env.Root(RegHive.CurrentUser).OpenSubKey(@"Software\Vendor\App")!)
            Assert.Equal(env.Missing, k.GetValue("Bad"));
    }

    [Fact]
    public void Key_findings_are_exported_whole_and_top_level_keys_are_refused()
    {
        using var env = new Env();
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\Vendor\Entry\Sub"))
            k.SetValue("v", "x");
        var cleaner = env.Cleaner();
        var key = new RegFinding
        {
            Category = RegCategory.UserEntries, Hive = RegHive.CurrentUser, Key = @"Software\Vendor\Entry", ValueName = "", Data = "",
            Target = RegTargetKind.Key, Reason = "t", ReasonText = "t",
        };
        var top = key with { Key = "Software" };
        var res = cleaner.Clean([key, top]);
        Assert.Equal(1, res.Removed);
        Assert.Single(res.Failed);
        Assert.Null(env.Root(RegHive.CurrentUser).OpenSubKey(@"Software\Vendor\Entry"));
        Assert.Contains(@"[HKEY_CURRENT_USER\Software\Vendor\Entry\Sub]", File.ReadAllText(res.BackupPath!));
    }

    [Fact]
    public void Settings_bits_map_to_options()
    {
        var o = RegScanOptions.FromSettingsReg(0xC, 0xFFFBF, thorough: false);
        Assert.False(o.IncludeRemovable);
        Assert.False(o.IncludeCd);
        Assert.True(o.UseIgnoreList);
        Assert.True(o.NoIgnoreForMru);
        Assert.Equal((RegCategory)0xFFFBF & ~RegScanOptions.Unsupported, o.Categories);
        Assert.True(RegScanOptions.FromSettingsReg(3, 0, true) is { IncludeRemovable: true, IncludeCd: true, UseIgnoreList: false, Thorough: true });
    }
}
