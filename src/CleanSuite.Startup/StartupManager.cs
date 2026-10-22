using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.Startup;

/// <summary>
/// Auto-Start Manager (Run keys and startup folders), with the original's behaviour.
/// Reads every disable mechanism the original writes, so entries it disabled show as
/// disabled and can be enabled again. New disables use StartupApproved where Windows has it (the standard, also used by
/// Task Manager); the remaining locations use the original's sibling key "&lt;RunKey&gt;-".
/// Every change is journaled, and registry data is exported to a .reg backup before it is changed.
/// </summary>
public sealed class StartupManager
{
    private readonly IRegistryRoots _roots;
    private readonly IReadOnlyList<StartupLocation> _locations;
    private readonly string? _journalBase;
    private readonly string _backupFolder;
    private readonly StartupMonitor? _monitor;

    public StartupManager(IRegistryRoots? roots = null, IReadOnlyList<StartupLocation>? locations = null,
        string? journalBase = null, string? backupFolder = null, StartupMonitor? monitor = null)
    {
        _roots = roots ?? WindowsRegistryRoots.Instance;
        _locations = locations ?? StartupLocations.RegistryLocations.Concat(StartupLocations.FolderLocations()).ToList();
        _journalBase = journalBase;
        _backupFolder = backupFolder ?? CleanSuitePaths.Backups;
        _monitor = monitor;
    }

    // ---------------- scan (read-only) ----------------

    public IReadOnlyList<StartupEntry> Scan(CancellationToken ct = default)
    {
        var list = new List<StartupEntry>();
        foreach (var loc in _locations)
        {
            ct.ThrowIfCancellationRequested();
            if (loc.Kind == LocationKind.RegistryRun) ScanRun(loc, list);
            else ScanFolder(loc, list);
        }
        return _monitor?.Annotate(list) ?? list;
    }

    private RegistryKey? Open(RegHive hive, string key)
    {
        try { return _roots.Root(hive).OpenSubKey(key, writable: false); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    private (EntryState State, DateTime? Since)? ApprovedState(StartupLocation loc, string name)
    {
        if (loc.ApprovedKey == null) return null;
        using var k = Open(loc.Hive, loc.ApprovedKey);
        if (k?.GetValue(name) is not byte[] { Length: >= 4 } data) return null;
        if ((data[0] & 1) == 0) return (EntryState.Enabled, null);
        DateTime? since = null;
        if (data.Length >= 12)
        {
            long ft = BitConverter.ToInt64(data, 4);
            if (ft > 0) try { since = DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { }
        }
        return (EntryState.DisabledApproved, since);
    }

    private void ScanRun(StartupLocation loc, List<StartupEntry> list)
    {
        void Read(string key, bool legacyDisabled)
        {
            using var k = Open(loc.Hive, key);
            if (k == null) return;
            foreach (var name in k.GetValueNames())
            {
                if (name.Length == 0) continue;                                  // (default) is not an entry
                if (k.GetValueKind(name) is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                var cmd = k.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                var state = legacyDisabled ? (EntryState.DisabledLegacy, (DateTime?)null) : ApprovedState(loc, name) ?? (EntryState.Enabled, null);
                list.Add(Describe(new StartupEntry(loc, name, cmd, state.Item1, state.Item2), CommandLineTarget.Resolve(cmd)));
            }
        }
        Read(loc.Key, legacyDisabled: false);
        Read(loc.Key + "-", legacyDisabled: true);
    }

    private void ScanFolder(StartupLocation loc, List<StartupEntry> list)
    {
        if (!Directory.Exists(loc.Key)) return;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(loc.Key).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var state = ApprovedState(loc, name) ?? (EntryState.Enabled, null);
            var target = name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShellLinkFile.Read(f)?.Target : f;
            list.Add(Describe(new StartupEntry(loc, name, f, state.Item1, state.Item2), target));
        }
    }

    private static StartupEntry Describe(StartupEntry e, string? target)
    {
        var (company, description) = CommandLineTarget.VersionInfo(target);
        return e with { TargetPath = target, Company = company, Description = description };
    }

    // ---------------- changes ----------------

    public sealed record ChangeResult(bool Success, string? Error, string JournalPath, string? RegistryBackupPath);

    public ChangeResult Disable(StartupEntry e) => Change("startup-disable", e, (j, b) => SetEnabled(e, false, j, b));
    public ChangeResult Enable(StartupEntry e) => Change("startup-enable", e, (j, b) => SetEnabled(e, true, j, b));
    public ChangeResult Delete(StartupEntry e) => Change("startup-delete", e, (j, b) => DeleteEntry(e, j, b));

    private ChangeResult Change(string tool, StartupEntry e, Action<OperationJournal, RegBackupWriter> work)
    {
        using var journal = new OperationJournal("startup", _journalBase);
        using var backup = new RegBackupWriter(_backupFolder, journal);
        try
        {
            work(journal, backup);
            journal.Record(tool, e.Location.Id + " :: " + e.Name, true, backupRef: backup.FilePath);
            _monitor?.Record(e, tool);
            return new ChangeResult(true, null, journal.FilePath, backup.FilePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            var msg = ex is UnauthorizedAccessException or System.Security.SecurityException ? "requires administrator rights" : ex.Message;
            journal.Record(tool, e.Location.Id + " :: " + e.Name, false, backupRef: backup.FilePath, error: msg);
            return new ChangeResult(false, msg, journal.FilePath, backup.FilePath);
        }
    }

    private void SetEnabled(StartupEntry e, bool enable, OperationJournal journal, RegBackupWriter backup)
    {
        var loc = e.Location;
        var root = _roots.Root(loc.Hive);
        if (e.State == EntryState.DisabledLegacy)
        {
            if (!enable) return;                                              // already disabled
            MoveValue(root, loc.Hive, loc.Key + "-", loc.Key, e.Name, backup);
            return;
        }
        if (loc.ApprovedKey != null)
        {
            // StartupApproved: 12 bytes, DWORD 2 + zero FILETIME = enabled, DWORD 3 + FILETIME of the change = disabled.
            backup.ExportValue(root, loc.Hive, loc.ApprovedKey, e.Name);
            using var k = root.CreateSubKey(loc.ApprovedKey, writable: true);
            var data = new byte[12];
            data[0] = (byte)(enable ? 2 : 3);
            if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            k.SetValue(e.Name, data, RegistryValueKind.Binary);
            journal.Record(enable ? "approved-enable" : "approved-disable", RegBackupWriter.HivePath(loc.Hive) + "\\" + loc.ApprovedKey + " :: " + e.Name, true);
            return;
        }
        if (!enable) MoveValue(root, loc.Hive, loc.Key, loc.Key + "-", e.Name, backup);
    }

    /// <summary>Moves a value with its type and data to another key (the original's "&lt;key&gt;-" mechanism).</summary>
    private static void MoveValue(RegistryKey root, RegHive hive, string fromKey, string toKey, string name, RegBackupWriter backup)
    {
        using var from = root.OpenSubKey(fromKey, writable: true) ?? throw new InvalidOperationException("entry no longer exists");
        if (!from.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("entry no longer exists");
        backup.ExportValue(root, hive, fromKey, name);
        var kind = from.GetValueKind(name);
        var data = from.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!;
        using (var to = root.CreateSubKey(toKey, writable: true)) to.SetValue(name, data, kind);
        from.DeleteValue(name);
    }

    private void DeleteEntry(StartupEntry e, OperationJournal journal, RegBackupWriter backup)
    {
        var loc = e.Location;
        var root = _roots.Root(loc.Hive);
        if (loc.Kind == LocationKind.RegistryRun)
        {
            var key = e.State == EntryState.DisabledLegacy ? loc.Key + "-" : loc.Key;
            if (!backup.ExportValue(root, loc.Hive, key, e.Name)) throw new InvalidOperationException("entry no longer exists");
            using var k = root.OpenSubKey(key, writable: true)!;
            k.DeleteValue(e.Name, throwOnMissingValue: false);
        }
        else
        {
            // Startup-folder files are moved to the backup folder, never deleted.
            if (!File.Exists(e.Command)) throw new InvalidOperationException("entry no longer exists");
            var dest = Path.Combine(_backupFolder, "Startup " + BackupNames.Timestamp());
            Directory.CreateDirectory(dest);
            var target = Path.Combine(dest, e.Name);
            File.Move(e.Command, target);
            journal.Record("move", e.Command, true, backupRef: target);
        }
        if (loc.ApprovedKey != null && backup.ExportValue(root, loc.Hive, loc.ApprovedKey, e.Name))
        {
            using var k = root.OpenSubKey(loc.ApprovedKey, writable: true);
            k?.DeleteValue(e.Name, throwOnMissingValue: false);
        }
    }
}
