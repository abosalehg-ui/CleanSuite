using CleanSuite.History;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.Tests;

/// <summary>Windows / Start-menu items and the registry layer, on a private hive file (RegLoadAppKey) — never the real registry.</summary>
public class HistoryWindowsTests
{
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    private sealed class Env : IDisposable
    {
        public Sandbox Sb { get; } = new();
        public AppHiveRegistryRoots Roots { get; }
        public Env() => Roots = new AppHiveRegistryRoots(Path.Combine(Sb.Root, "test.hiv"));
        public RegistryKey Hkcu => Roots.Root(RegHive.CurrentUser);

        public WindowsHistoryCleaner Cleaner(IReadOnlyList<string>? temps = null, bool clipboard = false) => new(
            new RulePlanner(new TokenExpander(new Dictionary<string, string>
            {
                ["APPDATA"] = Path.Combine(Sb.Root, "Roaming"), ["LOCALAPPDATA"] = Path.Combine(Sb.Root, "Local"),
            }, s => s)),
            new RegistryPlanner(Roots), () => temps ?? Array.Empty<string>(),
            eventLogs: () => new List<(string, long)>(), clipboardHasContent: () => clipboard);

        public HistoryRunResult Run(IReadOnlyList<HistoryAction> actions) =>
            new HistoryExecutor(new ProtectedPaths(), Path.Combine(Sb.Root, "_journal"), Roots, Path.Combine(Sb.Root, "_bak")).Execute(actions);

        public void Dispose()
        {
            Roots.Dispose();
            Sb.Dispose();
        }
    }

    [Fact]
    public void Run_mru_values_are_deleted_except_url_entries_and_backed_up()
    {
        using var env = new Env();
        using (var k = env.Hkcu.CreateSubKey(Explorer + @"\RunMRU"))
        {
            k.SetValue("a", @"cmd\1");
            k.SetValue("b", "notepad\\1");
            k.SetValue("MRUList", "ba");
            k.SetValue("url1", "keep");
        }
        var plan = env.Cleaner().Plan(new HashSet<int> { 4 });
        Assert.Equal(3, plan.Actions.Count);
        var res = env.Run(plan.Actions);
        Assert.Empty(res.Failed);
        using (var k = env.Hkcu.OpenSubKey(Explorer + @"\RunMRU")!)
            Assert.Equal(new[] { "url1" }, k.GetValueNames());
        Assert.NotNull(res.RegistryBackupPath);
        var reg = File.ReadAllText(res.RegistryBackupPath!);
        Assert.StartsWith("Windows Registry Editor Version 5.00", reg.TrimStart('\uFEFF'));
        Assert.Contains(@"[HKEY_CURRENT_USER\" + Explorer + @"\RunMRU]", reg);
        Assert.Contains(@"""a""=""cmd\\1""", reg);
    }

    [Fact]
    public void Registry_backup_restores_deleted_keys_with_all_value_types()
    {
        using var env = new Env();
        var key = Explorer + @"\ComDlg32";
        using (var k = env.Hkcu.CreateSubKey(key + @"\OpenSavePidlMRU\txt"))
        {
            k.SetValue("0", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            k.SetValue("MRUListEx", 7, RegistryValueKind.DWord);
            k.SetValue("q", 0x1122334455L, RegistryValueKind.QWord);
            k.SetValue("e", @"%TEMP%\x", RegistryValueKind.ExpandString);
            k.SetValue("m", new[] { "one", "two" }, RegistryValueKind.MultiString);
            k.SetValue("", "default \"quoted\" \\ value");
        }
        var res = env.Run(env.Cleaner().Plan(new HashSet<int> { 2 }).Actions);
        Assert.Empty(res.Failed);
        Assert.Null(env.Hkcu.OpenSubKey(key));

        RegFileImporter.Import(res.RegistryBackupPath!, env.Roots);
        using var r = env.Hkcu.OpenSubKey(key + @"\OpenSavePidlMRU\txt")!;
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])r.GetValue("0")!);
        Assert.Equal(7, r.GetValue("MRUListEx"));
        Assert.Equal(0x1122334455L, r.GetValue("q"));
        Assert.Equal(@"%TEMP%\x", r.GetValue("e", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(RegistryValueKind.ExpandString, r.GetValueKind("e"));
        Assert.Equal(new[] { "one", "two" }, (string[])r.GetValue("m")!);
        Assert.Equal("default \"quoted\" \\ value", r.GetValue(""));
    }

    [Fact]
    public void Empty_key_keeps_the_key_and_missing_data_plans_nothing()
    {
        using var env = new Env();
        using (var k = env.Hkcu.CreateSubKey(Explorer + @"\Map Network Drive MRU")) { k.SetValue("a", @"\\srv\share"); k.CreateSubKey("x"); }
        var plan = env.Cleaner().Plan(new HashSet<int> { 12, 16, 13, 19 });
        Assert.Single(plan.Actions);                               // only the key that exists and has content
        env.Run(plan.Actions);
        using var after = env.Hkcu.OpenSubKey(Explorer + @"\Map Network Drive MRU")!;
        Assert.Equal(0, after.ValueCount + after.SubKeyCount);
        Assert.Empty(env.Cleaner().Plan(new HashSet<int> { 12 }).Actions);
    }

    [Fact]
    public void Window_position_item_removes_streams_and_bags()
    {
        using var env = new Env();
        env.Hkcu.CreateSubKey(Explorer + @"\StreamMRU")!.SetValue("0", new byte[] { 1 });
        env.Hkcu.CreateSubKey(Explorer + @"\Streams\0")!.Dispose();
        env.Hkcu.CreateSubKey(Explorer + @"\Streams\3")!.Dispose();
        env.Hkcu.CreateSubKey(@"Software\Microsoft\Windows\Shell\Bags\1")!.Dispose();
        env.Roots.Root(RegHive.ClassesRoot).CreateSubKey(@"Local Settings\Software\Microsoft\Windows\Shell\MuiCache")!.SetValue("x", "y");
        var res = env.Run(env.Cleaner().Plan(new HashSet<int> { 28 }).Actions);
        Assert.Empty(res.Failed);
        Assert.Null(env.Hkcu.OpenSubKey(Explorer + @"\Streams\0"));
        Assert.Null(env.Hkcu.OpenSubKey(Explorer + @"\Streams\3"));
        Assert.Null(env.Hkcu.OpenSubKey(@"Software\Microsoft\Windows\Shell\Bags"));
        Assert.Null(env.Roots.Root(RegHive.ClassesRoot).OpenSubKey(@"Local Settings\Software\Microsoft\Windows\Shell\MuiCache"));
    }

    [Fact]
    public void Recent_documents_and_jump_lists()
    {
        using var env = new Env();
        var recent = Path.Combine(env.Sb.Root, "Recent");
        env.Sb.File(@"Recent\doc.lnk");
        env.Sb.File(@"Recent\keep.txt");
        env.Sb.File(@"Recent\AutomaticDestinations\a.automaticDestinations-ms");
        env.Hkcu.CreateSubKey(Explorer + @"\RecentDocs\.txt")!.Dispose();
        var res = env.Run(env.Cleaner().Plan(new HashSet<int> { 5, 32 }, recent).Actions);
        Assert.Empty(res.Failed);
        Assert.False(File.Exists(Path.Combine(recent, "doc.lnk")));
        Assert.True(File.Exists(Path.Combine(recent, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(recent, "AutomaticDestinations")));
        Assert.Null(env.Hkcu.OpenSubKey(Explorer + @"\RecentDocs"));
    }

    [Fact]
    public void Empty_temp_subfolders_item_and_clipboard_planning()
    {
        using var env = new Env();
        var temp = Path.Combine(env.Sb.Root, "Temp");
        Directory.CreateDirectory(Path.Combine(temp, @"a\b"));
        env.Sb.File(@"Temp\full\f.tmp");
        env.Run(env.Cleaner(new[] { temp }).Plan(new HashSet<int> { 21 }).Actions);
        Assert.False(Directory.Exists(Path.Combine(temp, "a")));
        Assert.True(File.Exists(Path.Combine(temp, @"full\f.tmp")));
        Assert.True(Directory.Exists(temp));

        Assert.Empty(env.Cleaner(clipboard: false).Plan(new HashSet<int> { 9 }).Actions);
        Assert.IsType<ClearClipboardAction>(Assert.Single(env.Cleaner(clipboard: true).Plan(new HashSet<int> { 9 }).Actions));
    }

    [Fact]
    public void User_assist_invalid_entries_only_or_all()
    {
        using var env = new Env();
        var key = WindowsHistoryCleaner.UserAssistKeys[2];
        var existing = env.Sb.File("tool.exe");
        using (var k = env.Hkcu.CreateSubKey(key))
        {
            k.SetValue(UserAssist.Rot13(existing), new byte[] { 1 });
            k.SetValue(UserAssist.Rot13(Path.Combine(env.Sb.Root, "gone.exe")), new byte[] { 1 });
            k.SetValue(UserAssist.Rot13("Microsoft.Windows.Explorer"), new byte[] { 1 });   // AppID: not a path, kept
        }
        env.Run(env.Cleaner().Plan(new HashSet<int> { 15 }).Actions);
        using (var k = env.Hkcu.OpenSubKey(key)!)
            Assert.Equal(new[] { existing, "Microsoft.Windows.Explorer" }.OrderBy(x => x),
                k.GetValueNames().Select(UserAssist.Rot13).OrderBy(x => x));
        env.Run(env.Cleaner().Plan(new HashSet<int> { 27 }).Actions);
        using (var k = env.Hkcu.OpenSubKey(key)!) Assert.Equal(0, k.ValueCount);
    }

    [Theory]
    [InlineData("HRZR_EHACNGU:P:\\Jvaqbjf\\abgrcnq.rkr", "UEME_RUNPATH:C:\\Windows\\notepad.exe")]
    [InlineData("Zvpebfbsg.Jvaqbjf.Rkcybere", "Microsoft.Windows.Explorer")]
    public void Rot13_decodes_user_assist_names(string encoded, string decoded) => Assert.Equal(decoded, UserAssist.Rot13(encoded));

    [Fact]
    public void User_assist_known_folder_guid_resolves_to_a_path()
    {
        var system = UserAssist.TargetPath("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\notepad.exe");   // FOLDERID_System
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), system, ignoreCase: true);
        Assert.Null(UserAssist.TargetPath("UEME_CTLSESSION"));
    }

    [Fact]
    public void Tray_item_restarts_explorer_around_the_value_deletes()
    {
        using var env = new Env();
        env.Hkcu.CreateSubKey(Explorer + @"\TrayNotify")!.SetValue("IconStreams", new byte[] { 1 });
        var acts = env.Cleaner().Plan(new HashSet<int> { 26 }).Actions;
        Assert.IsType<StopExplorerAction>(acts[0]);
        Assert.IsType<DeleteRegistryValueAction>(acts[1]);
        Assert.IsType<StartExplorerAction>(acts[^1]);
        Assert.Empty(env.Cleaner().Plan(new HashSet<int> { 3, 7, 25 }).Actions);   // IE and page-file items are not implemented
    }

    [Fact]
    public void Settings2_bytes_select_items_and_defaults_apply_without_import()
    {
        var bytes = new byte[256];
        bytes[4] = 1; bytes[37] = 1; bytes[3] = 1;                     // 3 = IE cache: not offered
        Assert.Equal(new[] { 4, 37 }, WindowsHistoryCatalog.FromHistorySettings2(bytes).OrderBy(i => i));
        var defaults = WindowsHistoryCatalog.FromHistorySettings2(null);
        Assert.Contains(4, defaults);
        Assert.DoesNotContain(45, defaults);                           // event logs are opt-in
        Assert.Equal(WindowsHistoryCatalog.Items.Count, WindowsHistoryCatalog.Items.Select(i => i.Key).Distinct().Count());
    }

    [Fact]
    public void Protected_paths_allow_only_explicit_history_exceptions()
    {
        var p = HistoryExecutor.ForHistory();
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Assert.Null(p.Check(Path.Combine(sys, @"spool\PRINTERS\00001.SPL")));
        Assert.NotNull(p.Check(Path.Combine(sys, @"spool\PRINTERS"), isDirectory: true));   // the folder itself stays protected
        Assert.Null(p.Check(Path.Combine(sys, "FNTCACHE.DAT")));
        Assert.NotNull(p.Check(Path.Combine(sys, "kernel32.dll")));
        Assert.NotNull(p.Check(Path.Combine(sys, @"spool\drivers\x.dll")));
    }

    [Fact]
    public void Missing_backup_writer_refuses_registry_changes()
    {
        using var env = new Env();
        env.Hkcu.CreateSubKey(@"Network\Recent\x")!.Dispose();
        var action = Assert.Single(env.Cleaner().Plan(new HashSet<int> { 16 }).Actions);
        using var journal = new OperationJournal("t", Path.Combine(env.Sb.Root, "_journal"));
        var ctx = new CleanSuite.History.ExecutionContext { Journal = journal, Protected = new ProtectedPaths(), Registry = env.Roots };
        Assert.False(action.Execute(ctx).Success);
        Assert.NotNull(env.Hkcu.OpenSubKey(@"Network\Recent"));
    }

    [Fact]
    public void Ace_history_settings_are_read_without_writing()
    {
        using var env = new Env();
        Assert.False(AceHistorySettings.Read(env.Roots).FoundInRegistry);
        Assert.Equal(WindowsHistoryCatalog.Items.Where(i => i.DefaultOn).Select(i => i.Id).ToHashSet(), AceHistorySettings.Read(env.Roots).WindowsItems);

        var bytes = new byte[50];
        bytes[4] = 1; bytes[37] = 1; bytes[2] = 0;
        using (var k = env.Hkcu.CreateSubKey(AceHistorySettings.Root + @"\History"))
        {
            k.SetValue("HistorySettings2", bytes, RegistryValueKind.Binary);
            k.SetValue("firefox", 0x5, RegistryValueKind.DWord);
        }
        using (var k = env.Hkcu.CreateSubKey(AceHistorySettings.Root + @"\History\Users")) k.SetValue("Cookies", 1, RegistryValueKind.DWord);
        using (var k = env.Hkcu.CreateSubKey(AceHistorySettings.Root + @"\Cookies"))
        {
            k.SetValue(".google.", 1, RegistryValueKind.DWord);
            k.SetValue(".live.com", 1, RegistryValueKind.DWord);
        }
        var s = AceHistorySettings.Read(env.Roots);
        Assert.True(s.FoundInRegistry);
        Assert.Equal(new HashSet<int> { 4, 37 }, s.WindowsItems);
        Assert.Equal(FirefoxItems.History | FirefoxItems.Cookies, s.Firefox);
        Assert.True(s.AllUsers);
        Assert.Equal(new[] { ".google.", ".live.com" }, s.CookieWhitelist.OrderBy(x => x));
    }
}
