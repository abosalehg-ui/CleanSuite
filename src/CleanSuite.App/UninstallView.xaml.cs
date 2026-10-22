using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.App.ViewModels;
using CleanSuite.Safety;
using CleanSuite.Uninstall;

namespace CleanSuite.App;

/// <summary>
/// Installed programs tab (Uninstall Manager): uninstall, change, repair, remove a dead entry (with .reg backup), save the list.
/// State in <see cref="UninstallViewModel"/>.
/// </summary>
public partial class UninstallView : UserControl, IToolPage
{
    private readonly UninstallViewModel _vm;
    private bool _loadedOnce;
    /// <summary>The last action's message, shown before the counts until the next refresh.</summary>
    private string? _note;

    public UninstallView()
    {
        InitializeComponent();
        _vm = new UninstallViewModel(showUpdates: () => ShowUpdates.IsChecked == true);
        _vm.Error += ex => UiErrors.Show(Owner, ex, "installed programs");
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Items)) Render();
            else if (e.PropertyName == nameof(_vm.IsBusy)) { RefreshButton.IsEnabled = !_vm.IsBusy; if (_vm.IsBusy) StatusText.Text = Loc.T("UnLoading"); else Render(); }
        };
        Loc.Instance.LanguageChanged += (_, _) => Render();
    }

    // ---------------------------------------------------------------- IToolPage

    public bool IsBusy => _vm.IsBusy;
    public bool IsChanging => _vm.IsChanging;
    Button? IToolPage.ScanButton => RefreshButton;
    Button? IToolPage.CancelButton => null;
    public void CancelWork() => _vm.Cancel();

    private Window Owner => Window.GetWindow(this)!;

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        _ = _vm.RefreshWithOptionsAsync();
    }

    private void Render()
    {
        var entries = _vm.Items;
        var selected = (Grid.SelectedItem as UninstallRow)?.Entry.KeyDisplay;
        var q = SearchBox.Text.Trim();
        var rows = entries.Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                          .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                          .Select(e => new UninstallRow(e)).ToList();
        Grid.ItemsSource = rows;
        Grid.SelectedItem = rows.FirstOrDefault(r => r.Entry.KeyDisplay == selected);
        if (!_vm.IsBusy)
        {
            var counts = Loc.F("UnCount", Loc.N0(entries.Count), Loc.N0(entries.Count(e => e.PossiblyInvalid))) + " · " + Loc.T("UnAdminHint");
            Bidi.SetText(StatusText, _note != null ? _note + " · " + counts : counts);
        }
        UpdateButtons();
    }

    private void Note(string text)
    {
        _note = text;
        Render();
    }

    private UninstallEntry? Selected => (Grid.SelectedItem as UninstallRow)?.Entry;

    private void UpdateButtons()
    {
        var e = Selected;
        UninstallButton.IsEnabled = e is { CanUninstall: true };
        ModifyButton.IsEnabled = e is { CanModify: true };
        RepairButton.IsEnabled = e is { CanRepair: true };
        RemoveButton.IsEnabled = !_vm.IsBusy && e is { CanRemoveEntry: true };
        WebsiteButton.IsEnabled = e != null && Website(e) != null;
    }

    private static string? Website(UninstallEntry e) =>
        new[] { e.UrlInfoAbout, e.HelpLink, e.UrlUpdateInfo }.FirstOrDefault(u => Uri.TryCreate(u, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Search_Changed(object sender, TextChangedEventArgs e) => Render();
    private void Refresh_Click(object sender, RoutedEventArgs e) { _note = null; _ = _vm.RefreshWithOptionsAsync(); }
    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (Selected is { CanUninstall: true }) Uninstall_Click(sender, e); }

    private void Report(UninstallEntry e, UninstallActionResult r)
    {
        if (r.Success) Note(Loc.F("UnStarted", Loc.Ltr(e.Name)));
        else Loc.Show(Owner, Loc.F("UnFailed", Loc.Ltr(e.Name), Loc.Reason(r.Error ?? "")), Loc.T("TabUninstall"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Uninstall_Click(object sender, RoutedEventArgs args)
    {
        var e = Selected;
        if (e == null) return;
        if (UninstallManager.IsSelf(e)) { Loc.Show(Owner, Loc.T("UnSelf"), Loc.T("UnUninstallTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (Loc.Show(Owner, Loc.F("UnUninstallConfirm", Loc.Ltr(e.Name)) + "\n\n" + Loc.F("UnCommand", Loc.Ltr(e.UninstallCommand)), Loc.T("UnUninstallTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Report(e, _vm.Manager.Uninstall(e));
    }

    private void Modify_Click(object sender, RoutedEventArgs args) { if (Selected is { } e) Report(e, _vm.Manager.Modify(e)); }
    private void Repair_Click(object sender, RoutedEventArgs args) { if (Selected is { } e) Report(e, _vm.Manager.Repair(e)); }

    private async void Remove_Click(object sender, RoutedEventArgs args)
    {
        var e = Selected;
        if (e == null || _vm.IsBusy) return;
        if (!e.CanRemoveEntry) { Loc.Show(Owner, Loc.T("UnMsiNoRemove"), Loc.T("UnRemoveTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (Loc.Show(Owner, Loc.F("UnRemoveConfirm", Loc.Ltr(e.Name)), Loc.T("UnRemoveTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var r = await _vm.RemoveEntryAsync(e);
        if (r == null) return;
        if (!r.Success)
        {
            Loc.Show(Owner, Loc.F("UnFailed", Loc.Ltr(e.Name), Loc.Reason(r.Error ?? "")), Loc.T("UnRemoveTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Note(Loc.F("UnRemoved", Loc.Ltr(r.BackupPath ?? "")));
    }

    private void Website_Click(object sender, RoutedEventArgs args)
    {
        if (Selected is { } e && Website(e) is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void Report_Click(object sender, RoutedEventArgs args)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "report.txt", Filter = Loc.T("UnReportFilter"), AddExtension = true, DefaultExt = ".txt" };
        if (dlg.ShowDialog(Owner) != true) return;
        var rows = (Grid.ItemsSource as IEnumerable<UninstallRow>)?.Select(r => r.Entry).ToList() ?? _vm.Items.ToList();
        try
        {
            UninstallManager.WriteReport(dlg.FileName, rows, tabDelimited: dlg.FilterIndex == 2,
                [Loc.T("UnColName"), Loc.T("UnColPublisher"), Loc.T("UnColDate"), Loc.T("UnColSize"), Loc.T("UnColVersion")]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Loc.Show(Owner, Loc.F("SaveFailed", ex.Message), Loc.T("TabUninstall"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Note(Loc.F("UnReportSaved", Loc.Ltr(dlg.FileName)));
    }
}

/// <summary>One row of the installed-programs grid (texts in the current UI language).</summary>
public sealed class UninstallRow(UninstallEntry entry)
{
    public UninstallEntry Entry { get; } = entry;
    public string DateText => Entry.InstalledOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    public string SizeText => Entry.SizeKb is { } kb ? Loc.FormatSize(kb * 1024) : "";

    public string Notes
    {
        get
        {
            var n = new List<string>();
            if (Entry.PossiblyInvalid) n.Add(Loc.T("UnNoteInvalid"));
            if (Entry.IsWindowsInstaller) n.Add(Loc.T("UnNoteMsi"));
            if (Entry.Is32Bit) n.Add(Loc.T("UnNote32"));
            if (Entry.Hive == RegHive.CurrentUser) n.Add(Loc.T("UnNoteUser"));
            return string.Join(" · ", n);
        }
    }
}
