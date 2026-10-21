using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CleanSuite.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Synchronous command; CanExecute is re-queried through <see cref="RaiseCanExecuteChanged"/>.</summary>
public sealed class RelayCommand(Action run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) run(); }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Asynchronous command that cannot run twice at the same time: while it runs CanExecute is false, so a second click
/// (or a second keyboard shortcut) does nothing. Exceptions are left to the task (the view models catch them).
/// </summary>
public sealed class AsyncRelayCommand(Func<Task> run, Func<bool>? canRun = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;
    public bool IsRunning => _running;
    public bool CanExecute(object? parameter) => !_running && (canRun?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await run(); }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
