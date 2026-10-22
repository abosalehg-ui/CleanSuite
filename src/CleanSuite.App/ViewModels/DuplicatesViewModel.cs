using System.IO;
using CleanSuite.Duplicates;
using CleanSuite.Safety;

namespace CleanSuite.App.ViewModels;

/// <summary>
/// Find Duplicate Files page: read-only scan into groups of identical files, review (nothing is checked after a scan, as in the
/// original), then removal of the checked copies to the Recycle Bin or a "Dup …" zip backup. A copy of every group is always
/// kept unless the page confirmed removing whole groups (<see cref="AllowWholeGroups"/>).
/// </summary>
public sealed class DuplicatesViewModel : ScanCleanViewModel<DupRow>
{
    private readonly Func<DupOptions?> _options;
    private readonly Func<RemovalMode> _mode;
    private readonly Func<DupOptions, CancellationToken, Action<string, long>, DupScanResult> _scan;
    private readonly Func<IReadOnlyList<DupGroup>, ISet<string>, RemovalMode, bool, CancellationToken, DupRemovalResult> _clean;

    /// <param name="options">The page's criteria, or null when they are not valid (the page has told the user).</param>
    /// <param name="scan">Tests replace the scanner; default: <see cref="DuplicateScanner"/>.</param>
    /// <param name="clean">Tests replace the remover; default: <see cref="DuplicateRemover"/>.</param>
    public DuplicatesViewModel(Func<DupOptions?> options, Func<RemovalMode> mode,
        Func<DupOptions, CancellationToken, Action<string, long>, DupScanResult>? scan = null,
        Func<IReadOnlyList<DupGroup>, ISet<string>, RemovalMode, bool, CancellationToken, DupRemovalResult>? clean = null)
    {
        _options = options;
        _mode = mode;
        _scan = scan ?? ((o, ct, progress) =>
        {
            var scanner = new DuplicateScanner(o);
            scanner.Progress += (phase, n) => progress(phase, n);
            return scanner.Scan(ct);
        });
        _clean = clean ?? ((groups, selected, m, whole, ct) => new DuplicateRemover().Remove(groups, selected, m, null, whole, ct));
        CheckOneCommand = new RelayCommand(CheckOnePerGroup, () => !IsBusy && Rows.Count > 0);
    }

    /// <summary>The original's "Check mark one file in each group": checks the first file of every group (all others stay).</summary>
    public RelayCommand CheckOneCommand { get; }

    public DupScanResult? Result { get; private set; }
    public DupRemovalResult? LastClean { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public bool WasCancelled { get; private set; }
    /// <summary>Progress of the running scan: "files" (listing) or "hash" (comparing content, in percent).</summary>
    public (string Phase, long Value)? Progress { get; private set; }
    /// <summary>Set by the page's confirmation when removing every copy of some groups was confirmed.</summary>
    public bool AllowWholeGroups { get; set; }

    public long SelectedBytes { get; private set; }

    /// <summary>The status line has something new to show.</summary>
    public event Action? Changed;
    /// <summary>A removal finished, with its result.</summary>
    public event Action<DupRemovalResult>? Cleaned;
    /// <summary>The scan needs at least one folder or drive (the page says so).</summary>
    public event Action? NoRoots;

    public IReadOnlyList<DupGroup> Groups => Result?.Groups ?? Array.Empty<DupGroup>();

    protected override async Task ScanAsync(CancellationToken ct)
    {
        if (_options() is not { } o) return;
        if (o.Roots.Count == 0) { NoRoots?.Invoke(); return; }
        WasCancelled = false;
        LastClean = null;
        Progress = null;
        Changed?.Invoke();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await Task.Run(() => _scan(o, ct, (phase, n) => dispatcher.BeginInvoke(() =>
        {
            if (!IsBusy) return;
            Progress = (phase, n);
            Changed?.Invoke();
        })), ct);
        ElapsedSeconds = sw.Elapsed.TotalSeconds;
        Result = result;
        Rows = result.Groups.SelectMany(g => g.Files.Select(f => new DupRow(g, f))).ToList();
        Changed?.Invoke();
    }

    protected override async Task CleanAsync(IReadOnlyList<DupRow> chosen, CancellationToken ct)
    {
        var groups = Groups;
        var selected = new HashSet<string>(chosen.Select(r => r.File.FullPath), StringComparer.OrdinalIgnoreCase);
        var mode = _mode();
        bool whole = AllowWholeGroups;
        WasCancelled = false;
        try
        {
            var res = await Task.Run(() => _clean(groups, selected, mode, whole, ct), ct);
            LastClean = res;
            var failed = new HashSet<string>(res.Failed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var rows = Rows.Where(r => !r.Selected || failed.Contains(r.File.FullPath) || File.Exists(r.File.FullPath)).ToList();
            // A group with a single remaining file is no longer a duplicate group.
            Rows = rows.GroupBy(r => r.GroupNumber).Where(g => g.Count() >= 2).SelectMany(g => g).ToList();
            Cleaned?.Invoke(res);
        }
        finally
        {
            AllowWholeGroups = false;
            Changed?.Invoke();
        }
    }

    private void CheckOnePerGroup() =>
        SetMany(Rows.GroupBy(r => r.GroupNumber).Where(g => g.Count() >= 2).Select(g => g.First()), _ => true);

    protected override void Cancelled()
    {
        WasCancelled = true;
        Changed?.Invoke();
    }

    protected override void RowsReplaced() => SelectedBytes = Rows.Where(r => r.Selected).Sum(r => r.File.Size);

    protected override void SelectionChanged(DupRow row) => SelectedBytes += row.Selected ? row.File.Size : -row.File.Size;

    protected override void SelectionSettled() => Changed?.Invoke();

    protected override void Requery()
    {
        base.Requery();
        CheckOneCommand?.RaiseCanExecuteChanged();
    }
}
