namespace CleanSuite.App.ViewModels;

/// <summary>
/// The state every tool page shares: one busy flag for every operation (a refresh, a scan, a change never overlap), whether the
/// running operation changes something (the window waits for it before closing), cancellation, and errors that never escape:
/// they go to <see cref="Error"/> and the page returns to a usable state. Pages with a scan → review → clean flow use
/// <see cref="ScanCleanViewModel{TRow}"/>; list pages with actions (startup programs, installed programs, backups) use this directly.
/// </summary>
public abstract class ToolViewModel : ViewModelBase
{
    private bool _isBusy, _isChanging, _cancellable;
    private CancellationTokenSource? _cts;

    protected ToolViewModel() => CancelCommand = new RelayCommand(Cancel, () => IsBusy && _cancellable);

    public RelayCommand CancelCommand { get; }

    /// <summary>Called with every unexpected error (the page shows it).</summary>
    public event Action<Exception>? Error;

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) Requery(); }
    }

    /// <summary>True while files, registry data or settings are being changed (the window does not close then: the step would stop half-way).</summary>
    public bool IsChanging
    {
        get => _isChanging;
        private set => Set(ref _isChanging, value);
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>Runs one operation under the busy flag; a second one at the same time is ignored (returns false).</summary>
    /// <param name="cancellable">Whether Cancel (Esc) may stop it; changes that must not stop half-way pass false.</param>
    protected async Task<bool> Run(Func<CancellationToken, Task> work, bool changing, bool cancellable = true)
    {
        if (IsBusy) return false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _cancellable = cancellable;
        IsChanging = changing;
        IsBusy = true;
        try { await work(cts.Token); }
        catch (OperationCanceledException) { Cancelled(); }
        catch (Exception ex) { Error?.Invoke(ex); }
        finally
        {
            IsChanging = false;
            IsBusy = false;
            _cts = null;
            cts.Dispose();
        }
        return true;
    }

    /// <summary>Called when an operation was cancelled.</summary>
    protected virtual void Cancelled() { }

    /// <summary>Re-evaluates the commands' CanExecute (busy state or selection changed).</summary>
    protected virtual void Requery() => CancelCommand.RaiseCanExecuteChanged();
}
