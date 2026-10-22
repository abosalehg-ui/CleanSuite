using System.IO;
using CleanSuite.Restore;
using CleanSuite.Startup;
using CleanSuite.Uninstall;

namespace CleanSuite.App.ViewModels;

/// <summary>
/// A page that lists items (read on a background thread) and acts on the selected one. Reading and acting share the busy flag of
/// <see cref="ToolViewModel"/>, so a change never overlaps a refresh; after every change the list is read again.
/// </summary>
public abstract class ListPageViewModel<T> : ToolViewModel
{
    private IReadOnlyList<T> _items = Array.Empty<T>();

    protected ListPageViewModel() => RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);

    public AsyncRelayCommand RefreshCommand { get; }

    public IReadOnlyList<T> Items
    {
        get => _items;
        protected set { _items = value; Raise(); }
    }

    /// <summary>True once the list has been read (successfully or not).</summary>
    public bool Loaded { get; private set; }

    /// <summary>Reads the items (on a worker thread).</summary>
    protected abstract IReadOnlyList<T> Read(CancellationToken ct);

    public Task RefreshAsync() => Run(async ct =>
    {
        Items = await Task.Run(() => Read(ct), ct);
        Loaded = true;
    }, changing: false);

    /// <summary>Runs a change on a worker thread, then reads the list again; returns the change's result, or default when busy or failed.</summary>
    protected async Task<TResult?> ChangeAsync<TResult>(Func<CancellationToken, TResult> change)
    {
        TResult? result = default;
        await Run(async ct =>
        {
            result = await Task.Run(() => change(ct), CancellationToken.None);
            Items = await Task.Run(() => Read(CancellationToken.None), CancellationToken.None);
        }, changing: true, cancellable: false);
        return result;
    }

    protected override void Requery()
    {
        base.Requery();
        RefreshCommand?.RaiseCanExecuteChanged();
    }
}

/// <summary>Startup programs page: Run keys and startup folders; disable, enable and delete (with backup).</summary>
public sealed class StartupViewModel(StartupManager? manager = null) : ListPageViewModel<StartupEntry>
{
    private readonly StartupManager _manager = manager ?? new StartupManager(monitor: new StartupMonitor());

    protected override IReadOnlyList<StartupEntry> Read(CancellationToken ct) => _manager.Scan(ct);

    public Task<StartupManager.ChangeResult?> DisableAsync(StartupEntry e) => ChangeAsync(_ => _manager.Disable(e));
    public Task<StartupManager.ChangeResult?> EnableAsync(StartupEntry e) => ChangeAsync(_ => _manager.Enable(e));
    public Task<StartupManager.ChangeResult?> DeleteAsync(StartupEntry e) => ChangeAsync(_ => _manager.Delete(e));
}

/// <summary>Installed programs page: the three Uninstall keys; uninstall, change, repair, remove a dead entry (with .reg backup).</summary>
public sealed class UninstallViewModel(UninstallManager? manager = null, Func<bool>? showUpdates = null) : ListPageViewModel<UninstallEntry>
{
    private readonly UninstallManager _manager = manager ?? new UninstallManager();
    private readonly Func<bool> _showUpdates = showUpdates ?? (() => false);
    private bool _updates;

    public UninstallManager Manager => _manager;

    /// <summary>Reads the page's "show updates" choice on the UI thread, then refreshes.</summary>
    public Task RefreshWithOptionsAsync()
    {
        _updates = _showUpdates();
        return RefreshAsync();
    }

    protected override IReadOnlyList<UninstallEntry> Read(CancellationToken ct) => new UninstallScanner { ShowUpdates = _updates }.Scan(ct);

    /// <summary>Removes a dead entry after a verified .reg export; the list drops it on success.</summary>
    public async Task<UninstallActionResult?> RemoveEntryAsync(UninstallEntry e)
    {
        UninstallActionResult? r = null;
        await Run(async _ =>
        {
            r = await Task.Run(() => _manager.RemoveEntry(e));
            if (r.Success) Items = Items.Where(x => !ReferenceEquals(x, e)).ToList();
        }, changing: true, cancellable: false);
        return r;
    }
}

/// <summary>Restore page: every backup in CleanSuite's and the original's backup folders, newest first.</summary>
public sealed class RestoreViewModel : ListPageViewModel<BackupItem>
{
    private readonly List<string> _folders = BackupCatalog.DefaultFolders().ToList();

    public IReadOnlyList<string> Folders => _folders;

    /// <summary>The backup folders that exist (shown in the status line).</summary>
    public IReadOnlyList<string> ExistingFolders { get; private set; } = Array.Empty<string>();

    public void AddFolder(string folder)
    {
        if (!_folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) _folders.Add(folder);
    }

    protected override IReadOnlyList<BackupItem> Read(CancellationToken ct)
    {
        var folders = _folders.ToList();
        ExistingFolders = folders.Where(Directory.Exists).ToList();
        return BackupCatalog.List(folders);
    }

    public Task<(int Removed, IReadOnlyList<string> Failed)> DeleteAsync(IReadOnlyList<BackupItem> backups) =>
        ChangeAsync(_ => BackupCatalog.Delete(backups));

    public Task<BackupCatalog.RegistryRestoreResult?> RestoreRegistryAsync(BackupItem backup) =>
        ChangeAsync(_ => BackupCatalog.RestoreRegistry(backup));

    /// <summary>
    /// Runs a step that only reads (e.g. listing a backup's entries) under the busy flag. A backup that cannot be read (damaged,
    /// gone, access denied) is rethrown to the page, which names the file; default when the page is busy.
    /// </summary>
    public async Task<TResult?> ReadAsync<TResult>(Func<TResult> read)
    {
        TResult? result = default;
        Exception? unreadable = null;
        await Run(async _ =>
        {
            try { result = await Task.Run(read); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { unreadable = e; }
        }, changing: false, cancellable: false);
        if (unreadable != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(unreadable);
        return result;
    }
}
