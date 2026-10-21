namespace CleanSuite.Junk;

/// <summary>Built-in defaults recovered from the original program.</summary>
public static class JunkDefaults
{
    /// <summary>Default value of Junk\SettingsJnk: Temp + IgnoreList + EmptyTempFolders.</summary>
    public const uint SettingsJnk = 0xD;

    /// <summary>(mask, category, inDefaultProfile). Masks not in the Default profile form the "Emergency" profile.</summary>
    public static readonly IReadOnlyList<(string Mask, string Category, bool InDefault)> Patterns = new[]
    {
        ("*.tmp", "Temp", true), ("*.temp", "Temp", true), ("*.chk", "Junk", true), ("*.old", "Backup", true),
        ("*.gid", "Junk", true), ("*.nch", "Junk", false), ("*.wbk", "Junk", false), ("*.fts", "Junk", true),
        ("*.ftg", "Junk", false), ("*.$$$", "Junk", true), ("*log.txt", "Log", false), ("*.err", "Junk", false),
        ("*.---", "Junk", true), ("*.~??", "Junk", false), ("~*.???", "Junk", false), ("~*.", "Junk", false),
        ("*.~*", "Junk", false), ("*.db.bak", "Junk", false), ("*.bk", "Junk", false), ("*.tmp*", "Junk", false),
        ("*.??$", "Junk", true), ("*.___", "Junk", true), ("*.log", "Log", false), ("*.~mp", "Junk", true),
        ("*._mp", "Junk", true), ("*.dmp", "Junk", true), ("*.prv", "Backup", true), ("*.sik", "Junk", false),
        ("*.bak", "Backup", false), ("thumbs.db", "Junk", false), ("thumbcache*.db", "Junk", true), ("CHKLIST.MS", "Junk", false),
        ("*.ilk", "Junk", false), ("*.aps", "Junk", false), ("*.ncb", "Junk", false), ("*.pch", "Junk", false),
        ("*.$db", "Junk", true), ("*.?$?", "Junk", false), ("*.??~", "Junk", true), ("*.?~?", "Junk", false),
        ("*.db$", "Junk", true), ("*.^", "Junk", false), ("*._dd", "Junk", false), ("*._detmp", "Junk", false),
        ("0*.nch", "Junk", false), ("chklist.*", "Junk", true),
    };

    /// <summary>Default folder exclusions written when Junk\Exclude is missing (all enabled).</summary>
    public static readonly IReadOnlyList<string> Exclusions = new[]
    {
        @"?:\program files*\common files", @"?:\Program Files\uninstall Information", @"?:\win*\security",
        @"*\sendto", @"*\i386", @"*\Resources\themes", @"*\temporary internet files", @"*\application data\microsoft",
        @"*\appdata\*\microsoft", @"*\application data\aim", @"*\system32\catroot2", @"*\system32\usmt",
        @"*\system32\dtclog", @"*\norton antivirus\quarantine", @"*\temp\incredimail", @"?:\recycle?",
        @"?:\$recycle.bin", @"?:\_restore", @"?:\system volume information", @"*\symantec shared\virusdefs",
        @"*\paprport\data", @"*\chaos32", @"*\microsoft\money\*\webcache", @"*\microsoft\office\data",
        @"*\microsoft publisher\pagewiz", @"?:\windows\winsxs",
    };

    public static IEnumerable<string> DefaultProfileMasks => Patterns.Where(p => p.InDefault).Select(p => p.Mask);
    public static IEnumerable<string> EmergencyProfileMasks => Patterns.Where(p => !p.InDefault).Select(p => p.Mask);
}
