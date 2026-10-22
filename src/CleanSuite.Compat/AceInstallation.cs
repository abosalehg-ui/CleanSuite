using Microsoft.Win32;

namespace CleanSuite.Compat;

/// <summary>Locates an existing installation of the original program (read-only) to reuse its data files.</summary>
public static class AceInstallation
{
    public static string? Locate()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ace Utilities_is1", false);
            if (key?.GetValue("InstallLocation") is string p && Directory.Exists(p)) return p.TrimEnd('\\');
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ace Utilities");
        return Directory.Exists(fallback) ? fallback : null;
    }

    public static string? JunkIgnoreFolder(string? installDir)
    {
        if (installDir == null) return null;
        var p = Path.Combine(installDir, "Data", "Ignore_Junk");
        return Directory.Exists(p) ? p : null;
    }

    /// <summary>CleanSuite's own junk ignore folder (%LocalAppData%\CleanSuite\Ignore_Junk).</summary>
    public static string OwnJunkIgnoreFolder =>
        CleanSuite.Safety.CleanSuitePaths.IgnoreJunk;

    /// <summary>The junk ignore folders that exist, as the Junk tab uses them: the original's Data\Ignore_Junk, then CleanSuite's.</summary>
    public static IReadOnlyList<string> DefaultJunkIgnoreFolders()
    {
        var list = new List<string>();
        if (JunkIgnoreFolder(Locate()) is { } ace) list.Add(ace);
        if (Directory.Exists(OwnJunkIgnoreFolder)) list.Add(OwnJunkIgnoreFolder);
        return list;
    }
}
