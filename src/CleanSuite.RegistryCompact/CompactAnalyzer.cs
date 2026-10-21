using CleanSuite.Safety;
namespace CleanSuite.RegistryCompact;

/// <summary>Reads the list of loaded hives (tests supply their own).</summary>
public interface IHiveSource
{
    IReadOnlyList<HiveListEntry> Entries();
    /// <summary>NT device name → drive ("\Device\HarddiskVolume3" → "C:").</summary>
    IReadOnlyDictionary<string, string> Devices();
}

/// <summary>Writes a compacted copy of a loaded hive to a file that does not exist yet (RegSaveKey).</summary>
public interface IHiveSaver
{
    /// <summary>
    /// Enables backup rights until the result is disposed; throws <see cref="PrivilegeNotHeldException"/> when they are missing
    /// (not running as administrator).
    /// </summary>
    IDisposable Prepare();
    /// <summary>Win32 error code, 0 on success.</summary>
    int Save(HiveCandidate hive, string file);
}

public sealed class PrivilegeNotHeldException(string privilege) : Exception(privilege + " is not held");

public enum HiveStatus
{
    /// <summary>Measured; the compacted copy is smaller.</summary>
    Measured,
    /// <summary>Measured; the copy would not be smaller (the original drops these).</summary>
    NoGain,
    /// <summary>Saving the copy failed (<see cref="HiveRow.Error"/>).</summary>
    SaveFailed,
    /// <summary>The size of the hive file could not be read.</summary>
    Unreadable,
    /// <summary>Not enough free space in the work folder for a copy of this size.</summary>
    NoSpace,
    /// <summary>Not measured, see <see cref="HiveCandidate.Skip"/>.</summary>
    Skipped,
    /// <summary>Not measured because the analysis was cancelled.</summary>
    Cancelled,
}

public sealed record HiveRow(HiveCandidate Hive, HiveStatus Status, long? CurrentSize, long? CompactSize, int Error = 0)
{
    public long Saving => Status == HiveStatus.Measured ? CurrentSize!.Value - CompactSize!.Value : 0;
}

public sealed class CompactReport
{
    public required IReadOnlyList<HiveRow> Rows { get; init; }
    public bool Cancelled { get; init; }
    /// <summary>Copies that could not be deleted (e.g. held by an antivirus scanner); the page names them.</summary>
    public IReadOnlyList<string> LeftOver { get; init; } = Array.Empty<string>();
    /// <summary>Sums over measured hives that would shrink, as the original's totals.</summary>
    public long TotalCurrent => Rows.Where(r => r.Status == HiveStatus.Measured).Sum(r => r.CurrentSize!.Value);
    public long TotalCompact => Rows.Where(r => r.Status == HiveStatus.Measured).Sum(r => r.CompactSize!.Value);
    public long TotalSaving => TotalCurrent - TotalCompact;

    /// <summary>For display: measured hives by saving (largest first), then hives that would not shrink, problems, skipped ones.</summary>
    public IEnumerable<HiveRow> Ordered() => Rows
        .OrderBy(r => r.Status switch { HiveStatus.Measured => 0, HiveStatus.NoGain => 1, HiveStatus.Skipped => 3, _ => 2 })
        .ThenByDescending(r => r.Saving)
        .ThenByDescending(r => r.CurrentSize ?? -1);
}

/// <summary>
/// Registry Compactor, analysis only: for every selected hive a
/// compacted copy is written to a private work folder and its size compared with the hive file. Nothing in Windows is changed;
/// CleanSuite does not schedule the replacement (RegReplaceKey) and needs no restart. Each copy is deleted as soon as it has
/// been measured and the work folder is removed at the end, also after an error or a cancel. Unlike the original it never
/// deletes other files from the temp folder, and the copies (which hold all of SYSTEM, SOFTWARE and other users' hives) are
/// written to %ProgramData%\CleanSuite\Compact in a folder only SYSTEM and Administrators can open, not to the user's TEMP folder.
/// A copy that cannot be deleted is retried and then reported.
/// </summary>
/// <param name="hiveSize">Size of a live hive file (tests supply one; default: the file's length).</param>
public sealed class CompactAnalyzer(IHiveSource source, IHiveSaver saver, string? workRoot = null, Func<string, long>? freeSpace = null,
    Func<string, long?>? hiveSize = null)
{
    /// <summary>Kept free on the work drive beyond the size of the copy.</summary>
    public const long SpaceMargin = 64L << 20;

    private readonly string _workRoot = workRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CleanSuite", "Compact");
    private readonly List<string> _leftOver = new();
    private readonly Func<string, long> _freeSpace = freeSpace ?? (dir => new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace);
    private readonly Func<string, long?> _hiveSize = hiveSize ?? FileSizes.Of;

    /// <param name="progress">(1-based index, count, hive) before each copy is written.</param>
    public CompactReport Analyze(Action<int, int, HiveCandidate>? progress = null, CancellationToken ct = default)
    {
        var devices = source.Devices();
        var candidates = HiveSelection.Select(source.Entries(), p => DevicePaths.ToDosPath(p, devices), _hiveSize);
        var todo = candidates.Where(c => c.Skip == HiveSkip.None).ToList();
        var rows = candidates.Where(c => c.Skip != HiveSkip.None).Select(c => new HiveRow(c, HiveStatus.Skipped, c.FileSize, null)).ToList();
        if (todo.Count == 0) return new CompactReport { Rows = rows };

        using var rights = saver.Prepare();
        var work = Path.Combine(_workRoot, "Compact-" + Guid.NewGuid().ToString("N"));
        CreatePrivateFolder(work);
        bool cancelled = false;
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                var hive = todo[i];
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    rows.AddRange(todo.Skip(i).Select(h => new HiveRow(h, HiveStatus.Cancelled, h.FileSize, null)));
                    break;
                }
                progress?.Invoke(i + 1, todo.Count, hive);
                rows.Add(Measure(hive, Path.Combine(work, $"hive{i}.dat")));
            }
        }
        finally
        {
            if (!TryDelete(() => Directory.Delete(work, recursive: true), () => Directory.Exists(work)))
                foreach (var f in SafeFiles(work)) if (!_leftOver.Contains(f, StringComparer.OrdinalIgnoreCase)) _leftOver.Add(f);
        }
        return new CompactReport { Rows = rows, Cancelled = cancelled, LeftOver = _leftOver.ToList() };
    }

    private HiveRow Measure(HiveCandidate hive, string file)
    {
        if ((hive.FileSize ?? _hiveSize(hive.FilePath!)) is not long current) return new HiveRow(hive, HiveStatus.Unreadable, null, null);
        if (_freeSpace(Path.GetDirectoryName(file)!) < current + SpaceMargin) return new HiveRow(hive, HiveStatus.NoSpace, current, null);
        try
        {
            int err = saver.Save(hive, file);
            if (err != 0) return new HiveRow(hive, HiveStatus.SaveFailed, current, null, err);
            long? compact = FileSizes.Of(file);
            if (compact == null) return new HiveRow(hive, HiveStatus.SaveFailed, current, null, 2);
            return new HiveRow(hive, compact < current ? HiveStatus.Measured : HiveStatus.NoGain, current, compact);
        }
        finally
        {
            if (!TryDelete(() => File.Delete(file), () => File.Exists(file))) _leftOver.Add(file);
        }
    }

    /// <summary>Up to three attempts a quarter of a second apart; true when the item is gone.</summary>
    private static bool TryDelete(Action delete, Func<bool> stillThere)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { delete(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (!stillThere()) return true;
            Thread.Sleep(250);
        }
        return !stillThere();
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The work folder. When elevated (always, for real hives) only SYSTEM and Administrators get access and nothing is
    /// inherited from the parent, so unelevated programs cannot read the copies.
    /// </summary>
    private static void CreatePrivateFolder(string path)
    {
        if (!Environment.IsPrivilegedProcess) { Directory.CreateDirectory(path); return; }
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null),
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new DirectoryInfo(path).Create(security);
    }
}
