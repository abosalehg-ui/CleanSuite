using Microsoft.Win32;

namespace CleanSuite.Shortcuts;

/// <summary>
/// Read-only import of the original's shortcut settings (HKCU\Software\Acelogix\Ace Utilities\Settings:
/// ScanOnlyStartMenu, ScanEmptyStartMenuFolders, DriveShortCuts) and its ignore folder (&lt;install&gt;\Data\Ignore_Lnk).
/// Opens keys with read access only; never writes.
/// </summary>
public static class AceShortcutSettings
{
    public const string Root = @"Software\Acelogix\Ace Utilities";

    /// <summary>The options as the original would scan, or null when it is not installed for this user.</summary>
    public static ShortcutScanOptions? Read(RegistryKey? hkcu = null)
    {
        using var root = (hkcu ?? Registry.CurrentUser).OpenSubKey(Root, writable: false);
        if (root == null) return null;
        using var s = root.OpenSubKey("Settings", writable: false);
        int startMenu = s?.GetValue("ScanOnlyStartMenu") is int a ? a : 1;
        int empty = s?.GetValue("ScanEmptyStartMenuFolders") is int b ? b : 0;
        var drives = s?.GetValue("DriveShortCuts") is int mask ? DrivesFromMask(unchecked((uint)mask)) : ShortcutScanOptions.FixedDrives();
        var install = root.GetValue(null) as string;
        var ignore = install != null ? Path.Combine(install, "Data", "Ignore_Lnk") : null;
        return new ShortcutScanOptions
        {
            StartMenuMode = startMenu != 0,
            EmptyStartMenuFolders = empty != 0,
            Drives = drives,
            IgnoreFolders = ignore != null && Directory.Exists(ignore) ? [ignore] : Array.Empty<string>(),
        };
    }

    /// <summary>Bit 0 = A:, bit 25 = Z:.</summary>
    public static IReadOnlyList<string> DrivesFromMask(uint mask) =>
        Enumerable.Range(0, 26).Where(i => (mask & (1u << i)) != 0).Select(i => (char)('A' + i) + ":").ToList();
}
