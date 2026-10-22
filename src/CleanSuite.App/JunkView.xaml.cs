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
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.App;

/// <summary>
/// Junk files page: drives, folders and options, the results grouped by disk, and the clean, restore and exclude actions. The
/// scan → review → clean state lives in <see cref="JunkViewModel"/>; this class shows it and asks the questions.
/// </summary>
public partial class JunkView : UserControl, IToolPage
{
    private readonly JunkViewModel _vm;
    private readonly ObservableCollection<DriveChoice> _drives = new();
    private readonly ObservableCollection<string> _folders = new();
    private readonly string _ownIgnoreFolder = AceInstallation.OwnJunkIgnoreFolder;
    private uint? _driveMask;
    private bool _startScan;
    /// <summary>Status text producer, re-run after a language switch so the status line follows the language.</summary>
    private Func<string>? _status;
    /// <summary>Results the options were folded away for (once per scan, so the user can open them again).</summary>
    private readonly HashSet<JunkScanResult> _collapsedFor = new(ReferenceEqualityComparer.Instance);
    private bool _autoCollapse;

    public JunkView()
    {
        InitializeComponent();
        _vm = new JunkViewModel(Input);
        _vm.Confirm = ConfirmClean;
        _vm.Error += ex => { UiErrors.Log(ex, "junk"); SetStatus(() => Loc.F(_vm.IsChanging ? "CleanFailed" : "ScanFailed", ex.Message)); };
        _vm.Changed += Render;
        _vm.Cleaned += ShowCleaned;
        _vm.NothingToScan += () => Loc.Show(Owner, Loc.T("SelectSomething"), Loc.T("TabJunk"), MessageBoxButton.OK, MessageBoxImage.Information);
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(_vm.IsBusy) or nameof(_vm.Rows)) Render(); };
        DataContext = _vm;
        ((CollectionViewSource)FindResource("RowsView")).Source = _vm.Rows;
        DrivesList.ItemsSource = _drives;
        FoldersList.ItemsSource = _folders;
        LoadDrives(null);
        UpdateSettingsInfo();
        RemovalCombo.SelectionChanged += (_, _) => UpdateCleanButtonStyle();
        // The options panel opens as the user left it last time (folding it after a scan is not remembered).
        ScanOptionsExpander.IsExpanded = App.Ui.JunkOptionsExpanded;
        ScanOptionsExpander.Expanded += (_, _) => RememberOptions(true);
        ScanOptionsExpander.Collapsed += (_, _) => RememberOptions(false);
        UpdateCleanButtonStyle();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        ApplyCommandLine(Environment.GetCommandLineArgs().Skip(1).ToArray());
        Loaded += (_, _) => { if (_startScan) { _startScan = false; _vm.ScanCommand.Execute(null); } };
        Render();
    }

    // ---------------------------------------------------------------- IToolPage

    public bool IsBusy => _vm.IsBusy;
    public bool IsChanging => _vm.IsChanging;
    Button? IToolPage.ScanButton => ScanButton;
    Button? IToolPage.CancelButton => CancelButton;
    public void CancelWork() => _vm.Cancel();

    /// <summary>Window title text for the current selection ("12 of 340 files selected, 1.2 GB"), or null for the plain title.</summary>
    public string? SelectionTitle => _vm.Rows.Count == 0 ? null
        : Loc.F("AppTitleSelection", Loc.N0(_vm.SelectedCount), Loc.N0(_vm.Rows.Count), Loc.FormatSize(_vm.SelectedBytes));

    /// <summary>The selection or the results changed (the window updates its title).</summary>
    public event Action? SelectionTitleChanged;

    private Window Owner => Window.GetWindow(this)!;

    /// <summary>"--scan &lt;folder&gt;" (repeatable): scan only these folders and start when the page is shown; "--with-temp" adds the temp folders.</summary>
    private void ApplyCommandLine(string[] args)
    {
        var folders = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--scan" && Directory.Exists(args[i + 1])) folders.Add(Path.GetFullPath(args[i + 1]));
        if (folders.Count == 0 || args.Contains("--tab") && !args.Contains("junk")) return;
        foreach (var d in _drives) d.IsChecked = false;
        foreach (var f in folders) _folders.Add(f);
        OptTemp.IsChecked = args.Contains("--with-temp");
        _startScan = true;
    }

    private JunkInput Input() => new(
        _drives.Where(d => d.IsChecked).Select(d => d.Root).Concat(_folders).ToList(),
        new JunkOptions
        {
            CleanTempFolders = OptTemp.IsChecked == true,
            FindZeroLength = OptZero.IsChecked == true,
            UseIgnoreList = OptIgnore.IsChecked == true,
            DeleteEmptyTempFolders = OptEmptyTemp.IsChecked == true,
            ExcludeModifiedWithinWeek = OptRecent.IsChecked == true,
            ApplyAllIgnoreListsInTemp = OptTempAllIgnores.IsChecked == true,
        },
        Enum.Parse<RemovalMode>((string)((ComboBoxItem)RemovalCombo.SelectedItem).Tag));

    // ---------------------------------------------------------------- rendering

    private void SetStatus(Func<string> status)
    {
        _status = status;
        Bidi.SetText(StatusText, status());
    }

    private void Render()
    {
        var view = (CollectionViewSource)FindResource("RowsView");
        if (!ReferenceEquals(view.Source, _vm.Rows)) view.Source = _vm.Rows;
        bool busy = _vm.IsBusy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        ResultsGrid.IsReadOnly = busy;                               // no ticking while a scan or clean runs
        DrivesList.IsEnabled = FoldersList.IsEnabled = !busy;
        if (busy)
            SetStatus(_vm.IsChanging ? () => Loc.T("Cleaning")
                : () => _vm.Progress > 0 ? Loc.F("ScanningCount", Loc.N0(_vm.Progress)) : Loc.T("Scanning"));
        else if (_vm.WasCancelled)
            SetStatus(_vm.CleanWasCancelled
                ? () => Loc.T("CleanCancelled") + " " + Loc.F("CleanCancelledCount", Loc.N0(_vm.CancelledRemoved))
                : () => Loc.T("ScanCancelled"));
        else if (_vm.LastClean is { } c)
            SetStatus(() => Loc.F("CleanStatus", Loc.N0(c.Deleted), Loc.FormatSize(c.BytesFreed), Loc.N0(c.Failed.Count), Loc.N0(c.EmptyFoldersRemoved)));
        else if (_vm.Result is { } r)
        {
            SetStatus(() => Loc.F("ScanFound", Loc.N0(r.Items.Count), Loc.FormatSize(r.TotalSize), Loc.N0(r.FilesSearched), Loc.N0(r.IgnoredCount),
                Loc.N0(r.SkippedLocked.Count), _vm.ElapsedSeconds));
            // The results need the room now; the options stay one click away.
            if (r.Items.Count > 0 && !_collapsedFor.Contains(r)) { _collapsedFor.Add(r); _autoCollapse = true; ScanOptionsExpander.IsExpanded = false; _autoCollapse = false; }
        }
        else if (_status == null) SetStatus(() => Loc.T("StatusReady"));
        SelectionTitleChanged?.Invoke();
    }

    private void RememberOptions(bool expanded)
    {
        if (_autoCollapse || App.Ui.JunkOptionsExpanded == expanded) return;
        App.Ui.JunkOptionsExpanded = expanded;
        App.Ui.Save();
    }

    /// <summary>Permanent deletion (no Recycle Bin, no zip backup) is shown on the button itself.</summary>
    private void UpdateCleanButtonStyle()
    {
        bool permanent = (RemovalCombo.SelectedItem as ComboBoxItem)?.Tag as string == nameof(RemovalMode.Permanent);
        CleanButton.Style = permanent ? (Style)FindResource("DangerButton") : (Style)FindResource(typeof(Button));
    }

    private bool ConfirmClean(IReadOnlyList<JunkRow> selected)
    {
        var roots = Input();
        if (selected.Count == 0) return false;
        string how = Loc.T(roots.Mode switch { RemovalMode.RecycleBin => "HowRecycle", RemovalMode.ZipBackup => "HowZip", _ => "HowPermanent" });
        return Loc.Show(Owner, Loc.F("CleanConfirm", Loc.N0(selected.Count), Loc.FormatSize(selected.Sum(r => r.Item.Size)), how), Loc.T("CleanTitle"),
            MessageBoxButton.YesNo, roots.Mode == RemovalMode.Permanent ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }

    private void ShowCleaned(JunkRemovalResult res)
    {
        var msg = Loc.F("CleanReport", Loc.N0(res.Deleted), Loc.FormatSize(res.BytesFreed), Loc.N0(res.Failed.Count), Loc.N0(res.EmptyFoldersRemoved));
        if (res.ZipPath != null) msg += "\n\n" + Loc.F("CleanReportBackup", Loc.Ltr(res.ZipPath));
        msg += "\n\n" + Loc.F("CleanReportJournal", Loc.Ltr(res.JournalPath));
        if (res.Failed.Count > 0)
            msg += "\n\n" + Loc.T("FirstProblems") + "\n" +
                   string.Join("\n", res.Failed.Take(8).Select(f => $"• {Loc.Ltr(Path.GetFileName(f.Path))}: {Loc.Reason(f.Reason)}"));
        Loc.Show(Owner, msg, Loc.T("CleanFinished"), MessageBoxButton.OK, res.Failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A ComboBox shows a snapshot of the selected item's content; re-select to pick up the new text.
        int i = RemovalCombo.SelectedIndex;
        RemovalCombo.SelectedIndex = -1;
        RemovalCombo.SelectedIndex = i;
        LoadDrives(_drives.Count > 0 ? CurrentDriveMask() : _driveMask);
        foreach (var r in _vm.Rows) r.RefreshTexts();
        ((CollectionViewSource)FindResource("RowsView")).View?.Refresh();
        UpdateSettingsInfo();
        if (_status != null) Bidi.SetText(StatusText, _status());
        SelectionTitleChanged?.Invoke();
    }

    // ---------------------------------------------------------------- setup

    private uint CurrentDriveMask()
    {
        uint m = 0;
        foreach (var d in _drives.Where(d => d.IsChecked)) m |= 1u << (char.ToUpperInvariant(d.Root[0]) - 'A');
        return m;
    }

    private void LoadDrives(uint? mask)
    {
        _driveMask = mask;
        _drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = d.Name.TrimEnd('\\');
            int bit = char.ToUpperInvariant(root[0]) - 'A';
            bool on = mask is uint m ? (m & (1u << bit)) != 0 : true;   // original default: all fixed drives
            var name = string.IsNullOrEmpty(d.VolumeLabel) ? root : $"{root} ({d.VolumeLabel})";
            _drives.Add(new DriveChoice(root, name, Loc.F("DriveFree", Loc.N0(d.AvailableFreeSpace / (1L << 30))), on));
        }
    }

    private void UpdateSettingsInfo()
    {
        var ignore = AceInstallation.DefaultJunkIgnoreFolders();
        Bidi.SetText(SettingsInfo, Loc.F("SettingsInfo",
            Loc.T("SettingsDefaults"),
            _vm.Settings.ActiveProfile,
            Loc.N0(_vm.Settings.ToPatternSet().Patterns.Count),
            Loc.N0(_vm.Settings.Exclusions.Count),
            ignore.Count == 0 ? Loc.T("None") : string.Join(Loc.Instance.IsRtl ? "، " : ", ", ignore.Select(Loc.Ltr))));
    }

    // ---------------------------------------------------------------- actions

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("RestoreOpenTitle"),
            Filter = Loc.T("RestoreFilter"),
            InitialDirectory = Directory.Exists(CleanSuitePaths.Backups) ? CleanSuitePaths.Backups : null,
        };
        if (dlg.ShowDialog(Owner) != true) return;
        IReadOnlyList<JunkBackupArchive.BackupEntry> entries;
        try { entries = JunkBackupArchive.List(dlg.FileName); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Loc.Show(Owner, Loc.F("RestoreFailed", Loc.Reason(ex.Message)), Loc.T("RestoreTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        new RestoreWindow(dlg.FileName, entries, JunkBackupArchive.IsLegacyBackup(dlg.FileName)) { Owner = Owner }.ShowDialog();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("AddFolderTitle"), Multiselect = true };
        if (dlg.ShowDialog(Owner) != true) return;
        foreach (var f in dlg.FolderNames)
            if (!_folders.Contains(f, StringComparer.OrdinalIgnoreCase)) _folders.Add(f);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in FoldersList.SelectedItems.Cast<string>().ToList()) _folders.Remove(f);
    }

    private List<JunkRow> Highlighted() => ResultsGrid.SelectedItems.OfType<JunkRow>().ToList();

    private void CheckHighlighted_Click(object sender, RoutedEventArgs e) => _vm.SetMany(Highlighted(), _ => true);
    private void UncheckHighlighted_Click(object sender, RoutedEventArgs e) => _vm.SetMany(Highlighted(), _ => false);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var row = Highlighted().FirstOrDefault();
        if (row == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Item.FullPath}\"") { UseShellExecute = true });
    }

    /// <summary>Adds the highlighted file names to CleanSuite's own ignore list (never to the original's files).</summary>
    private void ExcludeAlways_Click(object sender, RoutedEventArgs e)
    {
        var rows = Highlighted();
        if (rows.Count == 0 || _vm.IsBusy) return;
        var file = Path.Combine(_ownIgnoreFolder, "user.ini");
        var lines = rows.Select(r => r.Item.Flags.HasFlag(JunkReason.TempFolder) ? r.Name + " /T" : r.Name).Distinct(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(_ownIgnoreFolder);
            File.AppendAllLines(file, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Loc.Show(Owner, Loc.F("SaveFailed", ex.Message), Loc.T("TabJunk"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _vm.Remove(rows);
        UpdateSettingsInfo();
        int count = rows.Count;
        SetStatus(() => Loc.F("ExcludedStatus", Loc.N0(count), Loc.Ltr(file)));
    }

    /// <summary>Lets the Folder column fill the remaining width (replacement for a "*" column, see XAML).</summary>
    private void ResultsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double others = ResultsGrid.Columns.Where(c => c != FolderColumn).Sum(c => c.ActualWidth);
        double available = ResultsGrid.ActualWidth - others - SystemParameters.VerticalScrollBarWidth - 8;
        FolderColumn.Width = new DataGridLength(Math.Max(150, available));
    }
}
