using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CleanSuite.Safety;

namespace CleanSuite.Junk;

/// <summary>Moves files to the Recycle Bin. Abstracted so tests never touch the user's real Recycle Bin.</summary>
public interface IRecycleBin
{
    /// <summary>Recycles the files; returns the paths that could not be recycled.</summary>
    IReadOnlyList<string> Recycle(IReadOnlyList<string> paths, bool silent);

    /// <summary>
    /// Why this path cannot go to the Recycle Bin (its drive has none, so Windows would delete it for good), or null.
    /// Callers refuse such paths instead of recycling them.
    /// </summary>
    string? WhyNotRecyclable(string path) => null;
}

public sealed class JunkRemovalRequest
{
    public required IReadOnlyList<JunkItem> Items { get; init; }
    public RemovalMode Mode { get; init; } = RemovalMode.RecycleBin;
    /// <summary>Folder for "Jnk *.zip" backups (ZipBackup mode).</summary>
    public string BackupFolder { get; init; } = DefaultBackupFolder;
    /// <summary>File-name prefix of the zip backup: "Jnk" (junk files) or "Dup" (duplicate files), as in the original.</summary>
    public string BackupPrefix { get; init; } = "Jnk";
    /// <summary>Temp folders whose empty subfolders are removed afterwards (SettingsJnk bit3).</summary>
    public IReadOnlyList<string> TempFolders { get; init; } = Array.Empty<string>();
    public bool DeleteEmptyTempFolders { get; init; }
    /// <summary>True when running unattended (original adds FOF_SILENT|FOF_NOERRORUI in that case).</summary>
    public bool Silent { get; init; }
    public int BatchSize { get; init; } = 200;

    public static string DefaultBackupFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSuite", "Backups");
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
/// Removes junk items (behaviour of the original) with three additions: every operation is journaled,
/// a protected-path guard is applied, and an item is only deleted if it is still the same file that was scanned.
/// </summary>
public sealed class JunkRemover
{
    private readonly IRecycleBin _recycleBin;
    private readonly ProtectedPaths _protected;
    private readonly string? _journalBase;

    public JunkRemover(IRecycleBin? recycleBin = null, ProtectedPaths? protectedPaths = null, string? journalBase = null)
    {
        _recycleBin = recycleBin ?? new ShellRecycleBin();
        _protected = protectedPaths ?? ProtectedPaths.ForCurrentSystem();
        _journalBase = journalBase;
    }

    public JunkRemovalResult Remove(JunkRemovalRequest req, CancellationToken ct = default)
    {
        using var journal = new OperationJournal("junk", _journalBase);
        var failed = new List<(string, string)>();
        var ready = new List<JunkItem>();

        foreach (var item in req.Items)
        {
            var reason = _protected.Check(item.FullPath);
            if (reason != null) { failed.Add((item.FullPath, reason)); journal.Record("skip", item.FullPath, false, error: reason); continue; }
            var fi = new FileInfo(item.FullPath);
            if (!fi.Exists) { failed.Add((item.FullPath, "no longer exists")); continue; }
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) { failed.Add((item.FullPath, "reparse point")); continue; }
            if (fi.Length != item.Size || item.LastWriteUtc != default && fi.LastWriteTimeUtc != item.LastWriteUtc)
            {
                failed.Add((item.FullPath, "changed since scan"));
                journal.Record("skip", item.FullPath, false, error: "changed since scan");
                continue;
            }
            ready.Add(item);
        }

        int deleted = 0;
        long freed = 0;
        string? zipPath = null;
        switch (req.Mode)
        {
            case RemovalMode.RecycleBin:
                foreach (var it in ready.ToList())
                {
                    if (_recycleBin.WhyNotRecyclable(it.FullPath) is not { } why) continue;
                    ready.Remove(it);
                    failed.Add((it.FullPath, why));
                    journal.Record("skip", it.FullPath, false, error: why);
                }
                foreach (var batch in ready.Chunk(Math.Max(1, req.BatchSize)))
                {
                    ct.ThrowIfCancellationRequested();
                    var notRecycled = new HashSet<string>(_recycleBin.Recycle(batch.Select(b => b.FullPath).ToList(), req.Silent), StringComparer.OrdinalIgnoreCase);
                    foreach (var it in batch)
                    {
                        bool ok = !notRecycled.Contains(it.FullPath) && !File.Exists(it.FullPath);
                        journal.Record("recycle", it.FullPath, ok, backupRef: ok ? "RecycleBin" : null, error: ok ? null : "not recycled");
                        if (ok) { deleted++; freed += it.Size; } else failed.Add((it.FullPath, "could not be recycled"));
                    }
                }
                break;

            case RemovalMode.Permanent:
                foreach (var it in ready)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TryDelete(it.FullPath, out var err)) { deleted++; freed += it.Size; journal.Record("delete", it.FullPath, true); }
                    else { failed.Add((it.FullPath, err!)); journal.Record("delete", it.FullPath, false, error: err); }
                }
                break;

            case RemovalMode.ZipBackup:
                if (ready.Count == 0) break;
                zipPath = JunkBackupArchive.Create(req.BackupFolder, ready, journal, out var archived, ct, req.BackupPrefix);
                foreach (var it in archived)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TryDelete(it.FullPath, out var err)) { deleted++; freed += it.Size; journal.Record("delete", it.FullPath, true, backupRef: zipPath); }
                    else { failed.Add((it.FullPath, err!)); journal.Record("delete", it.FullPath, false, backupRef: zipPath, error: err); }
                }
                foreach (var it in ready.Except(archived)) failed.Add((it.FullPath, "could not be archived"));
                break;
        }

        int emptyRemoved = 0;
        if (req.DeleteEmptyTempFolders)
            foreach (var t in req.TempFolders)
                emptyRemoved += RemoveEmptySubfolders(t, journal, ct);

        return new JunkRemovalResult
        {
            Deleted = deleted, BytesFreed = freed, Failed = failed, EmptyFoldersRemoved = emptyRemoved,
            ZipPath = zipPath, JournalPath = journal.FilePath,
        };
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

    /// <summary>Removes empty subfolders of a temp folder bottom-up; never the temp folder itself, never through reparse points.</summary>
    private int RemoveEmptySubfolders(string root, OperationJournal journal, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return 0;
        int removed = 0;
        void Walk(string dir, bool isRoot)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var s in subs)
            {
                try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                Walk(s, false);
            }
            if (isRoot || _protected.Check(dir, true) != null) return;
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
                Directory.Delete(dir, false);
                removed++;
                journal.Record("rmdir", dir, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { journal.Record("rmdir", dir, false, error: e.Message); }
        }
        Walk(root.TrimEnd('\\'), true);
        return removed;
    }
}

/// <summary>
/// "Jnk yyyy_MM_dd_hh_mm_ss_AM.zip" backup (same naming scheme as the original) plus a
/// "cleansuite-manifest.json" entry mapping each entry back to its original full path for restore.
/// </summary>
public static class JunkBackupArchive
{
    public const string Comment = "\nCleanSuite junk backup. Restore it with CleanSuite; do not edit this file.";
    /// <summary>Marker of backups written by the original program, used for detection only.</summary>
    public const string LegacyCommentMarker = "Ace Utilities Backup file";
    public const string ManifestName = "cleansuite-manifest.json";

    public static bool IsLegacyBackup(string zipPath) =>
        ZipComment.Get(zipPath).Contains(LegacyCommentMarker, StringComparison.OrdinalIgnoreCase);

    public sealed record ManifestEntry(string Entry, string OriginalPath, long Size, DateTime LastWriteUtc);

    public static string Create(string folder, IReadOnlyList<JunkItem> items, OperationJournal journal, out List<JunkItem> archived,
        CancellationToken ct = default, string prefix = "Jnk")
    {
        Directory.CreateDirectory(folder);
        var name = prefix + " " + BackupNames.Timestamp() + ".zip";
        var path = Path.Combine(folder, name);
        for (int n = 1; File.Exists(path); n++) path = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + $"({n}).zip");

        archived = new List<JunkItem>();
        var manifest = new List<ManifestEntry>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var it in items)
            {
                ct.ThrowIfCancellationRequested();
                var entryName = EntryNameFor(it.FullPath);
                for (int n = 1; !used.Add(entryName); n++) entryName = EntryNameFor(it.FullPath) + $"({n})";
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(it.FullPath);
                    zip.CreateEntryFromFile(it.FullPath, entryName, CompressionLevel.Optimal);
                    manifest.Add(new ManifestEntry(entryName, it.FullPath, it.Size, lastWrite));
                    archived.Add(it);
                    journal.Record("archive", it.FullPath, true, backupRef: path + "|" + entryName);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    used.Remove(entryName);
                    journal.Record("archive", it.FullPath, false, error: e.Message);
                }
            }
            var m = zip.CreateEntry(ManifestName);
            using var s = m.Open();
            JsonSerializer.Serialize(s, manifest, new JsonSerializerOptions { WriteIndented = true });
        }
        ZipComment.Set(path, Comment);

        // Verify the archive is readable and complete before anything is deleted: every archived file must have its entry,
        // with the same length. (A lookup by original path keeps this linear for archives of many thousand files.)
        using (var check = ZipFile.OpenRead(path))
        {
            var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in check.Entries) lengths[e.FullName] = e.Length;
            var entryOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var me in manifest) entryOf.TryAdd(me.OriginalPath, me.Entry);
            archived = archived.Where(a => entryOf.TryGetValue(a.FullPath, out var en) && lengths.TryGetValue(en, out var len) && len == a.Size).ToList();
        }
        if (archived.Count == 0) { File.Delete(path); journal.Record("archive-empty", path, true); return path; }
        BackupLedger.Record(path);
        return path;
    }

    /// <summary>"C:\dir\file.tmp" -> "C/dir/file.tmp" (drive letter kept as first folder, no colon).</summary>
    public static string EntryNameFor(string fullPath)
    {
        var p = Path.GetFullPath(fullPath);
        if (p.Length >= 2 && p[1] == ':') p = p[0] + p[2..];
        else if (p.StartsWith(@"\\", StringComparison.Ordinal)) p = "UNC" + p[1..];
        return p.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>Restores archived files to their original locations. Existing files are kept unless <paramref name="overwrite"/>.</summary>
    public static (int Restored, List<string> Skipped) Restore(string zipPath, bool overwrite = false, Func<ManifestEntry, bool>? filter = null,
        string? journalBase = null)
    {
        var entries = List(zipPath)
            .Where(e => filter == null || filter(new ManifestEntry(e.EntryName, e.OriginalPath ?? "", e.Size, e.LastWriteUtc)))
            .ToList();
        var r = RestoreEntries(zipPath, entries, overwrite, journalBase: journalBase);
        return (r.Restored, r.Skipped.Select(s => $"{s.Path} ({s.Reason})").ToList());
    }

    /// <summary>One restorable file of a backup (CleanSuite or legacy Ace Utilities format).</summary>
    /// <param name="OriginalPath">Validated absolute path the file came from; null when it cannot be determined safely.</param>
    /// <param name="Problem">Why <paramref name="OriginalPath"/> is null, otherwise null.</param>
    public sealed record BackupEntry(string EntryName, string? OriginalPath, long Size, DateTime LastWriteUtc, string? Problem);

    public sealed record RestoreReport(int Restored, IReadOnlyList<(string Path, string Reason)> Skipped, string JournalPath);

    /// <summary>Lists the files of a CleanSuite ("cleansuite-manifest.json") or Ace Utilities junk backup.</summary>
    public static IReadOnlyList<BackupEntry> List(string zipPath)
    {
        using var zip = OpenRead(zipPath);
        var byName = EntriesByName(zip, zipPath);
        if (byName.TryGetValue(ManifestName, out var me))
        {
            List<ManifestEntry> manifest;
            using (var s = me.Open()) manifest = JsonSerializer.Deserialize<List<ManifestEntry>>(s) ?? new();
            return manifest.Select(m =>
            {
                var path = ValidateOriginalPath(m.OriginalPath, out var problem);
                return new BackupEntry(m.Entry, path, m.Size, m.LastWriteUtc, problem);
            }).ToList();
        }
        if (!IsLegacyBackup(zipPath))
            throw new InvalidDataException("not a junk backup (no CleanSuite manifest and no Ace Utilities backup comment)");
        return byName.Where(kv => !kv.Key.EndsWith('/') && !kv.Key.EndsWith('\\')).Select(kv =>
        {
            // Junk backups name the entry after the original path; duplicate backups keep the plain
            // file name and put the original path into the entry comment. A usable comment wins.
            string? problem = null;
            var path = string.IsNullOrWhiteSpace(kv.Value.Comment) ? null : ValidateOriginalPath(kv.Value.Comment.Trim(), out problem);
            if (path == null) path = LegacyOriginalPath(kv.Key, out problem);
            return new BackupEntry(kv.Key, path, kv.Value.Length, kv.Value.LastWriteTime.UtcDateTime, problem);
        }).ToList();
    }

    /// <summary>
    /// Original path of an entry written by the original: the file is added under its plain name and the entry is
    /// then renamed to the full source path, e.g. "C:\dir\file.tmp" (CZipFileHeader::SetFileName).
    /// '/' separators are accepted as well because other zip tools may rewrite them.
    /// </summary>
    public static string? LegacyOriginalPath(string entryName, out string? problem)
    {
        var p = entryName.Replace('/', '\\');
        if (p.Length > 2 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] != '\\')
            p = p[..2] + "\\" + p[2..];                           // "C:dir\f" → "C:\dir\f"
        return ValidateOriginalPath(p, out problem);
    }

    /// <summary>
    /// Accepts only fully qualified local or UNC paths that stay where they claim after normalisation
    /// ("..", "." and device paths are rejected), so a crafted archive cannot write outside its stated location.
    /// </summary>
    public static string? ValidateOriginalPath(string? path, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(path)) { problem = "original location unknown"; return null; }
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        { problem = "device path"; return null; }
        if (!Path.IsPathFullyQualified(path)) { problem = "original location unknown (relative name)"; return null; }
        if (path.Split('\\', '/').Any(seg => seg is ".." or ".")) { problem = "path contains '..'"; return null; }
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { problem = "invalid path"; return null; }
        if (Path.GetFileName(full).Length == 0) { problem = "not a file name"; return null; }
        return full;
    }

    /// <summary>
    /// Where a restored file may never go: the Startup folders (a file placed there runs at every sign-in), and, for a backup
    /// CleanSuite did not write or that changed since, Program Files. Null when the destination is allowed.
    /// </summary>
    public static string? RefusedDestination(string dest, bool trustedBackup)
    {
        string full;
        try { full = Path.GetFullPath(dest); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "invalid path"; }
        bool Under(Environment.SpecialFolder f)
        {
            var root = Environment.GetFolderPath(f);
            return root.Length > 0 && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        if (Under(Environment.SpecialFolder.Startup) || Under(Environment.SpecialFolder.CommonStartup)) return "startup folder";
        if (!trustedBackup && (Under(Environment.SpecialFolder.ProgramFiles) || Under(Environment.SpecialFolder.ProgramFilesX86)))
            return "program folder (backup not made by CleanSuite)";
        return null;
    }

    /// <summary>
    /// Restores the given entries to their original paths, or below <paramref name="targetFolder"/> when set (original
    /// folder structure kept, drive letter as first folder). Never writes into protected system folders or the Startup
    /// folders; every file is journaled.
    /// </summary>
    public static RestoreReport RestoreEntries(string zipPath, IReadOnlyList<BackupEntry> entries, bool overwrite = false,
        string? targetFolder = null, ProtectedPaths? protectedPaths = null, string? journalBase = null)
    {
        var prot = protectedPaths ?? ProtectedPaths.ForCurrentSystem();
        bool trusted = BackupLedger.Status(zipPath) == BackupTrust.Verified;
        var skipped = new List<(string, string)>();
        int restored = 0;
        using var journal = new OperationJournal("restore", journalBase);
        using var zip = OpenRead(zipPath);
        var byName = EntriesByName(zip, zipPath);
        foreach (var e in entries)
        {
            if (e.OriginalPath == null) { skipped.Add((e.EntryName, e.Problem ?? "original location unknown")); continue; }
            var dest = targetFolder == null
                ? e.OriginalPath
                : Path.Combine(targetFolder, EntryNameFor(e.OriginalPath).Replace('/', '\\'));
            if (!byName.TryGetValue(e.EntryName, out var entry)) { skipped.Add((dest, "entry missing")); continue; }
            if ((prot.Check(dest) is { } why && why.StartsWith("protected system", StringComparison.Ordinal))
                || (why = RefusedDestination(dest, trusted)) != null)
            {
                skipped.Add((dest, why));
                journal.Record("restore", dest, false, backupRef: zipPath, error: why);
                continue;
            }
            if (File.Exists(dest) && !overwrite) { skipped.Add((dest, "exists")); continue; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite);
                File.SetLastWriteTimeUtc(dest, e.LastWriteUtc);
                restored++;
                journal.Record("restore", dest, true, backupRef: zipPath + "|" + e.EntryName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                skipped.Add((dest, ex.Message));
                journal.Record("restore", dest, false, backupRef: zipPath, error: ex.Message);
            }
        }
        return new RestoreReport(restored, skipped, journal.FilePath);
    }

    private static ZipArchive OpenRead(string zipPath) => ZipFile.OpenRead(zipPath);

    /// <summary>
    /// Entries keyed by their correctly decoded names. .NET applies one encoding to every name, but each entry carries its
    /// own choice: the UTF-8 flag (general purpose bit 11) means UTF-8, otherwise the OEM code page, which is what the
    /// original's zip library (ZipArchive, DOS/FAT compatibility) writes on Windows. Names are therefore decoded from the
    /// central directory and paired with <see cref="ZipArchive.Entries"/>, which keeps central-directory order.
    /// </summary>
    private static Dictionary<string, ZipArchiveEntry> EntriesByName(ZipArchive zip, string zipPath)
    {
        var names = CentralDirectoryNames(zipPath);
        var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        for (int i = 0; i < zip.Entries.Count; i++)
        {
            var name = names != null && names.Count == zip.Entries.Count ? names[i] : zip.Entries[i].FullName;
            map.TryAdd(name, zip.Entries[i]);   // duplicate names: the first one wins, as in the original's FindFile
        }
        return map;
    }

    /// <summary>Names from the central directory decoded per entry; null when the archive layout is not the plain one (e.g. ZIP64).</summary>
    internal static List<string>? CentralDirectoryNames(string zipPath)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(GetOEMCP());
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long eocd = ZipComment.FindEocd(fs);
        var rec = new byte[22];
        fs.Position = eocd;
        fs.ReadExactly(rec);
        int count = BitConverter.ToUInt16(rec, 10);
        long size = BitConverter.ToUInt32(rec, 12), offset = BitConverter.ToUInt32(rec, 16);
        if (count == 0xFFFF || offset == 0xFFFFFFFF || offset + size > eocd) return null;
        var cd = new byte[size];
        fs.Position = offset;
        fs.ReadExactly(cd);
        var names = new List<string>(count);
        int p = 0;
        for (int i = 0; i < count; i++)
        {
            if (p + 46 > cd.Length || BitConverter.ToUInt32(cd, p) != 0x02014B50) return null;
            ushort flags = BitConverter.ToUInt16(cd, p + 8);
            int nameLen = BitConverter.ToUInt16(cd, p + 28), extraLen = BitConverter.ToUInt16(cd, p + 30), commentLen = BitConverter.ToUInt16(cd, p + 32);
            if (p + 46 + nameLen > cd.Length) return null;
            names.Add(((flags & 0x800) != 0 ? Encoding.UTF8 : oem).GetString(cd, p + 46, nameLen));
            p += 46 + nameLen + extraLen + commentLen;
        }
        return names;
    }

    /// <summary>System OEM code page (follows the system locale, not the current user culture).</summary>
    [DllImport("kernel32.dll")]
    private static extern int GetOEMCP();
}

/// <summary>Writes the archive comment into the End Of Central Directory record (not exposed by .NET 8's ZipArchive).</summary>
public static class ZipComment
{
    public static void Set(string zipPath, string comment)
    {
        var bytes = Encoding.ASCII.GetBytes(comment);
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite);
        long eocd = FindEocd(fs);
        fs.Position = eocd + 20;
        fs.Write(BitConverter.GetBytes((ushort)bytes.Length));
        fs.SetLength(eocd + 22);
        fs.Position = eocd + 22;
        fs.Write(bytes);
    }

    public static string Get(string zipPath)
    {
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read);
        long eocd = FindEocd(fs);
        var len = new byte[2];
        fs.Position = eocd + 20;
        fs.ReadExactly(len);
        var buf = new byte[BitConverter.ToUInt16(len)];
        fs.ReadExactly(buf);
        return Encoding.ASCII.GetString(buf);
    }

    internal static long FindEocd(FileStream fs)
    {
        long start = Math.Max(0, fs.Length - 22 - 0xFFFF);
        var buf = new byte[fs.Length - start];
        fs.Position = start;
        fs.ReadExactly(buf);
        for (int i = buf.Length - 22; i >= 0; i--)
            if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x05 && buf[i + 3] == 0x06) return start + i;
        throw new InvalidDataException("End of central directory not found");
    }
}

/// <summary>
/// Recycle Bin via SHFileOperationW(FO_DELETE, FOF_ALLOWUNDO), the original's mechanism, with two safeguards it lacks.
/// Windows silently deletes for good what it cannot recycle (network shares, removable drives, drives whose bin is turned
/// off, files larger than the bin): such drives are detected first and their paths are never passed on, and
/// FOF_WANTNUKEWARNING makes Windows ask before it would still destroy a file instead of recycling it.
/// </summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    private const ushort FofAllowUndo = 0x0040, FofNoConfirmation = 0x0010, FofNoErrorUi = 0x0400, FofSilent = 0x0004,
        FofNoConfirmMkDir = 0x0200, FofWantNukeWarning = 0x4000;

    private readonly Dictionary<string, string?> _rootReason = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Recycle(IReadOnlyList<string> paths, bool silent)
    {
        var refused = paths.Where(p => WhyNotRecyclable(p) != null).ToList();
        var send = paths.Except(refused).ToList();
        if (send.Count == 0) return refused;
        var from = string.Join("\0", send) + "\0\0";
        ushort flags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofWantNukeWarning;
        if (silent) flags |= FofSilent | FofNoConfirmMkDir;
        var op = new SHFILEOPSTRUCTW { wFunc = 3, pFrom = from, fFlags = flags };
        SHFileOperationW(ref op);
        // Whatever the return code (or a declined warning), the items that still exist are the ones not recycled.
        return refused.Concat(send.Where(p => File.Exists(p) || Directory.Exists(p))).ToList();
    }

    public string? WhyNotRecyclable(string path)
    {
        string? root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "invalid path"; }
        if (string.IsNullOrEmpty(root)) return "invalid path";
        lock (_rootReason)
        {
            if (!_rootReason.TryGetValue(root, out var why)) _rootReason[root] = why = NoRecycleBinReason(root);
            return why;
        }
    }

    /// <summary>Null when the volume of this root has a working Recycle Bin.</summary>
    public static string? NoRecycleBinReason(string root)
    {
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return "network location without a Recycle Bin (not deleted)";
        switch (GetDriveTypeW(root))
        {
            case 2: return "removable drive without a Recycle Bin (not deleted)";       // DRIVE_REMOVABLE
            case 4: return "network drive without a Recycle Bin (not deleted)";         // DRIVE_REMOTE
            case 5: return "CD/DVD drive (not deleted)";                                 // DRIVE_CDROM
            case 0 or 1: return "drive not available (not deleted)";                     // DRIVE_UNKNOWN / DRIVE_NO_ROOT_DIR
        }
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        if (SHQueryRecycleBinW(root, ref info) != 0) return "drive without a Recycle Bin (not deleted)";
        if (RecycleBinTurnedOff(root)) return "the Recycle Bin is turned off for this drive (not deleted)";
        return null;
    }

    /// <summary>"Don't move files to the Recycle Bin" (BitBucket NukeOnDelete), per volume or for all drives; read only.</summary>
    private static bool RecycleBinTurnedOff(string root)
    {
        const string bitBucket = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";
        try
        {
            using var all = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(bitBucket);
            if (all?.GetValue("NukeOnDelete") is int g && g != 0) return true;
            var name = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPointW(root, name, name.Capacity)) return false;
            var v = name.ToString();                                         // \\?\Volume{GUID}\
            int open = v.IndexOf('{'), close = v.IndexOf('}');
            if (open < 0 || close < open) return false;
            using var vol = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(bitBucket + @"\Volume\" + v[open..(close + 1)]);
            return vol?.GetValue("NukeOnDelete") is int n && n != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string root, ref SHQUERYRBINFO info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder name, int length);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);
}
