using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CleanSuite.App.Theming;

/// <summary>One part of a <see cref="DonutChart"/> and its legend row.</summary>
public sealed record ChartSlice(string Label, long Value, string ValueText, string PercentText, Brush Fill)
{
    public string ToolTipText => Label + "\n" + ValueText + " · " + PercentText;
}

/// <summary>
/// Part-to-whole doughnut (Disk Analysis, as the original's pie charts): one ring segment per slice, in the order given,
/// separated by a 2 px gap in the surface colour; hovering a segment shows its name, size and share. The legend is a
/// separate list bound to the same slices, so identity never rests on colour alone.
/// </summary>
public sealed class DonutChart : Canvas
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(nameof(Slices),
        typeof(IReadOnlyList<ChartSlice>), typeof(DonutChart), new PropertyMetadata(null, (d, _) => ((DonutChart)d).Rebuild()));

    public static readonly DependencyProperty CenterTextProperty = DependencyProperty.Register(nameof(CenterText),
        typeof(string), typeof(DonutChart), new PropertyMetadata("", (d, _) => ((DonutChart)d).Rebuild()));

    public IReadOnlyList<ChartSlice>? Slices
    {
        get => (IReadOnlyList<ChartSlice>?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public string CenterText
    {
        get => (string)GetValue(CenterTextProperty);
        set => SetValue(CenterTextProperty, value);
    }

    public DonutChart()
    {
        // Drawn in screen coordinates; the legend next to it carries the reading direction.
        FlowDirection = FlowDirection.LeftToRight;
        SizeChanged += (_, _) => Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        double size = Math.Min(ActualWidth, ActualHeight);
        var slices = Slices?.Where(s => s.Value > 0).ToList();
        if (size < 40 || slices is not { Count: > 0 }) return;
        double total = slices.Sum(s => (double)s.Value);
        double outer = size / 2 - 2, inner = outer * 0.62;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double start = -90;
        foreach (var s in slices)
        {
            double sweep = s.Value / total * 360;
            var path = new Path { Data = Segment(center, outer, inner, start, sweep), Fill = s.Fill, StrokeThickness = slices.Count > 1 ? 2 : 0,
                                  ToolTip = s.ToolTipText, Cursor = System.Windows.Input.Cursors.Hand };
            path.SetResourceReference(Shape.StrokeProperty, "SurfaceBg");
            System.Windows.Automation.AutomationProperties.SetName(path, s.ToolTipText.Replace('\n', ' '));
            Children.Add(path);
            start += sweep;
        }
        var text = new TextBlock { Text = CenterText, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                                   Width = inner * 1.6, IsHitTestVisible = false };
        text.Measure(new Size(text.Width, double.PositiveInfinity));
        SetLeft(text, center.X - text.Width / 2);
        SetTop(text, center.Y - text.DesiredSize.Height / 2);
        Children.Add(text);
    }

    /// <summary>Ring segment from <paramref name="start"/> degrees over <paramref name="sweep"/> degrees (a full ring when 360).</summary>
    private static Geometry Segment(Point c, double outer, double inner, double start, double sweep)
    {
        if (sweep >= 359.99)
            return new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(c, outer, outer), new EllipseGeometry(c, inner, inner));
        Point P(double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        bool large = sweep > 180;
        var fig = new PathFigure { StartPoint = P(outer, start), IsClosed = true };
        fig.Segments.Add(new ArcSegment(P(outer, start + sweep), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(P(inner, start + sweep), true));
        fig.Segments.Add(new ArcSegment(P(inner, start), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    /// <summary>
    /// Categorical colours in fixed order, validated for colour-vision deficiency on light and dark surfaces; the dark
    /// column is the same hues stepped for the dark surface. More parts than slots fold into "Other" (<see cref="OtherBrush"/>).
    /// </summary>
    private static readonly string[] Light = ["#2A78D6", "#EB6834", "#1BAF7A", "#EDA100", "#E87BA4", "#008300", "#6250D6", "#E34948"];
    private static readonly string[] Dark = ["#3987E5", "#D95926", "#199E70", "#C98500", "#D55181", "#008300", "#9085E9", "#E66767"];

    public static int SlotCount => Light.Length;

    public static Brush SlotBrush(int slot)
    {
        if (ThemeManager.IsHighContrast) return slot % 2 == 0 ? SystemColors.HighlightBrush : SystemColors.WindowTextBrush;
        var hex = (ThemeManager.IsDark ? Dark : Light)[slot % Light.Length];
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Brush OtherBrush => (Brush)Application.Current.FindResource("TrackBg");
}
