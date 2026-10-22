using System.IO.Compression;
using CleanSuite.DiskUsage;
using CleanSuite.History;
using CleanSuite.Junk;
using CleanSuite.Restore;
using CleanSuite.Safety;
using CleanSuite.Scheduler;

namespace CleanSuite.Tests;

/// <summary>
/// The hardening from the second review: links on the way to a target, wipe roots from writable settings, untrusted registry
/// restores, zip backups read back, the ledger, the shared folder walk. Temp sandboxes and a private registry hive only.
/// </summary>
public class HardeningTests
{
    private sealed class DeletingBin : IRecycleBin
    {
        public IReadOnlyList<string> Recycle(IReadOnlyList<string> paths, bool silent)
        {
            foreach (var p in paths)
                if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p);
            return Array.Empty<string>();
        }
    }

    private static void Junction(string link, string target)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction could not be created");
    }

    // ---------------------------------------------------------------- links on the way to a target

    [Fact]
    public void A_path_that_reaches_a_protected_folder_through_a_junction_is_refused()
    {
        using var sb = new Sandbox();
        var guarded = Path.Combine(sb.Root, "guarded");
        var file = sb.File(@"guarded\keep.tmp", 5);
        var prot = new ProtectedPaths().AddTree(guarded);
        Junction(Path.Combine(sb.Root, "temp"), guarded);
        var viaLink = Path.Combine(sb.Root, "temp", "keep.tmp");

        Assert.Null(prot.Check(viaLink));                                       // the written path looks harmless
        Assert.StartsWith("protected location reached through a link", prot.CheckResolved(viaLink));
        Assert.NotNull(prot.CheckResolved(file));
    }

    [Fact]
    public void The_file_remover_never_deletes_through_a_link_into_a_protected_folder()
    {
        using var sb = new Sandbox();
        var guarded = Path.Combine(sb.Root, "guarded");
        var file = sb.File(@"guarded\keep.tmp", 5);
        Junction(Path.Combine(sb.Root, "temp"), guarded);
        var viaLink = Path.Combine(sb.Root, "temp", "keep.tmp");
        using var journal = new OperationJournal("t", Path.Combine(sb.Root, "_j"));
        var item = new RemovalItem(viaLink, 5, File.GetLastWriteTimeUtc(file));

        foreach (var mode in new[] { RemovalMode.RecycleBin, RemovalMode.Permanent, RemovalMode.ZipBackup })
        {
            var res = new FileRemover(new DeletingBin(), new ProtectedPaths().AddTree(guarded))
                .Remove([item], mode, journal, Path.Combine(sb.Root, "bak"));
            Assert.Equal(0, res.Deleted);
            Assert.True(File.Exists(file), mode.ToString());
        }
        Assert.Contains(OperationJournal.Read(journal.FilePath), e => e.Action == "skip" && e.Error!.StartsWith("protected location reached"));
    }

    [Fact]
    public void Refusals_are_journaled_and_a_changed_file_keeps_one_reason_text()
    {
        using var sb = new Sandbox();
        var f = sb.File("a.tmp", 3);
        var link = Path.Combine(sb.Root, "dir-link");
        Junction(link, sb.Root);
        using var journal = new OperationJournal("t", Path.Combine(sb.Root, "_j"));
        var res = new FileRemover(new DeletingBin(), new ProtectedPaths()).Remove(
        [
            new RemovalItem(Path.Combine(sb.Root, "gone.tmp"), 1, default),
            new RemovalItem(link, 0, default),
            new RemovalItem(f, 99, default),
        ], RemovalMode.RecycleBin, journal);
        // A junction passed as a file is not a file: refused like a vanished one.
        Assert.Equal(["no longer exists", "no longer exists", FileIdentity.ChangedReason], res.Failed.Select(x => x.Reason));
        Assert.Equal(3, OperationJournal.Read(journal.FilePath).Count(e => e.Action == "skip"));
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Junction_roots_and_link_temp_folders_are_not_scanned()
    {
        using var sb = new Sandbox();
        sb.File(@"real\x.tmp", 3);
        var link = Path.Combine(sb.Root, "link");
        Junction(link, Path.Combine(sb.Root, "real"));
        var scan = new JunkScanner().Scan(new JunkScanRequest
        {
            Roots = [link], Patterns = PatternSet.BuildDefault(), Exclusions = [],
            Options = new JunkOptions { CleanTempFolders = true }, TempFolders = [link],
        });
        Assert.Empty(scan.Items);
        Assert.True(SafeWalk.IsLink(link));
    }

    [Fact]
    public void The_shared_walk_lists_in_order_never_enters_links_and_stops_at_the_length_limit()
    {
        using var sb = new Sandbox();
        sb.File(@"a\1.txt");
        sb.File(@"a\b\2.txt");
        sb.File(@"c\3.txt");
        Junction(Path.Combine(sb.Root, "loop"), sb.Root);
        var visited = new List<string>();
        SafeWalk.Walk(sb.Root, _ => true, (dir, _) => visited.Add(Path.GetRelativePath(sb.Root, dir)));
        Assert.Equal([".", "a", @"a\b", "c"], visited);
        Assert.Null(SafeWalk.List(@"C:\" + new string('x', SafeWalk.MaxPath)));
        var pruned = new List<string>();
        SafeWalk.Walk(sb.Root, d => !d.EndsWith(@"\a", StringComparison.Ordinal), (dir, _) => pruned.Add(Path.GetRelativePath(sb.Root, dir)));
        Assert.Equal([".", "c"], pruned);
    }

    [Fact]
    public void Disk_analysis_recycle_refuses_a_folder_reached_through_a_link()
    {
        using var sb = new Sandbox();
        var guarded = Path.Combine(sb.Root, "guarded");
        sb.File(@"guarded\sub\k.bin", 2);
        Junction(Path.Combine(sb.Root, "view"), guarded);
        var (removed, failed) = DiskScanner.Recycle([(Path.Combine(sb.Root, "view", "sub"), FileAttributes.Directory, false)], new DeletingBin(),
            new ProtectedPaths().AddTree(guarded), Path.Combine(sb.Root, "_j"));
        Assert.Empty(removed);
        Assert.Single(failed);
        Assert.True(Directory.Exists(Path.Combine(guarded, "sub")));
    }

    // ---------------------------------------------------------------- zip backups and the ledger

    [Fact]
    public void A_damaged_zip_entry_does_not_pass_the_read_back()
    {
        using var sb = new Sandbox();
        var zipPath = Path.Combine(sb.Root, "t.zip");
        var data = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("f.bin", CompressionLevel.Optimal).Open()) s.Write(data);
        using (var zip = ZipFile.OpenRead(zipPath)) Assert.True(JunkBackupArchive.ReadsBack(zip.Entries[0]));
        // Flip bytes in the middle of the compressed stream (after the local header).
        var bytes = File.ReadAllBytes(zipPath);
        for (int i = 200; i < 260; i++) bytes[i] ^= 0x5A;
        File.WriteAllBytes(zipPath, bytes);
        using (var zip = ZipFile.OpenRead(zipPath)) Assert.False(JunkBackupArchive.ReadsBack(zip.Entries[0]));
    }

    [Fact]
    public void A_backup_that_cannot_be_recorded_in_the_ledger_is_journaled()
    {
        using var sb = new Sandbox();
        var bak = Path.Combine(sb.Root, "bak");
        Directory.CreateDirectory(Path.Combine(bak, BackupLedger.FileName));     // a folder where the ledger file should be
        var f = sb.File("x.tmp", 4);
        using var journal = new OperationJournal("t", Path.Combine(sb.Root, "_j"));
        var zip = JunkBackupArchive.Create(bak, [new RemovalItem(f, 4, File.GetLastWriteTimeUtc(f))], journal, out var archived);
        Assert.Single(archived);
        Assert.False(BackupLedger.Record(zip));
        Assert.Contains(OperationJournal.Read(journal.FilePath), e => e.Action == "ledger" && !e.Success);
        Assert.Equal(BackupTrust.Unknown, BackupLedger.Status(zip));
    }

    // ---------------------------------------------------------------- untrusted registry restores

    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\evil", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\.xyz", false)]
    public void Auto_start_keys_are_recognised(string key, bool expected) => Assert.Equal(expected, AutoStartKeys.IsMatch(key));

    [Fact]
    public void An_untrusted_reg_file_restores_data_but_never_a_run_key()
    {
        using var sb = new Sandbox();
        using var roots = new AppHiveRegistryRoots(Path.Combine(sb.Root, "t.hiv"));
        var reg = Path.Combine(sb.Root, "foreign.reg");
        File.WriteAllText(reg, "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n\"evil\"=\"C:\\\\x.exe\"\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Test]\r\n\"ok\"=\"1\"\r\n", new System.Text.UnicodeEncoding(false, true));
        var item = new BackupItem(reg, BackupKind.Registry, DateTime.Now, new FileInfo(reg).Length, false);

        var r = BackupCatalog.RestoreRegistry(item, roots, Path.Combine(sb.Root, "_j"));
        Assert.Equal(1, r.Values);
        Assert.Contains(r.Errors, e => e.Contains(AutoStartKeys.Refused));
        Assert.Null(roots.Root(RegHive.CurrentUser).OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("evil"));
        Assert.Equal("1", roots.Root(RegHive.CurrentUser).OpenSubKey(@"Software\Test")?.GetValue("ok"));

        // The registry page's own restore applies the same rule.
        int n = new CleanSuite.RegistryClean.RegistryCleaner(roots).Restore(reg, out var refused);
        Assert.Equal(1, n);
        Assert.Single(refused);
    }

    [Fact]
    public void A_verified_backup_of_a_run_key_is_restored()
    {
        using var sb = new Sandbox();
        using var roots = new AppHiveRegistryRoots(Path.Combine(sb.Root, "t.hiv"));
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using (var k = roots.Root(RegHive.CurrentUser).CreateSubKey(run)) k.SetValue("app", @"C:\app.exe");
        string file;
        using (var w = new RegBackupWriter(Path.Combine(sb.Root, "bak")))
        {
            Assert.True(w.ExportValue(roots.Root(RegHive.CurrentUser), RegHive.CurrentUser, run, "app"));
            file = w.FilePath!;
        }
        roots.Root(RegHive.CurrentUser).OpenSubKey(run, true)!.DeleteValue("app");
        var item = new BackupItem(file, BackupKind.Registry, DateTime.Now, new FileInfo(file).Length, false);
        Assert.Equal(BackupTrust.Verified, BackupCatalog.Trust(item));
        var r = BackupCatalog.RestoreRegistry(item, roots, Path.Combine(sb.Root, "_j"));
        Assert.Empty(r.Errors);
        Assert.Equal(@"C:\app.exe", roots.Root(RegHive.CurrentUser).OpenSubKey(run)?.GetValue("app"));
    }

    // ---------------------------------------------------------------- wipe roots from writable settings

    [Fact]
    public void Only_a_real_Outlook_temp_folder_is_emptied()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cache = Environment.GetFolderPath(Environment.SpecialFolder.InternetCache);
        Assert.True(WindowsHistoryCleaner.IsOutlookTempFolder(Path.Combine(cache, "Content.Outlook")));
        Assert.True(WindowsHistoryCleaner.IsOutlookTempFolder(Path.Combine(Path.GetTempPath(), "OLK3F2")));
        Assert.False(WindowsHistoryCleaner.IsOutlookTempFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        Assert.False(WindowsHistoryCleaner.IsOutlookTempFolder(Path.Combine(local, "Content.Outlook", "..")));
        Assert.False(WindowsHistoryCleaner.IsOutlookTempFolder(@"D:\Data\Content.Outlook"));
    }

    private sealed class NoBrowserRunning : IProcessProbe
    {
        public bool IsRunning(string exeName) => false;
    }

    [Fact]
    public void A_profiles_ini_path_that_is_not_a_profile_and_a_custom_cache_folder_are_handled_safely()
    {
        using var sb = new Sandbox();
        var roaming = Path.Combine(sb.Root, "Roaming");
        var local = Path.Combine(sb.Root, "Local");
        var ff = Path.Combine(roaming, @"Mozilla\Firefox");
        var docs = Path.Combine(sb.Root, "Documents");
        sb.File(@"Documents\thesis.docx", 10);
        sb.File(@"Documents\cache2\mine.txt", 10);
        var profile = Path.Combine(ff, @"Profiles\p1");
        var custom = Path.Combine(sb.Root, "FastDisk");
        sb.File(@"FastDisk\cache2\entries\A1", 10);
        sb.File(@"FastDisk\cache2\index", 10);
        sb.File(@"FastDisk\thumbnails\keep.png", 10);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "prefs.js"),
            "user_pref(\"browser.cache.disk.parent_directory\", \"" + custom.Replace("\\", "\\\\") + "\");\n");
        File.WriteAllText(Path.Combine(ff, "profiles.ini"),
            "[Profile0]\r\nName=docs\r\nIsRelative=0\r\nPath=" + docs + "\r\n\r\n[Profile1]\r\nName=p1\r\nIsRelative=1\r\nPath=Profiles/p1\r\n");
        var files = new RulePlanner(new TokenExpander(new Dictionary<string, string> { ["APPDATA"] = roaming, ["LOCALAPPDATA"] = local }, s => s));
        var plan = new FirefoxCleaner(files, new NoBrowserRunning()).Plan(FirefoxItems.Cache | FirefoxItems.Cookies | FirefoxItems.History,
            new CookieWhitelist(Array.Empty<string>()), new UserFolders { Users = [(roaming, local)] });

        Assert.DoesNotContain(plan.Actions, a => a.Target.StartsWith(docs, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Code == "not-a-profile" && n.Text == docs);
        Assert.Contains(plan.Actions, a => a.Target.StartsWith(Path.Combine(custom, "cache2"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Actions, a => a.Target.StartsWith(Path.Combine(custom, "thumbnails"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_scheduled_run_never_clears_the_clipboard_or_closes_Explorer()
    {
        var keys = ScheduledRunner.UnattendedHistoryItems.Select(id => WindowsHistoryCatalog.ById(id)!.Key).ToList();
        Assert.DoesNotContain("win.clipboard", keys);
        Assert.DoesNotContain("win.trayicons", keys);
        var defaults = WindowsHistoryCatalog.Items.Where(i => i.DefaultOn && i.Key != "win.clipboard").Select(i => i.Key);
        Assert.Equal(defaults.OrderBy(k => k), keys.OrderBy(k => k));
    }

    [Fact]
    public void Every_history_item_has_a_planner()
    {
        var cleaner = new WindowsHistoryCleaner(new RulePlanner(new TokenExpander(new Dictionary<string, string>(), s => s)),
            new RegistryPlanner(WindowsRegistryRoots.Instance), () => Array.Empty<string>());
        Assert.Empty(WindowsHistoryCatalog.Items.Select(i => i.Key).Except(cleaner.PlannedKeys));
    }

    [Fact]
    public void Sql_identifiers_are_quoted()
    {
        Assert.Equal("\"moz_cookies\"", Sqlite.Quote("moz_cookies"));
        Assert.Equal("\"a\"\"; DROP TABLE x; --\"", Sqlite.Quote("a\"; DROP TABLE x; --"));
    }
}
