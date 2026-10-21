using CleanSuite.RegistryClean;
using CleanSuite.Safety;

namespace CleanSuite.App.ViewModels;

/// <summary>Registry page: read-only scan, review, removal of the checked findings after a .reg backup.</summary>
public sealed class RegistryViewModel : ScanCleanViewModel<RegistryRow>
{
    private readonly Func<RegScanOptions> _options;
    private readonly Func<RegScanOptions, CancellationToken, RegScanResult> _scan;
    private readonly Func<IReadOnlyList<RegFinding>, CancellationToken, RegCleanResult> _clean;

    /// <param name="scan">Tests replace the scanner; default: the registry of this computer.</param>
    /// <param name="clean">Tests replace the cleaner; default: <see cref="RegistryCleaner"/>.</param>
    public RegistryViewModel(Func<RegScanOptions> options,
        Func<RegScanOptions, CancellationToken, RegScanResult>? scan = null,
        Func<IReadOnlyList<RegFinding>, CancellationToken, RegCleanResult>? clean = null)
    {
        _options = options;
        _scan = scan ?? ((o, ct) => new RegistryScanner(WindowsRegistryRoots.Instance, o).Scan(ct));
        _clean = clean ?? ((f, ct) => new RegistryCleaner().Clean(f, ct));
    }

    public RegScanResult? Result { get; private set; }
    public string? LastBackup { get; private set; }
    public bool WasCancelled { get; private set; }

    /// <summary>A scan finished (rows replaced).</summary>
    public event Action? Scanned;
    /// <summary>A clean finished, with its result.</summary>
    public event Action<RegCleanResult>? Cleaned;

    protected override async Task ScanAsync(CancellationToken ct)
    {
        WasCancelled = false;
        var o = _options();
        var result = await Task.Run(() => _scan(o, ct), ct);
        Result = result;
        LastBackup = null;
        Rows = result.Findings.Select(f => new RegistryRow(f)).ToList();
        Scanned?.Invoke();
    }

    protected override async Task CleanAsync(IReadOnlyList<RegistryRow> chosen, CancellationToken ct)
    {
        var findings = chosen.Select(r => r.Finding).ToList();
        var res = await Task.Run(() => _clean(findings, ct), ct);
        LastBackup = res.BackupPath;
        var done = findings.Except(res.Skipped).Except(res.Failed.Select(f => f.Finding)).ToHashSet();
        Rows = Rows.Where(r => !done.Contains(r.Finding)).ToList();
        Cleaned?.Invoke(res);
    }

    protected override void Cancelled()
    {
        WasCancelled = true;
        Scanned?.Invoke();
    }
}
