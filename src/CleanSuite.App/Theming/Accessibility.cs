using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CleanSuite.App.Theming;

/// <summary>
/// <c>theming:Live.Announce="True"</c> on a status TextBlock: screen readers (Narrator, NVDA) read its new text without
/// the focus moving there ("live region", polite). Rapid progress updates are combined: at most one announcement every
/// 700 ms, always with the latest text.
/// </summary>
public static class Live
{
    public static readonly DependencyProperty AnnounceProperty =
        DependencyProperty.RegisterAttached("Announce", typeof(bool), typeof(Live), new PropertyMetadata(false, OnAnnounceChanged));

    private static readonly DependencyProperty TimerProperty =
        DependencyProperty.RegisterAttached("Timer", typeof(DispatcherTimer), typeof(Live));

    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    public static bool GetAnnounce(DependencyObject d) => (bool)d.GetValue(AnnounceProperty);
    public static void SetAnnounce(DependencyObject d, bool value) => d.SetValue(AnnounceProperty, value);

    private static void OnAnnounceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text || e.NewValue is not true) return;
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        TextDescriptor.AddValueChanged(text, OnTextChanged);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock text) return;
        if (text.GetValue(TimerProperty) is not DispatcherTimer timer)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, text.Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!text.IsVisible || string.IsNullOrEmpty(text.Text)) return;
                var peer = UIElementAutomationPeer.FromElement(text) ?? UIElementAutomationPeer.CreatePeerForElement(text);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            };
            text.SetValue(TimerProperty, timer);
        }
        if (!timer.IsEnabled) timer.Start();
    }
}

/// <summary>
/// Keyboard use of the tick boxes in result grids: Space on any cell of a row ticks or clears that row's box, like the
/// original program's lists. Set for every DataGrid by the implicit style. The box is toggled through its automation
/// pattern, so its Click handler and bindings run exactly as for a mouse click.
/// </summary>
public static class GridKeys
{
    public static readonly DependencyProperty SpaceTogglesProperty =
        DependencyProperty.RegisterAttached("SpaceToggles", typeof(bool), typeof(GridKeys), new PropertyMetadata(false, OnChanged));

    public static bool GetSpaceToggles(DependencyObject d) => (bool)d.GetValue(SpaceTogglesProperty);
    public static void SetSpaceToggles(DependencyObject d, bool value) => d.SetValue(SpaceTogglesProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.PreviewKeyDown -= OnKey;
        if (e.NewValue is true) grid.PreviewKeyDown += OnKey;
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None || sender is not DataGrid grid) return;
        if (e.OriginalSource is CheckBox or TextBox or ComboBox) return;                // the box itself or an editor has focus
        if (grid.CurrentItem is not { } item || grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return;
        if (FirstCheckBox(row) is not { IsEnabled: true } box) return;
        if ((UIElementAutomationPeer.FromElement(box) ?? UIElementAutomationPeer.CreatePeerForElement(box))?.GetPattern(PatternInterface.Toggle)
            is IToggleProvider toggle)
        {
            toggle.Toggle();
            e.Handled = true;
        }
    }

    private static CheckBox? FirstCheckBox(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is CheckBox box) return box;
            if (child is not ToggleButton && FirstCheckBox(child) is { } found) return found;
        }
        return null;
    }
}
