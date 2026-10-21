using System.ComponentModel;

namespace CleanSuite.App.ViewModels;

/// <summary>A result row the user can check for removal.</summary>
public interface ISelectableRow : INotifyPropertyChanged
{
    bool Selected { get; set; }
}

/// <summary>
/// The scan → review → clean pattern shared by the tool pages. One busy flag covers every operation, so a scan and a clean
/// (or two cleans) can never run at the same time on the same rows, and selection changes are refused while one runs.
/// Errors never escape: they are passed to <see cref="Error"/> and the page returns to a usable state.
/// </summary>
public abstract class ScanCleanViewModel<TRow> : ViewModelBase where TRow : ISelectableRow
{
    private IReadOnlyList<TRow> _rows = Array.Empty<TRow>();
    private bool _isBusy, _isChanging;
    private CancellationTokenSource? _cts;

    protected ScanCleanViewModel()
    {
        ScanCommand = new AsyncRelayCommand(() => Run(ScanAsync, changing: false), () => !IsBusy);
        CleanCommand = new AsyncRelayCommand(CleanSelectedAsync, () => CanClean);
        SelectAllCommand = new RelayCommand(() => SetAll(true), () => !IsBusy && _rows.Count > 0);
        SelectNoneCommand = new RelayCommand(() => SetAll(false), () => !IsBusy && _rows.Count > 0);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy && CanCancel);
    }

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>Called with every unexpected error (the page shows it).</summary>
    public event Action<Exception>? Error;

    /// <summary>Asked before a clean with the rows to remove; false cancels (the page shows its confirmation dialog).</summary>
    public Func<IReadOnlyList<TRow>, bool>? Confirm { get; set; }

    public IReadOnlyList<TRow> Rows
    {
        get => _rows;
        protected set
        {
            foreach (var r in _rows) r.PropertyChanged -= RowChanged;
            _rows = value;
            foreach (var r in _rows) r.PropertyChanged += RowChanged;
            Raise();
            Requery();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) Requery(); }
    }

    /// <summary>True while a clean runs (the window does not close then: the step would stop half-way).</summary>
    public bool IsChanging
    {
        get => _isChanging;
        private set => Set(ref _isChanging, value);
    }

    public bool CanClean => !IsBusy && _rows.Any(r => r.Selected);
    public int SelectedCount => _rows.Count(r => r.Selected);

    /// <summary>Whether the running scan can be cancelled.</summary>
    protected virtual bool CanCancel => true;

    protected abstract Task ScanAsync(CancellationToken ct);
    protected abstract Task CleanAsync(IReadOnlyList<TRow> chosen, CancellationToken ct);

    private Task CleanSelectedAsync()
    {
        var chosen = _rows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0 || Confirm?.Invoke(chosen) == false) return Task.CompletedTask;
        return Run(ct => CleanAsync(chosen, ct), changing: true);
    }

    /// <summary>Runs one operation under the busy flag; a second one at the same time is ignored.</summary>
    protected async Task Run(Func<CancellationToken, Task> work, bool changing)
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsChanging = changing;
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { Cancelled(); }
        catch (Exception ex) { Error?.Invoke(ex); }
        finally
        {
            IsChanging = false;
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Called when an operation was cancelled.</summary>
    protected virtual void Cancelled() { }

    private void SetAll(bool on)
    {
        if (IsBusy) return;
        foreach (var r in _rows) r.Selected = on;
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ISelectableRow.Selected)) return;
        Raise(nameof(SelectedCount));
        Requery();
    }

    protected void Requery()
    {
        Raise(nameof(CanClean));
        ScanCommand.RaiseCanExecuteChanged();
        CleanCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        SelectNoneCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}
