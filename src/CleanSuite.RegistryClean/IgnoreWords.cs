using System.Runtime.InteropServices;
using System.Text;

namespace CleanSuite.RegistryClean;

/// <summary>
/// Registry ignore words. A finding is ignored when its
/// upper-cased composite text "HIVE\key\value\data\reason" contains one of the words.
/// </summary>
public sealed class IgnoreWords
{
    /// <summary>Words built into the original (table at the original); always present.</summary>
    public static readonly IReadOnlyList<string> BuiltIn =
    [
        ".HTA", ".HTC", ".BAT", ".MANIFEST", "HTAFILE", "HTCFILE", "BATFILE", "EXEFILE", "LNKFILE", "CMDFILE", "DEFAULT.HTA",
        "CACHEPATH", "BOOT", "I386", "VARLDID", "KERNEL", "OLDWINBOOTDIR", "UNINSTALLDIR", "BACKUPDIR", "BACKUPPATH", "BACKUPFILE",
        "JAVA VM", "SETUPX",
    ];

    /// <summary>
    /// CleanSuite's own protective words (not in the original), always applied: areas Windows, installers and security
    /// software maintain themselves, where removing entries breaks updates, repairs, uninstallers or installed applications.
    /// Matching findings are always set aside, whatever the ignore settings. The list is part of the program
    /// (IgnoreLists\CleanSuite.txt, embedded), so it protects every computer, with or without the original program and its
    /// Data\Ignore_Reg lists; without it a scan of a typical Windows 11 installation reports thousands of servicing,
    /// package and indexer entries.
    /// </summary>
    public static readonly IReadOnlyList<string> Protective = LoadProtective();

    /// <summary>The user's own ignore lists: %LocalAppData%\CleanSuite\Ignore_Reg\*.txt, one word per line.</summary>
    public static string UserFolder =>
        CleanSuite.Safety.CleanSuitePaths.IgnoreReg;

    /// <summary>
    /// The ignore folders a scan uses by default: the original's Data\Ignore_Reg when that program is installed (read
    /// only), then CleanSuite's user folder; only the ones that exist.
    /// </summary>
    public static IReadOnlyList<string> DefaultFolders(string? originalIgnoreFolder) =>
        new[] { originalIgnoreFolder, UserFolder }.Where(f => f != null && Directory.Exists(f)).Select(f => f!).ToList();

    private static List<string> LoadProtective()
    {
        var list = new List<string>
        {
            @"\APPMODEL\STATEREPOSITORY\",          // packaged-app state cache
            @"\MICROSOFT\POWERSHELL\",               // PowerShell engine registration (points at files Windows moved to the GAC)
            @"\WINDOWSRUNTIME\ACTIVATABLECLASSID\",  // Windows Runtime class registrations of system components
        };
        using var stream = typeof(IgnoreWords).Assembly.GetManifestResourceStream("CleanSuite.RegistryClean.IgnoreLists.CleanSuite.txt")
            ?? throw new InvalidOperationException("the built-in registry protection list is missing from the program");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var w = StripComment(line);
            if (w.Length > 0 && !list.Contains(w)) list.Add(w);
        }
        return list;
    }

    /// <summary>Added in Standard mode only.</summary>
    public const string StandardOnlyWord = "LOGFILE";

    /// <summary>Areas whose findings are always checked against the words, even with the ignore list switched off.</summary>
    public static readonly IReadOnlyList<string> AlwaysChecked =
        [@"\MEDIA CENTER\", @"\FILEEXTS\DDECACHE", @"\FILEEXTS\DIRECTORY", @"\SHELL FOLDERS", @"\OFFICE15", @"\OFFICE16"];

    /// <summary>
    /// Keys that are never filtered by the words when "don't apply the ignore list to MRU/History lists" is on;
    /// value names containing "MRU" or "RECENTFILE" are treated the same way.
    /// </summary>
    public static readonly IReadOnlyList<string> MruKeys =
    [
        @"SOFTWARE\CLASSES\APPLICATIONS", @"\MRU", @"\FILE MRU", "MRULIST", @"\RECENT", @"\RUNMRU", @"\EXPLORER\FILEEXTS",
        @"\EXPLORER\MENUORDER", @"\EXPLORER\COMDLG32", @"\MUICACHE",
    ];

    private readonly List<string> _words = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Words => _words;

    /// <summary>Adds a word (upper-cased, duplicates ignored case-insensitively, as the original).</summary>
    public void Add(string word)
    {
        var w = word.Trim().ToUpperInvariant();
        if (w.Length > 0 && _seen.Add(w)) _words.Add(w);
    }

    /// <summary>The first word contained in the composite text, or null.</summary>
    public string? Match(string compositeUpper)
    {
        foreach (var w in _words)
            if (compositeUpper.Contains(w, StringComparison.Ordinal)) return w;
        return null;
    }

    /// <summary>
    /// Loads the words like the original: the built-in words; "LOGFILE" in Standard mode; every line of every file in
    /// each folder except "*.dat" (and, in Thorough mode, "StandardScanOnly*"); and the [data] entries listed in
    /// [settings] index= of every enabled "*.dat" list. Comments after "//" are removed.
    /// </summary>
    /// <param name="datEnabled">Whether a .dat list (by file name without extension) is enabled; default enabled (IgnoreListReg\name = 1).</param>
    public static IgnoreWords Load(IEnumerable<string> folders, bool thorough, Func<string, bool>? datEnabled = null)
    {
        var words = new IgnoreWords();
        foreach (var w in BuiltIn) words.Add(w);
        if (!thorough) words.Add(StandardOnlyWord);
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            var files = Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) continue;
                if (thorough && name.StartsWith("StandardScanOnly", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var line in ReadLines(f)) words.Add(StripComment(line));
            }
            foreach (var f in files.Where(f => f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)))
            {
                if (datEnabled != null && !datEnabled(Path.GetFileNameWithoutExtension(f))) continue;
                foreach (var w in DatWords(f)) words.Add(w);
            }
        }
        return words;
    }

    /// <summary>Line processing of the original: trim, upper-case, cut at "//", trim.</summary>
    internal static string StripComment(string line)
    {
        var s = line.Trim().ToUpperInvariant();
        int c = s.IndexOf("//", StringComparison.Ordinal);
        if (c >= 0) s = s[..c];
        return s.Trim();
    }

    private static IEnumerable<string> ReadLines(string file)
    {
        try { return File.ReadAllLines(file, DetectEncoding(file)); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static Encoding DetectEncoding(string file)
    {
        using var fs = File.OpenRead(file);
        var bom = new byte[2];
        int n = fs.Read(bom, 0, 2);
        if (n == 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        return Encoding.Latin1;
    }

    /// <summary>The words of a .dat list: [settings] index= tokens (separated by spaces or commas), each looked up in [data].</summary>
    internal static IEnumerable<string> DatWords(string file)
    {
        var index = Profile(file, "settings", "index");
        if (index == null) yield break;
        foreach (var token in index.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var v = Profile(file, "data", token);
            if (v == null) continue;
            var w = StripComment(v);
            if (w.Length > 0) yield return w;
        }
    }

    private static string? Profile(string file, string section, string key)
    {
        var buf = new char[0x208];
        int n = (int)GetPrivateProfileStringW(section, key, "null", buf, (uint)buf.Length, file);
        var s = new string(buf, 0, n);
        return string.Equals(s, "null", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetPrivateProfileStringW(string section, string key, string def, [Out] char[] buf, uint size, string file);
}
