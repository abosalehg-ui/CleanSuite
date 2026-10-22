using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.App.ViewModels;
using CleanSuite.Restore;
using CleanSuite.Safety;

namespace CleanSuite.App;

/// <summary>
/// Restore tab (the original's Restore tool): every backup of junk files, duplicate files and registry changes made by
/// CleanSuite or by the original, with restore (files never overwritten, registry data only added back), open folder and
/// delete (to the Recycle Bin). State in <see cref="RestoreViewModel"/>.
/// </summary>
public partial class RestoreView : UserControl, IToolPage
{
    private readonly RestoreViewModel _vm = new();
    private bool _loaded;
    private string? _note;

    public RestoreView()
    {
        InitializeComponent();
        _vm.Error += ex => UiErrors.Show(Owner, ex, "backups");
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Items)) Render();
            else if (e.PropertyName == nameof(_vm.IsBusy)) { UpdateButtons(); if (!_vm.IsBusy) Render(); }
        };
        Loc.Instance.LanguageChanged += (_, _) => Render();
    }

    // ---------------------------------------------------------------- IToolPage

    /// <summary>The list is still being read or a change is running (used by the snapshot aid).</summary>
    public bool IsBusy => _vm.IsBusy || !_vm.Loaded;
    /// <summary>A restore or delete is running (the window waits before closing).</summary>
    public bool IsChanging => _vm.IsChanging;
    Button? IToolPage.ScanButton => RefreshButton;
    Button? IToolPage.CancelButton => null;
    public void CancelWork() => _vm.Cancel();

    private Window Owner => Window.GetWindow(this)!;

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _ = _vm.RefreshAsync();
    }

    private void Render()
    {
        Grid.ItemsSource = _vm.Items.Select(b => new BackupRow(b)).ToList();
        var status = Loc.F("RsCount", Loc.N0(_vm.Items.Count)) + " · " + string.Join(" · ", _vm.ExistingFolders.Select(Loc.Ltr));
        Bidi.SetText(StatusText, _note != null ? _note + " · " + status : status);
        UpdateButtons();
    }

    private List<BackupItem> Selected() => Grid.SelectedItems.OfType<BackupRow>().Select(r => r.Item).ToList();

    private void UpdateButtons()
    {
        var s = Selected();
        RestoreButton.IsEnabled = !_vm.IsBusy && s.Count == 1;
        OpenFolderButton.IsEnabled = s.Count == 1;
        DeleteButton.IsEnabled = !_vm.IsBusy && s.Count > 0;
        RefreshButton.IsEnabled = !_vm.IsBusy;
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Restore_Click(sender, e);
    private void Refresh_Click(object sender, RoutedEventArgs e) { _note = null; _ = _vm.RefreshAsync(); }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog(Owner) != true) return;
        _vm.AddFolder(dlg.FolderName);
        _ = _vm.RefreshAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is [var b] && File.Exists(b.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{b.Path}\"") { UseShellExecute = true });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy || Selected() is not [var b]) return;
        StatusText.Text = Loc.T("RsReading");
        try
        {
            if (!b.IsRegistry)
            {
                var files = await _vm.ReadAsync(() => (JunkBackupArchive.List(b.Path), JunkBackupArchive.IsLegacyBackup(b.Path)));
                Render();
                if (files.Item1 != null) new RestoreWindow(b.Path, files.Item1, files.Item2) { Owner = Owner }.ShowDialog();
                return;
            }
            // Reading the backup (and hashing it for the ledger) can take a while for a large zip.
            var info = await _vm.ReadAsync(() => (b.Kind == BackupKind.RegistryZip ? BackupCatalog.RegistryZipItems(b.Path).Count : 1,
                                                  BackupCatalog.Trust(b), BackupCatalog.RegistryKeys(b)));
            Render();
            if (info.Item3 == null) return;
            var (count, trust, keys) = info;
            bool verified = trust == BackupTrust.Verified;
            var refused = verified ? new List<string>() : keys.Where(BackupCatalog.IsAutoStartKey).ToList();
            var text = b.Kind == BackupKind.RegistryZip ? Loc.F("RsRegZipConfirm", Loc.N0(count), Loc.Ltr(b.Name)) : Loc.F("RsRegConfirm", Loc.Ltr(b.Name));
            // Show what will be written, and whether CleanSuite made this backup: anything in the folder could be imported.
            text += "\n\n" + Loc.F("RsKeys", Loc.N0(keys.Count)) + "\n"
                    + string.Join("\n", keys.Take(8).Select(k => "• " + Loc.Ltr(k) + (refused.Contains(k) ? "  " + Loc.T("RsKeyRefused") : "")))
                    + (keys.Count > 8 ? "\n…" : "") + "\n\n" + Loc.T("RsTrust_" + trust);
            if (refused.Count > 0) text += "\n" + Loc.F("RsKeysRefused", Loc.N0(refused.Count));
            if (!verified && Environment.IsPrivilegedProcess) text += "\n" + Loc.T("RsAdminUnverified");
            var icon = verified ? MessageBoxImage.Question : MessageBoxImage.Warning;
            if (Loc.Show(Owner, text, Loc.T("RsRestore"), MessageBoxButton.YesNo, icon, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            StatusText.Text = Loc.T("RestoreRunning");
            var r = await _vm.RestoreRegistryAsync(b);
            if (r == null) return;
            var msg = Loc.F("RsRegDone", Loc.N0(r.Values));
            if (r.Errors.Count > 0) msg += Environment.NewLine + Loc.F("RsRegErrors", Loc.N0(r.Errors.Count), Loc.Reason(r.Errors[0]));
            Loc.Show(Owner, msg, Loc.T("RsRestore"), MessageBoxButton.OK, r.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Loc.Show(Owner, Loc.F("RsInvalid", Loc.Ltr(b.Name), ex.Message), Loc.T("RsRestore"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await _vm.RefreshAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        if (_vm.IsBusy || s.Count == 0) return;
        if (Loc.Show(Owner, Loc.F("RsDeleteConfirm", Loc.N0(s.Count)), Loc.T("RsDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        StatusText.Text = Loc.T("RsDeleting");
        var (removed, failed) = await _vm.DeleteAsync(s);
        if (failed is { Count: > 0 })
            Loc.Show(Owner, Loc.F("RsDeleteFailed", Loc.N0(failed.Count)), Loc.T("RsDelete"), MessageBoxButton.OK, MessageBoxImage.Warning);
        _note = Loc.F("RsDeleted", Loc.N0(removed));
        Render();
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
