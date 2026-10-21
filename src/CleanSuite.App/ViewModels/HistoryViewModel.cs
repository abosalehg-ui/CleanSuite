using CleanSuite.History;

namespace CleanSuite.App.ViewModels;

/// <summary>Erase History page: read-only analysis into reviewable actions, then execution of the checked ones.</summary>
public sealed class HistoryViewModel : ScanCleanViewModel<HistoryActionRow>
{
    private readonly Func<CancellationToken, HistoryPlan> _analyse;
    private readonly Func<IReadOnlyList<HistoryAction>, CancellationToken, HistoryRunResult> _execute;

    /// <param name="analyse">Builds the plan from the page's current choices (runs on a worker thread).</param>
    /// <param name="execute">Tests replace the executor; default: <see cref="HistoryExecutor"/>.</param>
    public HistoryViewModel(Func<CancellationToken, HistoryPlan> analyse,
        Func<IReadOnlyList<HistoryAction>, CancellationToken, HistoryRunResult>? execute = null)
    {
        _analyse = analyse;
        _execute = execute ?? ((a, ct) => new HistoryExecutor().Execute(a, ct));
    }

    public HistoryPlan? Plan { get; private set; }
    public HistoryRunResult? Last { get; private set; }
    public bool WasCancelled { get; private set; }

    public event Action? Analysed;
    public event Action<HistoryRunResult>? Cleaned;

    protected override async Task ScanAsync(CancellationToken ct)
    {
        WasCancelled = false;
        var plan = await Task.Run(() => _analyse(ct), ct);
        Plan = plan;
        Last = null;
        Rows = plan.Actions.Select(a => new HistoryActionRow(a)).ToList();
        Analysed?.Invoke();
    }

    protected override async Task CleanAsync(IReadOnlyList<HistoryActionRow> chosen, CancellationToken ct)
    {
        var actions = chosen.Select(r => r.Action).ToList();
        var res = await Task.Run(() => _execute(actions, ct), ct);
        Last = res;
        var failed = res.Failed.Select(f => f.Action).ToHashSet();
        Rows = Rows.Where(r => !actions.Contains(r.Action) || failed.Contains(r.Action)).ToList();
        Cleaned?.Invoke(res);
    }

    protected override void Cancelled()
    {
        WasCancelled = true;
        Analysed?.Invoke();
    }
}
