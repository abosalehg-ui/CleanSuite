using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.App.ViewModels;
using CleanSuite.Compat;
using CleanSuite.Duplicates;
using CleanSuite.Safety;

namespace CleanSuite.App;

/// <summary>
/// Find Duplicate Files page: folders and criteria, the groups found, and the questions before a removal. The scan → review →
/// clean state lives in <see cref="DuplicatesViewModel"/>.
/// </summary>
public partial class DuplicatesView : UserControl, IToolPage
{
    private readonly DuplicatesViewModel _vm;
    private readonly ObservableCollection<RootOption> _roots = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _profiles = new(DupProfiles.BuiltIn, StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _ignore = Array.Empty<string>();
    private readonly IReadOnlyList<string> _excludes = DupProfiles.DefaultExcludes;
    private bool _autoStarted;

    public DuplicatesView()
    {
        InitializeComponent();
        _vm = new DuplicatesViewModel(Options, () => RemovalCombo.SelectedIndex == 1 ? RemovalMode.ZipBackup : RemovalMode.RecycleBin);
        _vm.Confirm = ConfirmRemoval;
        _vm.Error += ex => { UiErrors.Show(Owner, ex, "duplicates"); Render(); };
        _vm.Changed += RenderStatus;
        _vm.Cleaned += ShowCleaned;
        _vm.NoRoots += () => Loc.Show(Owner, Loc.T("DpNoRoots"), Loc.T("TabDuplicates"), MessageBoxButton.OK, MessageBoxImage.Information);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Rows)) Render();
            else if (e.PropertyName == nameof(_vm.IsBusy)) { Grid.IsReadOnly = _vm.IsBusy; RenderStatus(); }
        };
        DataContext = _vm;
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
            _vm.ScanCommand.Execute(null);
        };
    }

    // ---------------------------------------------------------------- IToolPage

    public bool IsBusy => _vm.IsBusy;
    public bool IsChanging => _vm.IsChanging;
    Button? IToolPage.ScanButton => ScanButton;
    Button? IToolPage.CancelButton => CancelButton;
    public void CancelWork() => _vm.Cancel();

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

    // ---------------------------------------------------------------- rendering

    private void Render()
    {
        var rows = _vm.Rows;
        var view = new ListCollectionView(rows.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DupRow.GroupNumber)));
        // A GroupStyle on an empty grid collapses its columns, so grouping is attached only when there are rows.
        Grid.GroupStyle.Clear();
        if (rows.Count > 0) Grid.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)FindResource("GroupHeader") });
        Grid.ItemsSource = rows.Count > 0 ? view : rows;
        Grid.IsReadOnly = _vm.IsBusy;
        RenderStatus();
    }

    private void RenderStatus()
    {
        var r = _vm.Result;
        if (_vm.IsBusy)
            StatusText.Text = _vm.IsChanging ? Loc.T("DpRemoving")
                : _vm.Progress is { Phase: "hash" } p ? Loc.F("DpHashing", Loc.N0(p.Value))
                : _vm.Progress is { } q ? Loc.F("DpScanningCount", Loc.N0(q.Value)) : Loc.T("DpScanning");
        else if (_vm.WasCancelled) StatusText.Text = Loc.T("ScanCancelled");
        else if (r == null) StatusText.Text = Loc.T("DpIntro");
        else
            StatusText.Text = Loc.F("DpResult", Loc.N0(r.FilesSearched), Loc.N0(r.Groups.Count), Loc.N0(_vm.Rows.Count),
                                    Loc.FormatSize(r.Groups.Sum(g => g.Reclaimable)))
                              + " · " + Loc.F("DpElapsed", _vm.ElapsedSeconds.ToString("0.0", Loc.Instance.NumberCulture))
                              + (_vm.SelectedCount > 0 ? " · " + Loc.F("DpSelected", Loc.N0(_vm.SelectedCount), Loc.FormatSize(_vm.SelectedBytes)) : "");
        var warnings = new List<string>();
        if (r is { ContentVerified: false } && _vm.Rows.Count > 0) warnings.Add(Loc.T("DpNotVerified"));
        if (r is { Unreadable.Count: > 0 }) warnings.Add(Loc.F("DpUnreadable", Loc.N0(r.Unreadable.Count)));
        WarningText.Text = string.Join("\n", warnings);
        WarningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- actions

    /// <summary>The original's "Check mark one file in each group", with a warning first when content was not compared.</summary>
    private void CheckOne_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CheckOneCommand.CanExecute(null)) return;
        if (_vm.Result is { ContentVerified: false } && Loc.Show(Owner, Loc.T("DpCheckOneWarning"), Loc.T("TabDuplicates"), MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _vm.CheckOneCommand.Execute(null);
    }

    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is DupRow r && File.Exists(r.File.FullPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{r.File.FullPath}\"") { UseShellExecute = true });
    }

    /// <summary>Asks before a removal: first whether whole groups may go (when every copy of some group is checked), then the removal itself.</summary>
    private bool ConfirmRemoval(IReadOnlyList<DupRow> chosen)
    {
        var selected = new HashSet<string>(chosen.Select(r => r.File.FullPath), StringComparer.OrdinalIgnoreCase);
        int whole = DuplicateRemover.WholeGroupsSelected(_vm.Groups, selected);
        _vm.AllowWholeGroups = false;
        if (whole > 0)
        {
            if (Loc.Show(Owner, Loc.F("DpWholeGroups", Loc.N0(whole)), Loc.T("DpWholeGroupsTitle"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
            _vm.AllowWholeGroups = true;
        }
        bool zip = RemovalCombo.SelectedIndex == 1;
        bool ok = Loc.Show(Owner, Loc.F("DpConfirm", Loc.N0(selected.Count), Loc.FormatSize(chosen.Sum(r => r.File.Size)), Loc.T(zip ? "HowZip" : "HowRecycle")),
                      Loc.T("DpConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!ok) _vm.AllowWholeGroups = false;
        return ok;
    }

    private void ShowCleaned(DupRemovalResult res)
    {
        var msg = Loc.F("DpRemoved", Loc.N0(res.Deleted), Loc.FormatSize(res.BytesFreed));
        if (res.Failed.Count > 0) msg += "\n" + Loc.F("DpNotRemoved", Loc.N0(res.Failed.Count), Loc.Reason(res.Failed[0].Reason));
        if (res.ZipPath != null && File.Exists(res.ZipPath)) msg += "\n\n" + Loc.F("RgBackupAt", Loc.Ltr(res.ZipPath));
        Loc.Show(Owner, msg, Loc.T("TabDuplicates"), MessageBoxButton.OK, res.Failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}

public sealed class RootOption(string path) : ViewModelBase
{
    private bool _checked;
    public string Path { get; } = path;
    public bool IsChecked
    {
        get => _checked;
        set => Set(ref _checked, value);
    }
}

/// <summary>One file of a duplicate group in the grid; nothing is checked after a scan, as in the original.</summary>
public sealed class DupRow(DupGroup group, DupFile file) : SelectableRow(false)
{
    public DupGroup Group { get; } = group;
    public DupFile File { get; } = file;
    public int GroupNumber => Group.Number;
    public string GroupTitle => Loc.F("DpGroupTitle", Loc.N0(Group.Number), Loc.N0(Group.Files.Count), Loc.FormatSize(Group.Reclaimable));
    public string SizeText => Loc.FormatSize(File.Size);
    public string ModifiedText => Loc.FormatDate(File.LastWriteUtc.ToLocalTime());
}
