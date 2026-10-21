using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CleanSuite.App.Theming;

/// <summary>
/// Empty-state hint for a list: <c>theming:Empty.Text="{loc:T …}"</c> on a DataGrid (or any ItemsControl) draws a muted
/// icon and that text in the middle of the list while it has no rows. Drawn on the adorner layer, so the list's own layout
/// and template stay untouched. The same text is the list's automation help text while it is shown, so screen readers
/// say it when the empty list gets focus.
/// </summary>
public static class Empty
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Empty), new PropertyMetadata(null, OnTextChanged));

    private static readonly DependencyProperty AdornerProperty =
        DependencyProperty.RegisterAttached("Adorner", typeof(HintAdorner), typeof(Empty));

    private static readonly DependencyPropertyDescriptor HasItems =
        DependencyPropertyDescriptor.FromProperty(ItemsControl.HasItemsProperty, typeof(ItemsControl));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list) return;
        if (list.GetValue(AdornerProperty) is HintAdorner existing) { existing.Update(); return; }
        var adorner = new HintAdorner(list);
        list.SetValue(AdornerProperty, adorner);
        list.Loaded += (_, _) => Attach(list, adorner);
        list.Unloaded += (_, _) => AdornerLayer.GetAdornerLayer(list)?.Remove(adorner);
        HasItems.AddValueChanged(list, (_, _) => adorner.Update());
        if (list.IsLoaded) Attach(list, adorner);
    }

    private static void Attach(ItemsControl list, HintAdorner adorner)
    {
        var layer = AdornerLayer.GetAdornerLayer(list);
        if (layer == null) return;
        if (layer.GetAdorners(list)?.Contains(adorner) != true) layer.Add(adorner);
        adorner.Update();
    }

    private sealed class HintAdorner : Adorner
    {
        private readonly ItemsControl _list;

        public HintAdorner(ItemsControl list) : base(list)
        {
            _list = list;
            IsHitTestVisible = false;
        }

        public void Update()
        {
            Visibility = _list.HasItems || string.IsNullOrEmpty(GetText(_list)) ? Visibility.Collapsed : Visibility.Visible;
            if (Visibility == Visibility.Visible) System.Windows.Automation.AutomationProperties.SetHelpText(_list, GetText(_list));
            else _list.ClearValue(System.Windows.Automation.AutomationProperties.HelpTextProperty);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var text = GetText(_list);
            if (string.IsNullOrEmpty(text) || _list.HasItems) return;
            var brush = (Brush)_list.FindResource("TextMutedFg");
            var size = AdornedElement.RenderSize;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, _list.FlowDirection,
                new Typeface((FontFamily)_list.FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                14, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(120, Math.Min(460, size.Width - 40)),
                TextAlignment = TextAlignment.Center,
            };
            const double icon = 40, gap = 12;
            // A short list (small window) shows only the text; nothing is ever drawn outside the list.
            bool withIcon = size.Height >= 48 + icon + gap + ft.Height + 8;
            double top = withIcon ? Math.Max(48, (size.Height - (icon + gap + ft.Height)) / 2) : Math.Max(36, (size.Height - ft.Height) / 2);
            double cx = size.Width / 2;
            dc.PushClip(new RectangleGeometry(new Rect(size)));

            // The adorner layer of a right-to-left window is mirrored, and drawing-context text is not mirrored back as a
            // TextBlock's is; undo the mirror for the whole hint (both drawings are centred, so positions are unchanged).
            bool rtl = _list.FlowDirection == FlowDirection.RightToLeft;
            if (rtl) dc.PushTransform(new MatrixTransform(-1, 0, 0, 1, size.Width, 0));
            if (withIcon)
            {
                var geometry = ((Geometry)_list.FindResource("Icon.Search")).Clone();
                geometry.Transform = new MatrixTransform(icon / 24, 0, 0, icon / 24, cx - icon / 2, top);
                dc.DrawGeometry(null, new Pen(brush, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
            }
            dc.DrawText(ft, new Point(cx - ft.MaxTextWidth / 2, withIcon ? top + icon + gap : top));
            if (rtl) dc.Pop();
            dc.Pop();
        }
    }
}
