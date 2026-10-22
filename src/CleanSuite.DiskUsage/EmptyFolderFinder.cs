using CleanSuite.Matching;
using CleanSuite.Safety;

namespace CleanSuite.DiskUsage;

/// <summary>
/// Find Empty Folders: folders that contain no files at any depth. Hidden or
/// system folders are skipped (except chkdsk's "FOUND.???"), as are read-only folders, reparse points, paths of 261
/// characters or more, excluded patterns and protected system locations. A reported folder is not searched further,
/// so a tree of empty folders is reported once, at its top. Read-only; removal goes to the Recycle Bin.
/// </summary>
public static class EmptyFolderFinder
{
    /// <summary>
    /// Where the Disk page never looks for empty folders: Windows, programs and their data (installations keep empty
    /// plug-in, cache and configuration folders), the Recycle Bin and recovery areas, synchronised cloud folders, and the
    /// desktop (an empty folder there was made by the user).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExcludes =
    [
        @"?:\Windows*", @"?:\Program Files*", @"?:\ProgramData*", @"*\AppData*", @"?:\$Recycle.Bin*", @"?:\Recycler*",
        @"?:\System Volume Information*", @"?:\Recovery*", @"?:\Boot*", @"?:\MSOCache*", @"*\OneDrive*", @"?:\Users\*\Desktop\*",
        @"?:\Users\Public\Desktop*",
    ];

    public static IReadOnlyList<string> Find(string root, IEnumerable<string>? excludes = null, ProtectedPaths? protectedPaths = null,
        Action<int>? progress = null, CancellationToken ct = default)
    {
        var ex = (excludes ?? Array.Empty<string>()).Where(p => p.Length > 0).ToList();
        protectedPaths ??= ProtectedPaths.ForCurrentSystem();
        var found = new List<string>();
        int seen = 0;
        var start = root.Length == 2 && root[1] == ':' ? root + "\\" : root;

        // The shared walk (links never followed). A folder is entered unless excluded, already reported as empty (a tree of empty
        // folders is reported once, at its top), or hidden, system or read-only (chkdsk's "FOUND.???" excepted).
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var top = start.TrimEnd('\\');
        SafeWalk.Walk(top, dir =>
        {
            if (skip.Contains(dir) || ex.Any(p => AceWildcard.IsMatch(dir, p))) return false;
            if (dir.Equals(top, StringComparison.OrdinalIgnoreCase) || !IsEmptyTree(dir)) return true;
            if (protectedPaths.CheckResolved(dir, true) == null) found.Add(dir);
            return false;
        }, (dir, entries) =>
        {
            foreach (var e in entries)
            {
                var a = e.Attributes;
                if ((a & FileAttributes.Directory) == 0 || SafeWalk.IsLink(a)) continue;
                bool chkdsk = AceWildcard.IsMatch(e.Name, "FOUND.???");
                if (!chkdsk && (a & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly)) != 0) skip.Add(dir + "\\" + e.Name);
                else if (++seen % 500 == 0) progress?.Invoke(seen);
            }
        }, ct: ct);
        progress?.Invoke(seen);
        return found;
    }

    /// <summary>True when the folder and every folder below it contain no files (unreadable folders are never "empty").</summary>
    public static bool IsEmptyTree(string dir)
    {
        try
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                if ((e.Attributes & FileAttributes.Directory) == 0) return false;
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) return false;           // a link inside: leave it alone
                if (!IsEmptyTree(e.FullName)) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}
