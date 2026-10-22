using System.Text;
using CleanSuite.Matching;
using CleanSuite.Safety;

namespace CleanSuite.Junk;

/// <summary>
/// Junk ignore lists: every file in the ignore folder,
/// one mask per line, "//" starts a comment, masks upper-cased. Lines carrying "/T" also apply inside Temp folders.
/// </summary>
public sealed class IgnoreList
{
    private readonly List<string> _general = new();
    private readonly List<string> _temp = new();
    public IReadOnlyList<string> General => _general;
    public IReadOnlyList<string> TempOnly => _temp;

    public static IgnoreList LoadFolder(string folder) => LoadFolders([folder]);

    /// <summary>
    /// CleanSuite's own protection list (IgnoreLists\CleanSuite.txt, embedded): files a junk pattern catches by name that
    /// belong to programs or to Windows (database checkpoint files, program and script files). The junk scanner applies it
    /// in drive and folder scans always, whatever the ignore settings, and with or without the original program's lists.
    /// </summary>
    public static IgnoreList BuiltIn { get; } = LoadBuiltIn();

    private static IgnoreList LoadBuiltIn()
    {
        using var stream = typeof(IgnoreList).Assembly.GetManifestResourceStream("CleanSuite.Junk.IgnoreLists.CleanSuite.txt")
            ?? throw new InvalidOperationException("the built-in junk protection list is missing from the program");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var list = new IgnoreList();
        list.AddLines(reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')));
        return list;
    }

    /// <summary>Every file of each folder that exists, folders in the given order.</summary>
    public static IgnoreList LoadFolders(IEnumerable<string> folders)
    {
        var list = new IgnoreList();
        foreach (var folder in folders.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                list.AddLines(ReadLines(file));
        return list;
    }

    public void AddLines(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim().ToUpperInvariant();
            int c = line.IndexOf("//", StringComparison.Ordinal);
            if (c >= 0) line = line[..c];
            line = line.Trim();
            bool temp = false;
            int t = line.IndexOf("/T", StringComparison.Ordinal);
            if (t >= 0)
            {
                temp = true;
                line = line.Replace("/T", "", StringComparison.Ordinal).Trim();
            }
            if (line.Length == 0) continue;
            if (temp) _temp.Add(line);
            _general.Add(line);
        }
    }

    /// <summary>Returns the matching ignore mask, or null. Masks with '\' match the full path, others the name.</summary>
    /// <param name="allListsInTemp">
    /// Extension (off in the original): in Temp mode use every line, not only the "/T" lines.
    /// <see cref="General"/> already contains the "/T" lines, so it is the complete list.
    /// </param>
    public string? Match(string name, string fullPath, ScanMode mode, bool allListsInTemp = false)
    {
        foreach (var mask in mode == ScanMode.Temp && !allListsInTemp ? _temp : _general)
        {
            string target = mask.Contains('\\') ? fullPath : name;
            if (AceWildcard.IsMatch(target, mask)) return mask;
        }
        return null;
    }

    /// <summary>Reads UTF-16LE (BOM) or ANSI like the original CStdioFileEx; also accepts a UTF-8 BOM (extension).</summary>
    public static IEnumerable<string> ReadLines(string path) => TextFiles.ReadLines(path);
}
