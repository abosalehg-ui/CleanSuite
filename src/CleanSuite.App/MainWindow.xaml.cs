using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.Compat;
using CleanSuite.Junk;

namespace CleanSuite.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<JunkRow> _rows = new();
    private readonly ObservableCollection<DriveChoice> _drives = new();
    private readonly ObservableCollection<string> _folders = new();
    private readonly string _ownIgnoreFolder = AceInstallation.OwnJunkIgnoreFolder;

    private AceJunkSettings _settings = new();
    private IReadOnlyList<string> _lastTempFolders = Array.Empty<string>();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _cleaning;
    private bool _initializing = true;
    private uint? _driveMask;
    /// <summary>Status text producer, re-run after a language switch so the status line follows the language.</summary>
    private Func<string> _status = () => Loc.T("StatusReady");

    public MainWindow()
    {
        InitializeComponent();
        ((CollectionViewSource)FindResource("RowsView")).Source = _rows;
        DrivesList.ItemsSource = _drives;
        FoldersList.ItemsSource = _folders;
        LanguageCombo.SelectedIndex = Loc.Instance.Language == UiLanguage.Arabic ? 0 : 1;
        ThemeCombo.SelectedIndex = (int)ThemeManager.Choice;
        LoadDrives(null);
        UpdateSettingsInfo();
        SetStatus(_status);
        UpdateTitle();
        ThemeManager.ApplyTitleBar(this);
        // Snapshots render the visual tree, not the desktop composition, so they keep the solid colours.
        if (!Environment.GetCommandLineArgs().Contains("--snapshot")) ThemeManager.ApplyBackdrop(this);
        KeyDown += Window_KeyDown;
        RemovalCombo.SelectionChanged += (_, _) => UpdateCleanButtonStyle();
        UpdateCleanButtonStyle();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        _initializing = false;
        ApplyCommandLine(Environment.GetCommandLineArgs().Skip(1).ToArray());
    }

    /// <summary>"--scan &lt;folder&gt;" (repeatable): scan only these folders and start immediately.</summary>
    private void ApplyCommandLine(string[] args)
    {
        var folders = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--scan" && Directory.Exists(args[i + 1])) folders.Add(Path.GetFullPath(args[i + 1]));
        // Developer aid: "--size 760x460" opens the window at that size (layout checks at the minimum size).
        int size = Array.IndexOf(args, "--size");
        if (size >= 0 && size + 1 < args.Length && args[size + 1].Split('x') is [var w, var h]
            && double.TryParse(w, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var width)
            && double.TryParse(h, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var height))
        {
            Width = Math.Max(width, MinWidth);
            Height = Math.Max(height, MinHeight);
        }
        int tab = Array.IndexOf(args, "--tab");
        if (tab >= 0 && tab + 1 < args.Length) Open(args[tab + 1]);
        int snap = Array.IndexOf(args, "--snapshot");
        if (snap >= 0 && snap + 1 < args.Length) SnapshotWhenIdle(args[snap + 1]);
        if (folders.Count == 0) return;
        foreach (var d in _drives) d.IsChecked = false;
        foreach (var f in folders) _folders.Add(f);
        OptTemp.IsChecked = args.Contains("--with-temp");
        Loaded += (_, _) => Scan_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Developer aids: "--snapshot &lt;file.png&gt;" renders the window once loading and any start-up scan have finished, then
    /// closes; with "--open-restore &lt;zip&gt;" the restore window is opened and rendered instead, with "--open-compactor" the
    /// registry compactor with a sample report (nothing is analysed). Used to check layout,
    /// right-to-left mirroring and themes without screen capture.
    /// </summary>
    private void SnapshotWhenIdle(string file)
    {
        var args = Environment.GetCommandLineArgs();
        int r = Array.IndexOf(args, "--open-restore");
        string? restoreZip = r >= 0 && r + 1 < args.Length ? args[r + 1] : null;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        RestoreWindow? restore = null;
        timer.Tick += (_, _) =>
        {
            if (!IsLoaded || _busy || RegistryTab.Busy || HistoryTab.Busy || DuplicatesTab.Busy || DiskTab.Busy
                || HomeTab.Busy || StartupTab.Busy || UninstallTab.Busy || RestoreTab.Busy || ShortcutsTab.Busy) return;
            // The restore window checks its destinations in the background: open it, then wait for that too.
            if (restoreZip != null && restore == null)
            {
                restore = new RestoreWindow(restoreZip, JunkBackupArchive.List(restoreZip), JunkBackupArchive.IsLegacyBackup(restoreZip)) { Owner = this };
                restore.Show();
                return;
            }
            if (restore is { Busy: true }) return;
            timer.Stop();
            Window target = this;
            if (args.Contains("--open-about"))
            {
                target = new AboutWindow { Owner = this };
                target.Show();
                target.UpdateLayout();
            }
            else if (args.Contains("--open-compactor"))
            {
                target = new RegistryCompactWindow(RegistryCompactWindow.PreviewReport()) { Owner = this };
                target.Show();
                target.UpdateLayout();
            }
            else if (restore != null)
            {
                target = restore;
                target.UpdateLayout();
            }
            Dispatcher.BeginInvoke(() => { Snapshot.Save(target, file); Close(); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        timer.Start();
    }

    /// <summary>Permanent deletion (no Recycle Bin, no zip backup) is shown on the button itself.</summary>
    private void UpdateCleanButtonStyle()
    {
        bool permanent = (RemovalCombo.SelectedItem as ComboBoxItem)?.Tag as string == nameof(RemovalMode.Permanent);
        CleanButton.Style = permanent ? (Style)FindResource("DangerButton") : (Style)FindResource(typeof(Button));
    }

    /// <summary>
    /// Keyboard shortcuts of the pages: F5 runs the visible page's scan, analysis or refresh, Esc cancels what it is running.
    /// Only a button that is enabled is pressed, so nothing starts twice; a key an open list or editor handles never gets here.
    /// </summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.Key is not (Key.F5 or Key.Escape)) return;
        var page = Tabs.SelectedItem;
        Button? button = e.Key == Key.F5
            ? page == JunkItem ? ScanButton : page == HistoryItem ? HistoryTab.AnalyseButton : page == DuplicatesItem ? DuplicatesTab.ScanButton
            : page == ShortcutsItem ? ShortcutsTab.ScanButton : page == RegistryItem ? RegistryTab.ScanButton : page == StartupItem ? StartupTab.RefreshButton
            : page == UninstallItem ? UninstallTab.RefreshButton : page == DiskItem ? DiskTab.AnalyzeButton : page == RestoreItem ? RestoreTab.RefreshButton
            : null
            : page == JunkItem ? CancelButton : page == HistoryItem ? HistoryTab.CancelButton : page == DuplicatesItem ? DuplicatesTab.CancelButton
            : page == RegistryItem ? RegistryTab.CancelButton : page == DiskItem ? DiskTab.CancelButton : null;
        if (button is not { IsEnabled: true, IsVisible: true }) return;
        if (button.Command is { } command)
        {
            if (command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
        }
        else button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
        e.Handled = true;
    }

    /// <summary>Shows a page by its key: home, junk, history, duplicates, shortcuts, registry, startup, uninstall, disk, restore, scheduler.</summary>
    public void Open(string key)
    {
        Tabs.SelectedItem = key.ToLowerInvariant() switch
        {
            "junk" => JunkItem, "history" => HistoryItem, "duplicates" => DuplicatesItem, "shortcuts" => ShortcutsItem,
            "registry" => RegistryItem, "startup" => StartupItem, "uninstall" => UninstallItem, "disk" => DiskItem,
            "restore" => RestoreItem, "scheduler" => SchedulerItem, _ => HomeItem,
        };
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    // ---------- language / theme ----------

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        App.Ui.Language = Enum.Parse<UiLanguage>(tag);
        App.Ui.Save();
        Loc.Instance.Language = App.Ui.Language;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        App.Ui.Theme = Enum.Parse<ThemeChoice>(tag);
        App.Ui.Save();
        ThemeManager.Choice = App.Ui.Theme;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A ComboBox shows a snapshot of the selected item's content; re-select to pick up the new text.
        foreach (var combo in new[] { RemovalCombo, ThemeCombo })
        {
            int i = combo.SelectedIndex;
            _initializing = true;
            combo.SelectedIndex = -1;
            combo.SelectedIndex = i;
            _initializing = false;
        }
        LoadDrives(_drives.Count > 0 ? CurrentDriveMask() : _driveMask);
        foreach (var r in _rows) r.RefreshTexts();
        ((CollectionViewSource)FindResource("RowsView")).View?.Refresh();
        UpdateSettingsInfo();
        SetStatus(_status);
        UpdateTitle();
    }

    // ---------- setup ----------

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
        var ignore = JunkIgnoreFolders();
        SettingsInfo.Text = Loc.F("SettingsInfo",
            Loc.T("SettingsDefaults"),
            _settings.ActiveProfile,
            Loc.N0(_settings.ToPatternSet().Patterns.Count),
            Loc.N0(_settings.Exclusions.Count),
            ignore.Count == 0 ? Loc.T("None") : string.Join(Loc.Instance.IsRtl ? "، " : ", ", ignore));
    }

    private static IReadOnlyList<string> JunkIgnoreFolders() => AceInstallation.DefaultJunkIgnoreFolders();

    private JunkOptions CurrentOptions() => new()
    {
        CleanTempFolders = OptTemp.IsChecked == true,
        FindZeroLength = OptZero.IsChecked == true,
        UseIgnoreList = OptIgnore.IsChecked == true,
        DeleteEmptyTempFolders = OptEmptyTemp.IsChecked == true,
        ExcludeModifiedWithinWeek = OptRecent.IsChecked == true,
        ApplyAllIgnoreListsInTemp = OptTempAllIgnores.IsChecked == true,
    };

    private void SetStatus(Func<string> status)
    {
        _status = status;
        StatusText.Text = status();
    }

    private void SetBusy(bool busy, Func<string>? status = null)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && _rows.Any(r => r.IsChecked);
        CancelButton.IsEnabled = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        if (status != null) SetStatus(status);
    }

    // ---------- scan ----------

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var roots = _drives.Where(d => d.IsChecked).Select(d => d.Root).Concat(_folders).ToList();
        var options = CurrentOptions();
        if (roots.Count == 0 && !options.CleanTempFolders)
        {
            Loc.Show(this, Loc.T("SelectSomething"), Loc.T("Scan"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _rows.Clear();
        UpdateTitle();
        _cts = new CancellationTokenSource();
        SetBusy(true, () => Loc.T("Scanning"));
        var ignoreFolders = JunkIgnoreFolders();
        var settings = _settings;
        var sw = Stopwatch.StartNew();
        try
        {
            var (result, temps) = await Task.Run(() =>
            {
                var ignore = IgnoreList.LoadFolders(ignoreFolders);
                var tempFolders = options.CleanTempFolders ? TempFolderLocator.Locate(new WindowsEnvironment()) : Array.Empty<string>();
                var req = new JunkScanRequest
                {
                    Roots = roots,
                    Patterns = settings.ToPatternSet(),
                    Exclusions = settings.Exclusions,
                    Ignore = ignore,
                    Options = options,
                    TempFolders = tempFolders,
                };
                return (new JunkScanner().Scan(req, _cts.Token), tempFolders);
            });
            _lastTempFolders = temps;
            foreach (var item in result.Items)
            {
                var row = new JunkRow(item);
                row.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(JunkRow.IsChecked)) UpdateSelectionStatus(); };
                _rows.Add(row);
            }
            var elapsed = sw.Elapsed.TotalSeconds;
            SetBusy(false, () => Loc.F("ScanFound", Loc.N0(result.Items.Count), Loc.FormatSize(result.TotalSize),
                Loc.N0(result.FilesSearched), Loc.N0(result.IgnoredCount), Loc.N0(result.SkippedLocked.Count), elapsed));
            // The results need the room now; the options stay one click away.
            if (result.Items.Count > 0) ScanOptionsExpander.IsExpanded = false;
            UpdateSelectionStatus();
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, () => Loc.T("ScanCancelled"));
        }
        catch (Exception ex)
        {
            UiErrors.Log(ex, "junk scan");
            SetBusy(false, () => Loc.F("ScanFailed", ex.Message));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------- clean ----------

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsChecked).ToList();
        if (selected.Count == 0) return;
        var mode = Enum.Parse<RemovalMode>((string)((ComboBoxItem)RemovalCombo.SelectedItem).Tag);
        long size = selected.Sum(r => r.Item.Size);
        string how = Loc.T(mode switch
        {
            RemovalMode.RecycleBin => "HowRecycle",
            RemovalMode.ZipBackup => "HowZip",
            _ => "HowPermanent",
        });
        var answer = Loc.Show(this, Loc.F("CleanConfirm", Loc.N0(selected.Count), Loc.FormatSize(size), how), Loc.T("CleanTitle"),
            MessageBoxButton.YesNo, mode == RemovalMode.Permanent ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetBusy(true, () => Loc.T("Cleaning"));
        _cleaning = true;
        var options = CurrentOptions();
        var temps = _lastTempFolders;
        try
        {
            var res = await Task.Run(() => new JunkRemover().Remove(new JunkRemovalRequest
            {
                Items = selected.Select(r => r.Item).ToList(),
                Mode = mode,
                TempFolders = temps,
                DeleteEmptyTempFolders = options.DeleteEmptyTempFolders && options.CleanTempFolders,
            }, _cts.Token));

            foreach (var r in selected.Where(r => !File.Exists(r.Item.FullPath)).ToList()) _rows.Remove(r);
            SetBusy(false, () => Loc.F("CleanStatus", Loc.N0(res.Deleted), Loc.FormatSize(res.BytesFreed),
                Loc.N0(res.Failed.Count), Loc.N0(res.EmptyFoldersRemoved)));

            var msg = Loc.F("CleanReport", Loc.N0(res.Deleted), Loc.FormatSize(res.BytesFreed), Loc.N0(res.Failed.Count), Loc.N0(res.EmptyFoldersRemoved));
            if (res.ZipPath != null) msg += "\n\n" + Loc.F("CleanReportBackup", Loc.Ltr(res.ZipPath));
            msg += "\n\n" + Loc.F("CleanReportJournal", Loc.Ltr(res.JournalPath));
            if (res.Failed.Count > 0)
                msg += "\n\n" + Loc.T("FirstProblems") + "\n" +
                       string.Join("\n", res.Failed.Take(8).Select(f => $"• {Loc.Ltr(Path.GetFileName(f.Path))}: {Loc.Reason(f.Reason)}"));
            Loc.Show(this, msg, Loc.T("CleanFinished"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            // Files removed before the cancel are gone: drop their rows and say how many there were.
            var gone = selected.Where(r => !File.Exists(r.Item.FullPath)).ToList();
            foreach (var r in gone) _rows.Remove(r);
            int n = gone.Count;
            SetBusy(false, () => Loc.T("CleanCancelled") + " " + Loc.F("CleanCancelledCount", Loc.N0(n)));
        }
        catch (Exception ex)
        {
            UiErrors.Log(ex, "junk clean");
            SetBusy(false, () => Loc.F("CleanFailed", ex.Message));
        }
        finally { _cleaning = false; }
        UpdateSelectionStatus();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("RestoreOpenTitle"),
            Filter = Loc.T("RestoreFilter"),
            InitialDirectory = Directory.Exists(JunkRemovalRequest.DefaultBackupFolder) ? JunkRemovalRequest.DefaultBackupFolder : null,
        };
        if (dlg.ShowDialog(this) != true) return;
        IReadOnlyList<JunkBackupArchive.BackupEntry> entries;
        try
        {
            entries = JunkBackupArchive.List(dlg.FileName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Loc.Show(this, Loc.F("RestoreFailed", Loc.Reason(ex.Message)), Loc.T("RestoreTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        new RestoreWindow(dlg.FileName, entries, JunkBackupArchive.IsLegacyBackup(dlg.FileName)) { Owner = this }.ShowDialog();
    }

    // ---------- folders ----------

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("AddFolderTitle"), Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FolderNames)
            if (!_folders.Contains(f, StringComparer.OrdinalIgnoreCase)) _folders.Add(f);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in FoldersList.SelectedItems.Cast<string>().ToList()) _folders.Remove(f);
    }

    // ---------- selection ----------

    private void CheckAll_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.IsChecked = true; }
    private void UncheckAll_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.IsChecked = false; }
    private void Invert_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.IsChecked = !r.IsChecked; }
    private void CheckHighlighted_Click(object sender, RoutedEventArgs e) { foreach (var r in Highlighted()) r.IsChecked = true; }
    private void UncheckHighlighted_Click(object sender, RoutedEventArgs e) { foreach (var r in Highlighted()) r.IsChecked = false; }
    private void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateSelectionStatus();

    private List<JunkRow> Highlighted() => ResultsGrid.SelectedItems.OfType<JunkRow>().ToList();

    private void UpdateSelectionStatus()
    {
        if (_busy) return;
        CleanButton.IsEnabled = _rows.Any(r => r.IsChecked);
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        if (_rows.Count == 0) { Title = Loc.T("AppTitle"); return; }
        var sel = _rows.Where(r => r.IsChecked).ToList();
        Title = Loc.F("AppTitleSelection", Loc.N0(sel.Count), Loc.N0(_rows.Count), Loc.FormatSize(sel.Sum(r => r.Item.Size)));
    }

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
        if (rows.Count == 0) return;
        var file = Path.Combine(_ownIgnoreFolder, "user.ini");
        var lines = rows.Select(r => r.Item.Flags.HasFlag(JunkReason.TempFolder) ? r.Name + " /T" : r.Name).Distinct(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(_ownIgnoreFolder);
            File.AppendAllLines(file, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Loc.Show(this, Loc.F("SaveFailed", ex.Message), Loc.T("TabJunk"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var r in rows) _rows.Remove(r);
        UpdateSettingsInfo();
        UpdateSelectionStatus();
        int count = rows.Count;
        SetStatus(() => Loc.F("ExcludedStatus", Loc.N0(count), file));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // A removal stopped half-way leaves its backup and journal incomplete: wait for it. Scans are simply cancelled.
        bool changing = _cleaning || RegistryTab.IsChanging || HistoryTab.IsChanging || DuplicatesTab.IsChanging || DiskTab.IsChanging
                        || ShortcutsTab.IsChanging || StartupTab.IsChanging || RestoreTab.IsChanging;
        if (changing)
        {
            Loc.Show(this, Loc.T("WaitForClean"), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }
        _cts?.Cancel();
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Lets the Folder column fill the remaining width (replacement for a "*" column, see XAML).</summary>
    private void ResultsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double others = ResultsGrid.Columns.Where(c => c != FolderColumn).Sum(c => c.ActualWidth);
        double available = ResultsGrid.ActualWidth - others - SystemParameters.VerticalScrollBarWidth - 8;
        FolderColumn.Width = new DataGridLength(Math.Max(150, available));
    }
}
