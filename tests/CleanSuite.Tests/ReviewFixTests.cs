using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Regression tests for the fixes from the code review (docs/مراجعة-CleanSuite.md, §6). Temp sandboxes only.</summary>
public class ReviewFixTests
{
    /// <summary>Deletes what it is given; refuses (like a drive without a Recycle Bin) every path containing "nobin".</summary>
    private sealed class PickyBin : IRecycleBin
    {
        public readonly List<string> Sent = new();
        public IReadOnlyList<string> Recycle(IReadOnlyList<string> paths, bool silent)
        {
            foreach (var p in paths) { Sent.Add(p); File.Delete(p); }
            return Array.Empty<string>();
        }
        public string? WhyNotRecyclable(string path) => path.Contains("nobin") ? "drive without a Recycle Bin (not deleted)" : null;
    }

    private static JunkItem Item(string path) => new(path, Path.GetFileName(path), Path.GetDirectoryName(path)!, new FileInfo(path).Length,
        JunkReason.JunkType, "Temp", 1);

    // ---- 1. Recycle Bin that would really delete for good ----

    [Fact]
    public void Paths_on_drives_without_a_Recycle_Bin_are_refused_not_deleted()
    {
        using var sb = new Sandbox();
        var keep = sb.File(@"nobin\a.tmp", 10);
        var go = sb.File(@"ok\b.tmp", 10);
        var bin = new PickyBin();
        var res = new JunkRemover(bin, new ProtectedPaths(), Path.Combine(sb.Root, "_j"))
            .Remove(new JunkRemovalRequest { Items = [Item(keep), Item(go)], Mode = RemovalMode.RecycleBin });
        Assert.Equal(1, res.Deleted);
        Assert.True(File.Exists(keep));                                             // never handed to the bin
        Assert.Equal(new[] { go }, bin.Sent);
        Assert.Contains(res.Failed, f => f.Path == keep && f.Reason.Contains("Recycle Bin"));
    }

    [Fact]
    public void Network_locations_have_no_Recycle_Bin_and_the_system_drive_has_one()
    {
        Assert.NotNull(ShellRecycleBin.NoRecycleBinReason(@"\\server\share\"));
        var bin = new ShellRecycleBin();
        Assert.NotNull(bin.WhyNotRecyclable(@"\\server\share\x.tmp"));
        Assert.Null(bin.WhyNotRecyclable(Path.Combine(Path.GetTempPath(), "x.tmp")));   // read-only checks, nothing recycled
    }

    // ---- 3, 4, 20. Pages: one operation at a time, errors reported, never thrown ----

    private static CleanSuite.RegistryClean.RegFinding Finding(string key) => new()
    {
        Category = CleanSuite.RegistryClean.RegCategory.UserEntries, Hive = RegHive.CurrentUser, Key = key, ValueName = "v", Data = @"C:\gone.exe",
        Target = CleanSuite.RegistryClean.RegTargetKind.Value, Reason = "InvalidPath", ReasonText = "missing",
    };

    [Fact]
    public async Task While_a_scan_runs_nothing_else_can_start_and_errors_end_up_in_the_Error_event()
    {
        using var gate = new ManualResetEventSlim();
        int scans = 0, cleans = 0;
        var vm = new CleanSuite.App.ViewModels.RegistryViewModel(() => new CleanSuite.RegistryClean.RegScanOptions(),
            scan: (_, _) =>
            {
                Interlocked.Increment(ref scans);
                gate.Wait();
                return new CleanSuite.RegistryClean.RegScanResult { Findings = [Finding("a"), Finding("b")], Ignored = [], Unreadable = [] };
            },
            clean: (f, _) =>
            {
                Interlocked.Increment(ref cleans);
                if (f.Count == 1) throw new IOException("disk gone");
                return new CleanSuite.RegistryClean.RegCleanResult { Removed = f.Count, JournalPath = "j" };
            });
        var errors = new List<Exception>();
        vm.Error += errors.Add;
        vm.Confirm = _ => true;

        var first = vm.ScanCommand.ExecuteAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.SelectAllCommand.CanExecute(null));
        await vm.ScanCommand.ExecuteAsync();                                        // a second click while busy is ignored
        gate.Set();
        await first;
        Assert.Equal(1, scans);
        Assert.False(vm.IsBusy);
        Assert.Equal(2, vm.Rows.Count);
        Assert.True(vm.CanClean);

        vm.Rows[1].Selected = false;                                                // one row: the fake cleaner throws
        await vm.CleanCommand.ExecuteAsync();
        Assert.IsType<IOException>(Assert.Single(errors));
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsChanging);
        Assert.Equal(1, cleans);
        Assert.Equal(2, vm.Rows.Count);                                             // nothing reported as removed
    }

    // ---- 5. TEMP / TMP from the registry ----

    private sealed class EnvWithTemp(string userTemp) : ISystemEnvironment
    {
        public string GetTempPath() => @"C:\Users\u\AppData\Local\Temp\";
        public string GetWindowsDirectory() => @"C:\Windows";
        public string? GetRegistryEnvironment(bool machine, string name) => machine ? @"C:\Windows\TEMP" : userTemp;
        public IEnumerable<string> GetUserProfileDirectories() => [@"C:\Users\u", @"C:\Users\tmpuser"];
        public DateTime UtcNow => DateTime.UtcNow;
    }

    [Theory]
    [InlineData(@"D:\Downloads")]          // no temp in the name (the original only checked GetTempPath)
    [InlineData(@"C:\")]                   // a drive root
    [InlineData(@"C:\Users\tmpuser")]      // a profile folder whose name contains "tmp"
    [InlineData(@"D:\")]
    public void Unsafe_temp_variables_are_never_used(string userTemp)
    {
        var temps = TempFolderLocator.Locate(new EnvWithTemp(userTemp));
        Assert.DoesNotContain(temps, t => t.Equals(userTemp.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(@"C:\Windows\Temp", temps);
        Assert.False(TempFolderLocator.IsSafeTempFolder(@"C:\Temp-and-Users", [@"C:\Temp-and-Users\u"], @"C:\Windows"));   // contains a profile
        Assert.True(TempFolderLocator.IsSafeTempFolder(@"D:\Tmp", [@"C:\Users\u"], @"C:\Windows"));
    }

    // ---- 6. A folder that contains protected folders ----

    [Fact]
    public void Folders_above_protected_folders_and_long_path_prefixes_are_refused()
    {
        var p = new ProtectedPaths().AddRoot(@"C:\Users\u").AddRoot(@"C:\Users\u\AppData\Roaming").AddTree(@"C:\Windows\System32");
        Assert.NotNull(p.Check(@"C:\Users", isDirectory: true));                     // would take the profile along
        Assert.NotNull(p.Check(@"C:\Users\u\AppData", isDirectory: true));
        Assert.Null(p.Check(@"C:\Users\u\AppData\Local\Temp\x", isDirectory: true));
        Assert.Null(p.Check(@"C:\Users\notes.txt"));                                 // a file is not an ancestor
        Assert.NotNull(p.Check(@"\\?\C:\Windows\System32\drivers\x.sys"));
        Assert.NotNull(p.Check(@"\\?\C:\Users\u", isDirectory: true));
    }

    // ---- 8. Uninstall commands ----

    [Fact]
    public void Uninstall_commands_use_the_full_msiexec_path_and_only_real_product_codes()
    {
        CleanSuite.Uninstall.UninstallEntry Msi(string key) => new()
        {
            Name = "P", SubKey = key, Hive = RegHive.LocalMachine, ParentKey = "p", Flags = CleanSuite.Uninstall.UninstallFlags.WindowsInstaller,
        };
        var ok = Msi("{12345678-1234-1234-1234-1234567890AB}");
        Assert.Equal(CleanSuite.Uninstall.UninstallEntry.Msiexec + " /X {12345678-1234-1234-1234-1234567890AB}", ok.UninstallCommand);
        Assert.StartsWith("\"" + Environment.SystemDirectory, ok.RepairCommand);
        var forged = Msi("x /i http://host/a.msi /qn");
        Assert.Equal("", forged.UninstallCommand);                                  // no arguments smuggled through the key name
        Assert.Equal("", forged.ModifyCommand);
        Assert.Equal("", forged.RepairCommand);
    }

    [Fact]
    public void An_unquoted_path_resolves_to_the_longest_existing_program()
    {
        using var sb = new Sandbox();
        var planted = sb.File(@"My.exe", 1);                                        // what Windows would run for "…\My App\…"
        var real = sb.File(@"My App\unins.exe", 1);
        var r = CleanSuite.Uninstall.UninstallManager.ResolveCommand(Path.Combine(sb.Root, @"My App\unins.exe") + " /S /x");
        Assert.Equal((real, "/S /x"), r);
        Assert.Equal((planted, "/q"), CleanSuite.Uninstall.UninstallManager.ResolveCommand("\"" + planted + "\" /q"));
        Assert.Null(CleanSuite.Uninstall.UninstallManager.ResolveCommand(Path.Combine(sb.Root, @"Gone\setup.exe /u")));
        Assert.Null(CleanSuite.Uninstall.UninstallManager.ResolveCommand("msiexec /X {x}"));      // relative: never searched for
    }

    // ---- 9. One existence rule ----

    [Fact]
    public void Missing_paths_on_readable_or_absent_drives_are_still_missing()
    {
        using var sb = new Sandbox();
        var f = sb.File("here.txt", 1);
        Assert.True(PathProbe.HasAttributes(f));
        Assert.False(PathProbe.HasAttributes(Path.Combine(sb.Root, "gone.txt")));
        Assert.False(PathProbe.DriveUnreadable(Path.Combine(sb.Root, "gone.txt")));        // the drive is readable
        var free = Enumerable.Range('D', 23).Select(c => (char)c).First(c => !Directory.Exists(c + @":\"));
        Assert.False(PathProbe.ExistsInAnyView(free + @":\nothing\x.exe"));                // no such drive: really missing
    }

    // ---- 10. Nothing that changed after the scan is removed ----

    [Fact]
    public void A_registry_key_that_changed_after_the_scan_is_skipped()
    {
        using var sb = new Sandbox();
        using var roots = new AppHiveRegistryRoots(Path.Combine(sb.Root, "t.hiv"));
        var hkcu = roots.Root(RegHive.CurrentUser);
        using (var k = hkcu.CreateSubKey(@"Software\Old\App")) k.SetValue("x", "1");
        var finding = Finding(@"Software\Old\App") with
        {
            Target = CleanSuite.RegistryClean.RegTargetKind.Key, KeyStamp = CleanSuite.RegistryClean.KeyStamp.Of(hkcu, @"Software\Old\App"),
        };
        Assert.NotNull(finding.KeyStamp);
        using (var k = hkcu.OpenSubKey(@"Software\Old\App", writable: true)!) k.SetValue("reinstalled", "yes");   // changed in between
        var res = new CleanSuite.RegistryClean.RegistryCleaner(roots, Path.Combine(sb.Root, "_j"), Path.Combine(sb.Root, "bak")).Clean([finding]);
        Assert.Equal(0, res.Removed);
        Assert.Single(res.Skipped);
        using (var k = hkcu.OpenSubKey(@"Software\Old\App")) Assert.NotNull(k);

        var unchanged = finding with { KeyStamp = CleanSuite.RegistryClean.KeyStamp.Of(hkcu, @"Software\Old\App") };
        Assert.Equal(1, new CleanSuite.RegistryClean.RegistryCleaner(roots, Path.Combine(sb.Root, "_j"), Path.Combine(sb.Root, "bak")).Clean([unchanged]).Removed);
    }

    [Fact]
    public void A_junk_file_rewritten_with_the_same_size_is_kept()
    {
        using var sb = new Sandbox();
        var f = sb.File("cache.tmp", 32, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var item = Item(f) with { LastWriteUtc = File.GetLastWriteTimeUtc(f) };
        File.WriteAllBytes(f, Enumerable.Repeat((byte)1, 32).ToArray());           // same size, new content and time
        var res = new JunkRemover(new PickyBin(), new ProtectedPaths(), Path.Combine(sb.Root, "_j"))
            .Remove(new JunkRemovalRequest { Items = [item], Mode = RemovalMode.RecycleBin });
        Assert.Equal(0, res.Deleted);
        Assert.True(File.Exists(f));
    }

    // ---- 12, 13. Backups: ledger, refused destinations, linear verification ----

    [Fact]
    public void Backups_are_verified_against_the_ledger_and_risky_destinations_are_refused()
    {
        using var sb = new Sandbox();
        var bak = Path.Combine(sb.Root, "bak");
        using (var roots = new AppHiveRegistryRoots(Path.Combine(sb.Root, "t.hiv")))
        {
            var hkcu = roots.Root(RegHive.CurrentUser);
            using (var k = hkcu.CreateSubKey(@"Software\X")) k.SetValue("v", "1");
            using var w = new RegBackupWriter(bak);
            Assert.True(w.ExportValue(hkcu, RegHive.CurrentUser, @"Software\X", "v"));
        }
        Assert.Equal(BackupTrust.Verified, BackupLedger.Status(Directory.GetFiles(bak, "*.reg").Single()));   // recorded on close

        var own = sb.File(@"bak2\Reg own.reg", 10);
        BackupLedger.Record(own);
        Assert.Equal(BackupTrust.Verified, BackupLedger.Status(own));
        File.AppendAllText(own, "[HKEY_LOCAL_MACHINE\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]");
        Assert.Equal(BackupTrust.Modified, BackupLedger.Status(own));
        Assert.Equal(BackupTrust.Unknown, BackupLedger.Status(sb.File(@"bak2\planted.reg", 5)));

        var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "evil.lnk");
        Assert.NotNull(JunkBackupArchive.RefusedDestination(startup, trustedBackup: true));
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"App\x.dll");
        Assert.NotNull(JunkBackupArchive.RefusedDestination(pf, trustedBackup: false));
        Assert.Null(JunkBackupArchive.RefusedDestination(pf, trustedBackup: true));
        Assert.Null(JunkBackupArchive.RefusedDestination(Path.Combine(sb.Root, "x.tmp"), trustedBackup: false));
    }

    [Fact]
    public void The_registry_preview_lists_the_keys_a_backup_writes()
    {
        using var sb = new Sandbox();
        var reg = Path.Combine(sb.Root, "Reg 2026_10_21_01_02_03_PM.reg");
        File.WriteAllText(reg, "Windows Registry Editor Version 5.00\r\n\r\n[-HKEY_CURRENT_USER\\Software\\Gone]\r\n\r\n" +
                               "[HKEY_CURRENT_USER\\Software\\A]\r\n\"x\"=\"1\"\r\n\r\n[HKEY_LOCAL_MACHINE\\Software\\B]\r\n", System.Text.Encoding.Unicode);
        var item = CleanSuite.Restore.BackupCatalog.List([sb.Root]).Single();
        Assert.Equal(new[] { @"HKEY_CURRENT_USER\Software\A", @"HKEY_LOCAL_MACHINE\Software\B" }, CleanSuite.Restore.BackupCatalog.RegistryKeys(item));
        Assert.Equal(BackupTrust.Unknown, CleanSuite.Restore.BackupCatalog.Trust(item));
    }

    [Fact]
    public void A_large_zip_backup_is_verified_quickly_and_recorded()
    {
        using var sb = new Sandbox();
        var items = Enumerable.Range(0, 3000).Select(i => Item(sb.File($@"t\f{i}.tmp", 3))).ToList();
        using var journal = new OperationJournal("test", Path.Combine(sb.Root, "_j"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var zip = JunkBackupArchive.Create(Path.Combine(sb.Root, "bak"), items, journal, out var archived);
        Assert.Equal(3000, archived.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(BackupTrust.Verified, BackupLedger.Status(zip));
    }

    // ---- 14. Duplicate ignore lists with Arabic names ----

    [Fact]
    public void Duplicate_ignore_lists_in_UTF16_or_UTF8_match_Arabic_names()
    {
        using var sb = new Sandbox();
        var dir = Path.Combine(sb.Root, "Ignore_Dup");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "تقرير*.pdf  // comment", System.Text.Encoding.Unicode);
        File.WriteAllText(Path.Combine(dir, "b.txt"), "صورة.jpg", new System.Text.UTF8Encoding(true));
        Assert.Equal(new[] { "تقرير*.PDF", "صورة.JPG" }, CleanSuite.Duplicates.IgnoreFile.Load(dir));
    }

    // ---- 2. Duplicates: never the only copy ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    private static DupOptions DupOpt(params string[] roots) =>
        new() { Roots = roots, Patterns = DupProfiles.AllFiles, MinSize = 0, Excludes = Array.Empty<string>() };

    [Fact]
    public void A_hard_link_or_a_nested_root_is_not_a_duplicate_of_itself()
    {
        using var sb = new Sandbox();
        var file = sb.File(@"data\song.mp3", 500);
        var link = Path.Combine(sb.Root, @"other\song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Assert.True(CreateHardLinkW(link, file, IntPtr.Zero));                       // one file, two names

        var res = new DuplicateScanner(DupOpt(sb.Root, Path.Combine(sb.Root, "data"), sb.Root + "\\")).Scan();
        Assert.Empty(res.Groups);
        Assert.Equal(2, res.FilesSearched);                                         // the nested root is walked once

        Assert.Equal(new[] { sb.Root }, DuplicateScanner.DistinctRoots([sb.Root, sb.Root + @"\data", sb.Root.ToUpperInvariant() + "\\"]));
    }

    [Fact]
    public void A_duplicate_is_kept_when_the_other_copy_changed_or_is_the_same_file()
    {
        using var sb = new Sandbox();
        var a = sb.File(@"a\x.bin", 64);
        var b = sb.File(@"b\x.bin", 64);
        var groups = new DuplicateScanner(DupOpt(sb.Root)).Scan().Groups;
        Assert.Single(groups);
        File.WriteAllBytes(a, Enumerable.Repeat((byte)7, 64).ToArray());          // the kept copy changed after the scan
        var res = new CleanSuite.Duplicates.DuplicateRemover(new PickyBin(), new ProtectedPaths(), Path.Combine(sb.Root, "_j"))
            .Remove(groups, new HashSet<string>([b], StringComparer.OrdinalIgnoreCase), RemovalMode.RecycleBin);
        Assert.Equal(0, res.Deleted);
        Assert.True(File.Exists(b));
        Assert.Contains(res.Failed, f => f.Path == b && f.Reason.Contains("same content"));
    }

    // 16: every value comes back with its exact type and bytes, including ones a quoted line cannot carry.
    [Fact]
    public void Reg_backup_round_trips_every_value_type_exactly()
    {
        using var sb = new Sandbox();
        using var roots = new AppHiveRegistryRoots(Path.Combine(sb.Root, "t.hiv"));
        var hkcu = roots.Root(RegHive.CurrentUser);
        using (var k = hkcu.CreateSubKey(@"Software\RoundTrip"))
        {
            k.SetValue("plain", @"C:\Program Files\A ""quoted"" \\share", Microsoft.Win32.RegistryValueKind.String);
            k.SetValue("lines", "first\r\nsecond\ttab", Microsoft.Win32.RegistryValueKind.String);
            k.SetValue("", "default", Microsoft.Win32.RegistryValueKind.String);
            k.SetValue("expand", @"%SystemRoot%\x", Microsoft.Win32.RegistryValueKind.ExpandString);
            k.SetValue("multi", new[] { "a", "", "b" }, Microsoft.Win32.RegistryValueKind.MultiString);
            k.SetValue("dword", unchecked((int)0xFFFFFFFE), Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("qword", long.MinValue, Microsoft.Win32.RegistryValueKind.QWord);
            k.SetValue("bin", new byte[] { 0, 1, 255 }, Microsoft.Win32.RegistryValueKind.Binary);
            k.SetValue("none", new byte[] { 7, 8 }, Microsoft.Win32.RegistryValueKind.None);
            Raw(k, "sz-no-nul", 1, System.Text.Encoding.Unicode.GetBytes("abc"));            // REG_SZ without its terminator
            Raw(k, "dword-8", 4, [1, 2, 3, 4, 5, 6, 7, 8]);                                   // a DWORD of the wrong length
            Raw(k, "odd", 0x10, [9]);                                                         // an unnamed type
            using var sub = k.CreateSubKey("child");
            sub.SetValue("c", "1");
        }

        string first, second;
        using (var w = new RegBackupWriter(Path.Combine(sb.Root, "a"))) { w.ExportKey(hkcu, RegHive.CurrentUser, @"Software\RoundTrip"); first = w.FilePath!; }
        var text = File.ReadAllText(first);
        Assert.Contains("\"lines\"=hex(1):", text);
        Assert.Contains("\"sz-no-nul\"=\"abc\"", text);                                   // Windows reads it back with a terminator
        Assert.Contains("\"dword-8\"=hex(4):", text);
        Assert.Contains("\"odd\"=hex(10):09", text);
        Assert.Contains("\"dword\"=dword:fffffffe", text);
        Assert.Contains("\"plain\"=\"C:\\\\Program Files\\\\A \\\"quoted\\\" \\\\\\\\share\"", text);
        Assert.DoesNotContain("second\t", text);                                             // no raw line break or tab in the file

        hkcu.DeleteSubKeyTree(@"Software\RoundTrip");
        Assert.Equal(13, RegFileImporter.Import(first, roots));
        using (var w = new RegBackupWriter(Path.Combine(sb.Root, "b"))) { w.ExportKey(hkcu, RegHive.CurrentUser, @"Software\RoundTrip"); second = w.FilePath!; }
        Assert.Equal(text, File.ReadAllText(second));
        using (var k = hkcu.OpenSubKey(@"Software\RoundTrip")!)
        {
            Assert.Equal("first\r\nsecond\ttab", k.GetValue("lines"));
            Assert.Equal(@"C:\Program Files\A ""quoted"" \\share", k.GetValue("plain"));
        }
    }

    [Fact]
    public void Reg_import_reports_bad_data_instead_of_crashing()
    {
        using var sb = new Sandbox();
        using var roots = new AppHiveRegistryRoots(Path.Combine(sb.Root, "t.hiv"));
        foreach (var bad in new[] { "\"v\"=dword:zz", "\"v\"=hex:01,zz", "\"v\"=hex(q):01", "\"v\"=\"open" })
        {
            var reg = Path.Combine(sb.Root, "bad.reg");
            File.WriteAllText(reg, "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\Bad]\r\n" + bad + "\r\n", System.Text.Encoding.Unicode);
            Assert.Throws<InvalidDataException>(() => RegFileImporter.Import(reg, roots));
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string? name, int reserved, uint type, byte[] data, int size);

    private static void Raw(Microsoft.Win32.RegistryKey k, string name, uint type, byte[] data) =>
        Assert.Equal(0, RegSetValueExW(k.Handle, name, 0, type, data, data.Length));

    // 20: the shortcuts page on the shared view model: removed rows go, failed rows stay, nothing runs twice.
    [Fact]
    public async Task Shortcuts_view_model_keeps_failed_rows_and_drops_removed_ones()
    {
        static CleanSuite.Shortcuts.ShortcutItem Item(string name) => new()
        {
            Kind = CleanSuite.Shortcuts.ShortcutItemKind.InvalidShortcut, Name = name + ".lnk", Folder = @"C:\Links",
            Target = @"C:\Gone\" + name + ".exe", Reason = CleanSuite.Shortcuts.ShortcutReason.TargetMissing,
        };
        var items = new[] { Item("a"), Item("b"), Item("c") };
        int cleans = 0;
        var vm = new CleanSuite.App.ViewModels.ShortcutsViewModel(() => new CleanSuite.Shortcuts.ShortcutScanOptions(),
            scan: _ => new CleanSuite.App.ViewModels.ShortcutsViewModel.ScanOutcome(items, 10, 20),
            clean: chosen =>
            {
                cleans++;
                return new CleanSuite.Shortcuts.ShortcutRunResult
                {
                    Succeeded = chosen.Count - 1, Failed = [(chosen.First(i => i.Name == "b.lnk"), "in use")], JournalPath = "j",
                };
            });
        vm.Confirm = _ => true;

        await vm.ScanCommand.ExecuteAsync();
        Assert.Equal(3, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.True(r.Selected));                 // checked by default, as in the original
        Assert.False(vm.CancelCommand.CanExecute(null));                    // the scanner cannot be interrupted

        vm.Rows[2].Selected = false;                                        // "c" is kept out of the removal
        await vm.CleanCommand.ExecuteAsync();
        Assert.Equal(1, cleans);
        Assert.Equal(["b.lnk", "c.lnk"], vm.Rows.Select(r => r.Item.Name));  // "a" removed, "b" failed, "c" not chosen
        Assert.Equal(1, vm.LastClean!.Succeeded);

        vm.Retargeted(vm.Rows[0]);
        Assert.Equal(["c.lnk"], vm.Rows.Select(r => r.Item.Name));

        vm.SelectNoneCommand.Execute(null);
        Assert.False(vm.CanClean);
        await vm.CleanCommand.ExecuteAsync();                               // nothing checked: nothing runs
        Assert.Equal(1, cleans);
    }

    // 23: large files are compared by both ends first; only equal ends are read whole, and the whole file decides.
    [Fact]
    public void Duplicate_pre_check_never_merges_files_that_differ_only_in_the_middle()
    {
        using var sb = new Sandbox();
        var root = Path.Combine(sb.Root, "d");
        Directory.CreateDirectory(Path.Combine(root, "x"));
        Directory.CreateDirectory(Path.Combine(root, "y"));
        Directory.CreateDirectory(Path.Combine(root, "z"));
        Directory.CreateDirectory(Path.Combine(root, "w"));
        var data = new byte[300 * 1024];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(Path.Combine(root, "x", "big.bin"), data);
        File.WriteAllBytes(Path.Combine(root, "y", "big.bin"), data);                       // identical copy
        var middle = (byte[])data.Clone(); middle[150 * 1024] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(root, "z", "big.bin"), middle);                     // same ends, different middle
        var head = (byte[])data.Clone(); head[0] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(root, "w", "big.bin"), head);                       // differs in the first block

        Assert.Equal(DuplicateScanner.PartialHash(Path.Combine(root, "x", "big.bin")), DuplicateScanner.PartialHash(Path.Combine(root, "z", "big.bin")));
        Assert.NotEqual(DuplicateScanner.PartialHash(Path.Combine(root, "x", "big.bin")), DuplicateScanner.PartialHash(Path.Combine(root, "w", "big.bin")));

        foreach (var content in new[] { ContentCompare.Md5, ContentCompare.Crc32 })
        {
            var res = new DuplicateScanner(new DupOptions { Roots = [root], Content = content, Patterns = DupProfiles.AllFiles, MinSize = 0, Excludes = Array.Empty<string>() }).Scan();
            var group = Assert.Single(res.Groups);
            Assert.Equal(new[] { "x", "y" }, group.Files.Select(f => Path.GetFileName(Path.GetDirectoryName(f.FullPath)!)).Order());
        }
    }

    [Theory]
    [InlineData("file.INI", "*.ini", true)]
    [InlineData("Ünïcode-ß.txt", "ünÏ*.TXT", true)]
    [InlineData("a\\b\\c.tmp", "*\\B\\*.tmp", true)]
    [InlineData("abc", "a?d", false)]
    public void Wildcard_matches_without_allocating_the_same_as_before(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, CleanSuite.Matching.AceWildcard.IsMatch(text, pattern));
        var longName = new string('x', 2000) + ".log";                                     // beyond the stack buffers
        Assert.True(CleanSuite.Matching.AceWildcard.IsMatch(longName, "*.LOG"));
    }

    // 24: Arabic counted nouns take the form the number needs.
    [Theory]
    [InlineData(1, "حُذف 1 عنصر.")]
    [InlineData(2, "حُذف عنصران.")]
    [InlineData(5, "حُذف 5 عناصر.")]
    [InlineData(10, "حُذف 10 عناصر.")]
    [InlineData(11, "حُذف 11 عنصرًا.")]
    [InlineData(99, "حُذف 99 عنصرًا.")]
    [InlineData(100, "حُذف 100 عنصر.")]
    [InlineData(103, "حُذف 103 عناصر.")]
    [InlineData(1234, "حُذف 1,234 عنصرًا.")]
    [InlineData(0, "حُذف 0 عنصر.")]
    public void Arabic_plural_forms_follow_the_count(long n, string expected)
    {
        var old = CleanSuite.App.Localization.Loc.Instance.Language;
        try
        {
            CleanSuite.App.Localization.Loc.Instance.Language = CleanSuite.App.Localization.UiLanguage.Arabic;
            Assert.Equal(expected, CleanSuite.App.Localization.Loc.F("RgCleaned", CleanSuite.App.Localization.Loc.N0(n)));
            CleanSuite.App.Localization.Loc.Instance.Language = CleanSuite.App.Localization.UiLanguage.English;
            Assert.DoesNotContain("|", CleanSuite.App.Localization.Loc.F("RgCleaned", CleanSuite.App.Localization.Loc.N0(n)));
        }
        finally { CleanSuite.App.Localization.Loc.Instance.Language = old; }
    }

    [Fact]
    public void Every_plural_token_has_four_forms_and_no_string_is_left_with_a_bar()
    {
        var token = new System.Text.RegularExpressions.Regex(@"\{\d+:[^{}|]+\|[^{}|]+\|[^{}|]+\|[^{}|]+\}");
        var old = CleanSuite.App.Localization.Loc.Instance.Language;
        try
        {
            CleanSuite.App.Localization.Loc.Instance.Language = CleanSuite.App.Localization.UiLanguage.Arabic;
            foreach (var (key, ar) in CleanSuite.App.Localization.Strings.Ar)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(ar, @"\{\d+:[^}]*\|")) continue;     // file-dialog filters also use '|'
                Assert.True(token.IsMatch(ar), key);
                var holes = System.Text.RegularExpressions.Regex.Matches(ar, @"\{(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
                var args = Enumerable.Range(0, holes.Max() + 1).Select(_ => (object?)"7").ToArray();
                Assert.DoesNotMatch(@"\{\d+:[^}]*\|", CleanSuite.App.Localization.Loc.F(key, args));
            }
        }
        finally { CleanSuite.App.Localization.Loc.Instance.Language = old; }
    }

    // 25: "When Windows starts" is a delayed logon trigger (a boot trigger never ran for an interactive task), older tasks
    // with a boot trigger still read back, and a run nobody watches cleans only the low-risk registry categories.
    [Fact]
    public void Startup_schedule_uses_a_delayed_logon_trigger_and_reads_old_boot_triggers()
    {
        var s = new CleanSuite.Scheduler.Schedule(CleanSuite.Scheduler.ScheduledTasks.Junk, CleanSuite.Scheduler.ScheduleKind.AtStartup, new DateTime(2026, 10, 21, 9, 0, 0));
        var xml = CleanSuite.Scheduler.TaskXml.Build(s, @"C:\Apps\CleanSuite.App.exe", @"PC\user");
        Assert.DoesNotContain("BootTrigger", xml);
        Assert.Contains("<Delay>" + CleanSuite.Scheduler.TaskXml.StartupDelay + "</Delay>", xml);
        Assert.Equal(CleanSuite.Scheduler.ScheduleKind.AtStartup, CleanSuite.Scheduler.TaskXml.Parse(xml)!.Kind);

        var plainLogon = CleanSuite.Scheduler.TaskXml.Build(s with { Kind = CleanSuite.Scheduler.ScheduleKind.AtLogon }, @"C:\Apps\CleanSuite.App.exe", @"PC\user");
        Assert.DoesNotContain("<Delay>", plainLogon);
        Assert.Equal(CleanSuite.Scheduler.ScheduleKind.AtLogon, CleanSuite.Scheduler.TaskXml.Parse(plainLogon)!.Kind);

        var legacy = xml.Replace("<LogonTrigger>", "<BootTrigger>").Replace("</LogonTrigger>", "</BootTrigger>");
        Assert.Equal(CleanSuite.Scheduler.ScheduleKind.AtStartup, CleanSuite.Scheduler.TaskXml.Parse(legacy)!.Kind);

        var unattended = CleanSuite.Scheduler.ScheduledRunner.UnattendedRegistryCategories;
        foreach (var risky in new[] { CleanSuite.RegistryClean.RegCategory.FileExtensions, CleanSuite.RegistryClean.RegCategory.FileAssociations,
                     CleanSuite.RegistryClean.RegCategory.ActiveXCom, CleanSuite.RegistryClean.RegCategory.SharedDlls,
                     CleanSuite.RegistryClean.RegCategory.ShellExtensions, CleanSuite.RegistryClean.RegCategory.AddRemoveEntries,
                     CleanSuite.RegistryClean.RegCategory.SystemEntries, CleanSuite.RegistryClean.RegCategory.UserEntries })
            Assert.False(unattended.HasFlag(risky), risky.ToString());
        Assert.True(unattended.HasFlag(CleanSuite.RegistryClean.RegCategory.HistoryLists));
    }

    // 28: SQLite's SUBSTR counts from 1; with 0 the test read five characters and "place:" queries were never protected.
    [Fact]
    public void Firefox_history_cleaning_keeps_place_queries_and_drops_unvisited_pages()
    {
        using var sb = new Sandbox();
        var db = Path.Combine(sb.Root, "places.sqlite");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT, frecency INTEGER DEFAULT -1, visit_count INTEGER DEFAULT 0);
                CREATE TABLE moz_historyvisits (id INTEGER PRIMARY KEY, place_id INTEGER);
                CREATE TABLE moz_bookmarks (id INTEGER PRIMARY KEY, fk INTEGER, parent INTEGER);
                CREATE TABLE moz_anno_attributes (id INTEGER PRIMARY KEY, name TEXT);
                CREATE TABLE moz_annos (id INTEGER PRIMARY KEY, place_id INTEGER, anno_attribute_id INTEGER, mime_type TEXT, content TEXT,
                    flags INTEGER, expiration INTEGER, type INTEGER, dateAdded INTEGER, lastModified INTEGER);
                CREATE TABLE moz_items_annos (id INTEGER PRIMARY KEY, item_id INTEGER, anno_attribute_id INTEGER, mime_type TEXT, content TEXT,
                    flags INTEGER, expiration INTEGER, type INTEGER, dateAdded INTEGER, lastModified INTEGER);
                CREATE TABLE moz_inputhistory (place_id INTEGER, input TEXT, use_count INTEGER);
                CREATE TABLE moz_hosts (id INTEGER PRIMARY KEY, host TEXT);
                INSERT INTO moz_places (id, url) VALUES (1, 'place:sort=8&maxResults=10'), (2, 'https://example.org/'), (3, 'https://kept.example/');
                INSERT INTO moz_historyvisits (place_id) VALUES (2);
                INSERT INTO moz_bookmarks (fk, parent) VALUES (3, 1);
                INSERT INTO moz_hosts (host) VALUES ('example.org');
                """;
            cmd.ExecuteNonQuery();
            foreach (var statement in CleanSuite.History.FirefoxCleaner.PlacesStatements)
            {
                cmd.CommandText = statement;
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText = "SELECT group_concat(id) FROM (SELECT id FROM moz_places ORDER BY id)";
            Assert.Equal("1,3", cmd.ExecuteScalar());                                // the query and the bookmark stay, the visited page goes
            cmd.CommandText = "SELECT COUNT(*) FROM moz_historyvisits";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    // CleanSuite's own registry protection list: part of the program, always loaded, and never in the way of the areas the
    // scan checks on purpose.
    [Fact]
    public void Built_in_registry_protection_list_is_loaded_and_leaves_the_scanned_areas_alone()
    {
        var protective = CleanSuite.RegistryClean.IgnoreWords.Protective;
        Assert.Contains(@"\COMPONENT BASED SERVICING\", protective);
        Assert.Contains(@"\CURRENTVERSION\APPX\", protective);
        Assert.Contains(@"\APPMODEL\STATEREPOSITORY\", protective);
        Assert.All(protective, w => Assert.Equal(w, w.Trim().ToUpperInvariant()));
        Assert.DoesNotContain(protective, w => w.Contains("//"));

        string[] scannedOnPurpose =
        [
            @"HKEY_LOCAL_MACHINE\SOFTWARE\MICROSOFT\WINDOWS\CURRENTVERSION\INSTALLER\FOLDERS\C:\GONE\\\INVALID INSTALLER REFERENCE",
            @"HKEY_LOCAL_MACHINE\SYSTEM\CURRENTCONTROLSET\SERVICES\LANMANSERVER\SHARES\DATA\PATH=D:\GONE\INVALID SHARE",
            @"HKEY_LOCAL_MACHINE\SYSTEM\CONTROLSET001\SERVICES\SHAREDACCESS\PARAMETERS\FIREWALLPOLICY\FIREWALLRULES\{X}\C:\GONE\APP.EXE\INVALID PATH",
            @"HKEY_CURRENT_USER\SOFTWARE\MICROSOFT\WINDOWS\CURRENTVERSION\EXPLORER\RUNMRU\A\C:\GONE\X.EXE\INVALID HISTORY",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\MICROSOFT\WINDOWS\CURRENTVERSION\APP PATHS\GONE.EXE\\C:\GONE\GONE.EXE\INVALID PATH",
        ];
        foreach (var composite in scannedOnPurpose)
            Assert.DoesNotContain(protective, w => composite.Contains(w, StringComparison.Ordinal));

        var user = CleanSuite.RegistryClean.IgnoreWords.UserFolder;
        Assert.EndsWith(@"CleanSuite\Ignore_Reg", user);
        Assert.DoesNotContain(CleanSuite.RegistryClean.IgnoreWords.DefaultFolders(@"Z:\does\not\exist"), f => f.StartsWith("Z:", StringComparison.OrdinalIgnoreCase));
    }

    // CleanSuite's own junk protection list: live database checkpoints and program files are never junk by name alone,
    // while CHKDSK fragments and real junk still are.
    [Theory]
    [InlineData("edb.chk", @"C:\Users\u\AppData\Local\Packages\App\AppData\Indexed DB\edb.chk", true)]
    [InlineData("MSS.chk", @"C:\ProgramData\Microsoft\Search\Data\Applications\Windows\MSS.chk", true)]
    [InlineData("ChkList.tcl", @"C:\Python311\tcl\tix8.4.3\ChkList.tcl", true)]
    [InlineData("tool.exe", @"D:\Tools\tool.exe", true)]
    [InlineData("FILE0000.CHK", @"C:\FOUND.000\FILE0000.CHK", false)]
    [InlineData("report.tmp", @"C:\Work\report.tmp", false)]
    [InlineData("setup.log", @"C:\Work\setup.log", false)]
    public void Built_in_junk_protection_list_keeps_program_and_database_files(string name, string path, bool kept)
    {
        Assert.Equal(kept, CleanSuite.Junk.IgnoreList.BuiltIn.Match(name, path, CleanSuite.Junk.ScanMode.Drive) != null);
    }

    // The other lists CleanSuite now has itself: empty folders the Disk page leaves alone, files Erase History never deletes.
    [Theory]
    [InlineData(@"C:\Program Files\Vendor\Plugins", true)]
    [InlineData(@"C:\ProgramData\Vendor\Cache", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Vendor\Logs", true)]
    [InlineData(@"C:\Users\u\Desktop\New folder", true)]
    [InlineData(@"C:\Users\u\OneDrive\Empty", true)]
    [InlineData(@"C:\Windows\Temp\x", true)]
    [InlineData(@"D:\Data\Old project\empty", false)]
    [InlineData(@"C:\Users\u\Documents\empty", false)]
    public void Default_empty_folder_exclusions_cover_program_and_user_places(string folder, bool excluded)
    {
        Assert.Equal(excluded, CleanSuite.DiskUsage.EmptyFolderFinder.DefaultExcludes.Any(p => CleanSuite.Matching.AceWildcard.IsMatch(folder, p)));
        Assert.Contains("*.exe", CleanSuite.History.RulePlanner.DefaultNeverDelete);
        Assert.Contains("*.dll", CleanSuite.History.RulePlanner.DefaultNeverDelete);
    }
}
