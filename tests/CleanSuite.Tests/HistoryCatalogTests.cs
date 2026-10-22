using CleanSuite.History;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.Tests;

/// <summary>
/// Every registry-based Erase History item, planned and run against a private hive file (RegLoadAppKey) with its keys and values
/// in place: each item finds its data, the run removes it, and everything removed is in the .reg backup. Never the real registry.
/// </summary>
public class HistoryCatalogTests
{
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    /// <summary>Where each registry item keeps its data (hive, key, value), seeded before planning.</summary>
    private static readonly (string Item, RegHive Hive, string Key, string Value)[] Seeds =
    [
        ("start.netrecent", RegHive.CurrentUser, @"Network\Recent\x", "v"),
        ("win.mapdrives", RegHive.CurrentUser, Explorer + @"\Map Network Drive MRU", "a"),
        ("start.runmru", RegHive.CurrentUser, Explorer + @"\RunMRU", "a"),
        ("win.miscmru", RegHive.CurrentUser, Explorer + @"\TypedPaths", "url1"),
        ("win.ftp", RegHive.CurrentUser, @"Software\Microsoft\Ftp\Accounts", "server"),
        ("start.recentdocs", RegHive.CurrentUser, Explorer + @"\RecentDocs", "0"),
        ("win.windowpos", RegHive.CurrentUser, Explorer + @"\StreamMRU", "0"),
        ("win.opensave", RegHive.CurrentUser, Explorer + @"\ComDlg32\OpenSavePidlMRU", "0"),
        ("start.findfiles", RegHive.CurrentUser, Explorer + @"\Doc Find Spec MRU", "a"),
        ("start.findcomputer", RegHive.CurrentUser, Explorer + @"\FindComputerMRU", "a"),
        ("start.menuorder", RegHive.CurrentUser, Explorer + @"\MenuOrder\Start Menu", "Order"),
        ("win.appcompat", RegHive.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Persisted", @"C:\x.exe"),
        ("start.userassist.all", RegHive.CurrentUser, Explorer + @"\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count", "HRZR_EHACNGU"),
        ("win.wer", RegHive.CurrentUser, @"Software\Microsoft\Windows\Windows Error Reporting\Debug", "StoreLocation"),
        ("win.trayicons", RegHive.CurrentUser, Explorer + @"\TrayNotify", "IconStreams"),
    ];

    [Fact]
    public void Every_registry_item_finds_its_data_removes_it_and_backs_it_up()
    {
        using var sb = new Sandbox();
        using var roots = new AppHiveRegistryRoots(Path.Combine(sb.Root, "t.hiv"));
        foreach (var (_, hive, key, value) in Seeds)
            using (var k = roots.Root(hive).CreateSubKey(key)) k.SetValue(value, "data");

        var cleaner = new WindowsHistoryCleaner(
            new RulePlanner(new TokenExpander(new Dictionary<string, string>
            {
                ["APPDATA"] = Path.Combine(sb.Root, "Roaming"), ["LOCALAPPDATA"] = Path.Combine(sb.Root, "Local"),
            }, s => s)),
            new RegistryPlanner(roots), () => Array.Empty<string>(), eventLogs: () => new List<(string, long)>(), clipboardHasContent: () => false);
        var ids = Seeds.Select(s => WindowsHistoryCatalog.Items.Single(i => i.Key == s.Item).Id).ToHashSet();
        var plan = cleaner.Plan(ids, recentFolder: Path.Combine(sb.Root, "Recent"));

        foreach (var item in Seeds.Select(s => s.Item))
            Assert.True(plan.Actions.Any(a => a.ItemKey == item), $"{item} planned nothing");
        // The tray item closes and restarts Explorer around its change: planned, but never run in a test.
        Assert.Contains(plan.Actions, a => a.Kind == "explorer-stop");
        var actions = plan.Actions.Where(a => a.ItemKey != "win.trayicons").ToList();

        var res = new HistoryExecutor(new ProtectedPaths(), Path.Combine(sb.Root, "_j"), roots, Path.Combine(sb.Root, "_bak")).Execute(actions);
        Assert.Empty(res.Failed);
        Assert.NotNull(res.RegistryBackupPath);
        var backup = File.ReadAllText(res.RegistryBackupPath!);
        foreach (var (item, hive, key, value) in Seeds.Where(s => s.Item != "win.trayicons"))
        {
            using var k = roots.Root(hive).OpenSubKey(key);
            Assert.True(k == null || !k.GetValueNames().Contains(value, StringComparer.OrdinalIgnoreCase), $"{item}: {key} :: {value} is still there");
            Assert.Contains("\\" + key + "]", backup, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Nothing_is_planned_where_there_is_nothing_to_clean()
    {
        using var sb = new Sandbox();
        using var roots = new AppHiveRegistryRoots(Path.Combine(sb.Root, "t.hiv"));
        var cleaner = new WindowsHistoryCleaner(new RulePlanner(new TokenExpander(new Dictionary<string, string>(), s => s)),
            new RegistryPlanner(roots), () => Array.Empty<string>(), eventLogs: () => new List<(string, long)>(), clipboardHasContent: () => false);
        var plan = cleaner.Plan(Seeds.Select(s => WindowsHistoryCatalog.Items.Single(i => i.Key == s.Item).Id).ToHashSet(),
            recentFolder: Path.Combine(sb.Root, "Recent"));
        Assert.Empty(plan.Actions);
    }
}
