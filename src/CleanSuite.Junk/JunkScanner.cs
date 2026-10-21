using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CleanSuite.Matching;
using Microsoft.Win32.SafeHandles;

namespace CleanSuite.Junk;

public sealed class JunkScanRequest
{
    /// <summary>Scan roots: drive roots as "C:" (original form) or folder paths. Never scanned implicitly.</summary>
    public required IReadOnlyList<string> Roots { get; init; }
    public required PatternSet Patterns { get; init; }
    public IReadOnlyList<string> Exclusions { get; init; } = JunkDefaults.Exclusions;
    public IgnoreList Ignore { get; init; } = new();
    public JunkOptions Options { get; init; } = new();
    /// <summary>Temp folders scanned in Temp mode when Options.CleanTempFolders is set.</summary>
    public IReadOnlyList<string> TempFolders { get; init; } = Array.Empty<string>();
    /// <summary>Exclude temp folders from the drive scan so they are only reported as Temp items.</summary>
    public bool ExcludeTempFromDriveScan { get; init; } = true;
    /// <summary>Reference "now" for the 7-day window.</summary>
    public DateTime UtcNow { get; init; } = DateTime.UtcNow;
}

public sealed class JunkScanResult
{
    public required IReadOnlyList<JunkItem> Items { get; init; }
    public long FilesSearched { get; init; }
    public int IgnoredCount { get; init; }
    public IReadOnlyList<string> SkippedLocked { get; init; } = Array.Empty<string>();
    public long TotalSize => Items.Sum(i => i.Size);
}

/// <summary>
/// Junk scanner reproducing the original.
/// Read-only: the scanner never modifies the file system.
/// </summary>
public sealed class JunkScanner
{
    /// <summary>7 days in 100-ns units (0x58028E44000), as in the original.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromTicks(0x58028E44000);

    private readonly ConcurrentDictionary<string, JunkItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _locked = new();
    private long _files;
    private int _ignored;

    public JunkScanResult Scan(JunkScanRequest req, CancellationToken ct = default)
    {
        _items.Clear();
        _locked.Clear();
        _files = 0;
        _ignored = 0;
        var cutoff = req.UtcNow - RecentWindow;

        var baseExclusions = req.Exclusions.Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        var exclusions = new List<string>(baseExclusions);
        if (req.Options.CleanTempFolders && req.ExcludeTempFromDriveScan)
            exclusions.AddRange(req.TempFolders);

        // One worker per physical disk, at most 4 (original groups 0/100/200/300).
        var groups = req.Roots.GroupBy(r => DiskGrouping.GroupOf(r)).OrderBy(g => g.Key).ToList();
        Parallel.ForEach(groups, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, g =>
        {
            foreach (var root in g)
                ScanFolder(NormalizeRoot(root), req, exclusions, cutoff, ScanMode.Drive, g.Key * 100, ct);
        });

        if (req.Options.CleanTempFolders)
        {
            int group = 1000;
            foreach (var t in req.TempFolders)
            {
                ct.ThrowIfCancellationRequested();
                // The temp folder itself is removed from the exclusions; the remaining exclusions still apply.
                if (Directory.Exists(t)) ScanFolder(t.TrimEnd('\\'), req, baseExclusions, cutoff, ScanMode.Temp, group, ct);
                group++;
            }
        }

        return new JunkScanResult
        {
            Items = _items.Values.OrderBy(i => i.Group).ThenBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase).ToList(),
            FilesSearched = _files,
            IgnoredCount = _ignored,
            SkippedLocked = _locked.ToList(),
        };
    }

    private static string NormalizeRoot(string root)
    {
        root = root.TrimEnd('\\');
        return root.Length == 2 && root[1] == ':' ? root : Path.GetFullPath(root).TrimEnd('\\');
    }

    private void ScanFolder(string folder, JunkScanRequest req, List<string>? exclusions, DateTime cutoff,
        ScanMode mode, int group, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (exclusions != null && exclusions.Any(e => AceWildcard.IsMatch(folder, e))) return;

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folder + "\\").EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var attrs = entry.Attributes;
            if ((attrs & FileAttributes.ReparsePoint) != 0) continue;    // never follow junctions/symlinks
            var full = folder + "\\" + entry.Name;
            if ((attrs & FileAttributes.Directory) != 0)
            {
                ScanFolder(full, req, exclusions, cutoff, mode, group, ct);
                continue;
            }
            var fi = (FileInfo)entry;
            Interlocked.Increment(ref _files);
            if (req.Options.ExcludeModifiedWithinWeek && fi.LastWriteTimeUtc > cutoff) continue;

            if (mode == ScanMode.Temp)
            {
                TryAdd(full, entry.Name, folder, fi.Length, JunkReason.TempFolder, "Temp", mode, group, req, fi.LastWriteTimeUtc);
                continue;
            }
            var category = req.Patterns.Classify(entry.Name, full);
            if (category != null)
                TryAdd(full, entry.Name, folder, fi.Length, JunkReason.JunkType | JunkReason.Archivable, category, mode, group, req, fi.LastWriteTimeUtc);
            else if (req.Options.FindZeroLength && fi.Length == 0)
                TryAdd(full, entry.Name, folder, 0, JunkReason.ZeroLength, "", mode, group, req, fi.LastWriteTimeUtc);
        }
    }

    private void TryAdd(string full, string name, string folder, long size, JunkReason flags, string category,
        ScanMode mode, int group, JunkScanRequest req, DateTime lastWriteUtc)
    {
        // Access probe (original: DELETE|GENERIC_READ|GENERIC_WRITE, no sharing; fall back to read-only).
        var access = AccessProbe.Probe(full);
        if (access == AccessProbe.Result.FullAccess) flags |= JunkReason.CanOpen | JunkReason.CanDelete;
        else if (access == AccessProbe.Result.Locked && mode == ScanMode.Drive) { _locked.Enqueue(full); return; }
        else if (AccessProbe.CanRead(full)) flags |= JunkReason.CanOpen;
        else { _locked.Enqueue(full); return; }

        // CleanSuite's own protection: never offered, not even with the ignore lists switched off (expert mode).
        if (mode != ScanMode.Temp && IgnoreList.BuiltIn.Match(name, full, mode) != null)
        {
            Interlocked.Increment(ref _ignored);
            return;
        }
        bool ignored = req.Ignore.Match(name, full, mode, req.Options.ApplyAllIgnoreListsInTemp) != null;
        if (ignored) Interlocked.Increment(ref _ignored);
        bool showIgnored = !req.Options.UseIgnoreList && req.Options.ExpertMode;
        if (ignored && !showIgnored) return;

        flags |= ignored ? JunkReason.IgnoredShown : JunkReason.NotIgnored;
        if ((flags & JunkReason.EmptyTempFolder) == 0) flags |= JunkReason.Archivable;
        _items.TryAdd(full, new JunkItem(full, name, folder, size, flags, category, group) { LastWriteUtc = lastWriteUtc });
    }
}

/// <summary>Physical-disk grouping.</summary>
public static class DiskGrouping
{
    public static int GroupOf(string root)
    {
        try
        {
            var full = root.Length == 2 && root[1] == ':' ? root + "\\" : Path.GetFullPath(root);
            var drive = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(drive) || drive.Length < 2 || drive[1] != ':') return 0;
            int n = PhysicalDeviceNumber(drive[0]);
            return n is >= 0 and <= 3 ? n : 0;
        }
        catch { return 0; }
    }

    private static int PhysicalDeviceNumber(char letter)
    {
        using var h = Native.CreateFileW($@"\\.\{letter}:", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return 0;
        var buf = new byte[12];
        if (!Native.DeviceIoControl(h, 0x2D1080, IntPtr.Zero, 0, buf, buf.Length, out _, IntPtr.Zero)) return 0;
        int deviceType = BitConverter.ToInt32(buf, 0);
        return deviceType == 7 ? BitConverter.ToInt32(buf, 4) : 0;   // FILE_DEVICE_DISK
    }
}

internal static class AccessProbe
{
    public enum Result { FullAccess, Locked, Other }

    public static Result Probe(string path)
    {
        using var h = Native.CreateFileW(path, 0xC0010000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!h.IsInvalid) return Result.FullAccess;
        int err = Marshal.GetLastWin32Error();
        return err is 5 or 32 ? Result.Locked : Result.Other;
    }

    public static bool CanRead(string path)
    {
        using var h = Native.CreateFileW(path, 0x80000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        return !h.IsInvalid;
    }
}

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
}
