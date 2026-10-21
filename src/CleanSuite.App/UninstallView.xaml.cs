using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.Safety;
using CleanSuite.Uninstall;

namespace CleanSuite.App;

/// <summary>Installed programs tab (Uninstall Manager): uninstall, change, repair, remove a dead entry (with .reg backup), save the list.</summary>
public partial class UninstallView : UserControl
{
    private readonly UninstallManager _manager = new();
    private IReadOnlyList<UninstallEntry> _entries = Array.Empty<UninstallEntry>();
    private bool _loadedOnce;

    public UninstallView()
    {
        InitializeComponent();
        Loc.Instance.LanguageChanged += (_, _) => Render();
    }

    private Window Owner => Window.GetWindow(this)!;

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        Reload();
    }

    private bool _loading;

    /// <summary>The list is being read (used by the snapshot aid).</summary>
    public bool Busy => _loading;

    private async void Reload()
    {
        if (_loading) return;
        _loading = true;
        bool updates = ShowUpdates.IsChecked == true;
        StatusText.Text = Loc.T("UnLoading");
        try
        {
            _entries = await Task.Run(() => new UninstallScanner { ShowUpdates = updates }.Scan());
            Render();
        }
        catch (Exception ex) { StatusText.Text = ""; UiErrors.Show(Owner, ex, "installed programs"); }
        finally { _loading = false; }
    }

    private void Render()
    {
        var selected = (Grid.SelectedItem as UninstallRow)?.Entry.KeyDisplay;
        var q = SearchBox.Text.Trim();
        var rows = _entries.Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                           .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                           .Select(e => new UninstallRow(e)).ToList();
        Grid.ItemsSource = rows;
        Grid.SelectedItem = rows.FirstOrDefault(r => r.Entry.KeyDisplay == selected);
        StatusText.Text = Loc.F("UnCount", Loc.N0(_entries.Count), Loc.N0(_entries.Count(e => e.PossiblyInvalid))) + " · " + Loc.T("UnAdminHint");
        UpdateButtons();
    }

    private UninstallEntry? Selected => (Grid.SelectedItem as UninstallRow)?.Entry;

    private void UpdateButtons()
    {
        var e = Selected;
        UninstallButton.IsEnabled = e is { CanUninstall: true };
        ModifyButton.IsEnabled = e is { CanModify: true };
        RepairButton.IsEnabled = e is { CanRepair: true };
        RemoveButton.IsEnabled = e is { CanRemoveEntry: true };
        WebsiteButton.IsEnabled = e != null && Website(e) != null;
    }

    private static string? Website(UninstallEntry e) =>
        new[] { e.UrlInfoAbout, e.HelpLink, e.UrlUpdateInfo }.FirstOrDefault(u => Uri.TryCreate(u, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Search_Changed(object sender, TextChangedEventArgs e) => Render();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (Selected is { CanUninstall: true }) Uninstall_Click(sender, e); }

    private void Report(UninstallEntry e, UninstallActionResult r)
    {
        if (r.Success) StatusText.Text = Loc.F("UnStarted", e.Name);
        else Loc.Show(Owner, Loc.F("UnFailed", e.Name, r.Error ?? ""), Loc.T("TabUninstall"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Uninstall_Click(object sender, RoutedEventArgs args)
    {
        var e = Selected;
        if (e == null) return;
        if (UninstallManager.IsSelf(e)) { Loc.Show(Owner, Loc.T("UnSelf"), Loc.T("UnUninstallTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (Loc.Show(Owner, Loc.F("UnUninstallConfirm", e.Name) + "\n\n" + Loc.F("UnCommand", Loc.Ltr(e.UninstallCommand)), Loc.T("UnUninstallTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Report(e, _manager.Uninstall(e));
    }

    private void Modify_Click(object sender, RoutedEventArgs args) { if (Selected is { } e) Report(e, _manager.Modify(e)); }
    private void Repair_Click(object sender, RoutedEventArgs args) { if (Selected is { } e) Report(e, _manager.Repair(e)); }

    private void Remove_Click(object sender, RoutedEventArgs args)
    {
        var e = Selected;
        if (e == null) return;
        if (!e.CanRemoveEntry) { Loc.Show(Owner, Loc.T("UnMsiNoRemove"), Loc.T("UnRemoveTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (Loc.Show(Owner, Loc.F("UnRemoveConfirm", e.Name), Loc.T("UnRemoveTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var r = _manager.RemoveEntry(e);
        if (!r.Success)
        {
            Loc.Show(Owner, Loc.F("UnFailed", e.Name, Loc.Reason(r.Error ?? "")), Loc.T("UnRemoveTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _entries = _entries.Where(x => x != e).ToList();
        Render();
        StatusText.Text = Loc.F("UnRemoved", r.BackupPath) + " · " + StatusText.Text;
    }

    private void Website_Click(object sender, RoutedEventArgs args)
    {
        if (Selected is { } e && Website(e) is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void Report_Click(object sender, RoutedEventArgs args)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "report.txt", Filter = Loc.T("UnReportFilter"), AddExtension = true, DefaultExt = ".txt" };
        if (dlg.ShowDialog(Owner) != true) return;
        var rows = (Grid.ItemsSource as IEnumerable<UninstallRow>)?.Select(r => r.Entry).ToList() ?? _entries.ToList();
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
        StatusText.Text = Loc.F("UnReportSaved", dlg.FileName);
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
