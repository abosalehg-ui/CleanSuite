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

        void Walk(string dir, bool isRoot)
        {
            ct.ThrowIfCancellationRequested();
            if (dir.Length >= 0x105) return;
            if (ex.Any(p => AceWildcard.IsMatch(dir.TrimEnd('\\'), p))) return;
            if (!isRoot && IsEmptyTree(dir))
            {
                if (protectedPaths.Check(dir, true) == null) found.Add(dir);
                return;
            }
            IEnumerable<DirectoryInfo> subs;
            try { subs = new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return; }
            foreach (var s in subs)
            {
                var a = s.Attributes;
                if ((a & FileAttributes.ReparsePoint) != 0) continue;
                bool chkdsk = AceWildcard.IsMatch(s.Name, "FOUND.???");
                if (!chkdsk && (a & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly)) != 0) continue;
                if (++seen % 500 == 0) progress?.Invoke(seen);
                Walk(s.FullName, false);
            }
        }

        Walk(start, true);
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
