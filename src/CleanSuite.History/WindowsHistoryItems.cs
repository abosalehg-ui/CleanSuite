using CleanSuite.Safety;
using System.Runtime.InteropServices;

namespace CleanSuite.History;

public enum HistoryPage { Windows, StartMenu }

/// <param name="Id">Item id of the original (index into History\HistorySettings2).</param>
/// <param name="Key">Stable key used for UI strings and the journal, e.g. "win.runmru".</param>
/// <param name="DefaultOn">CleanSuite default when no settings are imported.</param>
/// <param name="NeedsAdmin">Part of the item only works with administrator rights (system folders, HKLM, event logs).</param>
public sealed record HistoryItemInfo(int Id, HistoryPage Page, string Key, bool DefaultOn, bool NeedsAdmin = false);

/// <summary>
/// Windows and Start-menu items of Erase History, re-specified from the original and its helpers.
/// Not implemented, by design: Internet Explorer, Outlook Express and Flash items (legacy, project decision), and
/// item 25 "page file" (the original schedules pagefile.sys for deletion at reboot; Windows manages that file).
/// </summary>
public static class WindowsHistoryCatalog
{
    public static readonly IReadOnlyList<HistoryItemInfo> Items = new[]
    {
        // Windows page
        new HistoryItemInfo(2, HistoryPage.Windows, "win.opensave", true),
        new HistoryItemInfo(9, HistoryPage.Windows, "win.clipboard", true),
        new HistoryItemInfo(12, HistoryPage.Windows, "win.mapdrives", true),
        new HistoryItemInfo(21, HistoryPage.Windows, "win.emptytemp", true),
        new HistoryItemInfo(23, HistoryPage.Windows, "win.prefetch", false, NeedsAdmin: true),
        new HistoryItemInfo(24, HistoryPage.Windows, "win.spool", false, NeedsAdmin: true),
        new HistoryItemInfo(26, HistoryPage.Windows, "win.trayicons", false),
        new HistoryItemInfo(28, HistoryPage.Windows, "win.windowpos", false),
        new HistoryItemInfo(30, HistoryPage.Windows, "win.outlooknk2", false),
        new HistoryItemInfo(31, HistoryPage.Windows, "win.outlooktemp", true),
        new HistoryItemInfo(34, HistoryPage.Windows, "win.miscmru", true),
        new HistoryItemInfo(35, HistoryPage.Windows, "win.ftp", false),
        new HistoryItemInfo(36, HistoryPage.Windows, "win.dumps", true, NeedsAdmin: true),
        new HistoryItemInfo(37, HistoryPage.Windows, "win.logs", true, NeedsAdmin: true),
        new HistoryItemInfo(38, HistoryPage.Windows, "win.wer", true, NeedsAdmin: true),
        new HistoryItemInfo(39, HistoryPage.Windows, "win.fontcache", false, NeedsAdmin: true),
        new HistoryItemInfo(40, HistoryPage.Windows, "win.iislogs", true, NeedsAdmin: true),
        new HistoryItemInfo(41, HistoryPage.Windows, "win.thumbcache", false),
        new HistoryItemInfo(43, HistoryPage.Windows, "win.appcompat", false, NeedsAdmin: true),
        new HistoryItemInfo(45, HistoryPage.Windows, "win.eventlogs", false, NeedsAdmin: true),
        // Start menu page
        new HistoryItemInfo(4, HistoryPage.StartMenu, "start.runmru", true),
        new HistoryItemInfo(5, HistoryPage.StartMenu, "start.recentdocs", true),
        new HistoryItemInfo(6, HistoryPage.StartMenu, "start.findfiles", true),
        new HistoryItemInfo(13, HistoryPage.StartMenu, "start.findcomputer", true),
        new HistoryItemInfo(15, HistoryPage.StartMenu, "start.userassist.invalid", true),
        new HistoryItemInfo(16, HistoryPage.StartMenu, "start.netrecent", true),
        new HistoryItemInfo(19, HistoryPage.StartMenu, "start.menuorder", false),
        new HistoryItemInfo(27, HistoryPage.StartMenu, "start.userassist.all", false),
        new HistoryItemInfo(32, HistoryPage.StartMenu, "start.jumplists", false),
    };

    public static HistoryItemInfo? ById(int id) => Items.FirstOrDefault(i => i.Id == id);

    /// <summary>Selection from the original's History\HistorySettings2 (one byte per item id, 1 = selected).</summary>
    public static IReadOnlySet<int> FromHistorySettings2(byte[]? settings) =>
        settings == null
            ? Items.Where(i => i.DefaultOn).Select(i => i.Id).ToHashSet()
            : Items.Where(i => i.Id < settings.Length && settings[i.Id] == 1).Select(i => i.Id).ToHashSet();
}

public sealed class WindowsHistoryCleaner
{
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string IeBars = @"Software\Microsoft\Internet Explorer\Explorer Bars\{C4EE31F3-4768-11D2-BE5C-00A0C9A83DA1}";

    /// <summary>UserAssist Count keys: XP-era {75048700}/{5E6AB780} and Windows 7+ {CEBFF5CD} (programs) / {F4E57C4B} (shortcuts).</summary>
    public static readonly IReadOnlyList<string> UserAssistKeys = new[]
    {
        Explorer + @"\UserAssist\{75048700-EF1F-11D0-9888-006097DEACF9}\Count",
        Explorer + @"\UserAssist\{5E6AB780-7743-11CF-A12B-00AA004AE837}\Count",
        Explorer + @"\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count",
        Explorer + @"\UserAssist\{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}\Count",
    };

    /// <summary>Item 37 rules, verbatim from the original.</summary>
    public static readonly IReadOnlyList<string> LogRules = new[]
    {
        @"%SystemDirectory%\wbem\Logs\*.log|*.lo_",
        @"%windir%\*.log|*.bak|*log.txt|SchedLgU.txt",
        @"%commonappdata%\Microsoft\Dr Watson\*.log|*.dmp",
        @"%windir%\Debug\*.log",
        @"%windir%\Debug\UserMode\*.log|*.bak",
        @"%windir%\security\logs\*.log|*.old",
        @"%windir%\SoftwareDistribution\*.log",
        @"%windir%\Logs\*.log/S",
        @"%windir%\ServiceProfiles\LocalService\AppData\*.log/S",
        @"%windir%\ServiceProfiles\NetworkService\AppData\*.log/S",
        @"%LocalAppData%\Microsoft\Windows\*.log/S",
        @"%LocalAppData%\Microsoft\Windows\WebCache.old\*.*/S/R",
        @"%AppData%\Microsoft\Windows\*.log/S",
        @"%windir%\Microsoft.NET\*.log/S",
        @"%LocalAppData%\MigWiz\*.log|*.xml",
        @"%windir%\inf\setupapi.app.log",
        @"%windir%\inf\setupapi.dev.log",
        @"%windir%\Panther\UnattendGC\setupact.log",
        @"%windir%\Panther\UnattendGC\setuperr.log",
        @"%windir%\Panther\setuperr.log",
        @"%windir%\Panther\setupact.log",
        @"%windir%\ModemLogs\*.txt",
        @"%windir%\Performance\WinSAT\winsat.log",
        @"%LocalAppData%\Microsoft\CLR_v4.0\*\*.log",
        @"%LocalAppData%\Microsoft\CLR_v4.0_32\*\*.log",
        @"%SystemDirectory%\config\systemprofile\AppData\Local\Microsoft\CLR_v4.0\*\*.log",
        @"%SystemDirectory%\config\systemprofile\AppData\Local\Microsoft\CLR_v4.0_32\*\*.log",
        @"%windir%\SysWOW64\config\systemprofile\AppData\Local\Microsoft\CLR_v4.0\*\*.log",
        @"%windir%\SysWOW64\config\systemprofile\AppData\Local\Microsoft\CLR_v4.0_32\*\*.log",
    };

    private readonly RulePlanner _files;
    private readonly RegistryPlanner _reg;
    private readonly Func<IReadOnlyList<string>> _tempFolders;
    private readonly Func<List<(string, long)>> _eventLogs;
    private readonly Func<bool> _clipboardHasContent;

    public WindowsHistoryCleaner(RulePlanner files, RegistryPlanner registry, Func<IReadOnlyList<string>> tempFolders,
        Func<List<(string, long)>>? eventLogs = null, Func<bool>? clipboardHasContent = null)
    {
        _files = files;
        _reg = registry;
        _tempFolders = tempFolders;
        _eventLogs = eventLogs ?? EventLogs.ChannelsWithRecords;
        _clipboardHasContent = clipboardHasContent ?? ClearClipboardAction.HasContent;
    }

    public HistoryPlan Plan(IReadOnlySet<int> selected, string? recentFolder = null, CancellationToken ct = default)
    {
        var plan = new HistoryPlan();
        var a = plan.Actions;
        string Key(int id) => WindowsHistoryCatalog.ById(id)!.Key;
        void Rules(int id, params string[] rules) { foreach (var r in rules) a.AddRange(_files.Plan(Key(id), r, ct)); }
        recentFolder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent");

        foreach (var id in WindowsHistoryCatalog.Items.Select(i => i.Id).Where(selected.Contains))
        {
            ct.ThrowIfCancellationRequested();
            var k = Key(id);
            switch (id)
            {
                case 16: _reg.DeleteKey(a, k, RegHive.CurrentUser, @"Network\Recent"); break;
                case 12: _reg.EmptyKey(a, k, RegHive.CurrentUser, Explorer + @"\Map Network Drive MRU"); break;
                case 4:
                    // Every RunMRU value except names starting with "url" (MRUList included).
                    foreach (var v in _reg.ValueNames(RegHive.CurrentUser, Explorer + @"\RunMRU")
                                 .Where(v => !v.StartsWith("url", StringComparison.OrdinalIgnoreCase)))
                        _reg.DeleteValue(a, k, RegHive.CurrentUser, Explorer + @"\RunMRU", v);
                    break;
                case 32:
                    _files.AddEmptyFolder(a, k, Path.Combine(recentFolder, "AutomaticDestinations"), removeFolder: true, ct);
                    break;
                case 34:
                    foreach (var sub in new[] { "PrnPortsMRU", "ComputerDescriptions", "WordWheelQuery", "TypedPaths", "Wallpaper", @"Wallpapers\Images" })
                        _reg.DeleteKey(a, k, RegHive.CurrentUser, Explorer + "\\" + sub);
                    _reg.DeleteValue(a, k, RegHive.CurrentUser, @"Software\Microsoft\Direct3D\MostRecentApplication", "Name");
                    _reg.DeleteValue(a, k, RegHive.LocalMachine, @"Software\Microsoft\Direct3D\MostRecentApplication", "Name");
                    _reg.DeleteValue(a, k, RegHive.CurrentUser, @"Software\Microsoft\DirectInput\MostRecentApplication", "Id");
                    _reg.DeleteValue(a, k, RegHive.CurrentUser, @"Software\Microsoft\DirectInput\MostRecentApplication", "Name");
                    _reg.DeleteKey(a, k, RegHive.LocalMachine, @"Software\Microsoft\DirectDraw\MostRecentApplication");
                    break;
                case 35: _reg.EmptyKey(a, k, RegHive.CurrentUser, @"Software\Microsoft\Ftp\Accounts"); break;
                case 36: Rules(id, @"%windir%\memory.dmp", @"%windir%\MiniDump\*.dmp", @"%LocalAppData%\CrashDumps\*.dmp"); break;
                case 37: Rules(id, LogRules.ToArray()); break;
                case 38:
                    Rules(id,
                        @"%ALLUSERSPROFILE%\Microsoft\Windows\WER\ReportArchive/E",
                        @"%ALLUSERSPROFILE%\Microsoft\Windows\WER\ReportQueue/E",
                        @"%LocalAppData%\Microsoft\Windows\WER\ReportArchive/E",
                        @"%LocalAppData%\Microsoft\Windows\WER\ReportQueue/E",
                        @"%LocalAppData%\ElevatedDiagnostics/E/R");
                    _reg.DeleteValue(a, k, RegHive.CurrentUser, @"Software\Microsoft\Windows\Windows Error Reporting\Debug", "StoreLocation");
                    _reg.DeleteValue(a, k, RegHive.LocalMachine, @"Software\Microsoft\Windows\Windows Error Reporting\Debug", "StoreLocation");
                    break;
                case 39:
                    Rules(id, @"%windir%\System32\FNTCACHE.DAT", @"%LocalAppData%\GDIPFONTCACHEV1.DAT",
                        @"%windir%\ServiceProfiles\LocalService\AppData\Local\FontCache*.dat");
                    break;
                case 40: Rules(id, @"%SystemDirectory%\LogFiles\*.* /X:""*\SCM"" /C/S", @"%SystemDirectory%\inetpub\logs\LogFiles/E"); break;
                case 41:
                    Rules(id, @"%LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db",
                        @"%LocalAppData%\Microsoft\Windows\Explorer\ThumbCacheToDelete\thm*.tmp");
                    break;
                case 5:
                    _reg.DeleteKey(a, k, RegHive.CurrentUser, Explorer + @"\RecentDocs");
                    _reg.DeleteKey(a, k, RegHive.CurrentUser, Explorer + @"\ComDlg32\LastVisitedMRU");
                    a.AddRange(_files.Plan(k, Path.Combine(recentFolder, "*.lnk"), ct));
                    break;
                case 28:
                    _reg.EmptyKey(a, k, RegHive.CurrentUser, Explorer + @"\StreamMRU");
                    for (int n = 0, misses = 0; n < 10000 && misses <= 10; n++)
                    {
                        var sub = Explorer + $@"\Streams\{n}";
                        if (_reg.KeyExists(RegHive.CurrentUser, sub)) { _reg.DeleteKey(a, k, RegHive.CurrentUser, sub); misses = 0; }
                        else misses++;
                    }
                    foreach (var sub in new[] { @"Software\Microsoft\Windows\Shell\BagMRU", @"Software\Microsoft\Windows\Shell\Bags",
                                                @"Software\Microsoft\Windows\ShellNoRoam\BagMRU", @"Software\Microsoft\Windows\ShellNoRoam\Bags" })
                        _reg.DeleteKey(a, k, RegHive.CurrentUser, sub);
                    foreach (var sub in new[] { "BagMRU", "Bags", "MuiCache" })
                        _reg.DeleteKey(a, k, RegHive.ClassesRoot, @"Local Settings\Software\Microsoft\Windows\Shell\" + sub);
                    break;
                case 2:
                    _reg.DeleteKey(a, k, RegHive.CurrentUser, Explorer + @"\ComDlg32");
                    _reg.DeleteKey(a, k, RegHive.Users, @".DEFAULT\" + Explorer + @"\ComDlg32");
                    break;
                case 6:
                    _reg.DeleteKey(a, k, RegHive.CurrentUser, IeBars + @"\FilesNamedMRU");
                    _reg.DeleteKey(a, k, RegHive.CurrentUser, IeBars + @"\ContainingTextMRU");
                    _reg.DeleteKey(a, k, RegHive.CurrentUser, @"Software\Microsoft\Search Assistant\ACMru");
                    _reg.EmptyKey(a, k, RegHive.CurrentUser, Explorer + @"\Doc Find Spec MRU");
                    break;
                case 13:
                    _reg.DeleteKey(a, k, RegHive.CurrentUser, Explorer + @"\FindComputerMRU");
                    _reg.DeleteKey(a, k, RegHive.CurrentUser, IeBars + @"\ComputerNameMRU");
                    break;
                case 9:
                    if (_clipboardHasContent()) a.Add(new ClearClipboardAction { ItemKey = k, Target = "clipboard" });
                    break;
                case 45:
                    foreach (var (channel, records) in _eventLogs())
                        a.Add(new ClearEventLogAction { ItemKey = k, Target = channel, Size = 0 });
                    break;
                case 30: Rules(id, @"%APPDATA%\Microsoft\Outlook\Outlook.NK2"); break;
                case 15: PlanUserAssist(a, k, all: false); break;
                case 27: PlanUserAssist(a, k, all: true); break;
                case 19: _reg.DeleteKey(a, k, RegHive.CurrentUser, Explorer + @"\MenuOrder\Start Menu"); break;
                case 23: Rules(id, @"%windir%\Prefetch\*.pf|Ag*.db|layout.ini"); break;
                case 31: PlanOutlookTemp(a, k, ct); break;
                case 24: Rules(id, @"%windir%\System32\spool\PRINTERS\*.spl"); break;
                case 21:
                    foreach (var t in _tempFolders())
                        if (Directory.Exists(t)) a.Add(new RemoveEmptySubfoldersAction { ItemKey = k, Target = t });
                    break;
                case 43:
                    _reg.EmptyKey(a, k, RegHive.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Persisted");
                    _reg.DeleteValue(a, k, RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache", "AppCompatCache");
                    _reg.EmptyKey(a, k, RegHive.LocalMachine, @"SOFTWARE\Microsoft\RADAR\HeapLeakDetection\DiagnosedApplications");
                    break;
                case 26:
                    var tray = new List<HistoryAction>();
                    foreach (var name in new[] { "IconStreams", "PastIconsStream" })
                    {
                        _reg.DeleteValue(tray, k, RegHive.CurrentUser, Explorer + @"\TrayNotify", name);
                        _reg.DeleteValue(tray, k, RegHive.ClassesRoot, @"Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify", name);
                    }
                    if (tray.Count > 0)
                    {
                        // Explorer rewrites these values when it exits, so it is closed first and started again afterwards.
                        a.Add(new StopExplorerAction { ItemKey = k, Target = "explorer.exe" });
                        a.AddRange(tray);
                        a.Add(new StartExplorerAction { ItemKey = k, Target = "explorer.exe" });
                    }
                    break;
            }
        }
        return plan;
    }

    /// <summary>
    /// UserAssist (start menu click history). Re-specified for current Windows, where the original's XP-era test (entries
    /// "UEME_RUNPIDL:%csidl2%\…" whose shortcut no longer exists) finds nothing:
    /// item 15 deletes the entries whose program or shortcut no longer exists, item 27 empties all Count keys.
    /// Names are ROT13-encoded and may start with a known-folder GUID.
    /// </summary>
    private void PlanUserAssist(List<HistoryAction> a, string itemKey, bool all)
    {
        foreach (var key in UserAssistKeys)
        {
            if (all) { _reg.EmptyKey(a, itemKey, RegHive.CurrentUser, key); continue; }
            foreach (var name in _reg.ValueNames(RegHive.CurrentUser, key))
                if (UserAssist.TargetPath(UserAssist.Rot13(name)) is { } path && !File.Exists(path) && !Directory.Exists(path))
                    _reg.DeleteValue(a, itemKey, RegHive.CurrentUser, key, name);
        }
    }

    /// <summary>Outlook's secure temp folder (Office\&lt;ver&gt;.0\Outlook\Security\OutlookSecureTempFolder), else INetCache\Content.Outlook.</summary>
    private void PlanOutlookTemp(List<HistoryAction> a, string itemKey, CancellationToken ct)
    {
        var folders = new List<string>();
        foreach (var ver in new[] { "16.0", "15.0", "14.0", "12.0" })
            if (_reg.ReadString(RegHive.CurrentUser, $@"Software\Microsoft\Office\{ver}\Outlook\Security", "OutlookSecureTempFolder") is { Length: > 0 } f)
                folders.Add(Environment.ExpandEnvironmentVariables(f).TrimEnd('\\'));
        if (folders.Count == 0)
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.InternetCache), "Content.Outlook"));
        foreach (var f in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var sub in Directory.Exists(f) ? Directory.EnumerateDirectories(f).ToList() : new List<string>())
                _files.AddEmptyFolder(a, itemKey, sub, removeFolder: true, ct);
    }
}

/// <summary>UserAssist value-name decoding.</summary>
public static class UserAssist
{
    public static string Rot13(string s) => new(s.Select(c => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
        _ => c,
    }).ToArray());

    /// <summary>File-system path of a decoded entry, or null when it is not a path (AppIDs, UEME_CTL… counters).</summary>
    public static string? TargetPath(string decoded)
    {
        var s = decoded;
        if (s.StartsWith("UEME_", StringComparison.Ordinal))
        {
            int colon = s.IndexOf(':');
            if (colon < 0) return null;
            s = s[(colon + 1)..];
        }
        if (s.Length > 38 && s[0] == '{' && s[37] == '}' && Guid.TryParse(s[..38], out var g))
        {
            var folder = KnownFolder(g);
            if (folder == null) return null;
            s = folder + s[38..];
        }
        s = Environment.ExpandEnvironmentVariables(s);
        return Path.IsPathFullyQualified(s) && s.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? s : null;
    }

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
