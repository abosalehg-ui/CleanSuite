using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace CleanSuite.App.Theming;

/// <summary>
/// Content shown above and below the navigation items of a TabControl drawn as a side bar (style NavTabs in
/// Themes/Controls.xaml): the application name at the top, language, theme and "About" at the bottom.
/// </summary>
public static class Nav
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.RegisterAttached("Header", typeof(object), typeof(Nav), new PropertyMetadata(null));

    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.RegisterAttached("Footer", typeof(object), typeof(Nav), new PropertyMetadata(null));

    public static object? GetHeader(DependencyObject d) => d.GetValue(HeaderProperty);
    public static void SetHeader(DependencyObject d, object? value) => d.SetValue(HeaderProperty, value);
    public static object? GetFooter(DependencyObject d) => d.GetValue(FooterProperty);
    public static void SetFooter(DependencyObject d, object? value) => d.SetValue(FooterProperty, value);
}

/// <summary>
/// The side bar TabControl. A TabControl tells UI Automation only about its tabs, so the header and footer of the side bar
/// (language, theme, About) would be invisible to screen readers and keyboard-automation tools; this one adds them.
/// </summary>
public sealed class NavTabControl : TabControl
{
    /// <summary>Below this width the side bar folds to icons (a menu button unfolds it again).</summary>
    public const double CompactBelow = 1000;

    public static readonly DependencyProperty IsNarrowProperty = DependencyProperty.Register(nameof(IsNarrow), typeof(bool), typeof(NavTabControl),
        new PropertyMetadata(false, (d, _) => ((NavTabControl)d).UpdateCompact()));

    public static readonly DependencyProperty UserExpandedProperty = DependencyProperty.Register(nameof(UserExpanded), typeof(bool), typeof(NavTabControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NavTabControl)d).UpdateCompact()));

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(NavTabControl),
        new PropertyMetadata(false));

    /// <summary>The window is narrower than <see cref="CompactBelow"/>.</summary>
    public bool IsNarrow
    {
        get => (bool)GetValue(IsNarrowProperty);
        private set => SetValue(IsNarrowProperty, value);
    }

    /// <summary>The user unfolded the side bar with the menu button (only meaningful while narrow).</summary>
    public bool UserExpanded
    {
        get => (bool)GetValue(UserExpandedProperty);
        set => SetValue(UserExpandedProperty, value);
    }

    /// <summary>The side bar shows icons only.</summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        IsNarrow = sizeInfo.NewSize.Width < CompactBelow;
    }

    private void UpdateCompact() => IsCompact = IsNarrow && !UserExpanded;

    /// <summary>Choosing a page with the mouse in an unfolded narrow side bar folds it again, so the page gets the room.</summary>
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (IsNarrow && UserExpanded && !IsKeyboardFocusWithin) UserExpanded = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(NavTabControl owner) : TabControlAutomationPeer(owner)
    {
        protected override List<AutomationPeer> GetChildrenCore()
        {
            var children = base.GetChildrenCore() ?? new List<AutomationPeer>();
            children.InsertRange(0, PeersOf(Nav.GetHeader(Owner)));
            children.AddRange(PeersOf(Nav.GetFooter(Owner)));
            return children;
        }

        /// <summary>The element's own peer, or for a plain panel the peers of the controls inside it.</summary>
        private static IEnumerable<AutomationPeer> PeersOf(object? part) => part switch
        {
            UIElement e when UIElementAutomationPeer.CreatePeerForElement(e) is { } peer => [peer],
            FrameworkElement fe => new FrameworkElementAutomationPeer(fe).GetChildren() ?? [],
            _ => [],
        };
    }
}
