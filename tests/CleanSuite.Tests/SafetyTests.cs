using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>The shared safety layer: Recycle Bin refusals, protected folders, existence checks, backups and the ledger, .reg backup and import. Temp sandboxes and a private registry hive only.</summary>
public class SafetyTests
{
    private static CleanSuite.RegistryClean.RegFinding Finding(string key) => new()
    {
        Category = CleanSuite.RegistryClean.RegCategory.UserEntries, Hive = RegHive.CurrentUser, Key = key, ValueName = "v", Data = @"C:\gone.exe",
        Target = CleanSuite.RegistryClean.RegTargetKind.Value, Reason = "InvalidPath", ReasonText = "missing",
    };

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string? name, int reserved, uint type, byte[] data, int size);

    private static void Raw(Microsoft.Win32.RegistryKey k, string name, uint type, byte[] data) =>
        Assert.Equal(0, RegSetValueExW(k.Handle, name, 0, type, data, data.Length));

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
        var zip = JunkBackupArchive.Create(Path.Combine(sb.Root, "bak"), items.Select(i => new RemovalItem(i.FullPath, i.Size, i.LastWriteUtc)).ToList(),
            journal, out var archived);
        Assert.Equal(3000, archived.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(BackupTrust.Verified, BackupLedger.Status(zip));
    }

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

    [Fact]
    public void A_registry_key_that_changed_after_the_scan_is_skipped()
    {
        using var sb = new Sandbox();
        using var roots = new AppHiveRegistryRoots(Path.Combine(sb.Root, "t.hiv"));
        var hkcu = roots.Root(RegHive.CurrentUser);
        using (var k = hkcu.CreateSubKey(@"Software\Old\App")) k.SetValue("x", "1");
        var finding = Finding(@"Software\Old\App") with
        {
            Target = CleanSuite.RegistryClean.RegTargetKind.Key,
            KeyStamp = CleanSuite.RegistryClean.KeyStamp.Of(hkcu, @"Software\Old\App"),
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
}
