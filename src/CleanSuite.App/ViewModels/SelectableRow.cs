namespace CleanSuite.App.ViewModels;

/// <summary>A row with a check box: the one implementation of <see cref="ISelectableRow.Selected"/> the result rows share.</summary>
public abstract class SelectableRow(bool selected) : ViewModelBase, ISelectableRow
{
    private bool _selected = selected;

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}
