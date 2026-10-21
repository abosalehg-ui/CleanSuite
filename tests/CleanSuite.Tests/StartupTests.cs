using CleanSuite.Safety;
using CleanSuite.Startup;
using Microsoft.Win32;

namespace CleanSuite.Tests;

/// <summary>Auto-Start Manager on a private hive and temporary startup folders.</summary>
public class StartupTests
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private sealed class Env : IDisposable
    {
        public Sandbox Sb { get; } = new();
        public AppHiveRegistryRoots Roots { get; }
        public string UserStartup => Path.Combine(Sb.Root, "UserStartup");
        public string CommonStartup => Path.Combine(Sb.Root, "CommonStartup");
        public StartupMonitor Monitor { get; }

        public Env()
        {
            Roots = new AppHiveRegistryRoots(Path.Combine(Sb.Root, "t.hiv"));
            Directory.CreateDirectory(UserStartup);
            Directory.CreateDirectory(CommonStartup);
            Monitor = new StartupMonitor(Path.Combine(Sb.Root, "monitor.json"), Roots);
        }

        public RegistryKey Hkcu => Roots.Root(RegHive.CurrentUser);

        public StartupManager Manager() => new(Roots,
            StartupLocations.RegistryLocations.Concat(StartupLocations.FolderLocations(UserStartup, CommonStartup)).ToList(),
            Path.Combine(Sb.Root, "_journal"), Path.Combine(Sb.Root, "_bak"), Monitor);

        public void Dispose() { Roots.Dispose(); Sb.Dispose(); }
    }

    [Fact]
    public void Scans_run_keys_states_and_startup_folders()
    {
        using var env = new Env();
        var exe = env.Sb.File("app.exe");
        using (var k = env.Hkcu.CreateSubKey(Run))
        {
            k.SetValue("App", $"\"{exe}\" /min");
            k.SetValue("Gone", @"C:\does\not\exist.exe");
            k.SetValue("", "ignored default value");
        }
        var approved = new byte[12]; approved[0] = 3;
        env.Hkcu.CreateSubKey(StartupLocations.Approved + @"\Run")!.SetValue("Gone", approved, RegistryValueKind.Binary);
        env.Hkcu.CreateSubKey(Run + "-")!.SetValue("OldAce", "x.exe");             // disabled by the original (legacy)
        File.WriteAllText(Path.Combine(env.UserStartup, "note.txt"), "x");

        var entries = env.Manager().Scan();
        var app = entries.Single(e => e.Name == "App");
        Assert.True(app.Enabled);
        Assert.Equal(exe, app.TargetPath, ignoreCase: true);
        Assert.True(app.TargetExists);
        var gone = entries.Single(e => e.Name == "Gone");
        Assert.Equal(EntryState.DisabledApproved, gone.State);
        Assert.False(gone.TargetExists);
        Assert.Equal(EntryState.DisabledLegacy, entries.Single(e => e.Name == "OldAce").State);
        Assert.Equal("user.startup", entries.Single(e => e.Name == "note.txt").Location.Id);
        Assert.DoesNotContain(entries, e => e.Name.Length == 0);
    }

    [Fact]
    public void Disable_and_enable_use_startup_approved_and_back_up_first()
    {
        using var env = new Env();
        env.Hkcu.CreateSubKey(Run)!.SetValue("App", "app.exe");
        var m = env.Manager();
        var res = m.Disable(m.Scan().Single(e => e.Name == "App"));
        Assert.True(res.Success, res.Error);
        var data = (byte[])env.Hkcu.OpenSubKey(StartupLocations.Approved + @"\Run")!.GetValue("App")!;
        Assert.Equal(12, data.Length);
        Assert.Equal(3, data[0]);
        Assert.True(BitConverter.ToInt64(data, 4) > 0);                           // FILETIME of the change
        Assert.Equal("app.exe", env.Hkcu.OpenSubKey(Run)!.GetValue("App"));      // the Run value itself is untouched
        var e2 = m.Scan().Single(e => e.Name == "App");
        Assert.Equal(EntryState.DisabledApproved, e2.State);
        Assert.NotNull(e2.DisabledSinceUtc);

        Assert.True(m.Enable(e2).Success);
        data = (byte[])env.Hkcu.OpenSubKey(StartupLocations.Approved + @"\Run")!.GetValue("App")!;
        Assert.Equal(2, data[0]);
        Assert.Equal(0, BitConverter.ToInt64(data, 4));
        Assert.True(m.Scan().Single(e => e.Name == "App").Enabled);
    }

    [Fact]
    public void Locations_without_startup_approved_use_the_dash_key_both_ways()
    {
        using var env = new Env();
        var runOnce = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
        env.Hkcu.CreateSubKey(runOnce)!.SetValue("Setup", @"%TEMP%\setup.exe", RegistryValueKind.ExpandString);
        var m = env.Manager();
        Assert.True(m.Disable(m.Scan().Single(e => e.Name == "Setup")).Success);
        Assert.Null(env.Hkcu.OpenSubKey(runOnce)!.GetValue("Setup"));
        using (var dash = env.Hkcu.OpenSubKey(runOnce + "-")!)
        {
            Assert.Equal(RegistryValueKind.ExpandString, dash.GetValueKind("Setup"));   // type and data kept
            Assert.Equal(@"%TEMP%\setup.exe", dash.GetValue("Setup", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
        var legacy = m.Scan().Single(e => e.Name == "Setup");
        Assert.Equal(EntryState.DisabledLegacy, legacy.State);
        Assert.True(m.Enable(legacy).Success);
        Assert.Equal(RegistryValueKind.ExpandString, env.Hkcu.OpenSubKey(runOnce)!.GetValueKind("Setup"));
    }

    [Fact]
    public void Delete_backs_up_registry_values_and_moves_startup_files()
    {
        using var env = new Env();
        env.Hkcu.CreateSubKey(Run)!.SetValue("App", "app.exe");
        var file = Path.Combine(env.UserStartup, "Tool.lnk");
        File.WriteAllText(file, "not a real shortcut");
        var m = env.Manager();
        var res = m.Delete(m.Scan().Single(e => e.Name == "App"));
        Assert.True(res.Success, res.Error);
        Assert.Null(env.Hkcu.OpenSubKey(Run)!.GetValue("App"));
        Assert.Contains("\"App\"=\"app.exe\"", File.ReadAllText(res.RegistryBackupPath!));
        RegFileImporter.Import(res.RegistryBackupPath!, env.Roots);                  // the backup restores it
        Assert.Equal("app.exe", env.Hkcu.OpenSubKey(Run)!.GetValue("App"));

        Assert.True(m.Delete(m.Scan().Single(e => e.Name == "Tool.lnk")).Success);
        Assert.False(File.Exists(file));
        Assert.Single(Directory.GetFiles(Path.Combine(env.Sb.Root, "_bak"), "Tool.lnk", SearchOption.AllDirectories));
    }

    [Fact]
    public void Startup_folder_entries_use_the_startup_folder_approved_key()
    {
        using var env = new Env();
        File.WriteAllText(Path.Combine(env.UserStartup, "a.cmd"), "@echo off");
        var m = env.Manager();
        Assert.True(m.Disable(m.Scan().Single(e => e.Name == "a.cmd")).Success);
        Assert.Equal(3, ((byte[])env.Hkcu.OpenSubKey(StartupLocations.Approved + @"\StartupFolder")!.GetValue("a.cmd")!)[0]);
        Assert.True(File.Exists(Path.Combine(env.UserStartup, "a.cmd")));          // disabling never moves the file
    }

    [Fact]
    public void Entries_that_re_enable_themselves_are_flagged()
    {
        using var env = new Env();
        env.Hkcu.CreateSubKey(Run)!.SetValue("Nag", "nag.exe");
        var m = env.Manager();
        m.Disable(m.Scan().Single(e => e.Name == "Nag"));
        // The program puts itself back: StartupApproved reset to enabled.
        var enabled = new byte[12]; enabled[0] = 2;
        env.Hkcu.OpenSubKey(StartupLocations.Approved + @"\Run", writable: true)!.SetValue("Nag", enabled, RegistryValueKind.Binary);
        Assert.True(env.Manager().Scan().Single(e => e.Name == "Nag").ReEnabledItself);
        m.Enable(env.Manager().Scan().Single(e => e.Name == "Nag"));               // enabled on purpose: no longer flagged
        Assert.False(env.Manager().Scan().Single(e => e.Name == "Nag").ReEnabledItself);
    }

    [Fact]
    public void Ace_md5_monitor_values_are_recognised()
    {
        using var env = new Env();
        const string cmd = "\"C:\\Program Files\\Docker\\Docker\\Docker Desktop.exe\"";
        env.Hkcu.CreateSubKey(Run)!.SetValue("Docker Desktop", cmd);
        var md5 = StartupMonitor.AceMd5("Docker Desktop", cmd);
        env.Hkcu.CreateSubKey(@"Software\Acelogix\Ace Utilities\Auto\Md5")!.SetValue($@"User\{md5}-1", 0, RegistryValueKind.DWord);
        Assert.True(env.Manager().Scan().Single(e => e.Name == "Docker Desktop").ReEnabledItself);
        Assert.Equal(32, md5.Length);
        Assert.Equal(md5.ToLowerInvariant(), md5);
    }

    [Theory]
    [InlineData("\"C:\\Windows\\System32\\notepad.exe\" /a", "notepad.exe")]
    [InlineData("notepad /a", "notepad.exe")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL", "shell32.dll")]
    public void Command_targets_are_resolved(string command, string expectedFile) =>
        Assert.Equal(expectedFile, Path.GetFileName(CommandLineTarget.Resolve(command)), ignoreCase: true);

    [Fact]
    public void Unquoted_paths_with_spaces_resolve_like_create_process()
    {
        using var sb = new Sandbox();
        var exe = sb.File(@"My App\run me.exe");
        Assert.Equal(exe, CommandLineTarget.Resolve(exe + " -silent"), ignoreCase: true);
    }
}
