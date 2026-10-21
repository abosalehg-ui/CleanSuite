using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CleanSuite.App.Localization;
using CleanSuite.Compat;
using CleanSuite.Duplicates;
using CleanSuite.Junk;

namespace CleanSuite.App;

/// <summary>
/// Find Duplicate Files tab: choose folders and criteria, scan (read-only), review the groups, check the copies to
/// remove, and send them to the Recycle Bin or a "Dup …" zip backup. Nothing is checked after a scan (as in the
/// original); a copy of every group is always kept unless removing whole groups is confirmed.
/// </summary>
public partial class DuplicatesView : UserControl
{
    private readonly ObservableCollection<RootOption> _roots = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _profiles = new(DupProfiles.BuiltIn, StringComparer.OrdinalIgnoreCase);
    private List<DupRow> _rows = new();
    private DupScanResult? _result;
    private IReadOnlyList<string> _ignore = Array.Empty<string>();
    private IReadOnlyList<string> _excludes = DupProfiles.DefaultExcludes;
    private CancellationTokenSource? _cts;
    private bool _autoStarted;

    public DuplicatesView()
    {
        InitializeComponent();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            _roots.Add(new RootOption(d.Name.TrimEnd('\\')));
        RootsList.ItemsSource = _roots;
        _ignore = IgnoreFile.Load(AceDupSettings.IgnoreFolder(AceInstallation.Locate()) ?? "");
        FillProfiles("Default");
        Loc.Instance.LanguageChanged += (_, _) => { FillProfiles((ProfileCombo.SelectedItem as ProfileItem)?.Name ?? "Default"); Render(); };
        Render();
        // Developer aid: "--auto-scan" with "--scan <folder>" scans that folder when the tab is shown (for --snapshot checks).
        Loaded += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (_autoStarted || !IsVisible || !args.Contains("--auto-scan") || !args.Contains("--scan")) return;
            _autoStarted = true;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--scan" && Directory.Exists(args[i + 1])) _roots.Add(new RootOption(Path.GetFullPath(args[i + 1]).TrimEnd('\\')) { IsChecked = true });
            ContentCombo.SelectedIndex = 2;
            Scan_Click(this, new RoutedEventArgs());
        };
    }

    /// <summary>True while a scan or removal is running (the snapshot aid waits for it).</summary>
    public bool Busy => _busy;

    /// <summary>True while files are being removed; the window waits for it before closing.</summary>
    public bool IsChanging { get; private set; }

    private bool _busy;

    /// <summary>Scan and removal never overlap, and the selection cannot change while either runs.</summary>
    private void SetBusy(bool busy, bool cancellable = false)
    {
        _busy = busy;
        ScanButton.IsEnabled = CheckOneButton.IsEnabled = UncheckAllButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy && cancellable;
        Grid.IsReadOnly = busy;
        UpdateButtons();
    }

    private Window Owner => Window.GetWindow(this)!;

    private sealed record ProfileItem(string Name, string Label)
    {
        public override string ToString() => Label;
    }

    private void FillProfiles(string selected)
    {
        string Label(string n) => Loc.T("DpProf_" + n.Replace(" ", "").Replace("(", "").Replace(")", "").Replace("*", "").Replace(".", "")) is var t
                                  && !t.StartsWith("DpProf_", StringComparison.Ordinal) ? t : n;
        var items = _profiles.Keys.Select(k => new ProfileItem(k, Label(k))).ToList();
        ProfileCombo.ItemsSource = items;
        ProfileCombo.SelectedItem = items.FirstOrDefault(i => i.Name.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? items[0];
    }

    /// <summary>The options shown, or null after telling the user that the minimum size is not a whole number.</summary>
    private DupOptions? Options()
    {
        long n = 1;
        if (MinSizeBox.IsChecked == true &&
            !(long.TryParse(Loc.Digits(MinSizeText.Text), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n)
              && n is >= 1 and <= 1_000_000))
        {
            Loc.Show(Owner, Loc.F("SdBadInterval", Loc.N0(1_000_000)), Loc.T("TabDuplicates"), MessageBoxButton.OK, MessageBoxImage.Information);
            MinSizeText.Focus();
            return null;
        }
        return new DupOptions
        {
            Roots = _roots.Where(r => r.IsChecked).Select(r => r.Path).ToList(),
            SameName = NameBox.IsChecked == true,
            SameSize = SizeBox.IsChecked == true,
            SameDate = DateBox.IsChecked == true,
            Content = (ContentCompare)Math.Max(0, ContentCombo.SelectedIndex),
            MinSize = MinSizeBox.IsChecked == true ? n << (UnitCombo.SelectedIndex == 1 ? 10 : 20) : 0,
            Patterns = _profiles[((ProfileItem)ProfileCombo.SelectedItem).Name],
            Excludes = _excludes,
            Ignore = _ignore,
        }.Normalized();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true };
        if (dlg.ShowDialog(Owner) != true) return;
        foreach (var f in dlg.FolderNames)
        {
            var p = f.TrimEnd('\\');
            if (_excludes.Any(x => CleanSuite.Matching.AceWildcard.IsMatch(p, x.EndsWith('*') ? x : x + "*")))
                Loc.Show(Owner, Loc.F("DpExcluded", Loc.Ltr(p)), Loc.T("TabDuplicates"), MessageBoxButton.OK, MessageBoxImage.Information);
            if (!_roots.Any(r => r.Path.Equals(p, StringComparison.OrdinalIgnoreCase))) _roots.Add(new RootOption(p) { IsChecked = true });
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Options() is not { } o) return;
        if (o.Roots.Count == 0)
        {
            Loc.Show(Owner, Loc.T("DpNoRoots"), Loc.T("TabDuplicates"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, cancellable: true);
        StatusText.Text = Loc.T("DpScanning");
        var sw = Stopwatch.StartNew();
        try
        {
            var scanner = new DuplicateScanner(o);
            scanner.Progress += (phase, n) => Dispatcher.BeginInvoke(() =>
                StatusText.Text = phase == "hash" ? Loc.F("DpHashing", Loc.N0(n)) : Loc.F("DpScanningCount", Loc.N0(n)));
            _result = await Task.Run(() => scanner.Scan(ct), ct);
            _rows = _result.Groups.SelectMany(g => g.Files.Select(f => new DupRow(g, f))).ToList();
            foreach (var r in _rows) r.PropertyChanged += (_, _) => UpdateButtons();
            Render();
            StatusText.Text += " · " + Loc.F("DpElapsed", sw.Elapsed.TotalSeconds.ToString("0.0", Loc.Instance.NumberCulture));
        }
        catch (OperationCanceledException) { StatusText.Text = Loc.T("ScanCancelled"); }
        catch (Exception ex) { StatusText.Text = ""; UiErrors.Show(Owner, ex, "duplicates scan"); }
        finally { SetBusy(false); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Render()
    {
        var view = new ListCollectionView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DupRow.GroupNumber)));
        // A GroupStyle on an empty grid collapses its columns, so grouping is attached only when there are rows.
        Grid.GroupStyle.Clear();
        if (_rows.Count > 0) Grid.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)FindResource("GroupHeader") });
        Grid.ItemsSource = _rows.Count > 0 ? view : _rows;
        if (_result == null) StatusText.Text = Loc.T("DpIntro");
        else
            StatusText.Text = Loc.F("DpResult", Loc.N0(_result.FilesSearched), Loc.N0(_result.Groups.Count), Loc.N0(_rows.Count),
                Loc.FormatSize(_result.Groups.Sum(g => g.Reclaimable)));
        var warnings = new List<string>();
        if (_result is { ContentVerified: false } && _rows.Count > 0) warnings.Add(Loc.T("DpNotVerified"));
        if (_result is { Unreadable.Count: > 0 }) warnings.Add(Loc.F("DpUnreadable", Loc.N0(_result.Unreadable.Count)));
        WarningText.Text = string.Join("\n", warnings);
        WarningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons() => DeleteButton.IsEnabled = !_busy && _rows.Any(r => r.Selected);

    /// <summary>The original's "Check mark one file in each group": checks the first file of every group (all but one stay).</summary>
    private void CheckOne_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _rows.Count == 0) return;
        if (_result is { ContentVerified: false } && Loc.Show(Owner, Loc.T("DpCheckOneWarning"), Loc.T("TabDuplicates"), MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        foreach (var g in _rows.GroupBy(r => r.GroupNumber))
            if (g.Count() >= 2) g.First().Selected = true;
    }

    private void UncheckAll_Click(object sender, RoutedEventArgs e) { if (!_busy) foreach (var r in _rows) r.Selected = false; }

    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is DupRow r && File.Exists(r.File.FullPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{r.File.FullPath}\"") { UseShellExecute = true });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _result == null) return;
        var selected = new HashSet<string>(_rows.Where(r => r.Selected).Select(r => r.File.FullPath), StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) return;
        int whole = DuplicateRemover.WholeGroupsSelected(_result.Groups, selected);
        bool allowWhole = false;
        if (whole > 0)
        {
            if (Loc.Show(Owner, Loc.F("DpWholeGroups", Loc.N0(whole)), Loc.T("DpWholeGroupsTitle"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            allowWhole = true;
        }
        var mode = RemovalCombo.SelectedIndex == 1 ? RemovalMode.ZipBackup : RemovalMode.RecycleBin;
        var bytes = _rows.Where(r => r.Selected).Sum(r => r.File.Size);
        if (Loc.Show(Owner, Loc.F("DpConfirm", Loc.N0(selected.Count), Loc.FormatSize(bytes), Loc.T(mode == RemovalMode.ZipBackup ? "HowZip" : "HowRecycle")),
                Loc.T("DpConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        DupRemovalResult res;
        var groups = _result.Groups;
        SetBusy(true);
        IsChanging = true;
        StatusText.Text = Loc.T("DpRemoving");
        try { res = await Task.Run(() => new DuplicateRemover().Remove(groups, selected, mode, null, allowWhole)); }
        catch (Exception ex) { UiErrors.Show(Owner, ex, "duplicates delete"); Render(); return; }
        finally { IsChanging = false; SetBusy(false); }
        var failed = new HashSet<string>(res.Failed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        _rows = _rows.Where(r => !r.Selected || failed.Contains(r.File.FullPath) || File.Exists(r.File.FullPath)).ToList();
        // a group with a single remaining file is no longer a duplicate group
        _rows = _rows.GroupBy(r => r.GroupNumber).Where(g => g.Count() >= 2).SelectMany(g => g).ToList();
        var msg = Loc.F("DpRemoved", Loc.N0(res.Deleted), Loc.FormatSize(res.BytesFreed));
        if (res.Failed.Count > 0) msg += "\n" + Loc.F("DpNotRemoved", Loc.N0(res.Failed.Count), Loc.Reason(res.Failed[0].Reason));
        if (res.ZipPath != null && File.Exists(res.ZipPath)) msg += "\n\n" + Loc.F("RgBackupAt", Loc.Ltr(res.ZipPath));
        Loc.Show(Owner, msg, Loc.T("TabDuplicates"), MessageBoxButton.OK, res.Failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        Render();
    }
}

public sealed class RootOption(string path) : INotifyPropertyChanged
{
    private bool _checked;
    public string Path { get; } = path;
    public bool IsChecked
    {
        get => _checked;
        set { if (_checked != value) { _checked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); } }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One file of a duplicate group in the grid.</summary>
public sealed class DupRow(DupGroup group, DupFile file) : INotifyPropertyChanged
{
    private bool _selected;
    public DupGroup Group { get; } = group;
    public DupFile File { get; } = file;
    public int GroupNumber => Group.Number;
    public string GroupTitle => Loc.F("DpGroupTitle", Loc.N0(Group.Number), Loc.N0(Group.Files.Count), Loc.FormatSize(Group.Reclaimable));
    public string SizeText => Loc.FormatSize(File.Size);
    public string ModifiedText => Loc.FormatDate(File.LastWriteUtc.ToLocalTime());
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    }
}
