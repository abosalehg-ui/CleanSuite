using CleanSuite.Junk;
using CleanSuite.Safety;
using CleanSuite.Shortcuts;

namespace CleanSuite.Tests;

/// <summary>Fix Invalid Shortcuts on synthetic shortcuts in a temp sandbox (never the real Start menu or Recycle Bin).</summary>
public class ShortcutTests
{
    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> Received { get; } = new();
        public string Store { get; } = Path.Combine(Path.GetTempPath(), "CleanSuiteTests", "bin_" + Guid.NewGuid().ToString("N"));
        public IReadOnlyList<string> Recycle(IReadOnlyList<string> paths, bool silent)
        {
            Directory.CreateDirectory(Store);
            Received.AddRange(paths);
            foreach (var p in paths)
            {
                var dest = Path.Combine(Store, Guid.NewGuid().ToString("N"));
                if (Directory.Exists(p)) Directory.Move(p, dest); else File.Move(p, dest);
            }
            return Array.Empty<string>();
        }
    }

    private static ShortcutScanner Scanner(Sandbox sb, bool empty = false, string? ignoreDir = null, ProfileInfo? profiles = null,
        Func<string, DriveKind>? drives = null, Func<string, LinkTarget?>? read = null, bool removable = false) =>
        new(new ShortcutScanOptions
        {
            Roots = [(Path.Combine(sb.Root, "menu"), empty)],
            IgnoreFolders = ignoreDir == null ? Array.Empty<string>() : [ignoreDir],
            IncludeRemovable = removable,
        }, profiles ?? ProfileInfo.Empty, drives, read);

    private static string Link(Sandbox sb, string rel, string target, string args = "")
    {
        var p = Path.Combine(sb.Root, "menu", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        ShellLinkFile.Create(p, target, args);
        return p;
    }

    [Fact]
    public void Missing_target_is_reported_existing_target_is_not()
    {
        using var sb = new Sandbox();
        var app = sb.File(@"apps\tool.exe");
        Link(sb, "good.lnk", app);
        Link(sb, @"sub\bad.lnk", Path.Combine(sb.Root, @"gone\missing.exe"));
        var items = Scanner(sb).Scan();
        var item = Assert.Single(items);
        Assert.Equal("bad.lnk", item.Name);
        Assert.Equal(ShortcutReason.TargetMissing, item.Reason);
        Assert.Equal(Path.Combine(sb.Root, "menu", "sub"), item.Folder);
        Assert.Equal(Path.Combine(sb.Root, @"gone\missing.exe"), item.Target, ignoreCase: true);
    }

    [Fact]
    public void Target_file_next_to_the_shortcut_counts_as_valid()
    {
        using var sb = new Sandbox();
        sb.File(@"menu\portable.exe");
        Link(sb, "p.lnk", Path.Combine(sb.Root, @"moved\portable.exe"));
        Assert.Empty(Scanner(sb).Scan());
    }

    [Fact]
    public void Missing_path_in_arguments_is_reported()
    {
        using var sb = new Sandbox();
        var app = sb.File(@"apps\editor.exe");
        var doc = sb.File(@"docs\a.txt");
        Link(sb, "ok.lnk", app, "\"" + doc + "\"");
        Link(sb, "bad.lnk", app, Path.Combine(sb.Root, @"docs\gone.txt"));
        Link(sb, "switch.lnk", app, "/x " + Path.Combine(sb.Root, @"docs\gone.txt"));     // starts with a switch: not a path
        var item = Assert.Single(Scanner(sb).Scan());
        Assert.Equal("bad.lnk", item.Name);
        Assert.Equal(ShortcutReason.ArgumentPathMissing, item.Reason);
    }

    [Fact]
    public void Network_cd_unknown_and_removable_drives_are_never_reported_by_default()
    {
        using var sb = new Sandbox();
        foreach (var n in new[] { "net", "cd", "nodrive", "usb" }) sb.File(@"menu\" + n + ".lnk");
        var targets = new Dictionary<string, string>
        {
            ["net.lnk"] = @"\\server\share\app.exe", ["cd.lnk"] = @"R:\setup.exe", ["nodrive.lnk"] = @"Q:\x.exe", ["usb.lnk"] = @"U:\tool.exe",
        };
        LinkTarget? Read(string p) => new(targets[Path.GetFileName(p)], "", false, false);
        DriveKind Drive(string root) => root.ToUpperInvariant() switch
        {
            @"R:\" => DriveKind.CdRom,
            @"U:\" => DriveKind.Removable,
            @"Q:\" => DriveKind.NoRootDir,
            _ => DriveKind.Fixed,
        };
        Assert.Empty(Scanner(sb, drives: Drive, read: Read).Scan());
        var withRemovable = Scanner(sb, drives: Drive, read: Read, removable: true).Scan();
        Assert.Equal("usb.lnk", Assert.Single(withRemovable).Name);
    }

    [Fact]
    public void Shell_only_and_unreadable_shortcuts_are_skipped()
    {
        using var sb = new Sandbox();
        sb.File(@"menu\control.lnk");
        sb.File(@"menu\broken.lnk");
        LinkTarget? Read(string p) => Path.GetFileName(p) == "control.lnk" ? new LinkTarget("", "", false, false) : null;
        Assert.Empty(Scanner(sb, read: Read).Scan());
    }

    [Fact]
    public void Ignore_patterns_match_names_or_full_paths_and_built_in_skips_apply()
    {
        using var sb = new Sandbox();
        var missing = Path.Combine(sb.Root, @"gone\x.exe");
        Link(sb, "Keep Me.lnk", missing);
        Link(sb, @"vendor\a.lnk", missing);
        Link(sb, "Windows Catalog.lnk", missing);
        Link(sb, @"Administrative Tools\b.lnk", missing);
        Link(sb, "report.lnk", missing);
        var ign = Path.Combine(sb.Root, "ignore");
        Directory.CreateDirectory(ign);
        File.WriteAllLines(Path.Combine(ign, "ignore.ini"), ["// comment line", "keep*.lnk  // by name", @"*\VENDOR\*"]);
        var s = Scanner(sb, ignoreDir: ign);
        Assert.Equal("report.lnk", Assert.Single(s.Scan()).Name);
        Assert.Equal(ShortcutScanner.BuiltInIgnore.Concat(["KEEP*.LNK", @"*\VENDOR\*"]), s.IgnorePatterns);
    }

    [Fact]
    public void Empty_start_menu_folders_are_reported_only_when_enabled()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sb.Root, @"menu\Old App\Sub"));
        Directory.CreateDirectory(Path.Combine(sb.Root, @"menu\Startup"));
        sb.File(@"menu\Full\readme.txt");
        Assert.Empty(Scanner(sb).Scan());
        var item = Assert.Single(Scanner(sb, empty: true).Scan());
        Assert.Equal(ShortcutItemKind.EmptyFolder, item.Kind);
        Assert.Equal("Old App", item.Name);
    }

    [Fact]
    public void Shortcut_moved_to_another_profile_counts_as_valid()
    {
        using var sb = new Sandbox();
        var a = Path.Combine(sb.Root, @"Users\Alice");
        var b = Path.Combine(sb.Root, @"Users\Bob");
        Directory.CreateDirectory(a);
        sb.File(@"Users\Bob\Apps\t.exe");
        Link(sb, "t.lnk", Path.Combine(a, @"Apps\t.exe"));
        Assert.Single(Scanner(sb).Scan());
        Assert.Empty(Scanner(sb, profiles: new ProfileInfo([a, b], ["Alice"])).Scan());
    }

    [Fact]
    public void Remove_recycles_reviewed_items_and_skips_changed_ones()
    {
        using var sb = new Sandbox();
        var missing = Path.Combine(sb.Root, @"gone\x.exe");
        Link(sb, "one.lnk", missing);
        var two = Link(sb, "two.lnk", missing);
        Directory.CreateDirectory(Path.Combine(sb.Root, @"menu\Empty"));
        var items = Scanner(sb, empty: true).Scan();
        Assert.Equal(3, items.Count);
        File.SetLastWriteTimeUtc(two, DateTime.UtcNow.AddMinutes(5));                 // changed after the scan
        var bin = new FakeRecycleBin();
        var res = new ShortcutCleaner(bin, new ProtectedPaths(), Path.Combine(sb.Root, "_journal")).Remove(items);
        Assert.Equal(2, res.Succeeded);
        Assert.Equal("two.lnk", Assert.Single(res.Failed).Item.Name);
        Assert.True(File.Exists(two));
        Assert.False(Directory.Exists(Path.Combine(sb.Root, @"menu\Empty")));
        var journal = OperationJournal.Read(res.JournalPath).ToList();
        Assert.Equal(3, journal.Count);
        Assert.Equal(2, journal.Count(j => j.Success));
    }

    [Fact]
    public void Retarget_backs_up_the_shortcut_and_keeps_its_arguments()
    {
        using var sb = new Sandbox();
        var lnk = Link(sb, "t.lnk", Path.Combine(sb.Root, @"gone\x.exe"), "--flag");
        var newTarget = sb.File(@"apps\new.exe");
        var backup = new ShortcutCleaner(new FakeRecycleBin(), new ProtectedPaths(), Path.Combine(sb.Root, "_journal"),
            Path.Combine(sb.Root, "_bak")).Retarget(lnk, newTarget);
        Assert.True(File.Exists(backup));
        Assert.Contains(@"gone\x.exe", ShellLinkFile.Read(backup)!.Target, StringComparison.OrdinalIgnoreCase);
        var now = ShellLinkFile.Read(lnk)!;
        Assert.Equal(newTarget, now.Target, ignoreCase: true);
        Assert.Equal("--flag", now.Arguments);
        Assert.Empty(Scanner(sb).Scan());
    }

    [Fact]
    public void Target_path_rules_follow_the_original()
    {
        using var sb = new Sandbox();
        var s = Scanner(sb);
        Assert.True(s.TargetPathOk("relative\\thing.exe"));                         // no "X:\" → never reported
        Assert.True(s.Acceptable(@"C:\Windows\System32\rundll32.exe gone.dll,Entry"));
        Assert.True(s.Acceptable(@"C:\NoSuchDir\RUNDLL32 x"));
        var exe = sb.File(@"apps\run.exe");
        Assert.True(s.Acceptable("\"" + exe + "\" -arg"));
        Assert.True(s.Acceptable(exe[..^4]));                                         // ".exe" appended
        Assert.False(s.Acceptable(Path.Combine(sb.Root, @"apps\none.exe")));
    }
}
