using System.Globalization;
using System.Security.Cryptography;
using CleanSuite.Matching;
using CleanSuite.Safety;

namespace CleanSuite.Duplicates;

/// <summary>
/// Find Duplicate Files, file mode. Read-only:
/// walks the roots, builds one key per file from the selected criteria, keeps keys shared by at least two files,
/// optionally regroups by whole-file content, and sorts groups by total size (largest first).
/// </summary>
public sealed class DuplicateScanner
{
    private readonly DupOptions _o;
    private readonly List<string> _excludes;
    private readonly List<string> _ignore;
    private readonly List<string> _patterns;
    private int _searched;
    private CancellationToken _ct;

    public DuplicateScanner(DupOptions options)
    {
        _o = options.Normalized();
        _excludes = _o.Excludes.Where(p => p.Length > 0 && !p.StartsWith('\\')).Select(p => p.EndsWith('*') ? p : p + "*").ToList();
        _ignore = _o.Ignore.Where(p => p.Length > 0).ToList();
        _patterns = _o.Patterns.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>Progress: phase ("scan" / "hash") and a counter.</summary>
    public event Action<string, int>? Progress;

    public DupScanResult Scan(CancellationToken ct = default)
    {
        _ct = ct;
        _searched = 0;
        var groups = new Dictionary<string, List<DupFile>>(StringComparer.Ordinal);
        foreach (var r in DistinctRoots(_o.Roots)) Walk(r, groups);

        // "Removing non-duplicates..." Different paths to one physical file (subst or network drives, hard links) are one
        // file, not two copies: deleting "the duplicate" would delete the only copy.
        var candidates = groups.Select(g => KeyValuePair.Create(g.Key, OnePathPerFile(g.Value)))
            .Where(g => g.Value.Count >= 2).ToList();
        var unreadable = new List<string>();
        if (_o.Content != ContentCompare.None)
            candidates = ByContent(candidates, unreadable);

        int n = 0;
        var result = candidates
            .Select(g => (Key: g.Key, Files: g.Value, Total: g.Value.Sum(f => f.Size)))
            .OrderByDescending(g => g.Total).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DupGroup(++n, g.Total, g.Files))
            .ToList();
        return new DupScanResult
        {
            Groups = result, FilesSearched = _searched, Unreadable = unreadable, ContentVerified = _o.Content != ContentCompare.None,
        };
    }

    /// <summary>Full paths without duplicates or roots nested in another root (each folder is walked once).</summary>
    public static List<string> DistinctRoots(IEnumerable<string> roots)
    {
        var full = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string r;
            try { r = Path.GetFullPath(root.Length == 2 && root[1] == ':' ? root + "\\" : root).TrimEnd('\\'); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (r.Length > 0) full.Add(r);
        }
        full = full.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return full.Where(r => !full.Any(o => !o.Equals(r, StringComparison.OrdinalIgnoreCase) &&
                                               r.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static List<DupFile> OnePathPerFile(List<DupFile> files)
    {
        var seen = new HashSet<FileIdentity>();
        var result = new List<DupFile>();
        foreach (var f in files)
            if (FileIdentity.Of(f.FullPath) is not { } id || seen.Add(id)) result.Add(f);
        return result;
    }

    // ---------------------------------------------------------------- walk

    private bool Excluded(string dir) => _excludes.Any(p => AceWildcard.IsMatch(dir, p));

    private void Walk(string dir, Dictionary<string, List<DupFile>> groups)
    {
        _ct.ThrowIfCancellationRequested();
        if (dir.Length >= 261 || Excluded(dir)) return;
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir + "\\").EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false }).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return; }

        var subdirs = new List<string>();
        foreach (var e in entries)
        {
            var a = e.Attributes;
            if ((a & FileAttributes.Directory) != 0)
            {
                if ((a & FileAttributes.ReparsePoint) != 0) continue;                 // junctions and links are never followed
                if ((a & FileAttributes.Hidden) != 0 && (a & FileAttributes.System) != 0) continue;
                subdirs.Add(e.FullName);
                continue;
            }
            if ((a & FileAttributes.System) != 0) continue;                           // system files are not even counted
            _searched++;
            if (_searched % 500 == 0) Progress?.Invoke("scan", _searched);
            var f = (FileInfo)e;
            long size;
            try { size = f.Length; }
            catch (IOException) { continue; }
            if (size == 0) continue;
            if (_o.MinSize > 0 && size < _o.MinSize) continue;
            var name = f.Name;
            if (!_patterns.Any(p => AceWildcard.IsMatch(name, p))) continue;
            if (_ignore.Any(p => AceWildcard.IsMatch(name, p))) continue;
            if (f.FullName.Length >= 0x106) continue;
            var file = new DupFile(f.FullName, size, f.LastWriteTimeUtc);
            var key = Key(file);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<DupFile>();
            list.Add(file);
        }
        foreach (var s in subdirs) Walk(s, groups);
    }

    /// <summary>"size|" + "\name" + "|MM/DD/YYYY  hh:mm|", case-folded.</summary>
    internal string Key(DupFile f)
    {
        var k = "";
        if (_o.SameSize) k += f.Size.ToString(CultureInfo.InvariantCulture) + "|";
        if (_o.SameName) k += "\\" + f.Name;
        if (_o.SameDate)
        {
            var t = f.LastWriteUtc;
            k += string.Create(CultureInfo.InvariantCulture, $"|{t.Month:00}/{t.Day:00}/{t.Year}  {t.Hour:00}:{t.Minute:00}|");
        }
        return k.ToUpperInvariant();
    }

    // ---------------------------------------------------------------- content pass

    /// <summary>
    /// Regroups every candidate group by a digest of the whole file. Deliberate safety deviation from the original:
    /// files that cannot be read are left out (the original gives them all the same digest, so unreadable files of
    /// equal size were reported as duplicates of each other). Files larger than <see cref="PartialSpan"/> twice are first
    /// compared by their first and last 64 KiB: most same-size files already differ there, and then the whole file is
    /// never read. Only files that agree on both ends are read completely, and the whole-file digest alone decides.
    /// </summary>
    private List<KeyValuePair<string, List<DupFile>>> ByContent(List<KeyValuePair<string, List<DupFile>>> candidates, List<string> unreadable)
    {
        var regrouped = new Dictionary<string, List<DupFile>>(StringComparer.Ordinal);
        int done = 0;
        foreach (var (key, files) in candidates)
        {
            foreach (var same in PreFilter(files, unreadable))
                foreach (var f in same)
                {
                    _ct.ThrowIfCancellationRequested();
                    var digest = Digest(f.FullPath, _o.Content);
                    if (digest == null) { unreadable.Add(f.FullPath); continue; }
                    var k = (digest + key).ToUpperInvariant();
                    if (!regrouped.TryGetValue(k, out var list)) regrouped[k] = list = new List<DupFile>();
                    list.Add(f);
                }
            Progress?.Invoke("hash", ++done * 100 / Math.Max(1, candidates.Count));
        }
        return regrouped.Where(g => g.Value.Count >= 2).ToList();
    }

    /// <summary>Bytes read from each end of a large file for the pre-check.</summary>
    public const int PartialSpan = 64 * 1024;

    /// <summary>
    /// Splits a candidate group by size and the hash of both ends; sub-groups of one file are dropped (that file has no copy).
    /// Small files go through unchanged: reading them whole costs no more than the pre-check.
    /// </summary>
    private IEnumerable<List<DupFile>> PreFilter(List<DupFile> files, List<string> unreadable)
    {
        var small = files.Where(f => f.Size <= 2L * PartialSpan).ToList();
        if (small.Count > 0) yield return small;
        var parts = new Dictionary<(long, ulong), List<DupFile>>();
        foreach (var f in files.Where(f => f.Size > 2L * PartialSpan))
        {
            _ct.ThrowIfCancellationRequested();
            if (PartialHash(f.FullPath) is not { } h) { unreadable.Add(f.FullPath); continue; }
            if (!parts.TryGetValue((f.Size, h), out var list)) parts[(f.Size, h)] = list = new List<DupFile>();
            list.Add(f);
        }
        foreach (var list in parts.Values)
            if (list.Count >= 2) yield return list;
    }

    /// <summary>XxHash64 of the first and the last <see cref="PartialSpan"/> bytes; null when the file cannot be read.</summary>
    public static ulong? PartialHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
            var buf = new byte[2 * PartialSpan];
            fs.ReadExactly(buf, 0, PartialSpan);
            fs.Seek(-PartialSpan, SeekOrigin.End);
            fs.ReadExactly(buf, PartialSpan, PartialSpan);
            return System.IO.Hashing.XxHash64.HashToUInt64(buf);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>Whole-file MD5 (lowercase hex) or CRC-32 ("0x%08x"), read in 1 MiB blocks; null when the file cannot be read.</summary>
    public static string? Digest(string path, ContentCompare kind)
    {
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            int n;
            if (kind == ContentCompare.Md5)
            {
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                while ((n = fs.Read(buf, 0, 1 << 20)) > 0) md5.AppendData(buf, 0, n);
                return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            }
            var crc = new System.IO.Hashing.Crc32();
            while ((n = fs.Read(buf, 0, 1 << 20)) > 0) crc.Append(buf.AsSpan(0, n));
            return "0x" + crc.GetCurrentHashAsUInt32().ToString("x8", CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
    }
}

/// <summary>CRC-32/IEEE (reflected, polynomial 0xEDB88320), the table the original uses.</summary>
public static class Crc32
{
    public static uint Compute(ReadOnlySpan<byte> data) => System.IO.Hashing.Crc32.HashToUInt32(data);
}
