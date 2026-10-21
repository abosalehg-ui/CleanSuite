using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace CleanSuite.App.Theming;

public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// Swaps the palette dictionary (Themes/Light.xaml or Themes/Dark.xaml) that every style reads through DynamicResource,
/// and colours the native title bar to match. "System" follows the Windows app theme and reacts to changes. When a
/// Windows contrast theme (High Contrast) is on, a palette built from the system colours replaces both, whatever the
/// choice, and follows changes of the contrast theme.
/// </summary>
public static class ThemeManager
{
    private static ThemeChoice _choice = ThemeChoice.System;
    private static bool _subscribed;
    private const string HighContrastMarker = "CleanSuite.HighContrast";

    public static bool IsDark { get; private set; }

    /// <summary>A Windows contrast theme is in use (or forced with "--high-contrast" to check the layout).</summary>
    public static bool IsHighContrast => SystemParameters.HighContrast || ForceHighContrast;

    public static bool ForceHighContrast { get; set; }

    public static event EventHandler? ThemeChanged;

    public static ThemeChoice Choice
    {
        get => _choice;
        set { _choice = value; Apply(); }
    }

    public static void Apply()
    {
        if (!_subscribed)
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                bool themeChanged = e.Category == UserPreferenceCategory.General && _choice == ThemeChoice.System;
                bool contrastColours = IsHighContrast && e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility;
                if (themeChanged || contrastColours) Application.Current?.Dispatcher.Invoke(Apply);
            };
            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.HighContrast)) Application.Current?.Dispatcher.BeginInvoke(Apply);
            };
            _subscribed = true;
        }

        bool contrast = IsHighContrast;
        IsDark = contrast ? IsDarkColour(SystemColors.WindowColor)
                          : _choice switch { ThemeChoice.Dark => true, ThemeChoice.Light => false, _ => SystemUsesDarkTheme() };
        var app = Application.Current;
        if (app == null) return;
        var palette = contrast ? HighContrastPalette()
                               : new ResourceDictionary { Source = new Uri($"Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        var merged = app.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Source?.OriginalString is "Themes/Dark.xaml" or "Themes/Light.xaml" || d.Contains(HighContrastMarker));
        if (old != null) merged[merged.IndexOf(old)] = palette;
        else merged.Insert(0, palette);

        foreach (Window w in app.Windows)
        {
            ApplyTitleBar(w);
            // A contrast theme needs opaque system colours: undo the Mica see-through background if it was applied.
            if (contrast && ReferenceEquals(w.Background, System.Windows.Media.Brushes.Transparent))
            {
                w.ClearValue(Window.BackgroundProperty);
                w.Resources.Remove("NavBg");
            }
        }
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Every palette key (taken from Dark.xaml, so a key added there is never missing here) mapped to a system colour of the
    /// contrast theme: backgrounds to Window, text and lines to WindowText, accent and selection to Highlight with
    /// HighlightText on it, disabled text to GrayText.
    /// </summary>
    private static ResourceDictionary HighContrastPalette()
    {
        var keys = new ResourceDictionary { Source = new Uri("Themes/Dark.xaml", UriKind.Relative) }.Keys.OfType<string>().ToList();
        var palette = new ResourceDictionary { [HighContrastMarker] = true };
        foreach (var key in keys)
        {
            palette[key] = key switch
            {
                "AccentBg" or "AccentHoverBg" or "SelectionBg" or "SelectionInactiveBg" or "ScrollThumbHover" => SystemColors.HighlightBrush,
                "AccentFg" or "SelectionFg" => SystemColors.HighlightTextBrush,
                "DisabledFg" or "TrackBg" => SystemColors.GrayTextBrush,
                "ControlHoverBg" or "ControlPressedBg" or "NavHoverBg" or "NavSelectedBg" => SystemColors.ControlBrush,
                _ when key.EndsWith("Bg", StringComparison.Ordinal) => SystemColors.WindowBrush,
                _ => SystemColors.WindowTextBrush,
            };
        }
        return palette;
    }

    private static bool IsDarkColour(System.Windows.Media.Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B < 128;

    /// <summary>Reads (never writes) HKCU\...\Themes\Personalize\AppsUseLightTheme; missing value = light.</summary>
    public static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    /// <summary>Dark or light native title bar (DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 10 20H1+; ignored where unsupported).</summary>
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            w.SourceInitialized -= OnSourceInitialized;
            w.SourceInitialized += OnSourceInitialized;
            return;
        }
        int on = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));   // attribute id before Windows 10 20H1
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window w) { w.SourceInitialized -= OnSourceInitialized; ApplyTitleBar(w); }
    }

    /// <summary>
    /// Windows 11 Mica backdrop behind the window (DWMWA_SYSTEMBACKDROP_TYPE = main window, build 22621+). Only when Windows
    /// accepts it are the window background and the side bar made transparent so the backdrop shows through; the content
    /// pane keeps its solid colour. Elsewhere (Windows 10, older builds, remote sessions that refuse it) nothing changes.
    /// </summary>
    public static void ApplyBackdrop(Window w)
    {
        if (Environment.OSVersion.Version.Build < 22621 || IsHighContrast) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            w.SourceInitialized += (_, _) => ApplyBackdrop(w);
            return;
        }
        int mainWindow = 2;
        if (DwmSetWindowAttribute(hwnd, 38, ref mainWindow, sizeof(int)) != 0) return;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return;
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = System.Windows.Media.Colors.Transparent;
        w.Background = System.Windows.Media.Brushes.Transparent;
        w.Resources["NavBg"] = System.Windows.Media.Brushes.Transparent;
        w.StateChanged += (_, _) => KeepClearOfCaptionButtons(w);
        KeepClearOfCaptionButtons(w);
    }

    /// <summary>
    /// With the frame extended over the whole window (needed for Mica), a maximized window's top border lies off the
    /// screen, yet Windows draws the caption buttons from the screen's top edge at their full height: the content would
    /// cover their lower part (the red hover of Close shows it cut). While maximized, the content starts lower by exactly
    /// the part of the window that is off the screen.
    /// </summary>
    private static void KeepClearOfCaptionButtons(Window w)
    {
        if (w.Content is not FrameworkElement content) return;
        double top = 0;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (w.WindowState == WindowState.Maximized && hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r))
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref info) && info.Monitor.Top > r.Top)
                top = (info.Monitor.Top - r.Top) / System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleY;
        }
        content.Margin = new Thickness(0, top, 0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
