using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.App.ViewModels;
using CleanSuite.Startup;

namespace CleanSuite.App;

/// <summary>Startup programs tab (Auto-Start Manager): list, disable/enable (reversible) and delete (with backup). State in <see cref="StartupViewModel"/>.</summary>
public partial class StartupView : UserControl, IToolPage
{
    private readonly StartupViewModel _vm = new();
    private bool _loadedOnce;
    private string? _changed;

    public StartupView()
    {
        InitializeComponent();
        _vm.Error += ex => UiErrors.Show(Owner, ex, "startup");
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Items)) Render();
            else if (e.PropertyName == nameof(_vm.IsBusy)) RenderBusy();
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
        _ = _vm.RefreshAsync();
    }

    private void RenderBusy()
    {
        RefreshButton.IsEnabled = !_vm.IsBusy;
        Grid.IsEnabled = !_vm.IsBusy;
        if (_vm.IsBusy) StatusText.Text = Loc.T("StLoading");
        else Render();
    }

    private void Render()
    {
        var entries = _vm.Items;
        var selected = (Grid.SelectedItem as StartupRow)?.Entry.Identity;
        var rows = entries.Where(e => HideMicrosoft.IsChecked != true || !e.IsMicrosoft).Select(e => new StartupRow(e)).ToList();
        Grid.ItemsSource = rows;
        Grid.SelectedItem = rows.FirstOrDefault(r => r.Entry.Identity == selected);
        if (!_vm.IsBusy)
        {
            var status = Loc.F("StCount", Loc.N0(entries.Count), Loc.N0(entries.Count(e => e.Enabled)), Loc.N0(entries.Count(e => !e.Enabled)))
                         + " · " + Loc.T("StAdminHint");
            Bidi.SetText(StatusText, _changed != null ? Loc.F("StChanged", Loc.Ltr(_changed)) + " · " + status : status);
        }
        int reEnabled = entries.Count(e => e.ReEnabledItself);
        WarningText.Text = Loc.F("StReEnabledWarning", Loc.N0(reEnabled));
        WarningText.Visibility = reEnabled > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private StartupEntry? Selected => (Grid.SelectedItem as StartupRow)?.Entry;

    private void UpdateButtons()
    {
        var e = Selected;
        bool idle = !_vm.IsBusy;
        DisableButton.IsEnabled = idle && e is { Enabled: true };
        EnableButton.IsEnabled = idle && e is { Enabled: false };
        DeleteButton.IsEnabled = idle && e != null;
        OpenButton.IsEnabled = e?.TargetPath != null;
    }

    private async void Apply(Func<StartupEntry, Task<StartupManager.ChangeResult?>> change)
    {
        var e = Selected;
        if (e == null) { Loc.Show(Owner, Loc.T("StNoSelection"), Loc.T("TabStartup"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_vm.IsBusy) return;
        _changed = null;
        var r = await change(e);
        if (r == null) return;                                     // busy, or an error the page has shown
        if (r.Success) _changed = e.Name;
        else Loc.Show(Owner, Loc.F("StFailed", Loc.Ltr(e.Name), Loc.Reason(r.Error ?? "")), Loc.T("TabStartup"), MessageBoxButton.OK, MessageBoxImage.Warning);
        Render();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) { _changed = null; _ = _vm.RefreshAsync(); }
    private void Disable_Click(object sender, RoutedEventArgs e) => Apply(_vm.DisableAsync);
    private void Enable_Click(object sender, RoutedEventArgs e) => Apply(_vm.EnableAsync);
    private void Filter_Click(object sender, RoutedEventArgs e) => Render();
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var entry = Selected;
        if (entry == null) return;
        if (Loc.Show(Owner, Loc.F("StDeleteConfirm", Loc.Ltr(entry.Name)), Loc.T("StDeleteTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Apply(_vm.DeleteAsync);
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

    /// <summary>The Run key in words ("Run (every sign-in)", "RunOnce (next sign-in only)", policies, Terminal Server, 32-bit).</summary>
    private static string KeyName(StartupLocation l)
    {
        var leaf = l.Key[(l.Key.LastIndexOf('\\') + 1)..];
        var name = Loc.T(leaf.Equals("RunOnce", StringComparison.OrdinalIgnoreCase) ? "StKeyRunOnce"
                       : leaf.Equals("RunServices", StringComparison.OrdinalIgnoreCase) ? "StKeyRunServices"
                       : leaf.Equals("RunServicesOnce", StringComparison.OrdinalIgnoreCase) ? "StKeyRunServicesOnce" : "StKeyRun");
        if (l.Key.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase)) name = Loc.F("StKeyPolicies", name);
        if (l.Key.Contains("Terminal Server", StringComparison.OrdinalIgnoreCase)) name = Loc.F("StKeyTerminalServer", name);
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
