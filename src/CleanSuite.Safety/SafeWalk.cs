namespace CleanSuite.Safety;

/// <summary>
/// The folder walk every scanner shares: never through junctions, symbolic links or mount points (they could lead outside the
/// chosen folder, or into a loop), never into paths of <see cref="MaxPath"/> characters or more (the original's limit, and where
/// older programs stop being able to open them), unreadable folders reported instead of thrown, cancellation between folders,
/// and an explicit stack instead of recursion so a deep tree cannot overflow the call stack.
/// </summary>
public static class SafeWalk
{
    /// <summary>Folders whose full path is this long or longer are not entered.</summary>
    public const int MaxPath = 260;

    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
    };

    public static bool IsLink(FileAttributes a) => (a & FileAttributes.ReparsePoint) != 0;

    /// <summary>True when the path exists and is a junction, symbolic link or mount point.</summary>
    public static bool IsLink(string path)
    {
        try { return IsLink(File.GetAttributes(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>The entries of one folder, or null when it cannot be listed (access denied, gone, too long).</summary>
    public static List<FileSystemInfo>? List(string folder)
    {
        if (folder.Length >= MaxPath) return null;
        try
        {
            var dir = folder.Length == 2 && folder[1] == ':' ? folder + "\\" : folder;
            return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Options).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>
    /// Visits <paramref name="root"/> and every folder below it (depth-first, children in listing order). For each folder,
    /// <paramref name="enter"/> decides whether to list it (false prunes it and everything below); <paramref name="visit"/> gets
    /// its entries (links included, so callers can count or report them, but links are never entered). Folders that cannot be
    /// listed go to <paramref name="unreadable"/>.
    /// </summary>
    public static void Walk(string root, Func<string, bool> enter, Action<string, IReadOnlyList<FileSystemInfo>> visit,
        Action<string>? unreadable = null, CancellationToken ct = default)
    {
        var stack = new Stack<string>();
        stack.Push(root.TrimEnd('\\'));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (!enter(dir)) continue;
            var entries = List(dir);
            if (entries == null) { unreadable?.Invoke(dir); continue; }
            visit(dir, entries);
            for (int i = entries.Count - 1; i >= 0; i--)                     // pushed in reverse so they come out in listing order
            {
                var e = entries[i];
                if ((e.Attributes & FileAttributes.Directory) != 0 && !IsLink(e.Attributes)) stack.Push(dir + "\\" + e.Name);
            }
        }
    }
}
