using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.Startup;

namespace CleanSuite.App;

/// <summary>Startup programs tab (Auto-Start Manager): list, disable/enable (reversible) and delete (with backup).</summary>
public partial class StartupView : UserControl
{
    private readonly StartupManager _manager = new(monitor: new StartupMonitor());
    private IReadOnlyList<StartupEntry> _entries = Array.Empty<StartupEntry>();
    private bool _loadedOnce, _working;

    public StartupView()
    {
        InitializeComponent();
        Loc.Instance.LanguageChanged += (_, _) => Render();
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        Reload();
    }

    /// <summary>Reads the entries on a background thread (registry, startup folders, shortcut targets, file versions).</summary>
    private async void Reload()
    {
        if (_working) return;
        SetWorking(true, Loc.T("StLoading"));
        try { _entries = await Task.Run(_manager.Scan); }
        catch (Exception ex) { UiErrors.Show(Owner, ex, "startup scan"); }
        finally { SetWorking(false); }
        Render();
    }

    private void SetWorking(bool working, string? status = null)
    {
        _working = working;
        RefreshButton.IsEnabled = !working;
        Grid.IsEnabled = !working;
        if (status != null) StatusText.Text = status;
        if (working) DisableButton.IsEnabled = EnableButton.IsEnabled = DeleteButton.IsEnabled = OpenButton.IsEnabled = false;
        else UpdateButtons();
    }

    private void Render()
    {
        var selected = (Grid.SelectedItem as StartupRow)?.Entry.Identity;
        var rows = _entries.Where(e => HideMicrosoft.IsChecked != true || !e.IsMicrosoft).Select(e => new StartupRow(e)).ToList();
        Grid.ItemsSource = rows;
        Grid.SelectedItem = rows.FirstOrDefault(r => r.Entry.Identity == selected);
        StatusText.Text = Loc.F("StCount", Loc.N0(_entries.Count), Loc.N0(_entries.Count(e => e.Enabled)), Loc.N0(_entries.Count(e => !e.Enabled)))
                          + " · " + Loc.T("StAdminHint");
        int reEnabled = _entries.Count(e => e.ReEnabledItself);
        WarningText.Text = Loc.F("StReEnabledWarning", Loc.N0(reEnabled));
        WarningText.Visibility = reEnabled > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private StartupEntry? Selected => (Grid.SelectedItem as StartupRow)?.Entry;

    private void UpdateButtons()
    {
        var e = Selected;
        DisableButton.IsEnabled = e is { Enabled: true };
        EnableButton.IsEnabled = e is { Enabled: false };
        DeleteButton.IsEnabled = e != null;
        OpenButton.IsEnabled = e?.TargetPath != null;
    }

    private Window Owner => Window.GetWindow(this)!;

    private async void Apply(Func<StartupEntry, StartupManager.ChangeResult> change)
    {
        var e = Selected;
        if (e == null) { Loc.Show(Owner, Loc.T("StNoSelection"), Loc.T("TabStartup"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_working) return;
        SetWorking(true, Loc.T("StLoading"));
        StartupManager.ChangeResult? r = null;
        try
        {
            r = await Task.Run(() => change(e));
            if (!r.Success)
                Loc.Show(Owner, Loc.F("StFailed", e.Name, Loc.Reason(r.Error ?? "")), Loc.T("TabStartup"), MessageBoxButton.OK, MessageBoxImage.Warning);
            _entries = await Task.Run(_manager.Scan);
        }
        catch (Exception ex) { UiErrors.Show(Owner, ex, "startup change"); }
        finally { SetWorking(false); }
        Render();
        if (r is { Success: true }) StatusText.Text = Loc.F("StChanged", e.Name) + " · " + StatusText.Text;
    }

    /// <summary>A change is being written (the window waits before closing).</summary>
    public bool IsChanging => _working;

    /// <summary>Entries are being read or changed (used by the snapshot aid).</summary>
    public bool Busy => _working;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Disable_Click(object sender, RoutedEventArgs e) => Apply(_manager.Disable);
    private void Enable_Click(object sender, RoutedEventArgs e) => Apply(_manager.Enable);
    private void Filter_Click(object sender, RoutedEventArgs e) => Render();
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var entry = Selected;
        if (entry == null) return;
        if (Loc.Show(Owner, Loc.F("StDeleteConfirm", entry.Name), Loc.T("StDeleteTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Apply(_manager.Delete);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var path = Selected?.TargetPath;
        if (path == null) return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : Directory.Exists(Path.GetDirectoryName(path)) ? $"\"{Path.GetDirectoryName(path)}\"" : null;
        if (args != null) Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Open_Click(sender, e);
}

/// <summary>One row of the startup grid (texts in the current UI language).</summary>
public sealed class StartupRow(StartupEntry entry)
{
    public StartupEntry Entry { get; } = entry;

    public string StateText => Entry.State switch
    {
        EntryState.Enabled => Loc.T("StEnabled"),
        _ when Entry.DisabledSinceUtc is { } since => Loc.F("StDisabledSince", Loc.FormatDate(since.ToLocalTime())[..10]),
        _ => Loc.T("StDisabled"),
    };

    public string ChipText => StateText;
    /// <summary>Enabled entries in green; one that turned itself back on after being disabled is a warning.</summary>
    public string Tone => Entry.ReEnabledItself ? "warning" : Entry.State == EntryState.Enabled ? "success" : "neutral";

    public string Publisher => Entry.Company ?? "";

    public string LocationText => Entry.Location.Kind == LocationKind.StartupFolder
        ? Loc.T("StLocFolder") + " · " + Loc.T(Entry.Location.PerUser ? "StLocUser" : "StLocMachine")
        : Loc.T(Entry.Location.PerUser ? "StLocUser" : "StLocMachine") + " · " + KeyName(Entry.Location);

    /// <summary>Short name of the Run key ("Run", "RunOnce", "Run 32-bit"…), from the key path itself.</summary>
    private static string KeyName(StartupLocation l)
    {
        var name = l.Key[(l.Key.LastIndexOf('\\') + 1)..];
        if (l.Key.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase)) name = "Policies " + name;
        if (l.Key.Contains("Terminal Server", StringComparison.OrdinalIgnoreCase)) name = "Terminal Server " + name;
        if (l.Key.Contains("Wow6432Node", StringComparison.OrdinalIgnoreCase)) name += " " + Loc.T("St32Bit");
        return name;
    }

    public string Notes
    {
        get
        {
            var notes = new List<string>();
            if (Entry.ReEnabledItself) notes.Add(Loc.T("StNoteReEnabled"));
            if (Entry.State == EntryState.DisabledLegacy) notes.Add(Loc.T("StNoteLegacy"));
            if (!Entry.TargetExists) notes.Add(Loc.T("StNoteMissing"));
            return string.Join(" · ", notes);
        }
    }
}
