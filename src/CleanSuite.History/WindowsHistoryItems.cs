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

    /// <summary>What one item's planner needs: where to add actions, the item's key, the Recent folder and cancellation.</summary>
    private sealed record PlanContext(List<HistoryAction> Actions, string Key, string RecentFolder, CancellationToken Ct);

    /// <summary>
    /// One planner per catalog item, keyed by the item's key (see <see cref="WindowsHistoryCatalog.Items"/>). Each states in a few
    /// lines which registry keys and file rules the item covers; adding an item is one catalog line and one entry here.
    /// </summary>
    private IReadOnlyDictionary<string, Action<PlanContext>> Planners() => new Dictionary<string, Action<PlanContext>>
    {
        ["start.netrecent"] = c => _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, @"Network\Recent"),
        ["win.mapdrives"] = c => _reg.EmptyKey(c.Actions, c.Key, RegHive.CurrentUser, Explorer + @"\Map Network Drive MRU"),
        // Every RunMRU value except names starting with "url" (MRUList included).
        ["start.runmru"] = c =>
        {
            foreach (var v in _reg.ValueNames(RegHive.CurrentUser, Explorer + @"\RunMRU").Where(v => !v.StartsWith("url", StringComparison.OrdinalIgnoreCase)))
                _reg.DeleteValue(c.Actions, c.Key, RegHive.CurrentUser, Explorer + @"\RunMRU", v);
        },
        ["start.jumplists"] = c => _files.AddEmptyFolder(c.Actions, c.Key, Path.Combine(c.RecentFolder, "AutomaticDestinations"), removeFolder: true, c.Ct),
        ["win.miscmru"] = c =>
        {
            foreach (var sub in new[] { "PrnPortsMRU", "ComputerDescriptions", "WordWheelQuery", "TypedPaths", "Wallpaper", @"Wallpapers\Images" })
                _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, Explorer + "\\" + sub);
            _reg.DeleteValue(c.Actions, c.Key, RegHive.CurrentUser, @"Software\Microsoft\Direct3D\MostRecentApplication", "Name");
            _reg.DeleteValue(c.Actions, c.Key, RegHive.LocalMachine, @"Software\Microsoft\Direct3D\MostRecentApplication", "Name");
            _reg.DeleteValue(c.Actions, c.Key, RegHive.CurrentUser, @"Software\Microsoft\DirectInput\MostRecentApplication", "Id");
            _reg.DeleteValue(c.Actions, c.Key, RegHive.CurrentUser, @"Software\Microsoft\DirectInput\MostRecentApplication", "Name");
            _reg.DeleteKey(c.Actions, c.Key, RegHive.LocalMachine, @"Software\Microsoft\DirectDraw\MostRecentApplication");
        },
        ["win.ftp"] = c => _reg.EmptyKey(c.Actions, c.Key, RegHive.CurrentUser, @"Software\Microsoft\Ftp\Accounts"),
        ["win.dumps"] = c => Rules(c, @"%windir%\memory.dmp", @"%windir%\MiniDump\*.dmp", @"%LocalAppData%\CrashDumps\*.dmp"),
        ["win.logs"] = c => Rules(c, LogRules.ToArray()),
        ["win.wer"] = c =>
        {
            Rules(c,
                @"%ALLUSERSPROFILE%\Microsoft\Windows\WER\ReportArchive/E",
                @"%ALLUSERSPROFILE%\Microsoft\Windows\WER\ReportQueue/E",
                @"%LocalAppData%\Microsoft\Windows\WER\ReportArchive/E",
                @"%LocalAppData%\Microsoft\Windows\WER\ReportQueue/E",
                @"%LocalAppData%\ElevatedDiagnostics/E/R");
            _reg.DeleteValue(c.Actions, c.Key, RegHive.CurrentUser, @"Software\Microsoft\Windows\Windows Error Reporting\Debug", "StoreLocation");
            _reg.DeleteValue(c.Actions, c.Key, RegHive.LocalMachine, @"Software\Microsoft\Windows\Windows Error Reporting\Debug", "StoreLocation");
        },
        ["win.fontcache"] = c => Rules(c, @"%windir%\System32\FNTCACHE.DAT", @"%LocalAppData%\GDIPFONTCACHEV1.DAT",
            @"%windir%\ServiceProfiles\LocalService\AppData\Local\FontCache*.dat"),
        ["win.iislogs"] = c => Rules(c, @"%SystemDirectory%\LogFiles\*.* /X:""*\SCM"" /C/S", @"%SystemDirectory%\inetpub\logs\LogFiles/E"),
        ["win.thumbcache"] = c => Rules(c, @"%LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db",
            @"%LocalAppData%\Microsoft\Windows\Explorer\ThumbCacheToDelete\thm*.tmp"),
        ["start.recentdocs"] = c =>
        {
            _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, Explorer + @"\RecentDocs");
            _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, Explorer + @"\ComDlg32\LastVisitedMRU");
            c.Actions.AddRange(_files.Plan(c.Key, Path.Combine(c.RecentFolder, "*.lnk"), c.Ct));
        },
        ["win.windowpos"] = c =>
        {
            _reg.EmptyKey(c.Actions, c.Key, RegHive.CurrentUser, Explorer + @"\StreamMRU");
            for (int n = 0, misses = 0; n < 10000 && misses <= 10; n++)
            {
                var sub = Explorer + $@"\Streams\{n}";
                if (_reg.KeyExists(RegHive.CurrentUser, sub)) { _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, sub); misses = 0; }
                else misses++;
            }
            foreach (var sub in new[] { @"Software\Microsoft\Windows\Shell\BagMRU", @"Software\Microsoft\Windows\Shell\Bags",
                                        @"Software\Microsoft\Windows\ShellNoRoam\BagMRU", @"Software\Microsoft\Windows\ShellNoRoam\Bags" })
                _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, sub);
            foreach (var sub in new[] { "BagMRU", "Bags", "MuiCache" })
                _reg.DeleteKey(c.Actions, c.Key, RegHive.ClassesRoot, @"Local Settings\Software\Microsoft\Windows\Shell\" + sub);
        },
        ["win.opensave"] = c =>
        {
            _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, Explorer + @"\ComDlg32");
            _reg.DeleteKey(c.Actions, c.Key, RegHive.Users, @".DEFAULT\" + Explorer + @"\ComDlg32");
        },
        ["start.findfiles"] = c =>
        {
            _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, IeBars + @"\FilesNamedMRU");
            _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, IeBars + @"\ContainingTextMRU");
            _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, @"Software\Microsoft\Search Assistant\ACMru");
            _reg.EmptyKey(c.Actions, c.Key, RegHive.CurrentUser, Explorer + @"\Doc Find Spec MRU");
        },
        ["start.findcomputer"] = c =>
        {
            _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, Explorer + @"\FindComputerMRU");
            _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, IeBars + @"\ComputerNameMRU");
        },
        ["win.clipboard"] = c => { if (_clipboardHasContent()) c.Actions.Add(new ClearClipboardAction { ItemKey = c.Key, Target = "clipboard" }); },
        ["win.eventlogs"] = c =>
        {
            foreach (var (channel, _) in _eventLogs()) c.Actions.Add(new ClearEventLogAction { ItemKey = c.Key, Target = channel, Size = 0 });
        },
        ["win.outlooknk2"] = c => Rules(c, @"%APPDATA%\Microsoft\Outlook\Outlook.NK2"),
        ["start.userassist.invalid"] = c => PlanUserAssist(c.Actions, c.Key, all: false),
        ["start.userassist.all"] = c => PlanUserAssist(c.Actions, c.Key, all: true),
        ["start.menuorder"] = c => _reg.DeleteKey(c.Actions, c.Key, RegHive.CurrentUser, Explorer + @"\MenuOrder\Start Menu"),
        ["win.prefetch"] = c => Rules(c, @"%windir%\Prefetch\*.pf|Ag*.db|layout.ini"),
        ["win.outlooktemp"] = c => PlanOutlookTemp(c.Actions, c.Key, c.Ct),
        ["win.spool"] = c => Rules(c, @"%windir%\System32\spool\PRINTERS\*.spl"),
        ["win.emptytemp"] = c =>
        {
            foreach (var t in _tempFolders())
                if (Directory.Exists(t)) c.Actions.Add(new RemoveEmptySubfoldersAction { ItemKey = c.Key, Target = t });
        },
        ["win.appcompat"] = c =>
        {
            _reg.EmptyKey(c.Actions, c.Key, RegHive.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Persisted");
            _reg.DeleteValue(c.Actions, c.Key, RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache", "AppCompatCache");
            _reg.EmptyKey(c.Actions, c.Key, RegHive.LocalMachine, @"SOFTWARE\Microsoft\RADAR\HeapLeakDetection\DiagnosedApplications");
        },
        ["win.trayicons"] = c =>
        {
            var tray = new List<HistoryAction>();
            foreach (var name in new[] { "IconStreams", "PastIconsStream" })
            {
                _reg.DeleteValue(tray, c.Key, RegHive.CurrentUser, Explorer + @"\TrayNotify", name);
                _reg.DeleteValue(tray, c.Key, RegHive.ClassesRoot, @"Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify", name);
            }
            if (tray.Count == 0) return;
            // Explorer rewrites these values when it exits, so it is closed first and started again afterwards.
            c.Actions.Add(new StopExplorerAction { ItemKey = c.Key, Target = "explorer.exe" });
            c.Actions.AddRange(tray);
            c.Actions.Add(new StartExplorerAction { ItemKey = c.Key, Target = "explorer.exe" });
        },
    };

    private void Rules(PlanContext c, params string[] rules)
    {
        foreach (var r in rules) c.Actions.AddRange(_files.Plan(c.Key, r, c.Ct));
    }

    /// <summary>The catalog keys that have a planner (every catalog item must; checked by a unit test).</summary>
    public IReadOnlyCollection<string> PlannedKeys => Planners().Keys.ToList();

    public HistoryPlan Plan(IReadOnlySet<int> selected, string? recentFolder = null, CancellationToken ct = default)
    {
        var plan = new HistoryPlan();
        recentFolder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent");
        var planners = Planners();
        foreach (var item in WindowsHistoryCatalog.Items.Where(i => selected.Contains(i.Id)))
        {
            ct.ThrowIfCancellationRequested();
            if (planners.TryGetValue(item.Key, out var planner)) planner(new PlanContext(plan.Actions, item.Key, recentFolder, ct));
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
        foreach (var f in folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(IsOutlookTempFolder))
            foreach (var sub in Directory.Exists(f) ? Directory.EnumerateDirectories(f).ToList() : new List<string>())
                _files.AddEmptyFolder(a, itemKey, sub, removeFolder: true, ct);
    }

    /// <summary>
    /// The registry value can name any folder (a mis-set Outlook, or another program): only a folder that is plainly Outlook's
    /// secure temp folder is emptied, i.e. "Content.Outlook" (or an older "OLK…" folder) inside the user's Internet cache, local
    /// application data or temp folder. Anything else, such as Documents, is left alone, also in scheduled runs.
    /// </summary>
    internal static bool IsOutlookTempFolder(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        var name = Path.GetFileName(full);
        if (!name.Equals("Content.Outlook", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("OLK", StringComparison.OrdinalIgnoreCase)) return false;
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.InternetCache), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
        };
        return roots.Where(r => r.Length > 0).Any(r => full.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
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
