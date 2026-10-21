using System.Windows;

namespace CleanSuite.App.Theming;

/// <summary>
/// <c>theming:Fit.MaxHeightShare="0.4"</c>: the element (normally a ScrollViewer around an options panel) is never taller
/// than that share of its window, so on a small screen the result grid below it keeps room and the panel scrolls instead.
/// At least 100 pixels are always left to it.
/// </summary>
public static class Fit
{
    public static readonly DependencyProperty MaxHeightShareProperty =
        DependencyProperty.RegisterAttached("MaxHeightShare", typeof(double), typeof(Fit), new PropertyMetadata(0.0, OnChanged));

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(Fit));

    public static double GetMaxHeightShare(DependencyObject d) => (double)d.GetValue(MaxHeightShareProperty);
    public static void SetMaxHeightShare(DependencyObject d, double value) => d.SetValue(MaxHeightShareProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || (bool)element.GetValue(HookedProperty)) return;
        element.SetValue(HookedProperty, true);
        element.Loaded += (_, _) =>
        {
            if (Window.GetWindow(element) is not { } window) return;
            void Apply() => element.MaxHeight = Math.Max(100, window.ActualHeight * GetMaxHeightShare(element));
            window.SizeChanged += (_, _) => Apply();
            Apply();
        };
    }
}
