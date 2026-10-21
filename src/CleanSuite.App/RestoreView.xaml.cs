using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.Junk;
using CleanSuite.Restore;

namespace CleanSuite.App;

/// <summary>
/// Restore tab (the original's Restore tool): every backup of junk files, duplicate files and registry changes made by
/// CleanSuite or by the original, with restore (files never overwritten, registry data only added back), open folder and
/// delete (to the Recycle Bin).
/// </summary>
public partial class RestoreView : UserControl
{
    private readonly List<string> _folders = BackupCatalog.DefaultFolders().ToList();
    private bool _loaded, _working;
    private int _reloadVersion, _shownVersion;

    /// <summary>The list is still being read or a change is running (used by the snapshot aid).</summary>
    public bool Busy => _working || _shownVersion != _reloadVersion;

    /// <summary>A restore or delete is running (the window waits before closing).</summary>
    public bool IsChanging => _working;

    public RestoreView()
    {
        InitializeComponent();
        Loc.Instance.LanguageChanged += (_, _) => _ = Reload();
    }

    private Window Owner => Window.GetWindow(this)!;

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _ = Reload();
    }

    /// <summary>Lists the backup folders on a background thread; the newest call wins.</summary>
    private async Task Reload()
    {
        int version = ++_reloadVersion;
        var folders = _folders.ToList();
        List<BackupItem> items;
        string existing;
        try
        {
            (items, existing) = await Task.Run(() => (BackupCatalog.List(folders).ToList(), string.Join(" · ", folders.Where(Directory.Exists))));
        }
        catch (Exception ex) { _shownVersion = version; UiErrors.Show(Owner, ex, "backup list"); return; }
        if (version != _reloadVersion) return;
        _shownVersion = version;
        Grid.ItemsSource = items.Select(b => new BackupRow(b)).ToList();
        StatusText.Text = Loc.F("RsCount", Loc.N0(items.Count)) + " · " + existing;
        UpdateButtons();
    }

    private List<BackupItem> Selected() => Grid.SelectedItems.OfType<BackupRow>().Select(r => r.Item).ToList();

    private void UpdateButtons()
    {
        var s = Selected();
        RestoreButton.IsEnabled = !_working && s.Count == 1;
        OpenFolderButton.IsEnabled = s.Count == 1;
        DeleteButton.IsEnabled = !_working && s.Count > 0;
    }

    private void SetWorking(bool working, string? status = null)
    {
        _working = working;
        if (status != null) StatusText.Text = status;
        UpdateButtons();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Restore_Click(sender, e);
    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = Reload();

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog(Owner) != true) return;
        if (!_folders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) _folders.Add(dlg.FolderName);
        _ = Reload();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is [var b] && File.Exists(b.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{b.Path}\"") { UseShellExecute = true });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_working || Selected() is not [var b]) return;
        SetWorking(true, Loc.T("RsReading"));
        try
        {
            if (!b.IsRegistry)
            {
                var (entries, legacy) = await Task.Run(() => (JunkBackupArchive.List(b.Path), JunkBackupArchive.IsLegacyBackup(b.Path)));
                SetWorking(false);
                new RestoreWindow(b.Path, entries, legacy) { Owner = Owner }.ShowDialog();
                return;
            }
            // Reading the backup (and hashing it for the ledger) can take a while for a large zip.
            var (count, trust, keys) = await Task.Run(() => (b.Kind == BackupKind.RegistryZip ? BackupCatalog.RegistryZipItems(b.Path).Count : 1,
                                                              BackupCatalog.Trust(b), BackupCatalog.RegistryKeys(b)));
            var text = b.Kind == BackupKind.RegistryZip ? Loc.F("RsRegZipConfirm", Loc.N0(count), Loc.Ltr(b.Name)) : Loc.F("RsRegConfirm", Loc.Ltr(b.Name));
            // Show what will be written, and whether CleanSuite made this backup: anything in the folder could be imported.
            text += "\n\n" + Loc.F("RsKeys", Loc.N0(keys.Count)) + "\n" + string.Join("\n", keys.Take(8).Select(k => "• " + Loc.Ltr(k)))
                    + (keys.Count > 8 ? "\n…" : "") + "\n\n" + Loc.T("RsTrust_" + trust);
            if (trust != CleanSuite.Safety.BackupTrust.Verified && Environment.IsPrivilegedProcess) text += "\n" + Loc.T("RsAdminUnverified");
            var icon = trust == CleanSuite.Safety.BackupTrust.Verified ? MessageBoxImage.Question : MessageBoxImage.Warning;
            if (Loc.Show(Owner, text, Loc.T("RsRestore"), MessageBoxButton.YesNo, icon, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            StatusText.Text = Loc.T("RestoreRunning");
            var r = await Task.Run(() => BackupCatalog.RestoreRegistry(b));
            var msg = Loc.F("RsRegDone", Loc.N0(r.Values));
            if (r.Errors.Count > 0) msg += Environment.NewLine + Loc.F("RsRegErrors", Loc.N0(r.Errors.Count), Loc.Reason(r.Errors[0]));
            Loc.Show(Owner, msg, Loc.T("RsRestore"), MessageBoxButton.OK, r.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Loc.Show(Owner, Loc.F("RsInvalid", Loc.Ltr(b.Name), ex.Message), Loc.T("RsRestore"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { UiErrors.Show(Owner, ex, "backup restore"); }
        finally { if (_working) SetWorking(false); }
        await Reload();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        if (_working || s.Count == 0) return;
        if (Loc.Show(Owner, Loc.F("RsDeleteConfirm", Loc.N0(s.Count)), Loc.T("RsDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetWorking(true, Loc.T("RsDeleting"));
        int removed = 0;
        try
        {
            var (r, failed) = await Task.Run(() => BackupCatalog.Delete(s));
            removed = r;
            if (failed.Count > 0)
                Loc.Show(Owner, Loc.F("RsDeleteFailed", Loc.N0(failed.Count)), Loc.T("RsDelete"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { UiErrors.Show(Owner, ex, "backup delete"); }
        finally { SetWorking(false); }
        await Reload();
        StatusText.Text = Loc.F("RsDeleted", Loc.N0(removed)) + " · " + StatusText.Text;
    }
}

public sealed class BackupRow(BackupItem item)
{
    public BackupItem Item { get; } = item;
    public string KindText => Loc.T("RsKind_" + Item.Kind);
    public string CreatedText => Loc.FormatDate(Item.Created);
    public string SizeText => Loc.FormatSize(Item.Size);
    public string SourceText => Loc.T(Item.FromOriginal ? "RsFromOriginal" : "RsFromCleanSuite");
}
