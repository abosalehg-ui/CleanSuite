using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.Tests;

/// <summary>Find Duplicate Files on temp sandboxes and a private registry hive.</summary>
public class DuplicateTests
{
    private sealed class FakeBin : IRecycleBin
    {
        public List<string> Recycled { get; } = new();
        public IReadOnlyList<string> Recycle(IReadOnlyList<string> paths, bool silent)
        {
            foreach (var p in paths) { File.Delete(p); Recycled.Add(p); }
            return Array.Empty<string>();
        }
    }

    private static string Write(Sandbox sb, string rel, string content, DateTime? utc = null)
    {
        var p = Path.Combine(sb.Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        if (utc is { } t) File.SetLastWriteTimeUtc(p, t);
        return p;
    }

    private static DupOptions Opt(Sandbox sb, Func<DupOptions, DupOptions>? f = null)
    {
        var o = new DupOptions { Roots = [sb.Root], Patterns = DupProfiles.AllFiles, MinSize = 0, Excludes = Array.Empty<string>() };
        return f?.Invoke(o) ?? o;
    }

    [Fact]
    public void Same_name_size_and_content_are_grouped_and_sorted_by_total_size()
    {
        using var sb = new Sandbox();
        var a1 = Write(sb, @"a\movie.mp4", new string('x', 300));
        var a2 = Write(sb, @"b\movie.mp4", new string('x', 300));
        var a3 = Write(sb, @"c\MOVIE.mp4", new string('x', 300));                   // name match is case-insensitive
        Write(sb, @"d\movie.mp4", new string('y', 300));                            // same size, other content
        var s1 = Write(sb, @"a\song.mp3", "abc");
        var s2 = Write(sb, @"b\song.mp3", "abc");
        Write(sb, @"a\empty.mp3", "");                                              // zero-byte files are never candidates
        Write(sb, @"b\empty.mp3", "");
        Write(sb, @"a\alone.txt", "abc");

        var r = new DuplicateScanner(Opt(sb)).Scan();
        Assert.Equal(2, r.Groups.Count);
        Assert.True(r.ContentVerified);
        Assert.Equal(new[] { a1, a2, a3 }.OrderBy(x => x), r.Groups[0].Files.Select(f => f.FullPath).OrderBy(x => x));
        Assert.Equal(900, r.Groups[0].TotalSize);
        Assert.Equal(600, r.Groups[0].Reclaimable);
        Assert.Equal(1, r.Groups[0].Number);
        Assert.Equal(new[] { s1, s2 }.OrderBy(x => x), r.Groups[1].Files.Select(f => f.FullPath).OrderBy(x => x));
        Assert.Equal(9, r.FilesSearched);
    }

    [Fact]
    public void Content_only_finds_renamed_copies_and_criteria_are_normalised()
    {
        using var sb = new Sandbox();
        Write(sb, @"a\one.dat", "same content");
        Write(sb, @"b\two.dat", "same content");
        Write(sb, @"c\three.dat", "same contenT");
        var byContent = new DuplicateScanner(Opt(sb, o => o with { SameName = false })).Scan();
        Assert.Equal(2, Assert.Single(byContent.Groups).Files.Count);

        var nameOnly = new DuplicateScanner(Opt(sb, o => o with { SameSize = false, Content = ContentCompare.None })).Scan();
        Assert.Empty(nameOnly.Groups);
        // nothing selected at all -> name + size + MD5 (the original's normalisation)
        Assert.Equal(new DupOptions { SameName = true, SameSize = true, Content = ContentCompare.Md5 },
            new DupOptions { SameName = false, SameSize = false, Content = ContentCompare.None }.Normalized());
        Assert.True(new DupOptions { SameSize = false, Content = ContentCompare.Crc32 }.Normalized().SameSize);
    }

    [Fact]
    public void Date_minute_precision_size_limit_profiles_excludes_and_ignore_list()
    {
        using var sb = new Sandbox();
        var t = new DateTime(2025, 3, 4, 10, 20, 5, DateTimeKind.Utc);
        Write(sb, @"a\x.bin", "1234", t);
        Write(sb, @"b\x.bin", "1234", t.AddSeconds(40));                          // same minute
        Write(sb, @"c\x.bin", "1234", t.AddMinutes(5));
        var dated = new DuplicateScanner(Opt(sb, o => o with { SameDate = true, Content = ContentCompare.None })).Scan();
        Assert.Equal(2, Assert.Single(dated.Groups).Files.Count);

        Assert.Empty(new DuplicateScanner(Opt(sb, o => o with { MinSize = 5 })).Scan().Groups);
        Assert.Empty(new DuplicateScanner(Opt(sb, o => o with { Patterns = DupProfiles.BuiltIn["Audio"] })).Scan().Groups);
        Assert.Empty(new DuplicateScanner(Opt(sb, o => o with { Ignore = ["X.*"] })).Scan().Groups);
        // "?:\...\b" excludes b and (implicit trailing *) any sibling that starts with it
        var ex = new DuplicateScanner(Opt(sb, o => o with { Excludes = [Path.Combine(sb.Root, "b"), Path.Combine(sb.Root, "c")] })).Scan();
        Assert.Empty(ex.Groups);
    }

    [Fact]
    public void Unreadable_files_are_not_reported_as_duplicates()
    {
        using var sb = new Sandbox();
        var a = Write(sb, @"a\locked.bin", "data");
        var b = Write(sb, @"b\locked.bin", "data");
        Write(sb, @"c\locked.bin", "data");
        using (new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = new DuplicateScanner(Opt(sb)).Scan();
            Assert.Empty(r.Groups);                                                // two unreadable copies are not "equal"
            Assert.Equal(2, r.Unreadable.Count);
        }
    }

    [Fact]
    public void Crc32_and_md5_digests_match_known_values()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        using var sb = new Sandbox();
        var p = Write(sb, "abc.txt", "abc");
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", DuplicateScanner.Digest(p, ContentCompare.Md5));
        Assert.Equal("0x352441c2", DuplicateScanner.Digest(p, ContentCompare.Crc32));
    }

    [Fact]
    public void Removal_keeps_a_copy_of_every_group_and_backs_up_to_a_dup_zip()
    {
        using var sb = new Sandbox();
        var a = Write(sb, @"a\f.txt", "dup");
        var b = Write(sb, @"b\f.txt", "dup");
        var c = Write(sb, @"a\g.txt", "other");
        var d = Write(sb, @"b\g.txt", "other");
        var groups = new DuplicateScanner(Opt(sb)).Scan().Groups;
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { b, c, d };      // g.txt: both copies checked
        Assert.Equal(1, DuplicateRemover.WholeGroupsSelected(groups, selected));

        var bin = new FakeBin();
        var res = new DuplicateRemover(bin, new ProtectedPaths(), Path.Combine(sb.Root, "_j")).Remove(groups, selected, RemovalMode.RecycleBin);
        Assert.Equal(1, res.Deleted);
        Assert.Equal(new[] { b }, bin.Recycled);
        Assert.True(File.Exists(c) && File.Exists(d));
        Assert.Equal(2, res.Failed.Count(f => f.Reason.Contains("kept")));

        var bak = Path.Combine(sb.Root, "_bak");
        var zipRes = new DuplicateRemover(bin, new ProtectedPaths(), Path.Combine(sb.Root, "_j"))
            .Remove(groups, new HashSet<string> { d }, RemovalMode.ZipBackup, bak);
        Assert.Equal(1, zipRes.Deleted);
        Assert.StartsWith("Dup ", Path.GetFileName(zipRes.ZipPath));
        Assert.False(File.Exists(d));
        var entries = JunkBackupArchive.List(zipRes.ZipPath!);
        Assert.Contains(entries, e => string.Equals(e.OriginalPath, d, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ace_settings_are_read_without_writing()
    {
        using var sb = new Sandbox();
        using var roots = new AppHiveRegistryRoots(Path.Combine(sb.Root, "t.hiv"));
        Assert.False(AceDupSettings.Read(roots).FoundInRegistry);
        var hkcu = roots.Root(RegHive.CurrentUser);
        using (var k = hkcu.CreateSubKey(AceDupSettings.Root))
        {
            k.SetValue("SettingsDup", 0x22, RegistryValueKind.DWord);              // size + content
            k.SetValue("matchcrc", 1, RegistryValueKind.DWord);
            k.SetValue("DelDupToBin", 2, RegistryValueKind.DWord);
        }
        using (var k = hkcu.CreateSubKey(AceDupSettings.Root + @"\DupScanPaths"))
        {
            k.SetValue(@"D:\Music\", 1, RegistryValueKind.DWord);
            k.SetValue(@"E:\Off\", 0, RegistryValueKind.DWord);
            k.SetValue(@"F:\NoSlash", 1, RegistryValueKind.DWord);
        }
        using (var k = hkcu.CreateSubKey(AceDupSettings.Root + @"\Profiles"))
        {
            k.SetValue("", "Mine");
            k.SetValue("Mine", "*.flac|*.ape");
        }
        using (var k = hkcu.CreateSubKey(AceDupSettings.Root + @"\ExcludeDup")) k.SetValue(@"?:\Backup", 1, RegistryValueKind.DWord);
        File.WriteAllText(Path.Combine(sb.Root, "ignorelist.ini"), "//comment\r\n*.dll\r\nunins*.exe //uninstallers\r\n");

        var s = AceDupSettings.Read(roots);
        Assert.True(s.FoundInRegistry);
        var o = s.ToOptions(sb.Root);
        Assert.Equal(new[] { @"D:\Music" }, o.Roots);
        Assert.False(o.SameName);
        Assert.True(o.SameSize);
        Assert.Equal(ContentCompare.Crc32, o.Content);
        Assert.Equal(0, o.MinSize);
        Assert.Equal(new[] { "*.flac", "*.ape" }, o.Patterns);
        Assert.Equal(new[] { @"?:\Backup" }, o.Excludes);
        Assert.Equal(new[] { "*.DLL", "UNINS*.EXE" }, o.Ignore);
        Assert.Equal(2u, s.RemovalMode);
    }
}
