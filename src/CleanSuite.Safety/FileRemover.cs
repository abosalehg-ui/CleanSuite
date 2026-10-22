namespace CleanSuite.Safety;

/// <summary>Value of Junk\DelJunkToBin (and DelDupToBin): how removed files are kept.</summary>
public enum RemovalMode { Permanent = 0, RecycleBin = 1, ZipBackup = 2 }

/// <summary>A file chosen at scan time, with its size and last write time then (to tell whether it changed before removal).</summary>
public sealed record RemovalItem(string FullPath, long Size, DateTime LastWriteUtc);

public sealed class FileRemovalResult
{
    public int Deleted { get; init; }
    public long BytesFreed { get; init; }
    public IReadOnlyList<(string Path, string Reason)> Failed { get; init; } = Array.Empty<(string, string)>();
    /// <summary>The zip backup (ZipBackup mode), or null.</summary>
    public string? ZipPath { get; init; }
}

/// <summary>
/// The one removal pipeline of the file tools (junk files, duplicates): every item is checked again right before it is touched
/// (protected locations, also through links and short names; still there; not a link itself; unchanged since the scan), then
/// sent to the Recycle Bin, archived in a verified zip and deleted, or (when chosen) deleted. Every refusal and every change is
/// journaled.
/// </summary>
public sealed class FileRemover(IRecycleBin? recycleBin = null, ProtectedPaths? protectedPaths = null)
{
    private readonly IRecycleBin _bin = recycleBin ?? new ShellRecycleBin();
    private readonly ProtectedPaths _protected = protectedPaths ?? ProtectedPaths.ForCurrentSystem();

    /// <param name="backupPrefix">File-name prefix of the zip backup: "Jnk" or "Dup", as in the original.</param>
    /// <param name="silent">True when running unattended (the original adds FOF_SILENT|FOF_NOERRORUI in that case).</param>
    public FileRemovalResult Remove(IReadOnlyList<RemovalItem> items, RemovalMode mode, OperationJournal journal, string? backupFolder = null,
        string backupPrefix = "Jnk", bool silent = false, int batchSize = 200, CancellationToken ct = default)
    {
        var failed = new List<(string, string)>();
        var ready = new List<RemovalItem>();
        foreach (var item in items)
        {
            if (WhyNot(item) is { } why) Refuse(item.FullPath, why);
            else ready.Add(item);
        }

        int deleted = 0;
        long freed = 0;
        string? zipPath = null;
        switch (mode)
        {
            case RemovalMode.RecycleBin:
                foreach (var batch in ready.Chunk(Math.Max(1, batchSize)))
                {
                    ct.ThrowIfCancellationRequested();
                    var bySize = batch.ToDictionary(b => b.FullPath, b => b.Size, StringComparer.OrdinalIgnoreCase);
                    var (recycled, notRecycled) = RecycleStep.Run(_bin, batch.Select(b => b.FullPath).ToList(), journal, silent);
                    deleted += recycled.Count;
                    freed += recycled.Sum(p => bySize[p]);
                    failed.AddRange(notRecycled);
                }
                break;

            case RemovalMode.Permanent:
                foreach (var it in ready)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Delete(it, null)) { deleted++; freed += it.Size; }
                }
                break;

            case RemovalMode.ZipBackup:
                if (ready.Count == 0) break;
                zipPath = JunkBackupArchive.Create(backupFolder ?? CleanSuitePaths.Backups, ready, journal, out var archived, ct, backupPrefix);
                foreach (var it in archived)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Delete(it, zipPath)) { deleted++; freed += it.Size; }
                }
                foreach (var it in ready.Except(archived)) failed.Add((it.FullPath, "could not be archived"));
                break;
        }
        return new FileRemovalResult { Deleted = deleted, BytesFreed = freed, Failed = failed, ZipPath = zipPath };

        void Refuse(string path, string why)
        {
            failed.Add((path, why));
            journal.Record("skip", path, false, error: why);
        }

        bool Delete(RemovalItem it, string? backupRef)
        {
            // Checked once more right before the delete: the window between the archive (or the batch) and this file is closed as far as possible.
            if (_protected.CheckResolved(it.FullPath) is { } why) { Refuse(it.FullPath, why); return false; }
            if (TryDelete(it.FullPath, out var err)) { journal.Record("delete", it.FullPath, true, backupRef: backupRef); return true; }
            failed.Add((it.FullPath, err!));
            journal.Record("delete", it.FullPath, false, backupRef: backupRef, error: err);
            return false;
        }
    }

    /// <summary>Why an item must not be removed now, or null.</summary>
    private string? WhyNot(RemovalItem item)
    {
        if (_protected.CheckResolved(item.FullPath) is { } protectedWhy) return protectedWhy;
        var fi = new FileInfo(item.FullPath);
        if (!fi.Exists) return "no longer exists";
        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) return "reparse point";
        return FileIdentity.ChangedSince(fi, item.Size, item.LastWriteUtc);
    }

    private static bool TryDelete(string path, out string? error)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            error = File.Exists(path) ? "still exists" : null;
            return error == null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }
}

/// <summary>
/// Sending paths to the Recycle Bin, the same way everywhere: paths whose drive has no Recycle Bin are refused (Windows would
/// delete them for good), the rest are recycled, and only what is really gone afterwards counts. Every path is journaled.
/// </summary>
public static class RecycleStep
{
    public static (List<string> Recycled, List<(string Path, string Reason)> Failed) Run(IRecycleBin bin, IReadOnlyList<string> paths,
        OperationJournal journal, bool silent)
    {
        var recycled = new List<string>();
        var failed = new List<(string, string)>();
        var ready = new List<string>();
        foreach (var p in paths)
        {
            if (bin.WhyNotRecyclable(p) is { } why) { failed.Add((p, why)); journal.Record("skip", p, false, error: why); }
            else ready.Add(p);
        }
        if (ready.Count == 0) return (recycled, failed);
        var notRecycled = new HashSet<string>(bin.Recycle(ready, silent), StringComparer.OrdinalIgnoreCase);
        foreach (var p in ready)
        {
            bool gone = !notRecycled.Contains(p) && !File.Exists(p) && !Directory.Exists(p);
            journal.Record("recycle", p, gone, backupRef: gone ? "RecycleBin" : null, error: gone ? null : "could not be recycled");
            if (gone) recycled.Add(p); else failed.Add((p, "could not be recycled"));
        }
        return (recycled, failed);
    }
}
