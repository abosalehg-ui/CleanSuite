using System.Diagnostics;
using CleanSuite.Compat;
using CleanSuite.Junk;
using CleanSuite.Matching;
using Xunit;

namespace CleanSuite.Tests;

/// <summary>Creates a unique scratch folder under %TEMP% and removes it afterwards.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "CleanSuiteTests", Guid.NewGuid().ToString("N"));
    public Sandbox() => Directory.CreateDirectory(Root);

    public string File(string relative, int size = 1, DateTime? lastWriteUtc = null)
    {
        var p = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, new byte[size]);
        if (lastWriteUtc is { } t) System.IO.File.SetLastWriteTimeUtc(p, t);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class MatchingTests
{
    [Theory]  // T1–T4
    [InlineData("file.TMP", "*.tmp", true)]
    [InlineData(@"c:\a\b\x.log", @"c:\*.log", true)]       // '*' crosses '\'
    [InlineData("README", "*.*", false)]                    // literal dot required
    [InlineData("a.~mp", "*.~??", true)]
    [InlineData("a.~m", "*.~??", false)]
    [InlineData("~WRL0001.tmp", "~*.???", true)]
    [InlineData("abc.", "~*.", false)]
    [InlineData("~abc.", "~*.", true)]
    [InlineData("thumbcache_256.db", "thumbcache*.db", true)]
    [InlineData("x", "", false)]
    [InlineData("", "*", true)]
    [InlineData("", "", true)]
    [InlineData("aaa", "a*a*a", true)]
    [InlineData("aa", "a*a*a", false)]
    public void AceWildcard_matches_like_original(string text, string pattern, bool expected)
        => Assert.Equal(expected, AceWildcard.IsMatch(text, pattern));
}

public class PatternTests
{
    private static readonly Dictionary<string, string> NoExt = new();

    [Fact]  // T5
    public void Folder_mask_without_S_matches_only_direct_children()
    {
        var set = PatternSet.Build(new[] { @"X:\d\*.tmp" }, NoExt, NoExt);
        Assert.NotNull(set.Classify("f.tmp", @"X:\d\f.tmp"));
        Assert.Null(set.Classify("f.tmp", @"X:\d\sub\f.tmp"));
    }

    [Fact]  // T6
    public void Folder_mask_with_S_matches_subfolders()
    {
        var set = PatternSet.Build(new[] { @"X:\d\*.tmp/S" }, NoExt, NoExt);
        Assert.True(set.Patterns[0].FullPath);
        Assert.Equal(@"X:\D\*.TMP", set.Patterns[0].Mask);
        Assert.NotNull(set.Classify("f.tmp", @"X:\d\sub\f.tmp"));
    }

    [Fact]
    public void Default_profile_matches_original_tables()
    {
        Assert.Equal(46, JunkDefaults.Patterns.Count);
        Assert.Equal(19, JunkDefaults.DefaultProfileMasks.Count());
        Assert.Equal(27, JunkDefaults.EmergencyProfileMasks.Count());
        // Same order as the profile string the original writes to Junk\Profiles\Default.
        Assert.Equal("*.tmp|*.temp|*.chk|*.old|*.gid|*.fts|*.$$$|*.---|*.??$|*.___|*.~mp|*._mp|*.dmp|*.prv|thumbcache*.db|*.$db|*.??~|*.db$|chklist.*",
            string.Join("|", JunkDefaults.DefaultProfileMasks));
        Assert.Equal("Backup", PatternSet.BuildDefault().Classify("x.old", @"C:\x.old"));
    }

    [Fact]  // T7
    public void Ignore_list_strips_comments_and_splits_T_lines()
    {
        var list = new IgnoreList();
        list.AddLines(new[] { "  setup*.log   // comment", "// only comment", "keep.tmp /T", "", "*\\drivers\\*" });
        Assert.Equal(new[] { "SETUP*.LOG", "KEEP.TMP", "*\\DRIVERS\\*" }, list.General);
        Assert.Equal(new[] { "KEEP.TMP" }, list.TempOnly);
        Assert.NotNull(list.Match("Setup01.log", @"C:\x\Setup01.log", ScanMode.Drive));
        Assert.Null(list.Match("Setup01.log", @"C:\x\Setup01.log", ScanMode.Temp));    // general lines don't apply in Temp
        Assert.NotNull(list.Match("keep.tmp", @"C:\t\keep.tmp", ScanMode.Temp));
        Assert.NotNull(list.Match("a.sys", @"C:\Windows\System32\drivers\a.sys", ScanMode.Drive));  // '\' => full path
    }

    [Fact]
    public void Ignore_lists_from_several_folders_are_combined_and_missing_folders_skipped()
    {
        using var sb = new Sandbox();
        File.WriteAllLines(sb.File(@"ace\a.ini"), ["first.tmp"]);
        File.WriteAllLines(sb.File(@"own\user.ini"), ["second.log /T"]);
        var list = IgnoreList.LoadFolders([Path.Combine(sb.Root, "ace"), Path.Combine(sb.Root, "missing"), Path.Combine(sb.Root, "own")]);
        Assert.Equal(new[] { "FIRST.TMP", "SECOND.LOG" }, list.General);
        Assert.Equal(new[] { "SECOND.LOG" }, list.TempOnly);
        Assert.Equal(list.General, IgnoreList.LoadFolder(Path.Combine(sb.Root, "ace")).General.Concat(["SECOND.LOG"]));
    }
}

public class ScannerTests
{
    private static JunkScanRequest Request(Sandbox sb, JunkOptions? o = null, IgnoreList? ign = null, IReadOnlyList<string>? temps = null) => new()
    {
        Roots = new[] { sb.Root },
        Patterns = PatternSet.BuildDefault(),
        Exclusions = JunkDefaults.Exclusions,
        Ignore = ign ?? new IgnoreList(),
        Options = o ?? new JunkOptions { CleanTempFolders = false },
        TempFolders = temps ?? Array.Empty<string>(),
        UtcNow = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc),
    };

    private static readonly DateTime Old = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Finds_default_profile_junk_and_flags()
    {
        using var sb = new Sandbox();
        var tmp = sb.File(@"a\x.tmp", 2000, Old);
        sb.File(@"a\keep.txt", 10, Old);
        sb.File(@"a\b\report.log", 10, Old);     // *.log is Emergency-only: not in Default profile
        var res = new JunkScanner().Scan(Request(sb));
        var item = Assert.Single(res.Items);
        Assert.Equal(tmp, item.FullPath, ignoreCase: true);
        Assert.Equal("Temp", item.Category);
        Assert.Equal(JunkReason.JunkType | JunkReason.Archivable | JunkReason.CanOpen | JunkReason.CanDelete | JunkReason.NotIgnored, item.Flags);
        Assert.Equal(2, item.SizeKb);                // 2000 bytes => 2 KB rounded up
        Assert.Equal(3, res.FilesSearched);
    }

    [Fact]  // T8
    public void Zero_length_only_when_enabled()
    {
        using var sb = new Sandbox();
        sb.File("empty.dat", 0, Old);
        Assert.Empty(new JunkScanner().Scan(Request(sb)).Items);
        var res = new JunkScanner().Scan(Request(sb, new JunkOptions { CleanTempFolders = false, FindZeroLength = true }));
        Assert.True(Assert.Single(res.Items).Flags.HasFlag(JunkReason.ZeroLength));
    }

    [Fact]  // T9 (7-day window)
    public void Recent_files_excluded_when_option_set()
    {
        using var sb = new Sandbox();
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        sb.File("new.tmp", 1, now.AddDays(-3));
        var old = sb.File("old.tmp", 1, now.AddDays(-10));
        var res = new JunkScanner().Scan(Request(sb, new JunkOptions { CleanTempFolders = false, ExcludeModifiedWithinWeek = true }));
        Assert.Equal(old, Assert.Single(res.Items).FullPath, ignoreCase: true);
    }

    [Fact]  // T10
    public void Junctions_are_not_followed()
    {
        using var sb = new Sandbox();
        using var target = new Sandbox();
        target.File("inside.tmp", 1, Old);
        var link = Path.Combine(sb.Root, "link");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target.Root}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction could not be created");
        try
        {
            Assert.Empty(new JunkScanner().Scan(Request(sb)).Items);
        }
        finally { Directory.Delete(link); }   // removes the junction only
    }

    [Fact]  // T11
    public void Locked_files_are_skipped_in_drive_scan()
    {
        using var sb = new Sandbox();
        var f = sb.File("busy.tmp", 1, Old);
        using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var res = new JunkScanner().Scan(Request(sb));
            Assert.Empty(res.Items);
            Assert.Single(res.SkippedLocked);
        }
    }

    [Fact]  // T12
    public void Excluded_folders_are_not_scanned()
    {
        using var sb = new Sandbox();
        sb.File(@"SendTo\x.tmp", 1, Old);
        sb.File(@"other\y.tmp", 1, Old);
        var res = new JunkScanner().Scan(Request(sb));
        Assert.EndsWith(@"other\y.tmp", Assert.Single(res.Items).FullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Temp_folder_files_all_listed_and_only_T_ignores_apply()
    {
        using var sb = new Sandbox();
        sb.File(@"tmp\anything.bin", 5, Old);
        sb.File(@"tmp\keep.dat", 5, Old);
        sb.File(@"tmp\setup.log", 5, Old);
        var ign = new IgnoreList();
        ign.AddLines(new[] { "keep.dat /T", "setup.log" });
        var temp = Path.Combine(sb.Root, "tmp");
        var res = new JunkScanner().Scan(Request(sb, new JunkOptions { CleanTempFolders = true }, ign, new[] { temp }));
        var names = res.Items.Select(i => i.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "anything.bin", "setup.log" }, names);
        Assert.All(res.Items, i => Assert.True(i.Flags.HasFlag(JunkReason.TempFolder)));
        Assert.All(res.Items, i => Assert.Equal(1000, i.Group));
    }

    [Fact]
    public void Option_applies_all_ignore_lines_inside_temp()
    {
        using var sb = new Sandbox();
        sb.File(@"tmp\anything.bin", 5, Old);
        sb.File(@"tmp\keep.dat", 5, Old);
        sb.File(@"tmp\desktop.ini", 5, Old);
        var ign = new IgnoreList();
        ign.AddLines(new[] { "keep.dat /T", "desktop.ini" });
        var temp = Path.Combine(sb.Root, "tmp");
        var res = new JunkScanner().Scan(Request(sb, new JunkOptions { CleanTempFolders = true, ApplyAllIgnoreListsInTemp = true }, ign, new[] { temp }));
        Assert.Equal("anything.bin", Assert.Single(res.Items).Name);
        Assert.Equal(2, res.IgnoredCount);

        // Off (default, original behaviour): the line without /T does not apply in Temp.
        Assert.False(new JunkOptions().ApplyAllIgnoreListsInTemp);
        Assert.Null(ign.Match("desktop.ini", temp + @"\desktop.ini", ScanMode.Temp));
        Assert.NotNull(ign.Match("desktop.ini", temp + @"\desktop.ini", ScanMode.Temp, allListsInTemp: true));
    }

    [Fact]
    public void Expert_mode_without_ignore_list_shows_ignored_items()
    {
        using var sb = new Sandbox();
        sb.File("x.tmp", 1, Old);
        var ign = new IgnoreList();
        ign.AddLines(new[] { "x.tmp" });
        Assert.Empty(new JunkScanner().Scan(Request(sb, new JunkOptions { CleanTempFolders = false }, ign)).Items);
        var res = new JunkScanner().Scan(Request(sb, new JunkOptions { CleanTempFolders = false, UseIgnoreList = false, ExpertMode = true }, ign));
        Assert.True(Assert.Single(res.Items).Flags.HasFlag(JunkReason.IgnoredShown));
    }

    [Fact]
    public void Scanner_never_modifies_files()
    {
        using var sb = new Sandbox();
        var f = sb.File("x.tmp", 10, Old);
        new JunkScanner().Scan(Request(sb));
        Assert.True(File.Exists(f));
        Assert.Equal(Old, File.GetLastWriteTimeUtc(f));
    }
}

public class TempLocatorTests
{
    private sealed class FakeEnv : ISystemEnvironment
    {
        public string Temp = @"C:\Users\u\AppData\Local\Temp\";
        public string GetTempPath() => Temp;
        public string GetWindowsDirectory() => @"C:\Windows";
        public string? GetRegistryEnvironment(bool machine, string name) => machine ? @"C:\Windows\TEMP" : @"C:\Users\u\AppData\Local\Temp";
        public IEnumerable<string> GetUserProfileDirectories() => Array.Empty<string>();
        public DateTime UtcNow => DateTime.UtcNow;
    }

    [Fact]  // T13
    public void Rejects_windows_folder_as_temp_and_deduplicates()
    {
        Assert.False(TempFolderLocator.IsAcceptableTempPath(@"C:\Windows"));
        Assert.False(TempFolderLocator.IsAcceptableTempPath(@"D:\data"));
        Assert.True(TempFolderLocator.IsAcceptableTempPath(@"D:\MyTmp"));
        var env = new FakeEnv();
        Assert.Equal(new[] { @"C:\Users\u\AppData\Local\Temp", @"C:\Windows\Temp" }, TempFolderLocator.Locate(env));
        env.Temp = @"C:\Windows\";
        Assert.Equal(@"C:\Windows\Temp", TempFolderLocator.Locate(env)[0]);
    }
}

public class CompatTests
{
    private sealed class FakeReader(Dictionary<string, Dictionary<string, object>> keys) : IKeyReader
    {
        public IReadOnlyDictionary<string, object>? ReadValues(string subKey) => keys.TryGetValue(subKey, out var v) ? v : null;
    }

    [Fact]  // T14
    public void Imports_original_junk_settings()
    {
        var reader = new FakeReader(new()
        {
            ["Junk"] = new() { ["SettingsJnk"] = 0xD, ["DriveJunkFiles"] = 92, ["DelJunkToBin"] = 2, ["JunkbakFolder"] = @"D:\bak" },
            [@"Junk\Profiles"] = new() { [""] = "Mine", ["Mine"] = @"*.old|C:\work\*.o/S", ["Default"] = "*.tmp" },
            [@"Junk\DefaultExt"] = new() { ["*.old"] = "Backup" },
            [@"Junk\UserExt"] = new() { [@"C:\work\*.o/S"] = "Build" },
            [@"Junk\Exclude"] = new() { [@"?:\windows"] = 1, [@"*\off"] = 0, [@"\bad"] = 1 },
        });
        var s = AceJunkSettings.Load(reader);
        var o = s.ToOptions();
        Assert.True(o.CleanTempFolders && o.UseIgnoreList && o.DeleteEmptyTempFolders && !o.FindZeroLength);
        Assert.Equal(new[] { "C:", "D:", "E:", "G:" }, AceJunkSettings.DrivesFromMask(s.DriveJunkFiles!.Value));
        Assert.Equal(RemovalMode.ZipBackup, s.DelJunkToBin);
        Assert.Equal(new[] { @"?:\windows" }, s.Exclusions);
        var set = s.ToPatternSet();
        Assert.Equal(2, set.Patterns.Count);
        Assert.Equal("Backup", set.Classify("a.old", @"Z:\a.old"));
        Assert.Equal("Build", set.Classify("m.o", @"C:\work\sub\m.o"));
        Assert.False(set.Patterns[1].IsDefault);
    }

    [Fact]
    public void Missing_settings_fall_back_to_defaults()
    {
        var s = AceJunkSettings.Load(new FakeReader(new()));
        Assert.False(s.FoundInRegistry);
        Assert.Equal(0xDu, s.SettingsJnk);
        Assert.Equal(19, s.ToPatternSet().Patterns.Count);
        Assert.Equal(26, s.Exclusions.Count);
    }

    [Fact]  // T15 – values taken from the shipped database
    public void Decodes_legacy_plugin_fields()
    {
        Assert.Equal(@"%AppData%\4Sync\*.log", LegacyPluginDecoder.Decode("5AC9639CC10A22349C4BA9995DD6C250D1743D941626E89B", "4Sync", 0));
        Assert.Equal(@"Software\4Sync", LegacyPluginDecoder.Decode("F6367D7A73E00536B09D070977C57D27", "4Sync", 0));
        Assert.True(LegacyPluginDecoder.TryGetIndex("Reg12", out var i) && i == 12);
        Assert.False(LegacyPluginDecoder.TryGetIndex("Description", out _));
    }
}
