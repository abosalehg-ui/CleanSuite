using CleanSuite.Safety;
using CleanSuite.Uninstall;
using Microsoft.Win32;

namespace CleanSuite.Tests;

/// <summary>Uninstall Manager on a private hive file (RegLoadAppKey) with a fake launcher — no real registry, nothing started.</summary>
public class UninstallTests
{
    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<string> Started { get; } = new();
        public void Start(string commandLine) => Started.Add(commandLine);
    }

    private sealed class Env : IDisposable
    {
        public Sandbox Sb { get; } = new();
        public AppHiveRegistryRoots Roots { get; }
        public FakeLauncher Launcher { get; } = new();
        public Env() => Roots = new AppHiveRegistryRoots(Path.Combine(Sb.Root, "test.hiv"));

        public RegistryKey Entry(RegHive hive, string sub, params (string Name, object Value)[] values) =>
            Entry(hive, UninstallScanner.UninstallKey, sub, values);

        public RegistryKey Entry(RegHive hive, string parent, string sub, params (string Name, object Value)[] values)
        {
            var k = Roots.Root(hive).CreateSubKey(parent + "\\" + sub, writable: true);
            foreach (var (n, v) in values) k.SetValue(n, v);
            return k;
        }

        public UninstallScanner Scanner(bool updates = false) =>
            new(Roots, "S-1-5-21-TEST", driveType: _ => DriveKind.Fixed) { ShowUpdates = updates };

        public UninstallManager Manager() => new(Roots, Launcher, Path.Combine(Sb.Root, "_journal"), Path.Combine(Sb.Root, "_bak"));

        public void Dispose() { Roots.Dispose(); Sb.Dispose(); }
    }

    [Fact]
    public void Lists_the_three_keys_in_order_with_the_original_filters()
    {
        using var env = new Env();
        var exe = env.Sb.File(@"apps\unins000.exe");
        env.Entry(RegHive.LocalMachine, "App64", ("DisplayName", "Sixty Four"), ("UninstallString", "\"" + exe + "\" /S"),
            ("Publisher", "Acme"), ("DisplayVersion", "1.2"), ("EstimatedSize", 2048), ("InstallDate", "20240315")).Dispose();
        env.Entry(RegHive.CurrentUser, "UserApp", ("DisplayName", "Per User"), ("UninstallString", "\"" + exe + "\"")).Dispose();
        env.Entry(RegHive.LocalMachine, UninstallScanner.UninstallKey32, "App32", ("DisplayName", "Thirty Two"), ("UninstallString", exe)).Dispose();
        env.Entry(RegHive.LocalMachine, "Sys", ("DisplayName", "Component"), ("UninstallString", exe), ("SystemComponent", 1)).Dispose();
        env.Entry(RegHive.LocalMachine, "KB1", ("DisplayName", "Update"), ("UninstallString", exe), ("ParentKeyName", "App64")).Dispose();
        env.Entry(RegHive.LocalMachine, "WIC", ("DisplayName", "Windows Imaging"), ("UninstallString", exe)).Dispose();
        env.Entry(RegHive.LocalMachine, "Locked", ("DisplayName", "Locked"), ("UninstallString", exe), ("NoRemove", 1), ("NoModify", 1)).Dispose();
        env.Entry(RegHive.LocalMachine, "QuietOnly", ("DisplayName", "Quiet"), ("QuietUninstallString", exe)).Dispose();
        env.Entry(RegHive.LocalMachine, "Empty").Dispose();

        var list = env.Scanner().Scan();
        Assert.Equal(new[] { "Sixty Four", "Per User", "Thirty Two" }, list.Select(e => e.Name));
        var a = list[0];
        Assert.Equal("Acme", a.Publisher);
        Assert.Equal("1.2", a.Version);
        Assert.Equal(2048, a.SizeKb);
        Assert.Equal(new DateTime(2024, 3, 15), a.InstalledOn);
        Assert.False(a.PossiblyInvalid);
        Assert.True(list[2].Is32Bit);
        Assert.Contains(env.Scanner(updates: true).Scan(), e => e.Name == "Update");
    }

    [Fact]
    public void Nameless_entries_show_their_key_name_only_when_nearly_empty()
    {
        using var env = new Env();
        env.Entry(RegHive.LocalMachine, "OldTool", ("UninstallString", @"C:\x.exe")).Dispose();
        env.Entry(RegHive.LocalMachine, "{ABC}", ("UninstallString", @"C:\x.exe")).Dispose();
        env.Entry(RegHive.LocalMachine, "Busy", ("UninstallString", @"C:\x.exe"), ("Other", "1")).Dispose();
        Assert.Equal("OldTool", Assert.Single(env.Scanner().Scan()).Name);
    }

    [Fact]
    public void Missing_uninstaller_is_possibly_invalid()
    {
        using var env = new Env();
        env.Entry(RegHive.LocalMachine, "Gone", ("DisplayName", "Gone"), ("UninstallString", "\"" + Path.Combine(env.Sb.Root, @"gone\unins.exe") + "\" /S")).Dispose();
        env.Entry(RegHive.LocalMachine, "Dll", ("DisplayName", "Dll"), ("UninstallString", @"C:\NoSuchWin\System32\rundll32.exe gone.dll,Remove")).Dispose();
        var list = env.Scanner().Scan();
        Assert.True(list.Single(e => e.Name == "Gone").PossiblyInvalid);
        Assert.False(list.Single(e => e.Name == "Dll").PossiblyInvalid);              // a RUNDLL path is never flagged
    }

    [Fact]
    public void Windows_installer_entries_use_msiexec_and_are_deduplicated()
    {
        using var env = new Env();
        const string code = "{12345678-ABCD-EF01-2345-6789ABCDEF01}";
        env.Entry(RegHive.LocalMachine, code, ("DisplayName", "Msi App"), ("UninstallString", "MsiExec.exe /X" + code), ("WindowsInstaller", 1)).Dispose();
        env.Entry(RegHive.LocalMachine, UninstallScanner.UninstallKey32, code, ("DisplayName", "Msi App"), ("UninstallString", "MsiExec.exe /X" + code), ("WindowsInstaller", 1)).Dispose();
        var packed = UninstallScanner.PackGuid(code);
        Assert.Equal("87654321DCBA10FE32547698BADCFE10", packed);
        using (var p = env.Roots.Root(RegHive.LocalMachine).CreateSubKey(
                   $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\{packed}\InstallProperties"))
        {
            p.SetValue("Publisher", "Msi Corp");
            p.SetValue("InstallDate", "20230102");
        }
        var e = Assert.Single(env.Scanner().Scan());
        Assert.Equal("Msi Corp", e.Publisher);
        Assert.Equal(new DateTime(2023, 1, 2), e.InstalledOn);
        Assert.Equal(UninstallEntry.Msiexec + " /X " + code, e.UninstallCommand);
        Assert.Equal(UninstallEntry.Msiexec + " /fv " + code, e.RepairCommand);
        Assert.False(e.CanRemoveEntry);
        var m = env.Manager();
        Assert.True(m.Uninstall(e).Success);
        Assert.True(m.Repair(e).Success);
        Assert.Equal(new[] { UninstallEntry.Msiexec + " /X " + code, UninstallEntry.Msiexec + " /fv " + code }, env.Launcher.Started);
        Assert.False(m.RemoveEntry(e).Success);
    }

    [Fact]
    public void Uninstall_prefers_the_normal_command_and_refuses_cleansuite_itself()
    {
        using var env = new Env();
        env.Entry(RegHive.LocalMachine, "A", ("DisplayName", "A"), ("UninstallString", "a.exe /u"), ("QuietUninstallString", "a.exe /u /quiet"),
            ("ModifyPath", "a.exe /modify")).Dispose();
        env.Entry(RegHive.LocalMachine, "Self", ("DisplayName", "CleanSuite"), ("UninstallString", "cs.exe")).Dispose();
        var list = env.Scanner().Scan();
        var m = env.Manager();
        Assert.True(m.Uninstall(list.Single(e => e.Name == "A")).Success);
        Assert.True(m.Modify(list.Single(e => e.Name == "A")).Success);
        Assert.False(m.Uninstall(list.Single(e => e.Name == "CleanSuite")).Success);
        Assert.False(m.Repair(list.Single(e => e.Name == "A")).Success);              // repair is Windows Installer only
        Assert.Equal(new[] { "a.exe /u", "a.exe /modify" }, env.Launcher.Started);
    }

    [Fact]
    public void Remove_entry_backs_up_the_key_before_deleting_and_can_be_restored()
    {
        using var env = new Env();
        using (var k = env.Entry(RegHive.CurrentUser, "Orphan", ("DisplayName", "Orphan App"), ("UninstallString", @"C:\gone\u.exe")))
            k.CreateSubKey("Sub").SetValue("x", 5);
        var e = Assert.Single(env.Scanner().Scan());
        var r = env.Manager().RemoveEntry(e);
        Assert.True(r.Success);
        Assert.Null(env.Roots.Root(RegHive.CurrentUser).OpenSubKey(e.KeyPath));
        var reg = File.ReadAllText(r.BackupPath!);
        Assert.Contains(@"[HKEY_CURRENT_USER\" + e.KeyPath + "]", reg);
        Assert.Contains(@"""DisplayName""=""Orphan App""", reg);
        RegFileImporter.Import(r.BackupPath!, env.Roots);
        Assert.Equal("Orphan App", Assert.Single(env.Scanner().Scan()).Name);
        Assert.Equal(5, env.Roots.Root(RegHive.CurrentUser).OpenSubKey(e.KeyPath + @"\Sub")!.GetValue("x"));
        Assert.Contains(OperationJournal.Read(Directory.GetFiles(Path.Combine(env.Sb.Root, "_journal"), "journal.jsonl", SearchOption.AllDirectories).Single()),
            j => j.Action == "remove-entry" && j.Success);
    }

    [Fact]
    public void Report_and_helpers_use_invariant_formats()
    {
        using var env = new Env();
        var file = Path.Combine(env.Sb.Root, "report.txt");
        var entries = new[]
        {
            new UninstallEntry { Name = "A", SubKey = "A", Hive = RegHive.LocalMachine, ParentKey = "p", Publisher = "P", Version = "1",
                SizeKb = 12_600, InstalledOn = new DateTime(2024, 1, 2) },
        };
        UninstallManager.WriteReport(file, entries, tabDelimited: true, ["Name"]);
        Assert.Equal("A\tP\t2024-01-02\t12.3 MB\t1\r\n", File.ReadAllText(file));
        UninstallManager.WriteReport(file, entries, tabDelimited: false, ["Name", "Publisher"]);
        Assert.Contains("Total 1 item(s)", File.ReadAllText(file));
        Assert.Equal((@"C:\Program Files\X\u.exe", "/S"), UninstallManager.SplitCommand(@"C:\Program Files\X\u.exe /S"));
        Assert.Equal((@"C:\a b\u.exe", "-x"), UninstallManager.SplitCommand("\"C:\\a b\\u.exe\" -x"));
        Assert.Null(UninstallScanner.ParseDate("2024"));
        Assert.Equal(new DateTime(2020, 5, 6), UninstallScanner.ParseDate("2020-05-06T00:00"));
        Assert.Equal(new DateTime(2020, 5, 6), UninstallScanner.ParseDate("2020/05/06"));
        // Hijri (Umm al-Qura) dates written by installers on an Arabic system: 1447-01-23 AH = 2025-07-18.
        Assert.Equal(new DateTime(2025, 7, 18), UninstallScanner.ParseDate("14470123"));
        Assert.Null(UninstallScanner.ParseDate("20241341"));
    }
}
