using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Restore;

/// <summary>Kinds of backups the Restore tool handles.</summary>
public enum BackupKind { JunkFiles, DuplicateFiles, Registry, RegistryZip }

/// <param name="Created">Local time parsed from the "&lt;Prefix&gt; yyyy_MM_dd_hh_mm_ss_tt" name, or the file time.</param>
/// <param name="FromOriginal">Written by the original program (its backup comment, or found in its backup folder).</param>
public sealed record BackupItem(string Path, BackupKind Kind, DateTime Created, long Size, bool FromOriginal)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsRegistry => Kind is BackupKind.Registry or BackupKind.RegistryZip;
}

/// <summary>One item of an original registry zip backup: entry comment "1&lt;key&gt;" (key) or "2&lt;key&gt;\&lt;value&gt;" (value).</summary>
public sealed record RegistryZipItem(string EntryName, bool IsKey, string Item);

/// <summary>
/// Finds and restores backups. Names: "Jnk*.zip", "Dup*.zip", "Reg*.zip" and "*.reg" (case-insensitive), in
/// CleanSuite's backup folder and the original's default backup folder. Restoring never deletes the backup, never
/// overwrites an existing file, and only adds registry data back (deletion lines in .reg files are ignored).
/// </summary>
public static class BackupCatalog
{
    public static string CleanSuiteFolder => JunkRemovalRequest.DefaultBackupFolder;

    /// <summary>The original's default backup folder (%LOCALAPPDATA%\Acelogix\Ace Utilities\Backups); its settings are not read.</summary>
    public static string OriginalFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Acelogix", "Ace Utilities", "Backups");

    public static IReadOnlyList<string> DefaultFolders() => [CleanSuiteFolder, OriginalFolder];

    private static readonly Regex Stamp = new(@"(\d{4})_(\d{2})_(\d{2})_(\d{2})_(\d{2})_(\d{2})_(AM|PM)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<BackupItem> List(IEnumerable<string> folders)
    {
        var list = new List<BackupItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            bool originalFolder = string.Equals(System.IO.Path.GetFullPath(folder).TrimEnd('\\'), System.IO.Path.GetFullPath(OriginalFolder).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
            {
                if (!seen.Add(f)) continue;
                var name = System.IO.Path.GetFileName(f);
                BackupKind? kind =
                    name.EndsWith(".reg", StringComparison.OrdinalIgnoreCase) ? BackupKind.Registry
                    : !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? null
                    : name.StartsWith("jnk", StringComparison.OrdinalIgnoreCase) ? BackupKind.JunkFiles
                    : name.StartsWith("dup", StringComparison.OrdinalIgnoreCase) ? BackupKind.DuplicateFiles
                    : name.StartsWith("reg", StringComparison.OrdinalIgnoreCase) ? BackupKind.RegistryZip : null;
                if (kind == null) continue;
                var fi = new FileInfo(f);
                bool fromOriginal = originalFolder || (kind != BackupKind.Registry && SafeIsLegacy(f));
                list.Add(new BackupItem(f, kind.Value, CreatedFrom(name) ?? fi.LastWriteTime, fi.Length, fromOriginal));
            }
        }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    private static bool SafeIsLegacy(string zip)
    {
        try { return JunkBackupArchive.IsLegacyBackup(zip); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>"Reg 2026_10_21_03_07_09_PM.zip" → 2026-10-21 15:07:09 (12-hour clock, local time).</summary>
    public static DateTime? CreatedFrom(string fileName)
    {
        var m = Stamp.Match(fileName);
        if (!m.Success) return null;
        int I(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        int hour = I(4) % 12 + (m.Groups[7].Value.Equals("PM", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        try { return new DateTime(I(1), I(2), I(3), hour, I(5), I(6), DateTimeKind.Local); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ---------------------------------------------------------------- registry

    /// <summary>Items of an original registry zip backup (for review before restoring).</summary>
    public static IReadOnlyList<RegistryZipItem> RegistryZipItems(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Where(e => !e.FullName.EndsWith('/'))
            .Select(e => new RegistryZipItem(e.FullName, e.Comment.StartsWith('1'), e.Comment.Length > 1 ? e.Comment[1..] : e.FullName))
            .ToList();
    }

    public sealed record RegistryRestoreResult(int Values, int Fragments, IReadOnlyList<string> Errors, string JournalPath);

    /// <summary>
    /// Imports a .reg backup, or every .reg fragment of an original registry zip backup (the original uses
    /// "regedit /s"; CleanSuite imports in-process with its own parser). Journaled.
    /// </summary>
    public static RegistryRestoreResult RestoreRegistry(BackupItem backup, IRegistryRoots? roots = null, string? journalBase = null)
    {
        roots ??= WindowsRegistryRoots.Instance;
        using var journal = new OperationJournal("restore", journalBase);
        var errors = new List<string>();
        int values = 0, fragments = 0;
        void Import(string regFile, string label)
        {
            try
            {
                values += RegFileImporter.Import(regFile, roots);
                fragments++;
                journal.Record("reg-import", label, true, backupRef: backup.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException
                                          or FormatException or ArgumentException or IndexOutOfRangeException)
            {
                var msg = e is UnauthorizedAccessException or System.Security.SecurityException ? "requires administrator rights" : e.Message;
                errors.Add(label + ": " + msg);
                journal.Record("reg-import", label, false, backupRef: backup.Path, error: msg);
            }
        }

        if (backup.Kind == BackupKind.Registry) Import(backup.Path, backup.Name);
        else if (backup.Kind == BackupKind.RegistryZip)
        {
            var temp = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CleanSuite", "restore-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                using var zip = ZipFile.OpenRead(backup.Path);
                int n = 0;
                foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
                {
                    var tmp = System.IO.Path.Combine(temp, (n++).ToString(CultureInfo.InvariantCulture) + ".reg");
                    e.ExtractToFile(tmp);
                    Import(tmp, e.Comment.Length > 1 ? e.Comment[1..] : e.FullName);
                }
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        else throw new ArgumentException("not a registry backup", nameof(backup));
        return new RegistryRestoreResult(values, fragments, errors, journal.FilePath);
    }

    /// <summary>Whether CleanSuite wrote this backup and it is unchanged (<see cref="BackupLedger"/>).</summary>
    public static BackupTrust Trust(BackupItem backup) => BackupLedger.Status(backup.Path);

    /// <summary>The keys a registry backup would write (deletion sections are ignored on restore and not listed).</summary>
    public static IReadOnlyList<string> RegistryKeys(BackupItem backup)
    {
        var keys = new List<string>();
        void Scan(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && !line.StartsWith("[-", StringComparison.Ordinal) && line.EndsWith(']')) keys.Add(line[1..^1]);
            }
        }
        if (backup.Kind == BackupKind.Registry) Scan(File.ReadAllText(backup.Path));
        else if (backup.Kind == BackupKind.RegistryZip)
        {
            using var zip = ZipFile.OpenRead(backup.Path);
            foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                using var r = new StreamReader(e.Open(), detectEncodingFromByteOrderMarks: true);
                Scan(r.ReadToEnd());
            }
        }
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- delete

    /// <summary>Removes backups to the Recycle Bin (the original deletes them permanently). Journaled.</summary>
    public static (int Removed, IReadOnlyList<string> Failed) Delete(IReadOnlyList<BackupItem> backups, IRecycleBin? bin = null, string? journalBase = null)
    {
        bin ??= new ShellRecycleBin();
        using var journal = new OperationJournal("restore", journalBase);
        var failed = new List<string>();
        var paths = new List<string>();
        foreach (var p in backups.Select(b => b.Path).Where(File.Exists))
        {
            if (bin.WhyNotRecyclable(p) is { } noBin) { failed.Add(p); journal.Record("skip", p, false, error: noBin); }
            else paths.Add(p);
        }
        var notRecycled = new HashSet<string>(paths.Count > 0 ? bin.Recycle(paths, silent: false) : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        int ok = 0;
        foreach (var p in paths)
        {
            bool gone = !notRecycled.Contains(p) && !File.Exists(p);
            journal.Record("recycle", p, gone, backupRef: gone ? "RecycleBin" : null);
            if (gone) ok++; else failed.Add(p);
        }
        return (ok, failed);
    }
}
