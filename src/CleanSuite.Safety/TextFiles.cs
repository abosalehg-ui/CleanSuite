using System.Text;

namespace CleanSuite.Safety;

/// <summary>Reading the plain-text lists (ignore lists) the original program and CleanSuite keep, in whatever encoding they were saved.</summary>
public static class TextFiles
{
    /// <summary>Reads UTF-16LE (BOM) or ANSI like the original CStdioFileEx; also accepts a UTF-8 BOM (extension).</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else text = AnsiEncoding.GetString(bytes);
        return text.Split('\n').Select(l => l.TrimEnd('\r'));
    }

    /// <summary>The system ANSI code page (CP_ACP), as used by the original.</summary>
    private static Encoding AnsiEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(0);
        }
    }
}
