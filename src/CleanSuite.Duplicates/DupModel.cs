namespace CleanSuite.Duplicates;

/// <summary>Content comparison (Dup\matchcrc): 1 = CRC-32, 2 = MD5 (default).</summary>
public enum ContentCompare { None = 0, Crc32 = 1, Md5 = 2 }

/// <summary>
/// Options of Find Duplicate Files. Defaults = the original's defaults: SettingsDup 0x2B
/// (same name, same size, ignore files under 1 MB, same content with MD5) and the "Default" file-type profile.
/// </summary>
public sealed record DupOptions
{
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();
    public bool SameName { get; init; } = true;
    public bool SameSize { get; init; } = true;
    /// <summary>Same modification time (UTC, minute precision).</summary>
    public bool SameDate { get; init; }
    public ContentCompare Content { get; init; } = ContentCompare.Md5;
    /// <summary>Files smaller than this are ignored (0 = no limit). Zero-byte files are never candidates.</summary>
    public long MinSize { get; init; } = 1L << 20;
    /// <summary>File-name patterns of the selected profile; a file must match one.</summary>
    public IReadOnlyList<string> Patterns { get; init; } = DupProfiles.Default;
    /// <summary>Folder patterns (Dup\ExcludeDup); each gets an implicit trailing "*".</summary>
    public IReadOnlyList<string> Excludes { get; init; } = DupProfiles.DefaultExcludes;
    /// <summary>File-name patterns never considered (Data\Ignore_Dup).</summary>
    public IReadOnlyList<string> Ignore { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The original's normalisation: content needs equal sizes; with no criterion at all, name + size + MD5 are used.
    /// </summary>
    public DupOptions Normalized()
    {
        var o = this;
        if (!o.SameName && !o.SameSize && o.Content == ContentCompare.None)
            o = o with { SameName = true, SameSize = true, Content = ContentCompare.Md5 };
        if (o.Content != ContentCompare.None && !o.SameSize) o = o with { SameSize = true };
        return o;
    }
}

public sealed record DupFile(string FullPath, long Size, DateTime LastWriteUtc)
{
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
}

public sealed record DupGroup(int Number, long TotalSize, IReadOnlyList<DupFile> Files)
{
    /// <summary>Bytes freed by keeping one copy.</summary>
    public long Reclaimable => TotalSize - Files.Min(f => f.Size);
}

public sealed class DupScanResult
{
    public required IReadOnlyList<DupGroup> Groups { get; init; }
    /// <summary>Files looked at (system files are not counted, as in the original).</summary>
    public int FilesSearched { get; init; }
    /// <summary>Files whose content could not be read; never reported as duplicates.</summary>
    public IReadOnlyList<string> Unreadable { get; init; } = Array.Empty<string>();
    public bool ContentVerified { get; init; }
}

/// <summary>Built-in file-type profiles and folder exclusions of the original.</summary>
public static class DupProfiles
{
    public static readonly IReadOnlyList<string> Default =
        ["*.exe", "*.mp3", "*.mp4", "*.zip", "*.mpg", "*.wav", "*.bmp", "*.jpg", "*.avi", "*.wma", "*.wmv", "*.ace", "*.rar", "*.iso"];

    public static readonly IReadOnlyList<string> AllFiles = ["*"];

    private static IReadOnlyList<string> Ext(string list) => list.Split(' ').Select(e => "*" + e).ToList();

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BuiltIn = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Default"] = Default,
        ["All Files (*.*)"] = AllFiles,
        ["Audio"] = Ext(".snd .stm .aac .ram .aif .rmi .dss .m3u .m4a .au .ra .aifc .aiff .mid .midi .wav .wax .cda .wpl .wma .mp3 .amr .awb .ogg .flac .idi .xm .ape .apl"),
        ["Video"] = Ext(".ogm .mp2v .asf .asx .m1v .avs .mpeg .dvr-ms .qt .wm .mod .mov .mpv2 .mmm .mp2 .mp4 .mpa .mpe .mpg .divx .wmx .wmv .avi .mkv .flv .rm"),
        ["Zip"] = Ext(".rar .tgz .gz .jar .z .cab .zip .wmz .wsz .7z .ace .alz .arc .dar .ice .lzh .lha .lzx .sqx .bz2 .tbz2 .lzma .tlz .yz1"),
        ["Image"] = Ext(".bmp .emf .eps .xbm .dib .ani .rle .jpeg .tif .ai .tiff .pct .pds .jiff .pic .png .psd .jpe .jpg .ico .cgm .wmf .gif .nef .cr2"),
        ["Document"] = Ext(".latex .docm .docx .odc .dot .xml .xlb .xlc .xls .xsl .otf .rtf .potm .potx .tsv .js .ps .mdb .mht .pot .c .h .pps .ppt .ppsm .ppsx .pptm .pptx .mhtml .chm .css .cpp .wri .wpd .text .wtx .cxx .html .pdf .txt .htm .doc"),
    };

    /// <summary>Folders the original excludes by default (seeded into Dup\ExcludeDup).</summary>
    public static readonly IReadOnlyList<string> DefaultExcludes =
    [
        @"?:\program files*\common files", @"?:\Program Files\Uninstall Information", @"?:\windows", @"?:\winnt", @"?:\recycle?",
        @"?:\$recycle.bin", @"*\sendto", @"*\i386", @"*\temporary internet files", @"*\*InetCache", @"*\application data",
        @"*\appdata", @"*\norton antivirus\quarantine", @"?:\Documents and Settings", @"*\Kaspersky Lab\*",
        @"*\Microsoft Silverlight\*", @"*\QuickTime\*", @"*\Microsoft SDKs\*", @"*\Windows Mobile*SDK*\*", @"*\Windows Sidebar\*",
        @"*\Microsoft Visual Studio*\*", @"*\Microsoft*\*", @"*\Adobe*\*", @"*\Reference Assemblies\*", @"?:\ProgramData\*",
        @"*\MSBuild\*", @"?:\program files*\*",
    ];

    /// <summary>Patterns of a profile: a built-in category, or a "|"-separated list (Dup\Profiles\&lt;name&gt;).</summary>
    public static IReadOnlyList<string> Parse(string list) =>
        list.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
