using CleanSuite.Shortcuts;

namespace CleanSuite.App.ViewModels;

/// <summary>Invalid shortcuts page: read-only scan, review, the checked items to the Recycle Bin.</summary>
public sealed class ShortcutsViewModel : ScanCleanViewModel<ShortcutRow>
{
    /// <summary>What a scan found, with the counts shown in the status line.</summary>
    public sealed record ScanOutcome(IReadOnlyList<ShortcutItem> Items, int ShortcutsScanned, int FilesScanned);

    private readonly Func<ShortcutScanOptions> _options;
    private readonly Func<ShortcutScanOptions, ScanOutcome> _scan;
    private readonly Func<IReadOnlyList<ShortcutItem>, ShortcutRunResult> _clean;

    /// <param name="scan">Tests replace the scanner; default: the shortcut folders or drives of this computer.</param>
    /// <param name="clean">Tests replace the cleaner; default: <see cref="ShortcutCleaner"/> (Recycle Bin, journaled).</param>
    public ShortcutsViewModel(Func<ShortcutScanOptions> options,
        Func<ShortcutScanOptions, ScanOutcome>? scan = null,
        Func<IReadOnlyList<ShortcutItem>, ShortcutRunResult>? clean = null)
    {
        _options = options;
        _scan = scan ?? (o =>
        {
            var s = new ShortcutScanner(o);
            var items = s.Scan();
            return new ScanOutcome(items, s.ShortcutsScanned, s.FilesScanned);
        });
        _clean = clean ?? (items => new ShortcutCleaner().Remove(items, silent: false));
    }

    public ScanOutcome? Last { get; private set; }
    public ShortcutRunResult? LastClean { get; private set; }

    /// <summary>A scan finished (rows replaced).</summary>
    public event Action? Scanned;
    /// <summary>A removal finished, with its result.</summary>
    public event Action<ShortcutRunResult>? Cleaned;

    /// <summary>The shortcut scanner cannot be interrupted.</summary>
    protected override bool CanCancel => false;

    protected override async Task ScanAsync(CancellationToken ct)
    {
        var o = _options();
        var outcome = await Task.Run(() => _scan(o), ct);
        Last = outcome;
        LastClean = null;
        Rows = outcome.Items.Select(i => new ShortcutRow(i)).ToList();
        Scanned?.Invoke();
    }

    protected override async Task CleanAsync(IReadOnlyList<ShortcutRow> chosen, CancellationToken ct)
    {
        var items = chosen.Select(r => r.Item).ToList();
        var res = await Task.Run(() => _clean(items), ct);
        // Rows that were removed go; the ones that failed stay checked so they can be retried.
        var failed = res.Failed.Select(f => f.Item.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = items.Where(i => !failed.Contains(i.FullPath)).ToHashSet();
        Rows = Rows.Where(r => !removed.Contains(r.Item)).ToList();
        LastClean = res;
        Cleaned?.Invoke(res);
    }

    /// <summary>A shortcut was pointed at a new target: it is no longer invalid.</summary>
    public void Retargeted(ShortcutRow row)
    {
        if (IsBusy) return;
        Rows = Rows.Where(r => !ReferenceEquals(r, row)).ToList();
    }
}
