using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.App.ViewModels;

namespace CleanSuite.App;

/// <summary>
/// The main window: the side bar with every tool page, language and theme, the keyboard shortcuts of the pages (F5, Esc,
/// Ctrl+A, Delete), the window's size and position (kept between runs), and the developer aids. Each page implements
/// <see cref="IToolPage"/>, so the window never needs to know what a page is doing.
/// </summary>
public partial class MainWindow : Window
{
    private bool _initializing = true;

    public MainWindow()
    {
        InitializeComponent();
        LanguageCombo.SelectedIndex = Loc.Instance.Language == UiLanguage.Arabic ? 0 : 1;
        ThemeCombo.SelectedIndex = (int)ThemeManager.Choice;
        RestorePlacement();
        ThemeManager.ApplyTitleBar(this);
        // Snapshots render the visual tree, not the desktop composition, so they keep the solid colours.
        if (!Environment.GetCommandLineArgs().Contains("--snapshot")) ThemeManager.ApplyBackdrop(this);
        PreviewKeyDown += Window_PreviewKeyDown;
        KeyDown += Window_KeyDown;
        JunkTab.SelectionTitleChanged += UpdateTitle;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        _initializing = false;
        UpdateTitle();
        ApplyCommandLine(Environment.GetCommandLineArgs().Skip(1).ToArray());
    }

    /// <summary>Every tool page in the side bar.</summary>
    private IEnumerable<IToolPage> Pages => Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<IToolPage>();

    private IToolPage? CurrentPage => (Tabs.SelectedItem as TabItem)?.Content as IToolPage;

    private void UpdateTitle() => Title = JunkTab.SelectionTitle ?? Loc.T("AppTitle");

    // ---------------------------------------------------------------- command line and developer aids

    private void ApplyCommandLine(string[] args)
    {
        // Developer aid: "--size 760x460" opens the window at that size (layout checks at the minimum size).
        int size = Array.IndexOf(args, "--size");
        if (size >= 0 && size + 1 < args.Length && args[size + 1].Split('x') is [var w, var h]
            && double.TryParse(w, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var width)
            && double.TryParse(h, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var height))
        {
            WindowState = WindowState.Normal;
            Width = Math.Max(width, MinWidth);
            Height = Math.Max(height, MinHeight);
        }
        int tab = Array.IndexOf(args, "--tab");
        if (tab >= 0 && tab + 1 < args.Length) Open(args[tab + 1]);
        else if (args.Contains("--scan")) Open("junk");
        int snap = Array.IndexOf(args, "--snapshot");
        if (snap >= 0 && snap + 1 < args.Length) SnapshotWhenIdle(args[snap + 1]);
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
            if (!IsLoaded || Pages.Any(p => p.IsBusy)) return;
            // The restore window checks its destinations in the background: open it, then wait for that too.
            if (restoreZip != null && restore == null)
            {
                restore = new RestoreWindow(restoreZip, CleanSuite.Safety.JunkBackupArchive.List(restoreZip),
                    CleanSuite.Safety.JunkBackupArchive.IsLegacyBackup(restoreZip))
                { Owner = this };
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

    // ---------------------------------------------------------------- keyboard

    /// <summary>
    /// F5 runs the visible page's scan, analysis or refresh; Esc cancels what it is running. Only a button that is enabled is
    /// pressed, so nothing starts twice; a key an open list or editor handles never gets here.
    /// </summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.Key is not (Key.F5 or Key.Escape) || CurrentPage is not { } page) return;
        if (Press(e.Key == Key.F5 ? page.ScanButton : page.CancelButton)) e.Handled = true;
    }

    /// <summary>
    /// In a result list with check boxes: Ctrl+A checks every row (as "Check all") and Delete starts the clean of the checked
    /// rows (which asks first). Handled before the list's own Ctrl+A, which would only highlight the rows.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || FindAncestor<DataGrid>(d) is not { } grid || FindAncestor<TextBox>(d) != null) return;
        if (CurrentPage is not FrameworkElement { DataContext: IScanCleanCommands commands } || !grid.Columns.Any(c => c is DataGridTemplateColumn)) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) e.Handled = Execute(commands.SelectAllCommand);
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) e.Handled = Execute(commands.CleanCommand);
    }

    private static bool Execute(ICommand command)
    {
        if (!command.CanExecute(null)) return false;
        command.Execute(null);
        return true;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (DependencyObject? x = d; x != null; x = x is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is T t) return t;
        return null;
    }

    private static bool Press(Button? button)
    {
        if (button is not { IsEnabled: true, IsVisible: true }) return false;
        if (button.Command is { } command)
        {
            if (command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
        }
        else button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
        return true;
    }

    /// <summary>Shows a page by its key: home, junk, history, duplicates, shortcuts, registry, startup, uninstall, disk, restore, scheduler.</summary>
    public void Open(string key)
    {
        Tabs.SelectedItem = key.ToLowerInvariant() switch
        {
            "junk" => JunkItem,
            "history" => HistoryItem,
            "duplicates" => DuplicatesItem,
            "shortcuts" => ShortcutsItem,
            "registry" => RegistryItem,
            "startup" => StartupItem,
            "uninstall" => UninstallItem,
            "disk" => DiskItem,
            "restore" => RestoreItem,
            "scheduler" => SchedulerItem,
            _ => HomeItem,
        };
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    // ---------------------------------------------------------------- language / theme

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
        int i = ThemeCombo.SelectedIndex;
        _initializing = true;
        ThemeCombo.SelectedIndex = -1;
        ThemeCombo.SelectedIndex = i;
        _initializing = false;
        UpdateTitle();
    }

    // ---------------------------------------------------------------- window placement

    /// <summary>Opens where and as large as the window was when it was last closed, if that place is still on a screen.</summary>
    private void RestorePlacement()
    {
        var ui = App.Ui;
        if (ui.WindowWidth is not { } w || ui.WindowHeight is not { } h) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Width = Math.Max(MinWidth, Math.Min(w, screen.Width));
        Height = Math.Max(MinHeight, Math.Min(h, screen.Height));
        if (ui.WindowLeft is { } left && ui.WindowTop is { } top && screen.IntersectsWith(new Rect(left, top, Width, Height)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (ui.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (Environment.GetCommandLineArgs().Contains("--snapshot")) return;      // layout checks never change the user's window
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty) return;
        var ui = App.Ui;
        ui.WindowLeft = bounds.Left;
        ui.WindowTop = bounds.Top;
        ui.WindowWidth = bounds.Width;
        ui.WindowHeight = bounds.Height;
        ui.WindowMaximized = WindowState == WindowState.Maximized;
        ui.Save();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // A removal stopped half-way leaves its backup and journal incomplete: wait for it. Scans are simply cancelled.
        if (Pages.Any(p => p.IsChanging))
        {
            Loc.Show(this, Loc.T("WaitForClean"), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }
        foreach (var p in Pages) p.CancelWork();
        SavePlacement();
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }
}
