namespace CleanSuite.DiskUsage;

/// <summary>File categories of the original's Disk Analysis: first match wins.</summary>
public enum FileCategory { Music = 0, Video = 1, Archives = 2, Images = 3, Documents = 4, Others = 5 }

public static class FileCategories
{
    public const int Count = 6;

    private static readonly Dictionary<string, FileCategory> Map = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var tables = new (FileCategory, string)[]
        {
            (FileCategory.Music, ".snd .stm .aac .ram .aif .rmi .dss .m3u .m4a .au .ra .aifc .aiff .mid .midi .wav .wax .cda .wpl .wma .mp3 .amr .awb .ogg .flac .idi .xm .ape .apl"),
            (FileCategory.Video, ".ogm .mp2v .asf .asx .m1v .avs .mpeg .dvr-ms .qt .wm .mod .mov .mpv2 .mmm .mp2 .mp4 .mpa .mpe .mpg .divx .wmx .wmv .avi .mkv .flv .rm .3gp"),
            (FileCategory.Archives, ".rar .tgz .gz .jar .z .cab .zip .zipx .wmz .wsz .7z .ace .alz .arc .dar .ice .lzh .lha .lzx .sqx .bz2 .tbz2 .lzma .tlz .yz1"),
            (FileCategory.Images, ".bmp .emf .eps .xbm .dib .ani .rle .jpeg .tif .ai .tiff .pct .pds .jiff .pic .png .psd .jpe .jpg .ico .cgm .wmf .gif .cr2 .nef"),
            (FileCategory.Documents, ".latex .docm .docx .odc .dot .xml .xlb .xlc .xls .xsl .otf .rtf .potm .potx .tsv .js .ps .mdb .mht .pot .c .h .pps .ppt .ppsm .ppsx .pptm .pptx .mhtml .chm .css .cpp .wri .wpd .text .wtx .cxx .html .pdf .txt .htm .doc"),
        };
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cat, list) in tables)
            foreach (var ext in list.Split(' '))
                map.TryAdd(ext, cat);                                     // first table wins (.mp2 is Video only once)
        return map;
    }

    /// <summary>Category of a file name by its extension (with the leading dot, case-insensitive); no extension = Others.</summary>
    public static FileCategory Of(string fileName) =>
        Map.TryGetValue(Path.GetExtension(fileName), out var c) ? c : FileCategory.Others;
}

/// <summary>A file seen by the scan (logical size, as the original; allocation size is not computed).</summary>
public sealed record DiskFile(string Name, long Size, DateTime LastWriteUtc, FileAttributes Attributes, DiskFolder Parent)
{
    public string FullPath => Path.Join(Parent.FullPath, Name);
    public FileCategory Category => FileCategories.Of(Name);
}

/// <summary>A folder or drive root with recursive totals (size, files, subfolders, per-category size and count).</summary>
public sealed class DiskFolder
{
    private readonly List<DiskFolder> _folders = new();
    private readonly List<DiskFile> _files = new();

    public DiskFolder(string name, string fullPath, DiskFolder? parent, DateTime lastWriteUtc, FileAttributes attributes)
    {
        Name = name;
        FullPath = fullPath;
        Parent = parent;
        LastWriteUtc = lastWriteUtc;
        Attributes = attributes;
    }

    public string Name { get; }
    public string FullPath { get; }
    public DiskFolder? Parent { get; }
    public DateTime LastWriteUtc { get; }
    public FileAttributes Attributes { get; }
    public bool IsRoot => Parent == null;
    /// <summary>The folder could not be listed (access denied); it counts as empty, as in the original.</summary>
    public bool Inaccessible { get; internal set; }

    public IReadOnlyList<DiskFolder> Folders => _folders;
    public IReadOnlyList<DiskFile> Files => _files;

    public long Size { get; private set; }
    /// <summary>Files in this folder and below.</summary>
    public long FileCount { get; private set; }
    /// <summary>Folders below this one.</summary>
    public long FolderCount { get; private set; }
    public long[] CategorySize { get; } = new long[FileCategories.Count];
    public long[] CategoryCount { get; } = new long[FileCategories.Count];

    /// <summary>Item(s) column: direct files + direct subfolders.</summary>
    public int ItemCount => _folders.Count + _files.Count;

    internal void Add(DiskFolder f) => _folders.Add(f);
    internal void Add(DiskFile f) => _files.Add(f);

    /// <summary>Recomputes the totals bottom-up (after a scan or a removal).</summary>
    internal void Total()
    {
        Size = 0; FileCount = 0; FolderCount = 0;
        Array.Clear(CategorySize); Array.Clear(CategoryCount);
        foreach (var f in _files)
        {
            Size += f.Size;
            FileCount++;
            CategorySize[(int)f.Category] += f.Size;
            CategoryCount[(int)f.Category]++;
        }
        foreach (var d in _folders)
        {
            d.Total();
            Size += d.Size;
            FileCount += d.FileCount;
            FolderCount += d.FolderCount + 1;
            for (int i = 0; i < FileCategories.Count; i++) { CategorySize[i] += d.CategorySize[i]; CategoryCount[i] += d.CategoryCount[i]; }
        }
    }

    internal bool Remove(DiskFolder f) => _folders.Remove(f);
    internal bool Remove(DiskFile f) => _files.Remove(f);

    /// <summary>Share of the parent's size (the original's "Percent": item / parent, 1.0 without a parent or when the parent is empty).</summary>
    public double ShareOfParent => Parent is { Size: > 0 } p ? (double)Size / p.Size : 1.0;

    public static double Share(long size, DiskFolder? parent) => parent is { Size: > 0 } p ? (double)size / p.Size : 1.0;

    public IEnumerable<DiskFile> AllFiles()
    {
        foreach (var f in _files) yield return f;
        foreach (var d in _folders)
            foreach (var f in d.AllFiles()) yield return f;
    }
}

/// <summary>Capacity of a drive (Overview tab; GetDiskFreeSpaceEx) and its kind (an unlabelled drive is named after it).</summary>
public sealed record DriveSpace(string Root, string Label, long Total, long Free, DriveType Type = DriveType.Fixed)
{
    public long Used => Total - Free;
    public int UsedPercent => Total > 0 ? (int)(Used * 100 / Total) : 0;
}
