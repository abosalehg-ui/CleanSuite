using CleanSuite.Safety;

namespace CleanSuite.Duplicates;

public sealed class DupRemovalResult
{
    public int Deleted { get; init; }
    public long BytesFreed { get; init; }
    public IReadOnlyList<(string Path, string Reason)> Failed { get; init; } = Array.Empty<(string, string)>();
    public string? ZipPath { get; init; }
    public required string JournalPath { get; init; }
}

/// <summary>
/// Removes checked duplicates: Recycle Bin (DelDupToBin 1, default) or zip backup then delete
/// (DelDupToBin 2, "Dup yyyy_MM_dd_hh_mm_ss_tt.zip", restorable like junk backups). Files that changed since the scan,
/// protected system locations and reparse points are skipped, and everything is journaled.
/// Safety beyond the original (which only warns): unless <paramref name="allowWholeGroups"/> is set, a file is removed
/// only when another member of its group is kept, still exists, is a different physical file (not the same file reached
/// through a subst or network drive or a hard link) and still has exactly the same content.
/// </summary>
public sealed class DuplicateRemover
{
    private readonly FileRemover _remover;
    private readonly string? _journalBase;

    public DuplicateRemover(IRecycleBin? recycleBin = null, ProtectedPaths? protectedPaths = null, string? journalBase = null)
    {
        _remover = new FileRemover(recycleBin, protectedPaths);
        _journalBase = journalBase;
    }

    /// <summary>Groups in which every remaining file is selected (the original's "All the files in %d group(s)…" warning).</summary>
    public static int WholeGroupsSelected(IEnumerable<DupGroup> groups, ISet<string> selected) =>
        groups.Count(g => g.Files.Count > 0 && g.Files.All(f => selected.Contains(f.FullPath)));

    public DupRemovalResult Remove(IReadOnlyList<DupGroup> groups, ISet<string> selected, RemovalMode mode, string? backupFolder = null,
        bool allowWholeGroups = false, CancellationToken ct = default)
    {
        var items = new List<RemovalItem>();
        var refused = new List<(string, string)>();
        foreach (var g in groups)
        {
            var chosen = g.Files.Where(f => selected.Contains(f.FullPath)).ToList();
            if (chosen.Count == 0) continue;
            var keepers = g.Files.Where(f => !selected.Contains(f.FullPath) && File.Exists(f.FullPath)).ToList();
            if (!allowWholeGroups && keepers.Count == 0)
            {
                refused.AddRange(chosen.Select(f => (f.FullPath, "no copy of this file would be kept")));
                continue;
            }
            foreach (var f in chosen)
            {
                if (!allowWholeGroups && !keepers.Any(k => !FileIdentity.Same(k.FullPath, f.FullPath) && FileIdentity.SameContent(k.FullPath, f.FullPath)))
                {
                    refused.Add((f.FullPath, "no other copy with the same content would be kept"));
                    continue;
                }
                items.Add(new RemovalItem(f.FullPath, f.Size, f.LastWriteUtc));
            }
        }
        using var journal = new OperationJournal("duplicates", _journalBase);
        foreach (var (path, why) in refused) journal.Record("skip", path, false, error: why);
        // Permanent deletion is never offered for duplicates: it is turned into the Recycle Bin.
        var res = _remover.Remove(items, mode == RemovalMode.Permanent ? RemovalMode.RecycleBin : mode, journal,
            backupFolder ?? CleanSuitePaths.Backups, "Dup", ct: ct);
        return new DupRemovalResult
        {
            Deleted = res.Deleted, BytesFreed = res.BytesFreed, Failed = refused.Concat(res.Failed).ToList(), ZipPath = res.ZipPath,
            JournalPath = journal.FilePath,
        };
    }
}
