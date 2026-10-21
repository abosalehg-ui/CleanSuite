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
