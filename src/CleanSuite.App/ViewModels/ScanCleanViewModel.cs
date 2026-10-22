using System.ComponentModel;

namespace CleanSuite.App.ViewModels;

/// <summary>A result row the user can check for removal.</summary>
public interface ISelectableRow : INotifyPropertyChanged
{
    bool Selected { get; set; }
}

/// <summary>The commands the main window's keyboard shortcuts reach on any scan → review → clean page (Ctrl+A, Delete).</summary>
public interface IScanCleanCommands
{
    System.Windows.Input.ICommand SelectAllCommand { get; }
    System.Windows.Input.ICommand CleanCommand { get; }
}

/// <summary>
/// The scan → review → clean pattern shared by the tool pages (junk files, duplicates, erase history, shortcuts, registry).
/// One busy flag covers every operation, so a scan and a clean (or two cleans) can never run at the same time on the same rows,
/// and selection changes are refused while one runs. The number of checked rows is kept as rows change, so checking 50,000
/// rows one by one ("Check all") stays linear.
/// </summary>
public abstract class ScanCleanViewModel<TRow> : ToolViewModel, IScanCleanCommands where TRow : ISelectableRow
{
    private IReadOnlyList<TRow> _rows = Array.Empty<TRow>();
    private int _selectedCount;
    private int _bulk;

    protected ScanCleanViewModel()
    {
        ScanCommand = new AsyncRelayCommand(() => Run(ScanAsync, changing: false), () => !IsBusy);
        CleanCommand = new AsyncRelayCommand(CleanSelectedAsync, () => CanClean);
        SelectAllCommand = new RelayCommand(() => SetAll(_ => true), () => !IsBusy && _rows.Count > 0);
        SelectNoneCommand = new RelayCommand(() => SetAll(_ => false), () => !IsBusy && _rows.Count > 0);
        InvertCommand = new RelayCommand(() => SetAll(r => !r.Selected), () => !IsBusy && _rows.Count > 0);
    }

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand InvertCommand { get; }

    System.Windows.Input.ICommand IScanCleanCommands.SelectAllCommand => SelectAllCommand;
    System.Windows.Input.ICommand IScanCleanCommands.CleanCommand => CleanCommand;

    /// <summary>Asked before a clean with the rows to remove; false cancels (the page shows its confirmation dialog).</summary>
    public Func<IReadOnlyList<TRow>, bool>? Confirm { get; set; }

    public IReadOnlyList<TRow> Rows
    {
        get => _rows;
        protected set
        {
            foreach (var r in _rows) r.PropertyChanged -= RowChanged;
            _rows = value;
            _selectedCount = 0;
            foreach (var r in _rows)
            {
                r.PropertyChanged += RowChanged;
                if (r.Selected) _selectedCount++;
            }
            RowsReplaced();
            Raise();
            Raise(nameof(SelectedCount));
            Requery();
        }
    }

    public bool CanClean => !IsBusy && _selectedCount > 0;
    public int SelectedCount => _selectedCount;

    protected abstract Task ScanAsync(CancellationToken ct);
    protected abstract Task CleanAsync(IReadOnlyList<TRow> chosen, CancellationToken ct);

    /// <summary>Called when the rows were replaced (scan, clean); derived pages recompute their own totals here.</summary>
    protected virtual void RowsReplaced() { }

    /// <summary>Called for every row whose check changed (derived pages keep running totals, e.g. the selected bytes).</summary>
    protected virtual void SelectionChanged(TRow row) { }

    /// <summary>Called once after a selection change, or once after a bulk change (Check all) instead of per row.</summary>
    protected virtual void SelectionSettled() { }

    private Task CleanSelectedAsync()
    {
        var chosen = _rows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0 || Confirm?.Invoke(chosen) == false) return Task.CompletedTask;
        return Run(ct => CleanAsync(chosen, ct), changing: true);
    }

    /// <summary>Applies a check to every row, announcing the change once at the end.</summary>
    protected void SetAll(Func<TRow, bool> value) => SetMany(_rows, value);

    /// <summary>Applies a check to the given rows (e.g. the highlighted ones), announcing the change once at the end.</summary>
    public void SetMany(IEnumerable<TRow> rows, Func<TRow, bool> value)
    {
        if (IsBusy) return;
        _bulk++;
        try { foreach (var r in rows.ToList()) r.Selected = value(r); }
        finally { _bulk--; }
        Settle();
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ISelectableRow.Selected) || sender is not TRow row) return;
        _selectedCount += row.Selected ? 1 : -1;
        SelectionChanged(row);
        if (_bulk == 0) Settle();
    }

    private void Settle()
    {
        Raise(nameof(SelectedCount));
        SelectionSettled();
        Requery();
    }

    protected override void Requery()
    {
        base.Requery();
        Raise(nameof(CanClean));
        ScanCommand.RaiseCanExecuteChanged();
        CleanCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        SelectNoneCommand.RaiseCanExecuteChanged();
        InvertCommand.RaiseCanExecuteChanged();
    }
}
