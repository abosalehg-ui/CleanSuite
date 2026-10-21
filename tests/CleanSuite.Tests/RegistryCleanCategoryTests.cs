using CleanSuite.RegistryClean;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.Tests;

/// <summary>Per-category scanners (Start Menu … Shared Folders) on the private test hive.</summary>
public partial class RegistryCleanTests
{
    private const string Cv = @"Software\Microsoft\Windows\CurrentVersion";

    private static RegFinding One(RegScanResult r, string reason)
    {
        var m = r.Findings.Where(f => f.Reason == reason).ToList();
        Assert.True(m.Count == 1, $"{reason}: expected one finding, got: " + string.Join(" // ", r.Findings.Select(Describe)));
        return m[0];
    }

    private static string Describe(RegFinding f) => $"{f.Reason} | {f.KeyDisplay} | {f.ValueName} | {f.Data}";

    [Fact]
    public void Start_menu_order_keys_without_a_folder_are_obsolete()
    {
        using var env = new Env();
        Directory.CreateDirectory(Path.Combine(env.Sb.Root, @"Programs\Kept"));
        const string mo = Cv + @"\Explorer\MenuOrder\Start Menu\Programs";
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(mo + @"\Kept")) k.SetValue("Order", new byte[] { 1 });
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(mo + @"\Gone")) k.SetValue("Order", new byte[] { 1 });
        env.Root(RegHive.CurrentUser).CreateSubKey(mo + @"\Empty").Dispose();
        var r = env.Scan(RegCategory.StartMenu);
        Assert.Equal(mo + @"\Gone", One(r, "ObsoleteStartMenuItem").Key);
        Assert.Equal(mo + @"\Empty", One(r, "EmptyUselessKey").Key);
        Assert.All(r.Findings, f => Assert.Equal(RegTargetKind.Key, f.Target));
    }

    [Fact]
    public void Obsolete_software_is_an_empty_vendor_tree()
    {
        using var env = new Env();
        env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\DeadVendor\App").Dispose();
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\LiveVendor")) k.SetValue("x", 1);
        env.Root(RegHive.CurrentUser).CreateSubKey(@"Software\Classes\Empty").Dispose();
        env.Root(RegHive.LocalMachine).CreateSubKey(@"Software\HklmDead\Sub").Dispose();
        env.Root(RegHive.LocalMachine).CreateSubKey(@"Software\HklmFlat").Dispose();
        var std = env.Scan(RegCategory.ObsoleteSoftware).Findings.Select(f => f.KeyDisplay).ToList();
        Assert.Contains(@"HKEY_CURRENT_USER\Software\DeadVendor", std);
        Assert.Contains(@"HKEY_LOCAL_MACHINE\Software\HklmFlat", std);
        Assert.DoesNotContain(@"HKEY_LOCAL_MACHINE\Software\HklmDead", std);              // subkeys only count in Thorough
        Assert.DoesNotContain(std, s => s.Contains("LiveVendor") || s.Contains("Classes"));
        var th = env.Scan(RegCategory.ObsoleteSoftware, new RegScanOptions { Thorough = true }).Findings.Select(f => f.KeyDisplay);
        Assert.Contains(@"HKEY_LOCAL_MACHINE\Software\HklmDead", th);
    }

    [Fact]
    public void File_extensions_report_missing_open_with_programs_and_progids()
    {
        using var env = new Env();
        const string fe = Cv + @"\Explorer\FileExts";
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(fe + @"\.abc\OpenWithList"))
        {
            k.SetValue("a", env.Existing);
            k.SetValue("b", env.Missing);
            k.SetValue("MRUList", "ab");
        }
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(fe + @"\.abc\OpenWithProgids"))
        {
            k.SetValue("Known.Prog", new byte[0], RegistryValueKind.None);
            k.SetValue("Gone.Prog", new byte[0], RegistryValueKind.None);
        }
        env.Root(RegHive.ClassesRoot).CreateSubKey("Known.Prog").Dispose();
        env.Root(RegHive.CurrentUser).CreateSubKey(fe + @"\.empty").Dispose();
        var r = env.Scan(RegCategory.FileExtensions);
        Assert.Equal("b", One(r, "OpenWithAppMissing").ValueName);
        Assert.Equal("Gone.Prog", One(r, "InvalidProgid").ValueName);
        Assert.Equal(fe + @"\.empty", One(r, "EmptyKey").Key);
    }

    [Fact]
    public void File_associations_report_useless_extensions_missing_progids_and_bad_commands()
    {
        using var env = new Env();
        var cr = env.Root(RegHive.ClassesRoot);
        cr.CreateSubKey(".useless").Dispose();
        using (var k = cr.CreateSubKey(".typed")) k.SetValue("Content Type", "text/plain");
        using (var k = cr.CreateSubKey(".orphan")) k.SetValue("", "Orphan.File");
        using (var k = cr.CreateSubKey(".bad")) k.SetValue("", "Bad.File");
        using (var k = cr.CreateSubKey(@"Bad.File\shell\open\command")) k.SetValue("", "\"" + env.Missing + "\" \"%1\"");
        using (var k = cr.CreateSubKey(".good")) k.SetValue("", "Good.File");
        using (var k = cr.CreateSubKey(@"Good.File\shell\open\command")) k.SetValue("", "\"" + env.Existing + "\" \"%1\"");
        using (var k = cr.CreateSubKey(@"Good.File\DefaultIcon")) k.SetValue("", env.Missing + ",0");
        cr.CreateSubKey(@"Good.File\shell\edit").Dispose();
        cr.CreateSubKey("EmptyClass").Dispose();

        var r = env.Scan(RegCategory.FileAssociations);
        Assert.Equal(".useless", One(r, "UselessFileExtension").Key);
        Assert.Equal(".orphan", One(r, "MissingProgid").Key);
        var bad = Assert.Single(r.Findings, f => f.Reason == "InvalidPath" && f.Key == ".bad");
        Assert.Contains(r.Findings, f => f.Reason == "InvalidCommand" && f.Key == @"Bad.File\shell\open");
        Assert.Equal(".bad", bad.Key);
        Assert.Equal("Bad.File", bad.ValueName);
        Assert.Equal(@"Good.File\DefaultIcon", One(r, "InvalidIconPath").Key);
        Assert.Equal(@"Good.File\shell\edit", One(r, "EmptyShellKey").Key);
        Assert.Equal("EmptyClass", One(r, "EmptyKey").Key);
        Assert.DoesNotContain(r.Findings, f => f.Key is ".typed" or ".good");
    }

    [Fact]
    public void Application_keys_app_paths_and_firewall_rules()
    {
        using var env = new Env();
        var cr = env.Root(RegHive.ClassesRoot);
        cr.CreateSubKey(@"Applications\nosuchtool_cs.exe").Dispose();
        using (var k = cr.CreateSubKey(@"Applications\kept_cs.exe")) k.SetValue("NoOpenWith", "");
        var lm = env.Root(RegHive.LocalMachine);
        lm.CreateSubKey(Cv + @"\App Paths\empty_cs.exe").Dispose();
        using (var k = lm.CreateSubKey(Cv + @"\App Paths\real_cs.exe")) k.SetValue("", env.Missing);   // target is not checked here
        using (var k = lm.CreateSubKey(@"SYSTEM\ControlSet001\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"))
        {
            k.SetValue("{rule-1}", "v2.30|Action=Allow|App=" + env.Missing + "|Name=x|");
            k.SetValue("{rule-2}", "v2.30|Action=Allow|App=" + env.Existing + "|Name=y|");
            k.SetValue("MCX-skip", "v2.30|App=" + env.Missing + "|");
        }
        var r = env.Scan(RegCategory.ApplicationPaths);
        Assert.Equal(@"Applications\nosuchtool_cs.exe", One(r, "UselessKey").Key);
        Assert.Equal(Cv + @"\App Paths\empty_cs.exe", One(r, "EmptyKey").Key);
        Assert.Equal("{rule-1}", One(r, "InvalidFirewallRule").ValueName);
    }

    [Fact]
    public void Add_remove_cache_installer_folders_and_shared_dlls()
    {
        using var env = new Env();
        var lm = env.Root(RegHive.LocalMachine);
        lm.CreateSubKey(Cv + @"\App Management\ARPCache\GoneApp").Dispose();
        lm.CreateSubKey(Cv + @"\App Management\ARPCache\LiveApp").Dispose();
        lm.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LiveApp").Dispose();
        using (var k = lm.CreateSubKey(Cv + @"\Installer\Folders"))
        {
            k.SetValue(Path.GetDirectoryName(env.Existing) + "\\", "");
            k.SetValue(Path.Combine(env.Sb.Root, @"nofolder\"), "");
        }
        using (var k = lm.CreateSubKey(Cv + @"\SharedDLLs"))
        {
            k.SetValue(env.Existing, 1, RegistryValueKind.DWord);
            k.SetValue(env.Missing, 2, RegistryValueKind.DWord);
        }
        var r = env.Scan(RegCategory.AddRemoveEntries | RegCategory.SharedDlls);
        Assert.Equal(Cv + @"\App Management\ARPCache\GoneApp", One(r, "ObsoleteUninstallEntry").Key);
        Assert.Equal(Path.Combine(env.Sb.Root, @"nofolder\"), One(r, "InvalidInstallerReference").ValueName);
        var dll = One(r, "InvalidEntry");
        Assert.Equal(env.Missing, dll.ValueName);
        Assert.Equal("2", dll.Data);
    }

    [Fact]
    public void Fonts_help_files_and_shares()
    {
        using var env = new Env();
        env.Sb.File(@"Fonts\here.ttf");
        var lm = env.Root(RegHive.LocalMachine);
        using (var k = lm.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"))
        {
            k.SetValue("Here (TrueType)", "here.ttf");
            k.SetValue("Gone (TrueType)", "gone.ttf");
        }
        var helpDir = Path.GetDirectoryName(env.Existing)!;
        File.WriteAllBytes(Path.Combine(helpDir, "empty.chm"), []);
        File.WriteAllBytes(Path.Combine(helpDir, "ok.chm"), [1]);
        using (var k = lm.CreateSubKey(@"Software\Microsoft\Windows\HTML Help"))
        {
            k.SetValue("ok.chm", helpDir);
            k.SetValue("empty.chm", helpDir);
            k.SetValue("missing.chm", helpDir);
            k.SetValue("elsewhere.chm", Path.Combine(env.Sb.Root, "nodir"));                  // missing folder: not reported
        }
        using (var k = lm.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Shares"))
        {
            k.SetValue("Gone", new[] { "Path=" + Path.Combine(env.Sb.Root, "noshare"), "Type=0" }, RegistryValueKind.MultiString);
            k.SetValue("Live", new[] { "Path=" + helpDir, "Type=0" }, RegistryValueKind.MultiString);
            k.SetValue("Printer", new[] { "Path=" + Path.Combine(env.Sb.Root, "noshare"), "Type=1" }, RegistryValueKind.MultiString);
        }
        using (var k = lm.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Shares\Security"))
            k.SetValue("Gone", new byte[] { 1 });

        var r = env.Scan(RegCategory.Fonts | RegCategory.HelpFiles | RegCategory.SharedFolders);
        var missing = r.Findings.Where(f => f.Reason == "FileMissing").Select(f => f.ValueName).ToList();
        Assert.True(missing.Count == 4, string.Join(" // ", r.Findings.Select(Describe)));
        Assert.Equal(new[] { "Gone (TrueType)", "missing.chm", "Gone", "Gone" }, missing);
        Assert.Equal("empty.chm", One(r, "FileDamaged").ValueName);
        Assert.Contains(r.Findings, f => f.Key.EndsWith(@"Shares\Security"));
    }

    [Fact]
    public void History_lists_decode_user_assist_names()
    {
        using var env = new Env();
        Directory.CreateDirectory(Path.Combine(env.Sb.Root, "Programs"));
        File.WriteAllBytes(Path.Combine(env.Sb.Root, "Programs", "Kept.lnk"), [1]);
        Directory.CreateDirectory(Path.Combine(env.Sb.Root, "Known"));
        File.WriteAllBytes(Path.Combine(env.Sb.Root, "Known", "tool.exe"), [1]);
        const string ua = Cv + @"\Explorer\UserAssist\";
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(ua + @"{75048700-EF1F-11D0-9888-006097DEACF9}\Count"))
        {
            k.SetValue(@"HRZR_EHACVQY:%pfvqy2%\" + RegistryScanner.Rot13("Kept.lnk"), new byte[16]);
            k.SetValue(@"HRZR_EHACVQY:%pfvqy2%\" + RegistryScanner.Rot13("Gone.lnk"), new byte[16]);
            k.SetValue("HRZR_EHACNGU:" + RegistryScanner.Rot13(env.Missing), new byte[16]);
        }
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(ua + @"{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count"))
        {
            k.SetValue(RegistryScanner.Rot13("{" + Env.KnownFolderId.ToString().ToUpperInvariant() + @"}\tool.exe"), new byte[72]);
            k.SetValue(RegistryScanner.Rot13("{" + Env.KnownFolderId.ToString().ToUpperInvariant() + @"}\gone.exe"), new byte[72]);
            k.SetValue(RegistryScanner.Rot13("Microsoft.Windows.Explorer"), new byte[72]);
        }
        var r = env.Scan(RegCategory.HistoryLists);
        Assert.Equal(new[] { "GONE.LNK", env.Missing.ToUpperInvariant(), Path.Combine(env.Sb.Root, "Known", "gone.exe").ToUpperInvariant() },
            r.Findings.Select(f => f.Data.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
        Assert.All(r.Findings, f => Assert.Equal(RegTargetKind.Value, f.Target));
    }

    [Fact]
    public void Sounds_and_autoplay_handlers()
    {
        using var env = new Env();
        Directory.CreateDirectory(Path.Combine(env.Sb.Root, @"Windows\Media"));
        File.WriteAllBytes(Path.Combine(env.Sb.Root, @"Windows\Media\ding.wav"), [1]);
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"AppEvents\Schemes\Apps\.Default\Beep\.current")) k.SetValue("", "ding.wav");
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(@"AppEvents\Schemes\Apps\.Default\Gone\.current")) k.SetValue("", "gone.wav");
        var lm = env.Root(RegHive.LocalMachine);
        const string ap = Cv + @"\Explorer\AutoplayHandlers";
        using (var k = lm.CreateSubKey(ap + @"\EventHandlers\PlayMusic"))
        {
            k.SetValue("VendorPlay", "");
            k.SetValue("MSPlayMedia", "");                                                    // Microsoft handlers are not checked
            k.SetValue("VendorGone", "");
        }
        using (var k = lm.CreateSubKey(ap + @"\Handlers\VendorPlay")) k.SetValue("ProgID", "Vendor.Player");
        env.Root(RegHive.ClassesRoot).CreateSubKey("Vendor.Player").Dispose();
        using (var k = lm.CreateSubKey(ap + @"\Handlers\VendorBroken")) k.SetValue("ProgID", "Vendor.Missing");

        var r = env.Scan(RegCategory.Sounds);
        Assert.Equal(@"AppEvents\Schemes\Apps\.Default\Gone\.current", One(r, "InvalidSound").Key);
        Assert.Equal("VendorGone", One(r, "InvalidAutoplayEventHandler").ValueName);
        Assert.Equal(ap + @"\Handlers\VendorBroken", One(r, "InvalidAutoplayHandler").Key, ignoreCase: true);
    }

    [Fact]
    public void Ace_settings_are_read_without_writing()
    {
        using var env = new Env();
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(AceRegistrySettings.Root + @"\Settings"))
        {
            k.SetValue("SettingsReg", 0x7, RegistryValueKind.DWord);
            k.SetValue("RegScanOption", 0x3, RegistryValueKind.DWord);
            k.SetValue("registryscanthorough", 1, RegistryValueKind.DWord);
        }
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(AceRegistrySettings.Root + @"\IgnoreListReg"))
        {
            k.SetValue("Acrobat", 0, RegistryValueKind.DWord);
            k.SetValue("Diskeeper", 1, RegistryValueKind.DWord);
        }
        var s = AceRegistrySettings.Read(env.Roots);
        Assert.True(s.FoundInRegistry);
        var o = s.ToOptions(null);
        Assert.True(o is { IncludeRemovable: true, IncludeCd: true, UseIgnoreList: true, NoIgnoreForMru: false, Thorough: true });
        Assert.Equal(RegCategory.UserEntries | RegCategory.AllUsersEntries, o.Categories);
        Assert.Contains("Acrobat", o.DisabledDatLists);
        Assert.DoesNotContain("Diskeeper", o.DisabledDatLists);
        using var empty = new AppHiveRegistryRoots(Path.Combine(env.Sb.Root, "empty.hiv"));
        Assert.False(AceRegistrySettings.Read(empty).FoundInRegistry);
    }

    [Fact]
    public void Safety_deviations_keep_value_only_verbs_and_os_caches()
    {
        using var env = new Env();
        using (var k = env.Root(RegHive.ClassesRoot).CreateSubKey(@"DesktopBackground\shell\Modern")) k.SetValue("ExplorerCommandHandler", "{0}");
        env.Root(RegHive.ClassesRoot).CreateSubKey(@"DesktopBackground\shell\Hollow").Dispose();
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(Cv + @"\AppModel\StateRepository\Cache\Package\Data"))
            k.SetValue("Path", env.Missing);
        var r = env.Scan(RegCategory.FileAssociations | RegCategory.UserEntries);
        Assert.Equal(@"DesktopBackground\shell\Hollow", One(r, "EmptyShellKey").Key);
        Assert.DoesNotContain(r.Findings, f => f.Key.Contains("StateRepository"));
        Assert.Contains(r.Ignored, f => f.IgnoredBy == IgnoreWords.Protective[0]);
    }

    [Fact]
    public void Command_mode_accepts_registered_application_names()
    {
        using var env = new Env();
        env.Root(RegHive.ClassesRoot).CreateSubKey(@"Applications\registeredtool_cs.exe").Dispose();
        using (var k = env.Root(RegHive.CurrentUser).CreateSubKey(Cv + @"\Explorer\FileExts\.xyz\OpenWithList"))
        {
            k.SetValue("a", "registeredtool_cs.exe");
            k.SetValue("b", "unknowntool_cs.exe");
        }
        var r = env.Scan(RegCategory.FileExtensions);
        Assert.Equal("b", One(r, "OpenWithAppMissing").ValueName);
    }
}
