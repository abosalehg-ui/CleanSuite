using System.IO;
using CleanSuite.Compat;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.App.ViewModels;

/// <summary>What the junk page's options say when a scan or clean starts.</summary>
public sealed record JunkInput(IReadOnlyList<string> Roots, JunkOptions Options, RemovalMode Mode);

/// <summary>
/// Junk files page: read-only scan of the chosen drives, folders and temp folders, review, then removal of the checked files
/// to the Recycle Bin, a zip backup, or (when chosen) for good. Totals of the selection (count and bytes) are kept as rows are
/// checked, so the window title and the clean button never re-count the whole list.
/// </summary>
public sealed class JunkViewModel : ScanCleanViewModel<JunkRow>
{
    private readonly Func<JunkInput> _input;
    private readonly Func<JunkInput, CancellationToken, Action<long>, (JunkScanResult Result, IReadOnlyList<string> Temps)> _scan;
    private readonly Func<JunkRemovalRequest, CancellationToken, JunkRemovalResult> _clean;
    private long _selectedBytes;

    /// <param name="input">The page's current choices (read on the UI thread when an operation starts).</param>
    /// <param name="scan">Tests replace the scanner; default: <see cref="JunkScanner"/> with the ignore lists of this computer.</param>
    /// <param name="clean">Tests replace the remover; default: <see cref="JunkRemover"/>.</param>
    public JunkViewModel(Func<JunkInput> input,
        Func<JunkInput, CancellationToken, Action<long>, (JunkScanResult, IReadOnlyList<string>)>? scan = null,
        Func<JunkRemovalRequest, CancellationToken, JunkRemovalResult>? clean = null)
    {
        _input = input;
        _scan = scan ?? DefaultScan;
        _clean = clean ?? ((req, ct) => new JunkRemover().Remove(req, ct));
    }

    public AceJunkSettings Settings { get; set; } = new();
    public JunkScanResult? Result { get; private set; }
    public JunkRemovalResult? LastClean { get; private set; }
    public IReadOnlyList<string> TempFolders { get; private set; } = Array.Empty<string>();
    public double ElapsedSeconds { get; private set; }
    public bool WasCancelled { get; private set; }
    /// <summary>The last cancelled operation was a clean (not a scan).</summary>
    public bool CleanWasCancelled { get; private set; }
    /// <summary>Files removed before a clean was cancelled.</summary>
    public int CancelledRemoved { get; private set; }
    /// <summary>Files looked at so far by the running scan.</summary>
    public long Progress { get; private set; }

    public long SelectedBytes => _selectedBytes;

    /// <summary>The status line or the window title has something new to show.</summary>
    public event Action? Changed;
    /// <summary>A clean finished, with its result.</summary>
    public event Action<JunkRemovalResult>? Cleaned;
    /// <summary>A scan was asked for with no drive, folder or temp folders chosen (the page says so).</summary>
    public event Action? NothingToScan;

    private (JunkScanResult, IReadOnlyList<string>) DefaultScan(JunkInput input, CancellationToken ct, Action<long> progress)
    {
        var ignore = IgnoreList.LoadFolders(AceInstallation.DefaultJunkIgnoreFolders());
        var temps = input.Options.CleanTempFolders ? TempFolderLocator.Locate(new WindowsEnvironment()) : Array.Empty<string>();
        var req = new JunkScanRequest
        {
            Roots = input.Roots,
            Patterns = Settings.ToPatternSet(),
            Exclusions = Settings.Exclusions,
            Ignore = ignore,
            Options = input.Options,
            TempFolders = temps,
        };
        return (new JunkScanner().Scan(req, ct, progress), temps);
    }

    protected override async Task ScanAsync(CancellationToken ct)
    {
        var input = _input();
        if (input.Roots.Count == 0 && !input.Options.CleanTempFolders) { NothingToScan?.Invoke(); return; }
        WasCancelled = CleanWasCancelled = false;
        LastClean = null;
        Progress = 0;
        Rows = Array.Empty<JunkRow>();
        Changed?.Invoke();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var (result, temps) = await Task.Run(() => _scan(input, ct,
            n => dispatcher.BeginInvoke(() => { if (IsBusy) { Progress = n; Changed?.Invoke(); } })), ct);
        ElapsedSeconds = sw.Elapsed.TotalSeconds;
        Result = result;
        TempFolders = temps;
        Rows = result.Items.Select(i => new JunkRow(i)).ToList();
        Changed?.Invoke();
    }

    protected override async Task CleanAsync(IReadOnlyList<JunkRow> chosen, CancellationToken ct)
    {
        var input = _input();
        var req = new JunkRemovalRequest
        {
            Items = chosen.Select(r => r.Item).ToList(),
            Mode = input.Mode,
            TempFolders = TempFolders,
            DeleteEmptyTempFolders = input.Options.DeleteEmptyTempFolders && input.Options.CleanTempFolders,
        };
        CancelledRemoved = 0;
        WasCancelled = CleanWasCancelled = false;
        try
        {
            var res = await Task.Run(() => _clean(req, ct), ct);
            LastClean = res;
            DropRemoved(chosen);
            Cleaned?.Invoke(res);
        }
        catch (OperationCanceledException)
        {
            // Files removed before the cancel are gone: drop their rows and say how many there were.
            CancelledRemoved = DropRemoved(chosen);
            CleanWasCancelled = true;
            throw;
        }
        finally { Changed?.Invoke(); }
    }

    /// <summary>Removes the rows whose file no longer exists; returns how many.</summary>
    private int DropRemoved(IReadOnlyList<JunkRow> chosen)
    {
        var gone = chosen.Where(r => !File.Exists(r.Item.FullPath)).ToHashSet();
        if (gone.Count > 0) Rows = Rows.Where(r => !gone.Contains(r)).ToList();
        return gone.Count;
    }

    /// <summary>Takes rows out of the list (excluded with "Always exclude").</summary>
    public void Remove(IReadOnlyCollection<JunkRow> rows)
    {
        if (IsBusy) return;
        var set = rows.ToHashSet();
        Rows = Rows.Where(r => !set.Contains(r)).ToList();
        Changed?.Invoke();
    }

    protected override void Cancelled()
    {
        WasCancelled = true;
        Changed?.Invoke();
    }

    protected override void RowsReplaced() => _selectedBytes = Rows.Where(r => r.Selected).Sum(r => r.Item.Size);

    protected override void SelectionChanged(JunkRow row) => _selectedBytes += row.Selected ? row.Item.Size : -row.Item.Size;

    protected override void SelectionSettled() => Changed?.Invoke();
}
