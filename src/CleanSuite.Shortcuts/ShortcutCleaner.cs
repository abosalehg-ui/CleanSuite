using System.Globalization;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Shortcuts;

public sealed class ShortcutRunResult
{
    public int Succeeded { get; init; }
    public IReadOnlyList<(ShortcutItem Item, string Reason)> Failed { get; init; } = Array.Empty<(ShortcutItem, string)>();
    public required string JournalPath { get; init; }
}

/// <summary>
/// Removes reviewed items to the Recycle Bin (as the original, the original program SHFileOperation FO_DELETE with
/// FOF_ALLOWUNDO) and retargets single shortcuts. Every change is journaled; a shortcut is copied to the
/// backup folder before it is retargeted.
/// </summary>
public sealed class ShortcutCleaner
{
    private readonly IRecycleBin _bin;
    private readonly ProtectedPaths _protected;
    private readonly string? _journalBase;
    private readonly string _backupFolder;

    public ShortcutCleaner(IRecycleBin? recycleBin = null, ProtectedPaths? protectedPaths = null, string? journalBase = null,
        string? backupFolder = null)
    {
        _bin = recycleBin ?? new ShellRecycleBin();
        _protected = protectedPaths ?? ProtectedPaths.ForCurrentSystem();
        _journalBase = journalBase;
        _backupFolder = backupFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSuite", "Backups");
    }

    /// <summary>
    /// Sends the items to the Recycle Bin. Skipped: shortcuts that changed since the scan (or whose target came back),
    /// folders that are no longer empty, and protected system locations.
    /// </summary>
    public ShortcutRunResult Remove(IReadOnlyList<ShortcutItem> items, bool silent = true)
    {
        using var journal = new OperationJournal("shortcuts", _journalBase);
        var failed = new List<(ShortcutItem, string)>();
        var batch = new List<ShortcutItem>();
        foreach (var it in items)
        {
            var path = it.FullPath;
            var why = it.Kind == ShortcutItemKind.EmptyFolder ? CheckFolder(path) : CheckShortcut(it);
            why ??= _protected.Check(path, it.Kind == ShortcutItemKind.EmptyFolder);
            why ??= _bin.WhyNotRecyclable(path);
            if (why != null) { failed.Add((it, why)); journal.Record("recycle", path, false, error: why); continue; }
            batch.Add(it);
        }
        _bin.Recycle(batch.Select(b => b.FullPath).ToList(), silent);
        int ok = 0;
        foreach (var it in batch)
        {
            bool gone = !File.Exists(it.FullPath) && !Directory.Exists(it.FullPath);
            journal.Record("recycle", it.FullPath, gone, error: gone ? null : "could not be recycled");
            if (gone) ok++; else failed.Add((it, "could not be recycled"));
        }
        return new ShortcutRunResult { Succeeded = ok, Failed = failed, JournalPath = journal.FilePath };
    }

    private static string? CheckShortcut(ShortcutItem it)
    {
        var fi = new FileInfo(it.FullPath);
        if (!fi.Exists) return "no longer exists";
        if (it.LastWriteUtc != default && fi.LastWriteTimeUtc != it.LastWriteUtc) return "changed since the scan";
        return null;
    }

    private static string? CheckFolder(string path)
    {
        if (!Directory.Exists(path)) return "no longer exists";
        return ShortcutScanner.HasNoFiles(path) ? null : "no longer empty";
    }

    /// <summary>
    /// Points one shortcut at a new target. The shortcut is copied to "Backups\Shortcuts yyyy_MM_dd_hh_mm_ss_tt\" first;
    /// all its other fields are kept. Returns the backup copy's path.
    /// </summary>
    public string Retarget(string lnkPath, string newTarget)
    {
        using var journal = new OperationJournal("shortcuts", _journalBase);
        if (!File.Exists(lnkPath)) throw new FileNotFoundException("Shortcut not found", lnkPath);
        if (!File.Exists(newTarget) && !Directory.Exists(newTarget)) throw new FileNotFoundException("New target not found", newTarget);
        var folder = Path.Combine(_backupFolder, "Shortcuts " + BackupNames.Timestamp());
        Directory.CreateDirectory(folder);
        var backup = Path.Combine(folder, Path.GetFileName(lnkPath));
        for (int n = 1; File.Exists(backup); n++)
            backup = Path.Combine(folder, Path.GetFileNameWithoutExtension(lnkPath) + $"({n})" + Path.GetExtension(lnkPath));
        File.Copy(lnkPath, backup);
        try
        {
            ShellLinkFile.Retarget(lnkPath, newTarget);
            journal.Record("retarget", lnkPath + " -> " + newTarget, true, backupRef: backup);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException)
        {
            journal.Record("retarget", lnkPath + " -> " + newTarget, false, backupRef: backup, error: e.Message);
            throw;
        }
        return backup;
    }
}
