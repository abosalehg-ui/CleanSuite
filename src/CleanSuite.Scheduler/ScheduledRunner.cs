using System.Globalization;
using System.Text;
using CleanSuite.Compat;
using CleanSuite.History;
using CleanSuite.Junk;
using CleanSuite.RegistryClean;
using CleanSuite.Safety;
using CleanSuite.Shortcuts;

namespace CleanSuite.Scheduler;

/// <param name="Found">Items the scan selected for removal.</param>
/// <param name="Done">Items removed or cleaned.</param>
/// <param name="BackupPath">Zip or .reg backup written by the step, when there is one.</param>
/// <param name="Error">Message when the step stopped with an error (later steps still run).</param>
public sealed record StepResult(ScheduledTasks Task, int Found, int Done, int Failed, long BytesFreed = 0, string? BackupPath = null,
    string? Error = null, bool Cancelled = false);

/// <summary>
/// Scheduled cleanup (the original's "/tasks:" mode): runs the requested tools silently in the fixed order junk, registry,
/// history, shortcuts. CleanSuite keeps no per-tool settings, so each step uses the defaults its tab starts with and the
/// selection the tab would show after a scan. Every step keeps its normal safety: junk files and shortcuts go to the Recycle
/// Bin, registry data is exported to a .reg backup first, everything is journaled. After a cancel no further step starts.
/// </summary>
public sealed class ScheduledRunner(IReadOnlyDictionary<ScheduledTasks, Func<CancellationToken, StepResult>>? steps = null)
{
    private readonly IReadOnlyDictionary<ScheduledTasks, Func<CancellationToken, StepResult>> _steps = steps ?? DefaultSteps;

    public static readonly IReadOnlyDictionary<ScheduledTasks, Func<CancellationToken, StepResult>> DefaultSteps =
        new Dictionary<ScheduledTasks, Func<CancellationToken, StepResult>>
        {
            [ScheduledTasks.Junk] = Junk,
            [ScheduledTasks.Registry] = Registry,
            [ScheduledTasks.History] = History,
            [ScheduledTasks.Shortcuts] = Shortcuts,
        };

    /// <param name="starting">Called before each step (status text).</param>
    public IReadOnlyList<StepResult> Run(ScheduledTasks tasks, Action<ScheduledTasks>? starting = null, CancellationToken ct = default)
    {
        var results = new List<StepResult>();
        foreach (var (task, _) in TaskLetters.Order)
        {
            if ((tasks & task) == 0 || !_steps.TryGetValue(task, out var step)) continue;
            if (ct.IsCancellationRequested) break;
            starting?.Invoke(task);
            try { results.Add(step(ct)); }
            catch (OperationCanceledException) { results.Add(new StepResult(task, 0, 0, 0, Cancelled: true)); break; }
            catch (Exception e) { results.Add(new StepResult(task, 0, 0, 0, Error: e.Message)); }   // unattended: report, go on
        }
        return results;
    }

    public static string DefaultLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSuite", "Scheduled.log");

    /// <summary>
    /// Appends one line per step to a plain log (the original keeps no record of scheduled runs; this one is CleanSuite's
    /// own addition). Times and numbers are written invariantly.
    /// </summary>
    public static void AppendLog(IReadOnlyList<StepResult> results, DateTime started, string? path = null)
    {
        path ??= DefaultLogPath;
        var sb = new StringBuilder();
        var when = started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (results.Count == 0) sb.Append(when).Append("  nothing ran").AppendLine();
        foreach (var r in results)
        {
            sb.Append(when).Append("  ").Append(r.Task).Append(CultureInfo.InvariantCulture,
                $"  found={r.Found} done={r.Done} failed={r.Failed} freed={r.BytesFreed}");
            if (r.BackupPath != null) sb.Append("  backup=").Append(r.BackupPath);
            if (r.Cancelled) sb.Append("  cancelled");
            if (r.Error != null) sb.Append("  error=").Append(r.Error.ReplaceLineEndings(" "));
            sb.AppendLine();
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---------- the tools, with the defaults of their tabs ----------

    /// <summary>Junk tab defaults: all fixed drives, temp folders, ignore lists, empty temp folders; to the Recycle Bin.</summary>
    private static StepResult Junk(CancellationToken ct)
    {
        var settings = new AceJunkSettings();
        var ignore = IgnoreList.LoadFolders(AceInstallation.DefaultJunkIgnoreFolders());
        var options = new JunkOptions();
        var temps = TempFolderLocator.Locate(new WindowsEnvironment());
        var roots = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name.TrimEnd('\\')).ToList();
        var scan = new JunkScanner().Scan(new JunkScanRequest
        {
            Roots = roots,
            Patterns = settings.ToPatternSet(),
            Exclusions = settings.Exclusions,
            Ignore = ignore,
            Options = options,
            TempFolders = temps,
        }, ct);
        var items = scan.Items.Where(i => !i.Flags.HasFlag(JunkReason.IgnoredShown)).ToList();
        if (items.Count == 0) return new StepResult(ScheduledTasks.Junk, 0, 0, 0);
        var res = new JunkRemover().Remove(new JunkRemovalRequest
        {
            Items = items,
            Mode = RemovalMode.RecycleBin,
            TempFolders = temps,
            DeleteEmptyTempFolders = options.DeleteEmptyTempFolders && options.CleanTempFolders,
            Silent = true,
        }, ct);
        return new StepResult(ScheduledTasks.Junk, items.Count, res.Deleted, res.Failed.Count, res.BytesFreed, res.ZipPath);
    }

    /// <summary>
    /// The registry categories a run without anyone watching may clean: lists of recent items, sounds, help files, fonts,
    /// application paths and Start menu entries. Entries that programs or Windows read to work (file types and associations,
    /// COM and shell extensions, shared DLLs, uninstall entries, program settings that point to a drive that may only be
    /// disconnected) are left to the Registry tab, where the user reviews every one.
    /// </summary>
    public const RegCategory UnattendedRegistryCategories = RegCategory.HistoryLists | RegCategory.Sounds | RegCategory.HelpFiles
        | RegCategory.Fonts | RegCategory.ApplicationPaths | RegCategory.StartMenu;

    /// <summary>Normal (not thorough) scan of the <see cref="UnattendedRegistryCategories"/>; every finding not covered by an ignore list.</summary>
    private static StepResult Registry(CancellationToken ct)
    {
        var options = new RegScanOptions
        {
            Thorough = false, Categories = UnattendedRegistryCategories,
            IgnoreFolders = IgnoreWords.DefaultFolders(AceRegistrySettings.IgnoreFolder(AceInstallation.Locate())),
        };
        var scan = new RegistryScanner(WindowsRegistryRoots.Instance, options).Scan(ct);
        var chosen = scan.Findings.Where(f => f.IgnoredBy == null).ToList();
        if (chosen.Count == 0) return new StepResult(ScheduledTasks.Registry, 0, 0, 0);
        var res = new RegistryCleaner().Clean(chosen, ct);
        return new StepResult(ScheduledTasks.Registry, chosen.Count, res.Removed, res.Failed.Count, BackupPath: res.BackupPath);
    }

    /// <summary>Erase History tab defaults: the items checked by default, Firefox defaults (no passwords, no sessions), this user.</summary>
    private static StepResult History(CancellationToken ct)
    {
        var ids = WindowsHistoryCatalog.Items.Where(i => i.DefaultOn).Select(i => i.Id).ToHashSet();
        var files = new RulePlanner(TokenExpander.ForCurrentUser(), RulePlanner.DefaultNeverDelete);
        var plan = new WindowsHistoryCleaner(files, new RegistryPlanner(WindowsRegistryRoots.Instance),
            () => TempFolderLocator.Locate(new WindowsEnvironment())).Plan(ids, ct: ct);
        var whitelist = new CookieWhitelist(AceHistorySettings.Read().CookieWhitelist);
        plan.Add(new FirefoxCleaner(files).Plan(FirefoxItems.Default, whitelist, UserFolders.CurrentUser(), ct));
        if (plan.Actions.Count == 0) return new StepResult(ScheduledTasks.History, 0, 0, 0);
        var res = new HistoryExecutor().Execute(plan.Actions, ct);
        return new StepResult(ScheduledTasks.History, plan.Actions.Count, res.Succeeded, res.Failed.Count, res.BytesFreed, res.RegistryBackupPath);
    }

    /// <summary>Invalid Shortcuts tab defaults: Start menus, desktops, AppData and Recent; every finding; to the Recycle Bin.</summary>
    private static StepResult Shortcuts(CancellationToken ct)
    {
        var ace = AceShortcutSettings.Read();
        var items = new ShortcutScanner(new ShortcutScanOptions
        {
            StartMenuMode = true,
            EmptyStartMenuFolders = false,
            IncludeRemovable = false,
            IgnoreFolders = ace?.IgnoreFolders ?? Array.Empty<string>(),
        }).Scan(ct);
        if (items.Count == 0) return new StepResult(ScheduledTasks.Shortcuts, 0, 0, 0);
        var res = new ShortcutCleaner().Remove(items, silent: true);
        return new StepResult(ScheduledTasks.Shortcuts, items.Count, res.Succeeded, res.Failed.Count);
    }
}
