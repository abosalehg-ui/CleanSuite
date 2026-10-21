using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.App.ViewModels;
using CleanSuite.Shortcuts;

namespace CleanSuite.App;

/// <summary>Invalid shortcuts tab (Fix Invalid Shortcuts): scan, send checked items to the Recycle Bin, retarget one shortcut.</summary>
public partial class ShortcutsView : UserControl
{
    private readonly ShortcutsViewModel _vm;

    public ShortcutsView()
    {
        InitializeComponent();
        _vm = new ShortcutsViewModel(Options);
        _vm.Confirm = chosen => Loc.Show(Owner, Loc.F("ScDeleteConfirm", Loc.N0(chosen.Count)), Loc.T("ScDeleteTitle"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.Error += ex => { UiErrors.Show(Owner, ex, "shortcuts"); Render(); };
        _vm.Scanned += Render;
        _vm.Cleaned += _ => Render();
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(_vm.IsBusy) or nameof(_vm.Rows)) Render(); };
        DataContext = _vm;
        Loc.Instance.LanguageChanged += (_, _) => { foreach (var r in _vm.Rows) r.Refresh(); Render(); };
        Render();
    }

    /// <summary>True while a scan or removal is running (the snapshot aid waits for it).</summary>
    public bool Busy => _vm.IsBusy;

    /// <summary>True while shortcuts are being removed; the window waits for it before closing.</summary>
    public bool IsChanging => _vm.IsChanging;

    private Window Owner => Window.GetWindow(this)!;

    private ShortcutScanOptions Options()
    {
        var ace = AceShortcutSettings.Read();            // read-only: only the original's ignore folder is taken from it
        return new ShortcutScanOptions
        {
            StartMenuMode = ModeStartMenu.IsChecked == true,
            EmptyStartMenuFolders = EmptyFolders.IsChecked == true,
            IncludeRemovable = IncludeRemovable.IsChecked == true,
            IgnoreFolders = ace?.IgnoreFolders ?? Array.Empty<string>(),
        };
    }

    private void Render()
    {
        var selected = Grid.SelectedItem;
        Grid.ItemsSource = null;
        Grid.ItemsSource = _vm.Rows;
        if (selected != null && _vm.Rows.Contains(selected)) Grid.SelectedItem = selected;
        Grid.IsReadOnly = _vm.IsBusy;                                       // no ticking while a scan or removal runs
        ModeStartMenu.IsEnabled = ModeDrives.IsEnabled = EmptyFolders.IsEnabled = IncludeRemovable.IsEnabled = !_vm.IsBusy;
        if (_vm.IsBusy) StatusText.Text = Loc.T(_vm.IsChanging ? "ScRemoving" : "ScScanning");
        else if (_vm.LastClean is { } res) StatusText.Text = Loc.F("ScDeleted", Loc.N0(res.Succeeded), Loc.N0(res.Failed.Count));
        else if (_vm.Last is { } l) StatusText.Text = Loc.F("ScResult", Loc.N0(l.Items.Count), Loc.N0(l.ShortcutsScanned), Loc.N0(l.FilesScanned));
        else StatusText.Text = Loc.T("ScReady");
        UpdateButtons();
    }

    private ShortcutRow? Selected => Grid.SelectedItem as ShortcutRow;

    private void UpdateButtons()
    {
        RetargetButton.IsEnabled = Selected is { Item.Kind: ShortcutItemKind.InvalidShortcut } && !_vm.IsBusy;
        OpenButton.IsEnabled = Selected != null;
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void Retarget_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (_vm.IsBusy) return;
        if (row is not { Item.Kind: ShortcutItemKind.InvalidShortcut })
        {
            Loc.Show(Owner, Loc.T("ScRetargetOnlyShortcut"), Loc.T("TabShortcuts"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("ScRetargetPick"), CheckFileExists = true };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            new ShortcutCleaner().Retarget(row.Item.FullPath, dlg.FileName);
            _vm.Retargeted(row);
            StatusText.Text = Loc.F("ScRetargetDone", row.Item.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Loc.Show(Owner, Loc.F("ScRetargetFailed", row.Item.Name, ex.Message), Loc.T("TabShortcuts"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateButtons();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected?.Item;
        if (item == null) return;
        var path = item.FullPath;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : Directory.Exists(item.Folder) ? $"\"{item.Folder}\"" : null;
        if (args != null) Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Open_Click(sender, e);
}

/// <summary>One row of the shortcut grid; checked by default, as in the original.</summary>
public sealed class ShortcutRow(ShortcutItem item) : ISelectableRow
{
    private bool _selected = true;
    public ShortcutItem Item { get; } = item;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public string ReasonText => Item.Reason switch
    {
        ShortcutReason.ArgumentPathMissing => Loc.T("ScReasonArgs"),
        ShortcutReason.EmptyFolder => Loc.T("ScReasonEmpty"),
        _ => Loc.T("ScReasonTarget"),
    };

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReasonText)));

    public event PropertyChangedEventHandler? PropertyChanged;
}
