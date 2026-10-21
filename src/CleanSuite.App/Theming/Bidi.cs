using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace CleanSuite.App.Theming;

/// <summary>
/// Shows a string that marks paths and names with <see cref="Localization.Loc.Ltr"/> in a TextBlock. Message boxes honour
/// those Unicode embedding controls; WPF's text engine does not, so a path inside an Arabic sentence would be reordered.
/// Here every marked part becomes a left-to-right Run (an embedding WPF does apply) and the controls are dropped.
/// </summary>
public static class Bidi
{
    private const char Lre = '‪', Pdf = '‬';

    private static readonly DependencyProperty RawProperty =
        DependencyProperty.RegisterAttached("Raw", typeof(string), typeof(Bidi));

    /// <summary>The string last given to <see cref="SetText"/> (with its marks), or the plain text if Text was set since.</summary>
    public static string GetText(TextBlock block) =>
        block.GetValue(RawProperty) is string raw && Plain(raw) == block.Text ? raw : block.Text;

    public static void SetText(TextBlock block, string text)
    {
        block.SetValue(RawProperty, text);
        block.Text = Plain(text);                          // the whole sentence, also what screen readers announce
        if (text.IndexOf(Lre) < 0) return;
        block.Inlines.Clear();
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf(Lre, i);
            if (start < 0) { block.Inlines.Add(new Run(Plain(text[i..]))); break; }
            if (start > i) block.Inlines.Add(new Run(Plain(text[i..start])));
            int end = text.IndexOf(Pdf, start + 1);
            if (end < 0) end = text.Length;
            block.Inlines.Add(new Run(text[(start + 1)..end]) { FlowDirection = FlowDirection.LeftToRight });
            i = Math.Min(text.Length, end + 1);
        }
    }

    /// <summary>The text without the embedding controls and direction marks.</summary>
    public static string Plain(string text) =>
        text.Replace("‪", "").Replace("‬", "").Replace("‎", "").Replace("‏", "");
}
