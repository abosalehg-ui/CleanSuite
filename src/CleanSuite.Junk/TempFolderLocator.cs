using CleanSuite.Matching;
using CleanSuite.Safety;

namespace CleanSuite.Junk;

/// <summary>
/// Temp folder discovery. Order and de-duplication follow the original. Safety beyond the original:
/// its temp-path rule is applied to every candidate, not only to GetTempPath, and drive roots, the Windows folder and user
/// profile folders are refused, so a TEMP variable pointing at e.g. D:\Downloads or C:\ never turns real files into junk; so is a
/// temp folder that is itself a junction or symbolic link.
/// </summary>
public static class TempFolderLocator
{
    public static IReadOnlyList<string> Locate(ISystemEnvironment env)
    {
        var profiles = env.GetUserProfileDirectories().Select(p => p.TrimEnd('\\')).ToList();
        var win = env.GetWindowsDirectory();
        var result = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.TrimEnd('\\');
            if (!IsSafeTempFolder(path, profiles, win)) return;
            if (SafeWalk.IsLink(path)) return;                     // a temp folder that is a link could lead anywhere: not cleaned
            if (!result.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) result.Add(path);
        }

        // 1. GetTempPath, accepted only if it looks like a temp folder and is not the Windows folder itself.
        Add(env.GetTempPath());

        // 2. %windir%\Temp
        if (!string.IsNullOrEmpty(win)) Add(Path.Combine(win.TrimEnd('\\'), "Temp"));

        // 3. Machine TMP/TEMP, 4. user TEMP/TMP (same order as the original).
        Add(env.GetRegistryEnvironment(true, "TMP"));
        Add(env.GetRegistryEnvironment(true, "TEMP"));
        Add(env.GetRegistryEnvironment(false, "TEMP"));
        Add(env.GetRegistryEnvironment(false, "TMP"));

        // 5. Every profile's local temp folder.
        foreach (var profile in env.GetUserProfileDirectories())
        {
            var p = Path.Combine(profile, "AppData", "Local", "Temp");
            if (Directory.Exists(p)) Add(p);
        }
        return result;
    }

    /// <summary>
    /// The original's rule plus: not a drive root, not the Windows folder, not a user profile folder and not a folder that
    /// contains a profile (such as C:\Users).
    /// </summary>
    public static bool IsSafeTempFolder(string path, IEnumerable<string> profiles, string? windowsDirectory)
    {
        path = path.TrimEnd('\\');
        if (path.Length <= 2 || !IsAcceptableTempPath(path)) return false;               // "C:" is a drive root
        if (windowsDirectory is { Length: > 0 } w && path.Equals(w.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var p in profiles)
            if (p.Equals(path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Safety rule from the original: reject "*\windows" and "*\winnt"; require "*temp*" or "*tmp*".</summary>
    public static bool IsAcceptableTempPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (AceWildcard.IsMatch(path, @"*\windows") || AceWildcard.IsMatch(path, @"*\winnt")) return false;
        return AceWildcard.IsMatch(path, "*temp*") || AceWildcard.IsMatch(path, "*tmp*");
    }
}
