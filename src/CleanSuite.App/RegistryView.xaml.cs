using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.App.ViewModels;
using CleanSuite.Compat;
using CleanSuite.RegistryClean;

namespace CleanSuite.App;

/// <summary>
/// Registry tab (Clean System Registry): scan (read-only), review, remove the checked entries after a .reg backup, and
/// restore a backup.
/// </summary>
public partial class RegistryView : UserControl
{
    private readonly RegistryViewModel _vm;

    public RegistryView()
    {
        InitializeComponent();
        _vm = new RegistryViewModel(Options);
        _vm.Confirm = chosen => Loc.Show(Owner, Loc.F("RgConfirm", Loc.N0(chosen.Count)), Loc.T("RgConfirmTitle"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _vm.Error += ex => { UiErrors.Show(Owner, ex, "registry"); Render(); };
        _vm.Scanned += Render;
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
    public bool Busy => _vm.IsBusy;

    /// <summary>True while entries are being removed or a backup restored; the window waits for it before closing.</summary>
    public bool IsChanging => _vm.IsChanging || _restoring;

    private bool _restoring;

    private Window Owner => Window.GetWindow(this)!;

    private RegScanOptions Options()
    {
        // CleanSuite's built-in protection list always applies; the ignore lists shipped with the original program
        // (Data\Ignore_Reg) are added when that program is installed, and the user's own lists (Ignore_Reg in
        // %LocalAppData%\CleanSuite). The original's settings are not used.
        return new RegScanOptions
        {
            Thorough = ThoroughBox.IsChecked == true,
            IgnoreFolders = IgnoreWords.DefaultFolders(AceRegistrySettings.IgnoreFolder(AceInstallation.Locate())),
        };
    }

    private void Render()
    {
        Grid.ItemsSource = null;
        Grid.ItemsSource = _vm.Rows;
        Grid.IsReadOnly = _vm.IsBusy;                                       // no ticking while a scan or clean runs
        ThoroughBox.IsEnabled = !_vm.IsBusy;
        Progress.Visibility = _vm.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        var result = _vm.Result;
        if (_vm.IsBusy) StatusText.Text = Loc.T(_vm.IsChanging ? "RgCleaning" : "RgScanning");
        else if (_vm.WasCancelled) StatusText.Text = Loc.T("ScanCancelled");
        else if (result == null)
        {
            StatusText.Text = Loc.T("RgIntro");
            WarningText.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusText.Text = Loc.F("RgCount", Loc.N0(result.Findings.Count), Loc.N0(result.Ignored.Count))
                              + (result.Unreadable.Count > 0 ? " · " + Loc.F("RgUnreadable", Loc.N0(result.Unreadable.Count)) : "")
                              + (_vm.LastBackup != null ? " · " + Loc.F("RgBackupAt", _vm.LastBackup) : "");
            WarningText.Text = Loc.T("RgWarning");
            WarningText.Visibility = _vm.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowCleaned(RegCleanResult res)
    {
        var msg = Loc.F("RgCleaned", Loc.N0(res.Removed));
        if (res.Skipped.Count > 0) msg += "\n" + Loc.F("RgSkipped", Loc.N0(res.Skipped.Count));
        if (res.Failed.Count > 0) msg += "\n" + Loc.F("RgFailed", Loc.N0(res.Failed.Count), Loc.Reason(res.Failed[0].Reason));
        if (res.BackupPath != null) msg += "\n\n" + Loc.F("RgBackupAt", Loc.Ltr(res.BackupPath));
        Loc.Show(Owner, msg, Loc.T("TabRegistry"), MessageBoxButton.OK, res.Failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void Compactor_Click(object sender, RoutedEventArgs e) => new RegistryCompactWindow { Owner = Owner }.ShowDialog();

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy || _restoring) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T("RgRegFilter") + " (*.reg)|*.reg",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSuite", "Backups"),
        };
        if (dlg.ShowDialog(Owner) != true) return;
        if (Loc.Show(Owner, Loc.F("RgRestoreConfirm", Loc.Ltr(dlg.FileName)), Loc.T("RgRestore"), MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var file = dlg.FileName;
        _restoring = true;
        try
        {
            int n = await Task.Run(() => new RegistryCleaner().Restore(file));
            Loc.Show(Owner, Loc.F("RgRestored", Loc.N0(n)), Loc.T("RgRestore"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException or FormatException)
        {
            var reason = ex is UnauthorizedAccessException or System.Security.SecurityException ? Loc.T("R_Admin") : ex.Message;
            Loc.Show(Owner, Loc.F("RgRestoreFailed", reason), Loc.T("RgRestore"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { UiErrors.Show(Owner, ex, "registry restore"); }
        finally { _restoring = false; }
    }
}

/// <summary>One finding in the grid (texts in the current UI language).</summary>
public sealed class RegistryRow : ISelectableRow
{
    private bool _selected;

    public RegistryRow(RegFinding finding)
    {
        Finding = finding;
        _selected = finding.IgnoredBy == null;
    }

    public RegFinding Finding { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    }

    /// <summary>Contains an ignore word (shown, not selected).</summary>
    public bool Marked => Finding.IgnoredBy != null;

    public string CategoryText => Loc.T("Rc_" + Finding.Category);

    public string ProblemText
    {
        get
        {
            var key = "Rr_" + Finding.Reason;
            var t = Loc.T(key);
            return t == key ? Finding.ReasonText : t;
        }
    }

    public string ValueText => Finding.Target == RegTargetKind.Key ? Loc.T("RgWholeKey")
        : Finding.ValueName.Length == 0 ? Loc.T("RgDefaultValue") : Finding.ValueName;

    public string Tip => Finding.ReasonText + (Finding.IgnoredBy is { } w ? "\n" + Loc.F("RgIgnoredBy", w) : "");
}
