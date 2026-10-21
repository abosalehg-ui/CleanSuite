using System.Collections;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.RegistryClean;

public sealed class RegCleanResult
{
    public int Removed { get; init; }
    /// <summary>Findings left alone because the entry changed or disappeared after the scan.</summary>
    public IReadOnlyList<RegFinding> Skipped { get; init; } = Array.Empty<RegFinding>();
    public IReadOnlyList<(RegFinding Finding, string Reason)> Failed { get; init; } = Array.Empty<(RegFinding, string)>();
    public required string JournalPath { get; init; }
    /// <summary>The .reg backup of everything removed (null when nothing was removed). Double-click or <see cref="RegistryCleaner.Restore"/> to undo.</summary>
    public string? BackupPath { get; init; }
}

/// <summary>
/// Removes reviewed findings: every key or value is exported to a REGEDIT5
/// .reg backup first, then deleted, and each operation is journaled. No backup, no change. An entry whose data changed
/// since the scan is skipped.
/// </summary>
public sealed class RegistryCleaner
{
    private readonly IRegistryRoots _roots;
    private readonly string? _journalBase;
    private readonly string? _backupFolder;

    public RegistryCleaner(IRegistryRoots? roots = null, string? journalBase = null, string? backupFolder = null)
    {
        _roots = roots ?? WindowsRegistryRoots.Instance;
        _journalBase = journalBase;
        _backupFolder = backupFolder;
    }

    public RegCleanResult Clean(IReadOnlyList<RegFinding> findings, CancellationToken ct = default)
    {
        using var journal = new OperationJournal("registry", _journalBase);
        using var backup = new RegBackupWriter(_backupFolder);
        int removed = 0;
        var skipped = new List<RegFinding>();
        var failed = new List<(RegFinding, string)>();
        // Values before keys, deepest keys first, so a key removal never hides a value removal of the same run.
        foreach (var f in findings.OrderBy(f => f.Target).ThenByDescending(f => f.Key.Length))
        {
            ct.ThrowIfCancellationRequested();
            var root = _roots.Root(f.Hive);
            try
            {
                if (f.Target == RegTargetKind.Value)
                {
                    using var key = root.OpenSubKey(f.Key, writable: false);
                    if (key == null || !key.GetValueNames().Contains(f.ValueName, StringComparer.OrdinalIgnoreCase)
                        || (f.RawValue != null && !SameValue(f.RawValue, key.GetValue(f.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames))))
                    {
                        skipped.Add(f);
                        continue;
                    }
                    if (!backup.ExportValue(root, f.Hive, f.Key, f.ValueName)) { skipped.Add(f); continue; }
                    using (var w = root.OpenSubKey(f.Key, writable: true)) w?.DeleteValue(f.ValueName, throwOnMissingValue: false);
                    journal.Record("reg-delete-value", f.KeyDisplay + " :: " + (f.ValueName.Length == 0 ? "(default)" : f.ValueName), true, backup.FilePath);
                }
                else
                {
                    if (f.Key.Length == 0 || !f.Key.Contains('\\') && f.Hive != RegHive.ClassesRoot)
                    {
                        failed.Add((f, "refusing to delete a top-level key"));
                        continue;
                    }
                    // The key must still be what was scanned: same subkeys, values and last write time.
                    if (f.KeyStamp != null && KeyStamp.Of(root, f.Key) != f.KeyStamp) { skipped.Add(f); continue; }
                    if (!backup.ExportKey(root, f.Hive, f.Key)) { skipped.Add(f); continue; }
                    root.DeleteSubKeyTree(f.Key, throwOnMissingSubKey: false);
                    journal.Record("reg-delete-key", f.KeyDisplay, true, backup.FilePath);
                }
                removed++;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
            {
                var msg = e is UnauthorizedAccessException or System.Security.SecurityException ? "requires administrator rights" : e.Message;
                journal.Record(f.Target == RegTargetKind.Key ? "reg-delete-key" : "reg-delete-value", f.KeyDisplay, false, backup.FilePath, msg);
                failed.Add((f, msg));
            }
        }
        return new RegCleanResult { Removed = removed, Skipped = skipped, Failed = failed, JournalPath = journal.FilePath, BackupPath = backup.FilePath };
    }

    /// <summary>Restores a backup written by <see cref="Clean"/> (or by the original's .reg backups); returns the number of values written.</summary>
    public int Restore(string regFile) => RegFileImporter.Import(regFile, _roots);

    private static bool SameValue(object a, object? b) => (a, b) switch
    {
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        (string[] x, string[] y) => x.SequenceEqual(y),
        _ => Equals(a, b),
    };
}
