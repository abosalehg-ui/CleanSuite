using CleanSuite.Safety;
using CleanSuite.RegistryCompact;

namespace CleanSuite.Tests;

/// <summary>
/// Registry Compactor (analysis only): hive selection, device paths, measuring with a fake saver in a temp folder. Saving a
/// real hive needs administrator rights, so the system is only read here (hive list, device names, privilege check).
/// </summary>
public class RegistryCompactTests
{
    private const string Vol = @"\Device\HarddiskVolume3";

    private sealed class FakeSource(IReadOnlyList<HiveListEntry> entries) : IHiveSource
    {
        public IReadOnlyList<HiveListEntry> Entries() => entries;
        public IReadOnlyDictionary<string, string> Devices() => new Dictionary<string, string> { [Vol] = "C:" };
    }

    /// <summary>Writes a copy of the given size instead of saving a hive; records what it was asked to do.</summary>
    private sealed class FakeSaver(Func<HiveCandidate, long> size, Func<HiveCandidate, int>? error = null) : IHiveSaver
    {
        public readonly List<string> Files = new();
        public bool Prepared, Released;
        public IDisposable Prepare() { Prepared = true; return new Release(this); }
        public int Save(HiveCandidate hive, string file)
        {
            Assert.False(File.Exists(file));                                              // RegSaveKey needs a new file
            Files.Add(file);
            if (error?.Invoke(hive) is int e and not 0) return e;
            File.WriteAllBytes(file, new byte[size(hive)]);
            return 0;
        }
        private sealed class Release(FakeSaver s) : IDisposable { public void Dispose() => s.Released = true; }
    }

    private static readonly HiveListEntry[] Typical =
    [
        new(@"\REGISTRY\MACHINE\HARDWARE", ""),
        new(@"\REGISTRY\MACHINE\SYSTEM", Vol + @"\Windows\System32\config\SYSTEM"),
        new(@"\REGISTRY\MACHINE\SOFTWARE", Vol + @"\Windows\System32\config\SOFTWARE"),
        new(@"\REGISTRY\MACHINE\SAM", Vol + @"\Windows\System32\config\SAM"),
        new(@"\REGISTRY\MACHINE\security", Vol + @"\Windows\System32\config\SECURITY"),
        new(@"\REGISTRY\MACHINE\BCD00000000", @"\Device\HarddiskVolume1\EFI\Microsoft\Boot\BCD"),
        new(@"\REGISTRY\MACHINE\DRIVERS", Vol + @"\Windows\System32\config\DRIVERS"),
        new(@"\REGISTRY\MACHINE\ELAM", Vol + @"\Windows\System32\config\ELAM"),
        new(@"\REGISTRY\USER\.DEFAULT", Vol + @"\Windows\System32\config\DEFAULT"),
        new(@"\REGISTRY\USER\S-1-5-21-1-2-3-1001", Vol + @"\Users\me\NTUSER.DAT"),
        new(@"\REGISTRY\USER\S-1-5-21-1-2-3-1001_Classes", Vol + @"\Users\me\AppData\Local\Microsoft\Windows\UsrClass.dat"),
        new(@"\REGISTRY\A\{aaaa}", Vol + @"\ProgramData\app.hve"),
        new(@"\REGISTRY\WC\Silo1", Vol + @"\silo\hive"),
        new(@"\REGISTRY\MACHINE\OTHERDISK", @"\Device\HarddiskVolume9\x\hive"),
    ];

    private static long? Sizes(string path) => Path.GetFileName(path) switch
    {
        "ELAM" => 0x40000,
        "DEFAULT" => 0x40001,
        "SYSTEM" => 30 << 20,
        "SOFTWARE" => 120 << 20,
        _ => 2 << 20,
    };

    [Fact]
    public void Selection_follows_the_original_rules()
    {
        var c = HiveSelection.Select(Typical, p => DevicePaths.ToDosPath(p, new FakeSource([]).Devices()), Sizes)
            .ToDictionary(x => x.RegistryPath, x => x);
        HiveSkip Skip(string reg) => c[reg].Skip;
        Assert.Equal(HiveSkip.Volatile, Skip(@"\REGISTRY\MACHINE\HARDWARE"));
        foreach (var n in new[] { "SAM", "security", "BCD00000000", "DRIVERS" }) Assert.Equal(HiveSkip.Excluded, Skip(@"\REGISTRY\MACHINE\" + n));
        Assert.Equal(HiveSkip.Small, Skip(@"\REGISTRY\MACHINE\ELAM"));                   // exactly 256 KiB
        Assert.Equal(HiveSkip.None, Skip(@"\REGISTRY\USER\.DEFAULT"));                     // one byte more
        Assert.Equal(HiveSkip.OtherNamespace, Skip(@"\REGISTRY\A\{aaaa}"));
        Assert.Equal(HiveSkip.OtherNamespace, Skip(@"\REGISTRY\WC\Silo1"));
        Assert.Equal(HiveSkip.UnmappedPath, Skip(@"\REGISTRY\MACHINE\OTHERDISK"));        // no guessing of the drive letter

        var soft = c[@"\REGISTRY\MACHINE\SOFTWARE"];
        Assert.Equal((HiveRoot.LocalMachine, "SOFTWARE", @"C:\Windows\System32\config\SOFTWARE"), (soft.Root, soft.SubKey, soft.FilePath));
        var classes = c[@"\REGISTRY\USER\S-1-5-21-1-2-3-1001_Classes"];
        Assert.Equal((HiveRoot.Users, "S-1-5-21-1-2-3-1001_Classes"), (classes.Root, classes.SubKey));
        Assert.False(classes.LatestFormat);                                                 // UsrClass.dat: standard format
        Assert.True(c[@"\REGISTRY\USER\S-1-5-21-1-2-3-1001"].LatestFormat);
    }

    [Fact]
    public void Device_paths_use_the_longest_known_device_only()
    {
        var devices = new Dictionary<string, string> { [@"\Device\HarddiskVolume1"] = "D:", [@"\Device\HarddiskVolume12"] = "E:" };
        Assert.Equal(@"E:\a\b", DevicePaths.ToDosPath(@"\Device\HarddiskVolume12\a\b", devices));
        Assert.Equal(@"D:\a", DevicePaths.ToDosPath(@"\device\harddiskvolume1\a", devices));
        Assert.Null(DevicePaths.ToDosPath(@"\Device\HarddiskVolume2\a", devices));
        Assert.Null(DevicePaths.ToDosPath(@"\Device\HarddiskVolume1", devices));            // the device alone is no file
        Assert.Equal(@"C:\x\y", DevicePaths.ToDosPath(@"\??\C:\x\y", devices));
    }

    [Fact]
    public void Analysis_measures_each_hive_and_leaves_no_files()
    {
        using var sb = new Sandbox();
        var saver = new FakeSaver(h => h.SubKey switch { "SOFTWARE" => 90 << 20, "SYSTEM" => 31 << 20, _ => 1 << 20 },
            h => h.SubKey == ".DEFAULT" ? 1314 : 0);
        var progress = new List<(int, int, string)>();
        var report = new CompactAnalyzer(new FakeSource(Typical), saver, sb.Root, _ => long.MaxValue, Sizes)
            .Analyze((i, n, h) => progress.Add((i, n, h.SubKey)));

        var by = report.Rows.ToDictionary(r => r.Hive.RegistryPath, r => r);
        var soft = by[@"\REGISTRY\MACHINE\SOFTWARE"];
        Assert.Equal((HiveStatus.Measured, 120L << 20, 90L << 20, 30L << 20), (soft.Status, soft.CurrentSize, soft.CompactSize, soft.Saving));
        Assert.Equal(HiveStatus.NoGain, by[@"\REGISTRY\MACHINE\SYSTEM"].Status);                  // would grow: not counted
        Assert.Equal((HiveStatus.SaveFailed, 1314), (by[@"\REGISTRY\USER\.DEFAULT"].Status, by[@"\REGISTRY\USER\.DEFAULT"].Error));
        Assert.Equal(HiveStatus.Skipped, by[@"\REGISTRY\MACHINE\SAM"].Status);
        Assert.Equal(Typical.Length, report.Rows.Count);
        var order = report.Ordered().ToList();
        Assert.Equal(new[] { "SOFTWARE", "S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001_Classes" }, order.Take(3).Select(r => r.Hive.SubKey));
        Assert.Equal(HiveStatus.NoGain, order[3].Status);
        Assert.Equal(HiveStatus.SaveFailed, order[4].Status);
        Assert.All(order.Skip(5), r => Assert.Equal(HiveStatus.Skipped, r.Status));
        Assert.Equal("Silo1", by[@"\REGISTRY\WC\Silo1"].Hive.Name);

        // SOFTWARE 120→90, NTUSER 2→1, UsrClass 2→1; SYSTEM grows, DEFAULT failed.
        Assert.Equal((124L << 20, 92L << 20, 32L << 20), (report.TotalCurrent, report.TotalCompact, report.TotalSaving));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, progress.Select(p => p.Item1));
        Assert.All(progress, p => Assert.Equal(5, p.Item2));
        Assert.True(saver.Prepared && saver.Released);
        Assert.All(saver.Files, f => Assert.False(File.Exists(f)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(sb.Root));                              // work folder removed
    }

    [Fact]
    public void Too_little_space_cancel_and_missing_rights_are_reported()
    {
        using var sb = new Sandbox();
        var saver = new FakeSaver(_ => 1 << 20);
        var tight = new CompactAnalyzer(new FakeSource(Typical), saver, sb.Root, _ => 100L << 20, Sizes).Analyze();
        Assert.Equal(HiveStatus.NoSpace, tight.Rows.Single(r => r.Hive.SubKey == "SOFTWARE").Status);   // 120 MB + margin > 100 MB
        Assert.Equal(HiveStatus.Measured, tight.Rows.Single(r => r.Hive.SubKey == "SYSTEM").Status);

        using var cts = new CancellationTokenSource();
        var cancelled = new CompactAnalyzer(new FakeSource(Typical), new FakeSaver(_ => 1 << 20), sb.Root, _ => long.MaxValue, Sizes)
            .Analyze((i, _, _) => { if (i == 2) cts.Cancel(); }, cts.Token);
        Assert.True(cancelled.Cancelled);
        Assert.Equal(2, cancelled.Rows.Count(r => r.Status is HiveStatus.Measured or HiveStatus.NoGain));
        Assert.Equal(3, cancelled.Rows.Count(r => r.Status == HiveStatus.Cancelled));
        Assert.Empty(Directory.EnumerateFileSystemEntries(sb.Root));

        var none = new CompactAnalyzer(new FakeSource([new HiveListEntry(@"\REGISTRY\MACHINE\SAM", Vol + @"\x\SAM")]), saver, sb.Root).Analyze();
        Assert.Equal(0, none.TotalSaving);                                                         // nothing to do: rights not needed

        var denied = new DeniedSaver();
        Assert.Throws<PrivilegeNotHeldException>(() => new CompactAnalyzer(new FakeSource(Typical), denied, sb.Root, _ => long.MaxValue, Sizes).Analyze());
        Assert.Empty(Directory.EnumerateFileSystemEntries(sb.Root));
    }

    private sealed class DeniedSaver : IHiveSaver
    {
        public IDisposable Prepare() => throw new PrivilegeNotHeldException("SeBackupPrivilege");
        public int Save(HiveCandidate hive, string file) => throw new InvalidOperationException("must not be called");
    }

    [Fact]
    public void Real_hive_list_maps_the_system_hives_to_existing_files()
    {
        var source = new WindowsHiveSource();                                                      // reads hivelist and device names only
        var devices = source.Devices();
        var c = HiveSelection.Select(source.Entries(), p => DevicePaths.ToDosPath(p, devices), FileSizes.Of);
        var soft = c.Single(x => x.RegistryPath.Equals(@"\REGISTRY\MACHINE\SOFTWARE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HiveSkip.None, soft.Skip);
        if (Environment.IsPrivilegedProcess) Assert.True(soft.FileSize > HiveSelection.SmallHiveLimit);   // config\ is admin-only
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var mine = c.Single(x => x.Root == HiveRoot.Users && x.SubKey.Equals(sid, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("NTUSER.DAT", mine.Name, ignoreCase: true);
        Assert.True(mine.FileSize > 0);                                                            // in use, still readable
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "config", "SOFTWARE"), soft.FilePath, ignoreCase: true);
        Assert.Contains(c, x => x.Root == HiveRoot.Users && x.SubKey.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HiveSkip.Excluded, c.Single(x => x.SubKey.Equals("SAM", StringComparison.OrdinalIgnoreCase)).Skip);
    }

    [Fact]
    public void Backup_rights_are_refused_without_elevation()
    {
        if (Environment.IsPrivilegedProcess)
            new WindowsHiveSaver().Prepare().Dispose();
        else
            Assert.Throws<PrivilegeNotHeldException>(() => new WindowsHiveSaver().Prepare());
    }
}
