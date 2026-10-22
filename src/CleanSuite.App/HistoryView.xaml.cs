using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.App.ViewModels;
using CleanSuite.History;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.App;

/// <summary>
/// Erase History tab: choose items, analyse (read-only), review the planned actions, clean the checked ones. Registry
/// changes are exported to a .reg backup first and everything is journaled (HistoryExecutor). The Chromium cleaner and
/// the plug-in engine are listed but unavailable: their analysis was stopped by the automated safety system.
/// </summary>
public partial class HistoryView : UserControl, IToolPage
{
    private readonly List<HistoryOption> _windows;
    private readonly List<HistoryOption> _start;
    private readonly List<HistoryOption> _firefox;
    private readonly List<HistoryOption> _unavailable;
    private readonly HistoryViewModel _vm;

    private static readonly FirefoxItems[] FirefoxChoices =
    [
        FirefoxItems.History, FirefoxItems.Cache, FirefoxItems.Cookies, FirefoxItems.Forms, FirefoxItems.SearchHistory,
        FirefoxItems.Downloads, FirefoxItems.Passwords, FirefoxItems.Sessions, FirefoxItems.SitePreferences,
        FirefoxItems.CrashReports, FirefoxItems.Compact,
    ];

    public HistoryView()
    {
        InitializeComponent();
        HistoryOption Item(HistoryItemInfo i) => new("Hi_" + Key(i.Key), i.DefaultOn) { Id = i.Id, NeedsAdmin = i.NeedsAdmin };
        _windows = WindowsHistoryCatalog.Items.Where(i => i.Page == HistoryPage.Windows).Select(Item).ToList();
        _start = WindowsHistoryCatalog.Items.Where(i => i.Page == HistoryPage.StartMenu).Select(Item).ToList();
        _firefox = FirefoxChoices.Select(f => new HistoryOption("Hf_" + f, (FirefoxItems.Default & f) != 0) { Flag = f }).ToList();
        _unavailable =
        [
            new HistoryOption("HsChromium", false) { Enabled = false, TipKey = "HsBlockedTip" },
            new HistoryOption("HsPlugins", false) { Enabled = false, TipKey = "HsBlockedTip" },
        ];
        WindowsList.ItemsSource = _windows;
        StartList.ItemsSource = _start;
        FirefoxList.ItemsSource = _firefox;
        UnavailableList.ItemsSource = _unavailable;
        _vm = new HistoryViewModel(ct =>
        {
            var (ids, ff, allUsers) = Dispatcher.Invoke(() => (
                (IReadOnlySet<int>)_windows.Concat(_start).Where(o => o.IsChecked).Select(o => o.Id).ToHashSet(),
                _firefox.Where(o => o.IsChecked).Aggregate(FirefoxItems.None, (a, o) => a | o.Flag),
                AllUsersBox.IsChecked == true));
            return Analyse(ids, ff, allUsers, ct);
        });
        _vm.Confirm = chosen => Loc.Show(Owner, Loc.F("HsConfirm", Loc.N0(chosen.Count), Loc.FormatSize(chosen.Sum(r => r.Action.Size))),
            Loc.T("HsConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.Error += ex => { UiErrors.Show(Owner, ex, "history"); Render(); };
        _vm.Analysed += Render;
        _vm.Cleaned += ShowCleaned;
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(_vm.IsBusy) or nameof(_vm.Rows)) Render(); };
        DataContext = _vm;
        Loc.Instance.LanguageChanged += (_, _) => Render();
        Render();
        // Developer aid: "--auto-scan" starts the (read-only) analysis when this tab is shown, for --snapshot checks.
        Loaded += (_, _) =>
        {
            if (!_autoStarted && IsVisible && Environment.GetCommandLineArgs().Contains("--auto-scan")) { _autoStarted = true; _vm.ScanCommand.Execute(null); }
        };
    }

    private bool _autoStarted;

    /// <summary>True while an analysis or clean is running (the snapshot aid waits for it).</summary>
    public bool IsBusy => _vm.IsBusy;

    Button? IToolPage.ScanButton => AnalyseButton;
    Button? IToolPage.CancelButton => CancelButton;
    public void CancelWork() => _vm.Cancel();

    /// <summary>True while history items are being removed; the window waits for it before closing.</summary>
    public bool IsChanging => _vm.IsChanging;

    internal static string Key(string itemKey) => itemKey.Replace('.', '_');

    private Window Owner => Window.GetWindow(this)!;

    private IEnumerable<HistoryOption> AllOptions => _windows.Concat(_start).Concat(_firefox);

    private HistoryPlan Analyse(IReadOnlySet<int> ids, FirefoxItems ff, bool allUsers, CancellationToken ct)
    {
        var files = new RulePlanner(TokenExpander.ForCurrentUser(), RulePlanner.DefaultNeverDelete);
        var plan = new WindowsHistoryCleaner(files, new RegistryPlanner(WindowsRegistryRoots.Instance),
            () => TempFolderLocator.Locate(new WindowsEnvironment())).Plan(ids, ct: ct);
        if (ff != FirefoxItems.None)
        {
            var whitelist = new CookieWhitelist(AceHistorySettings.Read().CookieWhitelist);
            plan.Add(new FirefoxCleaner(files).Plan(ff, whitelist, allUsers ? UserFolders.AllUsers() : UserFolders.CurrentUser(), ct));
        }
        return plan;
    }

    private void Render()
    {
        foreach (var o in AllOptions.Concat(_unavailable)) o.Refresh();
        Grid.ItemsSource = null;
        Grid.ItemsSource = _vm.Rows;
        Grid.IsReadOnly = _vm.IsBusy;
        OptionsPanel.IsEnabled = !_vm.IsBusy;
        Progress.Visibility = _vm.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        var plan = _vm.Plan;
        if (_vm.IsBusy) StatusText.Text = Loc.T(_vm.IsChanging ? "HsCleaning" : "HsAnalysing");
        else if (_vm.WasCancelled) StatusText.Text = Loc.T("ScanCancelled");
        else if (plan == null) StatusText.Text = Loc.T("HsIntro");
        else
        {
            StatusText.Text = Loc.F("HsCount", Loc.N0(_vm.Rows.Count), Loc.FormatSize(_vm.Rows.Sum(r => r.Action.Size)))
                              + (_vm.Last is { } l ? " · " + Loc.F("HsDone", Loc.N0(l.Succeeded), Loc.FormatSize(l.BytesFreed)) : "");
            var notes = plan.Notes.Select(n => n.Code switch
            {
                "browser-running" => Loc.F("HsBrowserRunning", Loc.Ltr(n.Text)),
                "not-a-profile" => Loc.F("HsNotProfile", Loc.Ltr(n.Text)),
                _ => n.Text,
            }).ToList();
            if (AllOptions.Any(o => o.IsChecked && o.NeedsAdmin)) notes.Add(Loc.T("HsAdminHint"));
            Bidi.SetText(NotesText, string.Join("\n", notes));
            NotesText.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowCleaned(HistoryRunResult res)
    {
        var msg = Loc.F("HsDone", Loc.N0(res.Succeeded), Loc.FormatSize(res.BytesFreed));
        if (res.Failed.Count > 0) msg += "\n" + Loc.F("HsFailed", Loc.N0(res.Failed.Count), Loc.Reason(res.Failed[0].Reason));
        if (res.RegistryBackupPath != null) msg += "\n\n" + Loc.F("RgBackupAt", Loc.Ltr(res.RegistryBackupPath));
        Loc.Show(Owner, msg, Loc.T("TabHistory"), MessageBoxButton.OK, res.Failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}

/// <summary>One choosable item (Windows/Start-menu item id or Firefox flag).</summary>
public sealed class HistoryOption(string labelKey, bool isChecked) : INotifyPropertyChanged
{
    private bool _checked = isChecked;
    public int Id { get; init; }
    public FirefoxItems Flag { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool Enabled { get; init; } = true;
    public string? TipKey { get; init; }
    public string Label => Loc.T(labelKey) + (NeedsAdmin ? " " + Loc.T("HsAdminMark") : "");
    public string? Tip => TipKey != null ? Loc.T(TipKey) : NeedsAdmin ? Loc.T("HsAdminHint") : null;

    public bool IsChecked
    {
        get => _checked;
        set { if (_checked != value) { _checked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new(nameof(Label)));
        PropertyChanged?.Invoke(this, new(nameof(Tip)));
    }
}

/// <summary>One planned action in the review grid.</summary>
public sealed class HistoryActionRow(HistoryAction action) : SelectableRow(true)
{
    public HistoryAction Action { get; } = action;

    public string ItemText
    {
        get
        {
            foreach (var key in new[] { "Hi_" + HistoryView.Key(Action.ItemKey), "Hfk_" + HistoryView.Key(Action.ItemKey) })
                if (Loc.T(key) is var t && t != key) return t;
            return Action.ItemKey;
        }
    }

    public string KindText => Loc.T("Hk_" + Action.Kind.Replace('-', '_')) is var t && !t.StartsWith("Hk_", StringComparison.Ordinal) ? t : Action.Kind;
    /// <summary>The file, folder or key, then what is done there (SQL, INI section…) after a separator that reads in both directions.</summary>
    public string TargetText => Action.Detail is { } d ? Action.Target + "  —  " + d : Action.Target;
    public string SizeText => Action.Size > 0 ? Loc.FormatSize(Action.Size) : "";
    public string Tip => TargetText;
}
