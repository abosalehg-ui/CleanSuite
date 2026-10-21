using CleanSuite.Safety;

namespace CleanSuite.RegistryClean;

/// <summary>Scan categories with the original's bits (Settings\RegScanOption).</summary>
[Flags]
public enum RegCategory : uint
{
    None = 0,
    UserEntries = 0x1,
    AllUsersEntries = 0x2,
    StartMenu = 0x4,
    ObsoleteSoftware = 0x8,
    FileExtensions = 0x10,
    FileAssociations = 0x20,
    ActiveXCom = 0x40,
    SystemEntries = 0x80,
    ApplicationPaths = 0x100,
    AddRemoveEntries = 0x200,
    SharedDlls = 0x400,
    Fonts = 0x800,
    HelpFiles = 0x1000,
    IeContextMenu = 0x2000,
    IeToolbarButtons = 0x4000,
    BrowserHelperObjects = 0x8000,
    ShellExtensions = 0x10000,
    Sounds = 0x20000,
    HistoryLists = 0x40000,
    SharedFolders = 0x80000,
}

/// <summary>What deleting a finding removes.</summary>
public enum RegTargetKind { Value, Key }

/// <summary>One invalid registry entry. The scan only reads; nothing changes until a finding is cleaned.</summary>
public sealed record RegFinding
{
    public required RegCategory Category { get; init; }
    public required RegHive Hive { get; init; }
    /// <summary>Key path below the hive root.</summary>
    public required string Key { get; init; }
    /// <summary>Value name ("" = default value); for key findings, the value that made the key invalid (may be empty).</summary>
    public required string ValueName { get; init; }
    /// <summary>The data as shown (strings as read, DWORD as decimal, binary as "&lt;BINARY&gt;").</summary>
    public required string Data { get; init; }
    public required RegTargetKind Target { get; init; }
    /// <summary>Stable reason code (e.g. "InvalidPath"); <see cref="ReasonText"/> is the original's English text.</summary>
    public required string Reason { get; init; }
    public required string ReasonText { get; init; }
    /// <summary>The original's string-table id of the reason, when it has one (0xA172 Invalid Path, 0xA173 Invalid Entry…).</summary>
    public int ReasonId { get; init; }
    /// <summary>The ignore word that matched, when the finding was ignored.</summary>
    public string? IgnoredBy { get; init; }
    /// <summary>Raw registry kind and bytes at scan time, used to skip entries that changed before cleaning.</summary>
    public object? RawValue { get; init; }
    /// <summary>For key findings: <see cref="KeyStamp"/> at scan time, used to skip keys that changed before cleaning.</summary>
    public string? KeyStamp { get; init; }

    public string KeyDisplay => RegBackupWriter.HivePath(Hive) + "\\" + Key;
}

/// <summary>Scan settings (defaults = the original's defaults: SettingsReg 0xC, Standard, all categories but the IE ones).</summary>
public sealed record RegScanOptions
{
    public RegCategory Categories { get; init; } = DefaultCategories;
    /// <summary>SettingsReg bit0.</summary>
    public bool IncludeRemovable { get; init; }
    /// <summary>SettingsReg bit1.</summary>
    public bool IncludeCd { get; init; }
    /// <summary>SettingsReg bit2 "Use ignore list".</summary>
    public bool UseIgnoreList { get; init; } = true;
    /// <summary>SettingsReg bit3: MRU/History lists are not filtered by the ignore words.</summary>
    public bool NoIgnoreForMru { get; init; } = true;
    /// <summary>Settings\registryscanthorough = 1.</summary>
    public bool Thorough { get; init; }
    /// <summary>
    /// The original's expert mode: enables the shellex checks (with Thorough and the Shell Extensions category) and judges
    /// paths on drives that do not exist.
    /// </summary>
    public bool Expert { get; init; }
    /// <summary>Folders with ignore lists (the original's Data\Ignore_Reg); read only.</summary>
    public IReadOnlyList<string> IgnoreFolders { get; init; } = Array.Empty<string>();
    /// <summary>Disabled .dat ignore lists (IgnoreListReg\name = 0), by file name without extension.</summary>
    public IReadOnlySet<string> DisabledDatLists { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Internet Explorer categories (context menu, toolbar buttons, BHO) are excluded by the project's design decision
    /// (legacy IE support is not reimplemented). ActiveX &amp; COM is not implemented (its analysis was stopped by the
    /// automated safety system and not regenerated, by the user's instruction).
    /// </summary>
    public const RegCategory Unsupported =
        RegCategory.IeContextMenu | RegCategory.IeToolbarButtons | RegCategory.BrowserHelperObjects | RegCategory.ActiveXCom;

    public const RegCategory DefaultCategories = (RegCategory)0xFFFFF & ~Unsupported;

    public static RegScanOptions FromSettingsReg(uint settingsReg, uint regScanOption, bool thorough) => new()
    {
        Categories = (RegCategory)regScanOption & ~Unsupported,
        IncludeRemovable = (settingsReg & 1) != 0,
        IncludeCd = (settingsReg & 2) != 0,
        UseIgnoreList = (settingsReg & 4) != 0,
        NoIgnoreForMru = (settingsReg & 8) != 0,
        Thorough = thorough,
    };
}

/// <summary>Result of a scan: findings to review, and the ones the ignore words filtered out ("Entries with ignore words").</summary>
public sealed class RegScanResult
{
    public required IReadOnlyList<RegFinding> Findings { get; init; }
    public required IReadOnlyList<RegFinding> Ignored { get; init; }
    /// <summary>Keys that could not be read (access denied); reported instead of silently skipped.</summary>
    public required IReadOnlyList<string> Unreadable { get; init; }
}
