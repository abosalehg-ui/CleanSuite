using CleanSuite.Safety;

namespace CleanSuite.Junk;

public sealed class JunkRemovalRequest
{
    public required IReadOnlyList<JunkItem> Items { get; init; }
    public RemovalMode Mode { get; init; } = RemovalMode.RecycleBin;
    /// <summary>Folder for "Jnk *.zip" backups (ZipBackup mode).</summary>
    public string BackupFolder { get; init; } = CleanSuitePaths.Backups;
    /// <summary>File-name prefix of the zip backup: "Jnk" (junk files) or "Dup" (duplicate files), as in the original.</summary>
    public string BackupPrefix { get; init; } = "Jnk";
    /// <summary>Temp folders whose empty subfolders are removed afterwards (SettingsJnk bit3).</summary>
    public IReadOnlyList<string> TempFolders { get; init; } = Array.Empty<string>();
    public bool DeleteEmptyTempFolders { get; init; }
    /// <summary>True when running unattended (original adds FOF_SILENT|FOF_NOERRORUI in that case).</summary>
    public bool Silent { get; init; }
    public int BatchSize { get; init; } = 200;
}

public sealed class JunkRemovalResult
{
    public int Deleted { get; init; }
    public long BytesFreed { get; init; }
    public IReadOnlyList<(string Path, string Reason)> Failed { get; init; } = Array.Empty<(string, string)>();
    public int EmptyFoldersRemoved { get; init; }
    public string? ZipPath { get; init; }
    public required string JournalPath { get; init; }
}

/// <summary>
/// Removes junk items (behaviour of the original) through the shared <see cref="FileRemover"/>: every operation is journaled,
/// protected locations are refused (also when reached through a link), and an item is only removed if it is still the same file
/// that was scanned. Afterwards the empty subfolders of the temp folders are removed when that option is on.
/// </summary>
public sealed class JunkRemover
{
    private readonly FileRemover _remover;
    private readonly ProtectedPaths _protected;
    private readonly string? _journalBase;

    public JunkRemover(IRecycleBin? recycleBin = null, ProtectedPaths? protectedPaths = null, string? journalBase = null)
    {
        _protected = protectedPaths ?? ProtectedPaths.ForCurrentSystem();
        _remover = new FileRemover(recycleBin, _protected);
        _journalBase = journalBase;
    }

    public JunkRemovalResult Remove(JunkRemovalRequest req, CancellationToken ct = default)
    {
        using var journal = new OperationJournal("junk", _journalBase);
        var items = req.Items.Select(i => new RemovalItem(i.FullPath, i.Size, i.LastWriteUtc)).ToList();
        var res = _remover.Remove(items, req.Mode, journal, req.BackupFolder, req.BackupPrefix, req.Silent, req.BatchSize, ct);

        int emptyRemoved = 0;
        if (req.DeleteEmptyTempFolders)
            foreach (var t in req.TempFolders)
                emptyRemoved += RemoveEmptySubfolders(t, journal, ct);

        return new JunkRemovalResult
        {
            Deleted = res.Deleted, BytesFreed = res.BytesFreed, Failed = res.Failed, EmptyFoldersRemoved = emptyRemoved,
            ZipPath = res.ZipPath, JournalPath = journal.FilePath,
        };
    }

    /// <summary>
    /// Removes empty subfolders of a temp folder bottom-up; never the temp folder itself, never through links, never a folder
    /// whose real location is protected.
    /// </summary>
    private int RemoveEmptySubfolders(string root, OperationJournal journal, CancellationToken ct)
    {
        root = root.TrimEnd('\\');
        if (!Directory.Exists(root) || SafeWalk.IsLink(root)) return 0;
        // Folders in visiting order (parents before children); removed in reverse, so children go first.
        var folders = new List<string>();
        SafeWalk.Walk(root, _ => true, (dir, _) => { if (!dir.Equals(root, StringComparison.OrdinalIgnoreCase)) folders.Add(dir); }, ct: ct);
        int removed = 0;
        for (int i = folders.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var dir = folders[i];
            if (_protected.CheckResolved(dir, true) != null) continue;
            try
            {
                if (SafeWalk.IsLink(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                Directory.Delete(dir, false);
                removed++;
                journal.Record("rmdir", dir, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { journal.Record("rmdir", dir, false, error: e.Message); }
        }
        return removed;
    }
}
