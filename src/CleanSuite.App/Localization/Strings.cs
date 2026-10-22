using System.Collections;
using System.Globalization;
using System.Resources;

namespace CleanSuite.App.Localization;

/// <summary>
/// String tables, read from Strings.resx (English, the neutral resources) and Strings.ar.resx (Arabic, a satellite assembly), so a
/// translator edits a resource file instead of C#. Every key must exist in both tables (checked by a unit test). Arabic counted
/// nouns use the four-form tokens "{0:ملف|ملفان|ملفات|ملفًا}" that <see cref="Loc.F"/> resolves.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Resources = new("CleanSuite.App.Localization.Strings", typeof(Strings).Assembly);

    public static readonly IReadOnlyDictionary<string, string> En = Load(CultureInfo.InvariantCulture);

    public static readonly IReadOnlyDictionary<string, string> Ar = Load(CultureInfo.GetCultureInfo("ar"));

    /// <summary>One language's table only (no fallback to English, so a missing Arabic text is found by the tests, not hidden).</summary>
    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new MissingManifestResourceException($"the {culture.Name} string table is missing from the program");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }

    /// <summary>Engine reason texts (JunkRemover, JunkBackupArchive, ProtectedPaths) → string keys. Longest prefixes first.</summary>
    public static readonly IReadOnlyList<(string Prefix, string Key)> ReasonPrefixes = new[]
    {
        ("original location unknown (relative name)", "R_UnknownRelative"),
        ("original location unknown", "R_Unknown"),
        ("protected location reached through a link or short name", "R_ProtectedLink"),
        ("contains a protected folder", "R_ContainsProtected"),
        ("protected system folder", "R_ProtectedSystem"),
        ("protected folder", "R_Protected"),
        ("no longer exists", "R_NoLongerExists"),
        ("reparse point", "R_ReparsePoint"),
        ("changed since scan", "R_Changed"),
        ("could not be recycled", "R_NotRecycled"),
        ("could not be archived", "R_NotArchived"),
        ("still exists", "R_StillExists"),
        ("drive root", "R_DriveRoot"),
        ("invalid path", "R_InvalidPath"),
        ("entry missing", "R_EntryMissing"),
        ("path contains '..'", "R_Traversal"),
        ("device path", "R_Device"),
        ("not a file name", "R_NotFile"),
        ("not a junk backup", "R_NotBackup"),
        ("requires administrator rights", "R_Admin"),
        ("no copy of this file would be kept", "R_NoCopyKept"),
        ("no other copy with the same content would be kept", "R_NoSameCopy"),
        ("entry no longer exists", "R_NoLongerExists"),
        ("network location without a Recycle Bin (not deleted)", "R_NoBinNetworkPath"),
        ("removable drive without a Recycle Bin (not deleted)", "R_NoBinRemovable"),
        ("network drive without a Recycle Bin (not deleted)", "R_NoBinNetwork"),
        ("CD/DVD drive (not deleted)", "R_NoBinCd"),
        ("drive not available (not deleted)", "R_DriveUnavailable"),
        ("drive without a Recycle Bin (not deleted)", "R_NoBin"),
        ("the Recycle Bin is turned off for this drive (not deleted)", "R_BinOff"),
        ("hidden system item", "R_HiddenSystem"),
        ("Recycle Bin folder", "R_BinFolder"),
        ("startup folder", "R_StartupFolder"),
        ("program folder (backup not made by CleanSuite)", "R_ProgramFolder"),
        ("no longer empty", "R_NotEmpty"),
        ("starts programs; not restored from a backup CleanSuite did not make", "R_AutoStart"),
        ("refusing to delete a top-level key", "R_TopLevelKey"),
        ("Explorer did not close", "R_ExplorerOpen"),
        ("backup failed", "R_BackupFailed"),
        ("windows installer", "R_WindowsInstaller"),
        ("not allowed", "R_NotAllowed"),
        ("no command", "R_NoCommand"),
        ("drive", "R_Drive"),
        ("exists", "R_Exists"),
    };
}
