using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.Junk;

namespace CleanSuite.App;

/// <summary>Lists the files of a junk backup (CleanSuite or Ace Utilities format) and restores a selection of them.</summary>
public partial class RestoreWindow : Window
{
    private readonly string _zipPath;
    private readonly bool _legacy;
    private readonly List<RestoreRow> _rows;
    private readonly System.Windows.Threading.DispatcherTimer _folderTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private int _locateVersion;
    private bool _restoring, _located;

    /// <summary>Destinations are still being checked or a restore is running (used by the snapshot aid).</summary>
    public bool Busy => _restoring || !_located;

    public RestoreWindow(string zipPath, IReadOnlyList<JunkBackupArchive.BackupEntry> entries, bool legacy)
    {
        InitializeComponent();
        _zipPath = zipPath;
        _legacy = legacy;
        _rows = entries.Select(e => new RestoreRow(e)).ToList();
        EntriesGrid.ItemsSource = _rows;
        FileText.Text = zipPath;
        ThemeManager.ApplyTitleBar(this);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => { Loc.Instance.LanguageChanged -= OnLanguageChanged; _folderTimer.Stop(); };
        Closing += (_, e) => e.Cancel = _restoring;
        // Typing a folder waits for a pause before the destinations are checked again.
        _folderTimer.Tick += (_, _) => { _folderTimer.Stop(); Locate(); };
        Refresh();
        Locate(firstTime: true);
    }

    /// <summary>
    /// Works out every destination and whether a file is already there on a background thread (a large backup or a slow
    /// or network folder would otherwise freeze the window), then shows the result unless a newer check has started.
    /// </summary>
    private async void Locate(bool firstTime = false)
    {
        int version = ++_locateVersion;
        var folder = TargetFolder;
        _located = false;
        Refresh();
        var found = await Task.Run(() => _rows.Select(r => r.Locate(folder)).ToList());
        if (version != _locateVersion) return;
        for (int i = 0; i < _rows.Count; i++) _rows[i].Located(found[i]);
        // Start with what can actually be restored: files whose location is known and not occupied.
        if (firstTime) foreach (var r in _rows) r.IsChecked = r.CanRestore && !r.Exists;
        _located = true;
        Refresh();
    }

    private string? TargetFolder => OptOtherFolder.IsChecked == true && FolderBox.Text.Trim().Length > 0 ? FolderBox.Text.Trim() : null;

    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        foreach (var r in _rows) r.UpdateTexts();
        FormatText.Text = Loc.T(_legacy ? "FormatAce" : "FormatCleanSuite");
        SummaryText.Text = Loc.F("RestoreSummary", Loc.N0(_rows.Count), Loc.FormatSize(_rows.Sum(r => r.Entry.Size)),
            Loc.N0(_rows.Count(r => !r.CanRestore)));
        if (_restoring) SummaryText.Text = Loc.T("RestoreRunning") + " · " + SummaryText.Text;
        else if (!_located) SummaryText.Text = Loc.T("RestoreChecking") + " · " + SummaryText.Text;
        RestoreButton.IsEnabled = _located && !_restoring && _rows.Any(r => r.IsChecked && r.CanRestore);
    }

    private void Options_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Locate(); }
    private void FolderBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _folderTimer.Stop();
        _folderTimer.Start();
    }
    private void RowCheck_Click(object sender, RoutedEventArgs e) => Refresh();
    private void CheckAll_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows.Where(r => r.CanRestore)) r.IsChecked = true; Refresh(); }
    private void UncheckAll_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.IsChecked = false; Refresh(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("ChooseFolderTitle") };
        if (dlg.ShowDialog(this) != true) return;
        FolderBox.Text = dlg.FolderName;
        OptOtherFolder.IsChecked = true;
        _folderTimer.Stop();
        Locate();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_restoring || !_located) return;
        var chosen = _rows.Where(r => r.IsChecked && r.CanRestore).ToList();
        if (chosen.Count == 0)
        {
            Loc.Show(this, Loc.T("NothingSelected"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (OptOtherFolder.IsChecked == true && TargetFolder == null)
        {
            Loc.Show(this, Loc.T("NeedFolder"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        bool overwrite = OptOverwrite.IsChecked == true;
        int existing = chosen.Count(r => r.Exists);
        if (overwrite && existing > 0 &&
            Loc.Show(this, Loc.F("RestoreConfirmOverwrite", Loc.N0(existing)), Title, MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var target = TargetFolder;
        var entries = chosen.Select(r => r.Entry).ToList();
        _restoring = true;
        Refresh();
        try
        {
            var report = await Task.Run(() => JunkBackupArchive.RestoreEntries(_zipPath, entries, overwrite, target));
            var msg = Loc.F("RestoreDone", Loc.N0(report.Restored));
            if (report.Skipped.Count > 0)
                msg += "\n" + Loc.F("RestoreSkipped", Loc.N0(report.Skipped.Count)) + "\n" +
                       string.Join("\n", report.Skipped.Take(10).Select(s => $"• {Loc.Ltr(Path.GetFileName(s.Path))}: {Loc.Reason(s.Reason)}"));
            msg += "\n\n" + Loc.F("CleanReportJournal", Loc.Ltr(report.JournalPath));
            Loc.Show(this, msg, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Loc.Show(this, Loc.F("RestoreFailed", ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex) { UiErrors.Show(this, ex, "junk restore"); }
        finally { _restoring = false; }
        foreach (var r in chosen) r.IsChecked = false;
        Locate();
    }
}

public sealed class RestoreRow : INotifyPropertyChanged
{
    private bool _isChecked;

    public RestoreRow(JunkBackupArchive.BackupEntry entry)
    {
        Entry = entry;
        _isChecked = entry.OriginalPath != null;
    }

    public JunkBackupArchive.BackupEntry Entry { get; }
    public bool CanRestore => Entry.OriginalPath != null;
    public bool Exists { get; private set; }
    public string Destination { get; private set; } = "";
    public string DisplayPath => Entry.OriginalPath ?? Entry.EntryName;
    /// <summary>KB rounded up, as in the results grid (JunkItem.SizeKb).</summary>
    public string SizeText => Loc.N0((Entry.Size >> 10) + ((Entry.Size & 0x3FF) != 0 ? 1 : 0));
    public string ModifiedText => Loc.FormatDate(Entry.LastWriteUtc.ToLocalTime());
    public string StatusText { get; private set; } = "";

    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked != value) { _isChecked = value; OnChanged(); } }
    }

    /// <summary>Destination and whether a file is there now. Touches only the file system, so it runs on a background thread.</summary>
    public (string Destination, bool Exists) Locate(string? targetFolder)
    {
        if (Entry.OriginalPath == null) return ("", false);
        var dest = targetFolder == null
            ? Entry.OriginalPath
            : Path.Combine(targetFolder, JunkBackupArchive.EntryNameFor(Entry.OriginalPath).Replace('/', '\\'));
        try { return (dest, File.Exists(dest)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return (dest, false); }
    }

    /// <summary>Takes the result of <see cref="Locate"/> (on the UI thread).</summary>
    public void Located((string Destination, bool Exists) found)
    {
        (Destination, Exists) = found;
        UpdateTexts();
    }

    /// <summary>Recomputes the (language-dependent) texts.</summary>
    public void UpdateTexts()
    {
        StatusText = Entry.OriginalPath == null ? Loc.Reason(Entry.Problem ?? "original location unknown")
                                                : Loc.T(Exists ? "StatusExistsNow" : "StatusRestorable");
        OnChanged(nameof(StatusText));
        OnChanged(nameof(SizeText));
        OnChanged(nameof(ModifiedText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
