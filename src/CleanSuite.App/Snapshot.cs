using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CleanSuite.App;

/// <summary>Renders a window's content to a PNG (developer aid, see MainWindow.SnapshotWhenIdle).</summary>
public static class Snapshot
{
    public static void Save(Window window, string file)
    {
        // The window's first visual child holds the content and the adorner layer (empty-list hints are drawn there).
        bool chromeRoot = VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is FrameworkElement;
        var root = chromeRoot ? (FrameworkElement)VisualTreeHelper.GetChild(window, 0) : (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var size = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        var bmp = new RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var host = new DrawingVisual();
        using (var dc = host.RenderOpen())
        {
            // A VisualBrush of the content drops its right-to-left mirror transform, so apply it here; the window chrome keeps it.
            if (!chromeRoot && window.FlowDirection == FlowDirection.RightToLeft)
                dc.PushTransform(new MatrixTransform(-1, 0, 0, 1, size.Width, 0));
            dc.DrawRectangle(window.Background, null, size);
            dc.DrawRectangle(new VisualBrush(root), null, size);
        }
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
