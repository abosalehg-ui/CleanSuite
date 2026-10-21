using System.Diagnostics;
using System.Globalization;
using System.Text;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.DiskUsage;

/// <summary>
/// Disk Analysis: read-only walk that totals
/// folder sizes. Hidden and system files and folders are included; junctions, symbolic links and mount points are never
/// followed; paths of 260 characters or more and folders that cannot be listed count as empty.
/// </summary>
public static class DiskScanner
{
    public static DiskFolder Scan(string root, Action<long>? progress = null, CancellationToken ct = default)
    {
        var path = root.Length == 2 && root[1] == ':' ? root + "\\" : root;
        var info = new DirectoryInfo(path);
        var full = info.FullName;
        bool driveRoot = full.Length == 3 && full[1] == ':';
        var top = new DiskFolder(driveRoot ? full[..2] : info.Name, driveRoot ? full : full.TrimEnd('\\'), null, SafeTime(info), SafeAttributes(info));
        long files = 0;
        var clock = Stopwatch.StartNew();
        var stack = new Stack<DiskFolder>();
        stack.Push(top);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (dir.FullPath.Length >= 260) continue;
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir.FullPath).EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                dir.Inaccessible = true;
                continue;
            }
            foreach (var e in entries)
            {
                var a = e.Attributes;
                if ((a & FileAttributes.Directory) != 0)
                {
                    if ((a & FileAttributes.ReparsePoint) != 0) continue;                // never followed, not counted
                    var sub = new DiskFolder(e.Name, e.FullName, dir, e.LastWriteTimeUtc, a);
                    dir.Add(sub);
                    stack.Push(sub);
                }
                else
                {
                    long size;
                    try { size = ((FileInfo)e).Length; }
                    catch (IOException) { size = 0; }
                    dir.Add(new DiskFile(e.Name, size, e.LastWriteTimeUtc, a, dir));
                    files++;
                }
            }
            if (progress != null && clock.ElapsedMilliseconds >= 1000) { progress(files); clock.Restart(); }
        }
        top.Total();
        progress?.Invoke(files);
        return top;
    }

    private static DateTime SafeTime(DirectoryInfo d)
    {
        try { return d.LastWriteTimeUtc; } catch (IOException) { return default; }
    }

    private static FileAttributes SafeAttributes(DirectoryInfo d)
    {
        try { return d.Attributes; } catch (IOException) { return FileAttributes.Directory; }
    }

    /// <summary>Drives the original lists: removable (not floppy), fixed, CD, RAM disks that are ready, and network drives.</summary>
    public static IReadOnlyList<DriveSpace> Drives()
    {
        var list = new List<DriveSpace>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is DriveType.Unknown or DriveType.NoRootDirectory) continue;
            try
            {
                if (!d.IsReady) continue;
                list.Add(new DriveSpace(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.TotalFreeSpace));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    // ---------------------------------------------------------------- views

    /// <summary>"Top 100 files": the largest files below a folder, largest first; on equal sizes the later one first.</summary>
    public static IReadOnlyList<DiskFile> Largest(DiskFolder scope, int count = 100)
    {
        var top = new List<DiskFile>(count + 1);
        foreach (var f in scope.AllFiles())
        {
            if (top.Count == count && f.Size < top[^1].Size) continue;
            int i = top.FindIndex(x => x.Size <= f.Size);
            if (i < 0) i = top.Count;
            if (i >= count) continue;
            top.Insert(i, f);
            if (top.Count > count) top.RemoveAt(count);
        }
        return top;
    }

    public sealed record ExtensionStat(string Extension, long Count, long Size, FileCategory Category);

    /// <summary>File Types tab, extension list: one row per extension of the category below the folder.</summary>
    public static IReadOnlyList<ExtensionStat> Extensions(DiskFolder scope, FileCategory? category = null) =>
        scope.AllFiles()
            .Where(f => category == null || f.Category == category)
            .GroupBy(f => Path.GetExtension(f.Name).ToLowerInvariant())
            .Select(g => new ExtensionStat(g.Key, g.LongCount(), g.Sum(f => f.Size), FileCategories.Of("x" + g.Key)))
            .OrderByDescending(s => s.Size)
            .ToList();

    // ---------------------------------------------------------------- report (File > Report…)

    /// <summary>Plain-text report: date, fixed drives with capacity/used/free, then each chosen folder's subfolders with KB and counts.</summary>
    public static string Report(IEnumerable<DriveSpace> drives, IEnumerable<DiskFolder> folders, Func<long, string> size)
    {
        var sb = new StringBuilder();
        sb.Append("Report created on ").AppendLine(DateTime.Now.ToString("d MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture)).AppendLine();
        sb.AppendLine(new string('-', 103)).AppendLine(new string('-', 103)).AppendLine();
        foreach (var d in drives)
            sb.AppendLine($"{d.Root}\\ {d.Label} Capacity: {size(d.Total)}, Used: {size(d.Used)}, Free: {size(d.Free)}").AppendLine();
        foreach (var f in folders)
        {
            sb.AppendLine($"<Folder(s) of {f.FullPath}>");
            foreach (var c in f.Folders.OrderByDescending(c => c.Size))
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{c.Name} [{c.Size >> 10:N0} KB, {c.FileCount:N0} File(s), {c.FolderCount:N0} Folder(s)]"));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- delete

    /// <summary>Why an item may not be deleted, or null: drives, hidden+system items and Recycle Bin folders are refused.</summary>
    public static string? DeleteBlocked(string fullPath, FileAttributes attributes, bool isRoot)
    {
        if (isRoot || Path.GetPathRoot(fullPath)?.TrimEnd('\\').Equals(fullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true) return "drive";
        if ((attributes & FileAttributes.Hidden) != 0 && (attributes & FileAttributes.System) != 0) return "hidden system item";
        foreach (var p in new[] { @"?:\$recycle.bin*", @"?:\recycled*", @"?:\recycler*" })
            if (CleanSuite.Matching.AceWildcard.IsMatch(fullPath, p)) return "Recycle Bin folder";
        return null;
    }

    /// <summary>
    /// Sends items to the Recycle Bin (journaled) and removes them from the tree. Protected system locations
    /// (ProtectedPaths) are refused as well. Returns the paths that were removed.
    /// </summary>
    public static (IReadOnlyList<string> Removed, IReadOnlyList<(string Path, string Reason)> Failed) Recycle(
        IReadOnlyList<(string Path, FileAttributes Attributes, bool IsRoot)> items, IRecycleBin? bin = null, ProtectedPaths? protectedPaths = null,
        string? journalBase = null)
    {
        bin ??= new ShellRecycleBin();
        protectedPaths ??= ProtectedPaths.ForCurrentSystem();
        using var journal = new OperationJournal("diskanalysis", journalBase);
        var ok = new List<string>();
        var failed = new List<(string, string)>();
        var ready = new List<string>();
        foreach (var (path, attrs, isRoot) in items)
        {
            var why = DeleteBlocked(path, attrs, isRoot) ?? protectedPaths.Check(path, (attrs & FileAttributes.Directory) != 0);
            if (why != null) { failed.Add((path, why)); journal.Record("skip", path, false, error: why); continue; }
            if (!File.Exists(path) && !Directory.Exists(path)) { failed.Add((path, "no longer exists")); continue; }
            if (bin.WhyNotRecyclable(path) is { } noBin) { failed.Add((path, noBin)); journal.Record("skip", path, false, error: noBin); continue; }
            ready.Add(path);
        }
        if (ready.Count > 0)
        {
            var notRecycled = new HashSet<string>(bin.Recycle(ready, silent: false), StringComparer.OrdinalIgnoreCase);
            foreach (var p in ready)
            {
                bool gone = !notRecycled.Contains(p) && !File.Exists(p) && !Directory.Exists(p);
                journal.Record("recycle", p, gone, backupRef: gone ? "RecycleBin" : null, error: gone ? null : "not recycled");
                if (gone) ok.Add(p); else failed.Add((p, "could not be recycled"));
            }
        }
        return (ok, failed);
    }

    /// <summary>Removes deleted items from the scanned tree and recomputes the totals of the affected root.</summary>
    public static void Forget(DiskFolder root, IEnumerable<string> removedPaths)
    {
        var set = new HashSet<string>(removedPaths, StringComparer.OrdinalIgnoreCase);
        void Walk(DiskFolder d)
        {
            foreach (var f in d.Files.Where(f => set.Contains(f.FullPath)).ToList()) d.Remove(f);
            foreach (var s in d.Folders.ToList())
                if (set.Contains(s.FullPath)) d.Remove(s); else Walk(s);
        }
        Walk(root);
        root.Total();
    }
}
