using CleanSuite.Safety;

namespace CleanSuite.History;

public sealed class HistoryRunResult
{
    public int Succeeded { get; init; }
    public long BytesFreed { get; init; }
    public IReadOnlyList<(HistoryAction Action, string Reason)> Failed { get; init; } = Array.Empty<(HistoryAction, string)>();
    public required string JournalPath { get; init; }
    /// <summary>.reg backup of every registry change of the session, or null when the registry was not changed.</summary>
    public string? RegistryBackupPath { get; init; }
}

/// <summary>Executes reviewed History actions in order, journaling every change and backing up the registry first.</summary>
public sealed class HistoryExecutor
{
    private readonly ProtectedPaths _protected;
    private readonly string? _journalBase;
    private readonly IRegistryRoots _registry;
    private readonly string? _backupFolder;

    public HistoryExecutor(ProtectedPaths? protectedPaths = null, string? journalBase = null,
        IRegistryRoots? registry = null, string? backupFolder = null)
    {
        _protected = protectedPaths ?? ForHistory();
        _journalBase = journalBase;
        _registry = registry ?? WindowsRegistryRoots.Instance;
        _backupFolder = backupFolder;
    }

    /// <summary>
    /// The system guard plus the explicit exceptions the History items need inside System32/SysWOW64 (exact folders
    /// from the original's rules: print spool, WMI and service logs, font cache, .NET logs of the system profile).
    /// </summary>
    public static ProtectedPaths ForHistory()
    {
        var p = ProtectedPaths.ForCurrentSystem();
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (sys.Length == 0 || win.Length == 0) return p;
        foreach (var rel in new[] { @"spool\PRINTERS", @"wbem\Logs", "LogFiles", "FNTCACHE.DAT",
                                    @"config\systemprofile\AppData\Local\Microsoft\CLR_v4.0",
                                    @"config\systemprofile\AppData\Local\Microsoft\CLR_v4.0_32" })
            p.Allow(Path.Combine(sys, rel));
        foreach (var rel in new[] { @"SysWOW64\config\systemprofile\AppData\Local\Microsoft\CLR_v4.0",
                                    @"SysWOW64\config\systemprofile\AppData\Local\Microsoft\CLR_v4.0_32" })
            p.Allow(Path.Combine(win, rel));
        return p;
    }

    public HistoryRunResult Execute(IReadOnlyList<HistoryAction> actions, CancellationToken ct = default)
    {
        using var journal = new OperationJournal("history", _journalBase);
        using var regBackup = new RegBackupWriter(_backupFolder, journal);
        var ctx = new ExecutionContext
        {
            Journal = journal, Protected = _protected, Cancellation = ct, Registry = _registry, RegistryBackup = regBackup,
        };
        int ok = 0;
        long freed = 0;
        var failed = new List<(HistoryAction, string)>();
        foreach (var a in actions)
        {
            ct.ThrowIfCancellationRequested();
            ActionResult r;
            try { r = a.Execute(ctx); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                r = ActionResult.Fail(e.Message);
                journal.Record(a.Kind, a.Target, false, error: e.Message);
            }
            if (r.Success) { ok++; freed += r.BytesFreed; }
            else failed.Add((a, r.Error ?? "failed"));
        }
        return new HistoryRunResult
        {
            Succeeded = ok, BytesFreed = freed, Failed = failed, JournalPath = journal.FilePath, RegistryBackupPath = regBackup.FilePath,
        };
    }
}
