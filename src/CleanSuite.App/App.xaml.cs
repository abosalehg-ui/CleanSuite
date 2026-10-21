using System.Windows;
using System.Windows.Threading;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.Scheduler;

namespace CleanSuite.App;

public partial class App : Application
{
    /// <summary>Held while the main window is open; a scheduled run that finds it exits at once.</summary>
    private const string MainMutexName = @"Local\CleanSuite.Main";
    private static Mutex? _mainMutex;

    public static UiSettings Ui { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Last line of defence: an unexpected error is logged and shown instead of closing the program silently.
        DispatcherUnhandledException += (_, a) =>
        {
            UiErrors.Show(MainWindow, a.Exception, "unhandled (UI)");
            a.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            UiErrors.Log(a.Exception, "unobserved task");
            a.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) UiErrors.Log(ex, "unhandled (fatal)"); };
        // Developer aid: "--export-icon <file.ico> [<png folder>]" writes the vector application icon and exits.
        if (Arg(e.Args, "--export-icon", lower: false) is { } ico)
        {
            int i = Array.IndexOf(e.Args, "--export-icon");
            IconExport.Write((System.Windows.Media.Drawing)FindResource("AppIconDrawing"), ico, i + 2 < e.Args.Length ? e.Args[i + 2] : null);
            Shutdown();
            return;
        }
        Ui = UiSettings.Load();
        // "--lang ar|en" and "--theme system|light|dark" override the saved choice for this run only (not saved);
        // "--high-contrast" shows the contrast-theme palette without turning a Windows contrast theme on (layout checks).
        ThemeManager.ForceHighContrast = e.Args.Contains("--high-contrast", StringComparer.OrdinalIgnoreCase);
        var lang = Arg(e.Args, "--lang");
        var theme = Arg(e.Args, "--theme");
        Loc.Instance.Language = lang switch { "ar" => UiLanguage.Arabic, "en" => UiLanguage.English, _ => Ui.Language };
        ThemeManager.Choice = Enum.TryParse<ThemeChoice>(theme, true, out var t) ? t : Ui.Theme;   // palette before the first window

        // "--tasks <letters>": scheduled cleanup started by Task Scheduler (see the Scheduler tab).
        if (Arg(e.Args, TaskXml.Switch) is { } letters)
        {
            var snapshot = Arg(e.Args, "--snapshot", lower: false);
            if (snapshot == null && Mutex.TryOpenExisting(MainMutexName, out var open))
            {
                open.Dispose();
                Shutdown();                                       // skipped while CleanSuite is open, as in the original
                return;
            }
            var tasks = TaskLetters.Parse(letters);
            var run = new ScheduledRunWindow(tasks, snapshot != null ? PreviewResults(tasks) : null);
            if (snapshot != null)
                run.ContentRendered += (_, _) => run.Dispatcher.BeginInvoke(() => { Snapshot.Save(run, snapshot); Shutdown(); }, DispatcherPriority.ApplicationIdle);
            MainWindow = run;
            run.Show();
            return;
        }

        _mainMutex = new Mutex(false, MainMutexName);
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Developer aid: sample results so "--tasks … --snapshot" can check the layout; nothing is run or deleted.</summary>
    private static IReadOnlyList<StepResult> PreviewResults(ScheduledTasks tasks) =>
        TaskLetters.Order.Where(o => (tasks & o.Task) != 0).Select(o => o.Task switch
        {
            ScheduledTasks.Junk => new StepResult(o.Task, 1204, 1198, 6, 734_003_200),
            ScheduledTasks.Registry => new StepResult(o.Task, 37, 37, 0),
            ScheduledTasks.History => new StepResult(o.Task, 0, 0, 0),
            _ => new StepResult(o.Task, 0, 0, 0, Error: "Access is denied."),
        }).ToList();

    private static string? Arg(string[] args, string name, bool lower = true)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? (lower ? args[i + 1].ToLowerInvariant() : args[i + 1]) : null;
    }
}
