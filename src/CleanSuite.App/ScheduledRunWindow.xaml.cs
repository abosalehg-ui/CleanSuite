using System.Windows;
using System.Windows.Threading;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.Scheduler;

namespace CleanSuite.App;

/// <summary>
/// Window of a scheduled run (the original "/tasks:" dialog): appears in the corner of the screen without taking focus, shows the
/// step being run, a summary per tool and closes itself 5 seconds after finishing (as the original's timer), unless the
/// pointer is over it. Cancel stops after the current step.
/// </summary>
public partial class ScheduledRunWindow : Window
{
    private readonly ScheduledTasks _tasks;
    private readonly IReadOnlyList<StepResult>? _preview;
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<StepResult>? _results;
    private ScheduledTasks? _current;
    private bool _done;

    /// <param name="preview">Developer aid (--snapshot): show these results as a finished run; nothing is run.</param>
    public ScheduledRunWindow(ScheduledTasks tasks, IReadOnlyList<StepResult>? preview = null)
    {
        InitializeComponent();
        _tasks = tasks;
        _preview = preview;
        ThemeManager.ApplyTitleBar(this);
        Loc.Instance.LanguageChanged += (_, _) => Render();
        Loaded += (_, _) => PlaceInCorner();
        ContentRendered += async (_, _) => await RunAsync();
        Render();
    }

    private void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;                             // next to the notification area
        Top = area.Bottom - ActualHeight - 12;
        SizeChanged += (_, _) => Top = area.Bottom - ActualHeight - 12;
    }

    private async Task RunAsync()
    {
        var started = DateTime.Now;
        if (_preview != null) _results = _preview;
        else
        {
            _results = await Task.Run(() => new ScheduledRunner().Run(_tasks,
                t => Dispatcher.BeginInvoke(() => { _current = t; Render(); }), _cts.Token));
            ScheduledRunner.AppendLog(_results, started);
        }
        _done = true;
        Render();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { if (!IsMouseOver) { timer.Stop(); Close(); } };
        if (_preview == null) timer.Start();
    }

    private static string ToolName(ScheduledTasks t) => Loc.T(t switch
    {
        ScheduledTasks.Junk => "TabJunk", ScheduledTasks.Registry => "TabRegistry", ScheduledTasks.History => "TabHistory", _ => "TabShortcuts",
    });

    internal static string Line(StepResult r)
    {
        if (r.Cancelled) return ToolName(r.Task) + ": " + Loc.T("SrCancelled");
        if (r.Error != null) return Loc.F("SrError", ToolName(r.Task), r.Error);
        if (r.Found == 0) return Loc.F("SrNothing", ToolName(r.Task));
        var line = r.Task switch
        {
            ScheduledTasks.Junk => Loc.F("SrJunk", Loc.N0(r.Done), Loc.FormatSize(r.BytesFreed)),
            ScheduledTasks.Registry => Loc.F("SrRegistry", Loc.N0(r.Done)),
            ScheduledTasks.History => Loc.F("SrHistory", Loc.N0(r.Done), Loc.FormatSize(r.BytesFreed)),
            _ => Loc.F("SrShortcuts", Loc.N0(r.Done)),
        };
        return r.Failed > 0 ? line + Loc.F("SrFailedPart", Loc.N0(r.Failed)) : line;
    }

    private void Render()
    {
        Progress.Visibility = _done ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = _done ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Visibility = _done ? Visibility.Visible : Visibility.Collapsed;
        HeadText.Text = _done
            ? (_results?.Any(r => r.Cancelled) == true || _cts.IsCancellationRequested ? Loc.T("SrCancelled") : Loc.T("SrFinished"))
            : _current is { } c ? Loc.F("SrRunning", ToolName(c)) : Loc.T("SrTitle");
        ResultList.ItemsSource = _results?.Select(Line).ToList();
        Bidi.SetText(LogText, Loc.F("SrLog", Loc.Ltr(ScheduledRunner.DefaultLogPath)));
        LogText.Visibility = _done ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_done) { _cts.Cancel(); e.Cancel = true; }                  // let the current step finish its journal and backup
        base.OnClosing(e);
    }
}
