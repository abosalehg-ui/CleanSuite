namespace CleanSuite.Junk;

/// <summary>Result flags; values match the original result record.</summary>
[Flags]
public enum JunkReason
{
    None = 0,
    IgnoredShown = 0x1,
    NotIgnored = 0x2,
    ZeroLength = 0x4,
    JunkType = 0x8,
    TempFolder = 0x10,
    AlwaysInclude = 0x20,   // present in the original UI, never produced by 6.6.0
    Archivable = 0x40,
    CanOpen = 0x80,
    CanDelete = 0x100,
    EmptyTempFolder = 0x200,
}

/// <summary>Which ignore list applies: drive scan = General, Temp-folder scan = TempOnly ("/T" lines).</summary>
public enum ScanMode { Drive = 1, Temp = 2 }

/// <param name="Mask">Upper-cased mask with any "/S" suffix removed.</param>
/// <param name="HasBackslash">Mask contained '\' (path-aware match).</param>
/// <param name="FullPath">Mask had "/S": matched against the full path (subfolders included).</param>
public sealed record JunkPattern(string Mask, string Category, bool IsDefault, bool HasBackslash, bool FullPath);

public sealed record JunkItem(string FullPath, string Name, string Folder, long Size, JunkReason Flags, string Category, int Group)
{
    /// <summary>Size column as shown by the original: KB rounded up ("%lu KB").</summary>
    public long SizeKb => (Size >> 10) + ((Size & 0x3FF) != 0 ? 1 : 0);

    /// <summary>Last write time at scan time (UTC); with the size it tells whether the file changed before removal.</summary>
    public DateTime LastWriteUtc { get; init; }
}

public sealed class JunkOptions
{
    /// <summary>SettingsJnk bit0.</summary>
    public bool CleanTempFolders { get; init; } = true;
    /// <summary>SettingsJnk bit1.</summary>
    public bool FindZeroLength { get; init; }
    /// <summary>SettingsJnk bit2.</summary>
    public bool UseIgnoreList { get; init; } = true;
    /// <summary>SettingsJnk bit3.</summary>
    public bool DeleteEmptyTempFolders { get; init; } = true;
    /// <summary>Junk\ExcludeFilesModifiedLately.</summary>
    public bool ExcludeModifiedWithinWeek { get; init; }
    /// <summary>Expert mode: with UseIgnoreList off, ignored items are listed (flag 0x1) instead of dropped.</summary>
    public bool ExpertMode { get; init; }
    /// <summary>
    /// Extension, not in the original (off by default for compatibility): apply every ignore-list line inside
    /// Temp folders, not only lines marked "/T". Not part of SettingsJnk.
    /// </summary>
    public bool ApplyAllIgnoreListsInTemp { get; init; }

    public static JunkOptions FromSettingsJnk(uint settingsJnk, bool excludeRecent, bool expert = false) => new()
    {
        CleanTempFolders = (settingsJnk & 1) != 0,
        FindZeroLength = (settingsJnk & 2) != 0,
        UseIgnoreList = (settingsJnk & 4) != 0,
        DeleteEmptyTempFolders = (settingsJnk & 8) != 0,
        ExcludeModifiedWithinWeek = excludeRecent,
        ExpertMode = expert,
    };

    public uint ToSettingsJnk() =>
        (CleanTempFolders ? 1u : 0) | (FindZeroLength ? 2u : 0) | (UseIgnoreList ? 4u : 0) | (DeleteEmptyTempFolders ? 8u : 0);
}
