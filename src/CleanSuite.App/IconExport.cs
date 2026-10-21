using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CleanSuite.App;

/// <summary>
/// Developer aid ("--export-icon &lt;file.ico&gt;"): renders the vector application icon (Themes/Icons.xaml, AppIconDrawing) at
/// every size Windows asks for and writes them as PNG frames of one .ico file, so the program icon and the window icon come
/// from the same drawing. The result is committed as Assets/CleanSuite.ico and embedded through ApplicationIcon.
/// </summary>
public static class IconExport
{
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    public static void Write(Drawing drawing, string file, string? pngFolder = null)
    {
        var frames = Sizes.Select(s => (Size: s, Png: Render(drawing, s))).ToList();
        if (pngFolder != null)
        {
            Directory.CreateDirectory(pngFolder);
            foreach (var f in frames) File.WriteAllBytes(Path.Combine(pngFolder, $"icon{f.Size}.png"), f.Png);
        }
        using var w = new BinaryWriter(File.Create(file));
        w.Write((short)0);                                   // reserved
        w.Write((short)1);                                   // 1 = icon
        w.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var f in frames)
        {
            w.Write((byte)(f.Size >= 256 ? 0 : f.Size));     // 0 means 256
            w.Write((byte)(f.Size >= 256 ? 0 : f.Size));
            w.Write((byte)0);                                // no palette
            w.Write((byte)0);
            w.Write((short)1);                               // planes
            w.Write((short)32);                              // bits per pixel
            w.Write(f.Png.Length);
            w.Write(offset);
            offset += f.Png.Length;
        }
        foreach (var f in frames) w.Write(f.Png);
    }

    private static byte[] Render(Drawing drawing, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var b = drawing.Bounds;
            dc.PushTransform(new ScaleTransform(size / b.Width, size / b.Height));
            dc.PushTransform(new TranslateTransform(-b.X, -b.Y));
            dc.DrawDrawing(drawing);
        }
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
