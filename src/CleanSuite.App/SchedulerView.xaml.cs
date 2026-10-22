using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.Scheduler;

namespace CleanSuite.App;

/// <summary>
/// Scheduler tab (the original's Scheduled Task page): which tools to run and when, kept as one Windows Task Scheduler task
/// that starts CleanSuite with "--tasks". Nothing is written until "Save schedule" is pressed.
/// </summary>
public partial class SchedulerView : UserControl, IToolPage
{
    private static readonly DayOfWeek[] Days = Enum.GetValues<DayOfWeek>();
    private readonly TaskSchedule _schedule = new(new WindowsTaskStore(), Environment.ProcessPath ?? "CleanSuite.App.exe");
    private Schedule? _saved;
    private bool _exists;
    private string? _problem;
    private bool _loaded;

    /// <summary>Saving or removing the task is one quick call to Task Scheduler on the UI thread: never busy in between.</summary>
    public bool IsBusy => false;
    public bool IsChanging => false;
    Button? IToolPage.ScanButton => null;
    Button? IToolPage.CancelButton => null;
    public void CancelWork() { }

    public SchedulerView()
    {
        InitializeComponent();
        for (int h = 0; h < 24; h++) HourCombo.Items.Add(h.ToString("00", CultureInfo.InvariantCulture));
        for (int m = 0; m < 60; m++) MinuteCombo.Items.Add(m.ToString("00", CultureInfo.InvariantCulture));
        for (int d = 1; d <= 31; d++) DayOfMonthCombo.Items.Add(d.ToString(CultureInfo.InvariantCulture));
        FillDays();
        Loc.Instance.LanguageChanged += (_, _) => { FillDays(); Render(); };
    }

    private Window Owner => Window.GetWindow(this)!;

    private void FillDays()
    {
        int i = DayOfWeekCombo.SelectedIndex;
        DayOfWeekCombo.Items.Clear();
        foreach (var d in Days) DayOfWeekCombo.Items.Add(Loc.T("Day_" + d));
        DayOfWeekCombo.SelectedIndex = i;
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        Reload();
    }

    /// <summary>Reads the task back (or shows defaults: daily at the current time, no task checked, as the original).</summary>
    private void Reload()
    {
        _problem = null;
        try { (_exists, _saved) = _schedule.Load(); }
        catch (Exception ex) when (WindowsTaskStore.IsComFailure(ex)) { _exists = false; _saved = null; _problem = ex.Message; }
        Show(_saved ?? new Schedule(ScheduledTasks.None, ScheduleKind.Daily, DateTime.Now));
        Render();
    }

    private void Show(Schedule s)
    {
        TaskJunk.IsChecked = (s.Tasks & ScheduledTasks.Junk) != 0;
        TaskRegistry.IsChecked = (s.Tasks & ScheduledTasks.Registry) != 0;
        TaskHistory.IsChecked = (s.Tasks & ScheduledTasks.History) != 0;
        TaskShortcuts.IsChecked = (s.Tasks & ScheduledTasks.Shortcuts) != 0;
        var kind = s.Kind switch
        {
            ScheduleKind.Once => KindOnce,
            ScheduleKind.Daily => KindDaily,
            ScheduleKind.Weekly => KindWeekly,
            ScheduleKind.Monthly => KindMonthly,
            ScheduleKind.AtStartup => KindStartup,
            _ => KindLogon,
        };
        kind.IsChecked = true;
        var start = s.IsTimed ? s.Start : DateTime.Now;
        DateBox.Text = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        DaysBox.Text = (s.Kind == ScheduleKind.Daily ? s.Interval : 1).ToString(CultureInfo.InvariantCulture);
        WeeksBox.Text = (s.Kind == ScheduleKind.Weekly ? s.Interval : 1).ToString(CultureInfo.InvariantCulture);
        DayOfWeekCombo.SelectedIndex = (int)(s.Kind == ScheduleKind.Weekly ? s.DayOfWeek : start.DayOfWeek);
        DayOfMonthCombo.SelectedIndex = (s.Kind == ScheduleKind.Monthly ? s.DayOfMonth : start.Day) - 1;
        HourCombo.SelectedIndex = start.Hour;
        MinuteCombo.SelectedIndex = start.Minute;
        UpdateControls();
    }

    private void Render()
    {
        Bidi.SetText(WhatText, Loc.F("SdWhat", Loc.Ltr(ScheduledRunner.DefaultLogPath)));
        RemoveButton.IsEnabled = _exists;
        if (_problem != null) { StatusText.Text = Loc.F("SdUnavailable", _problem); return; }
        if (!_exists) { StatusText.Text = Loc.T("SdNone"); return; }
        if (_saved == null) { Bidi.SetText(StatusText, Loc.F("SdForeign", Loc.Ltr(_schedule.Name))); return; }
        DateTime? next = null;
        try { next = _schedule.NextRun(); }
        catch (Exception ex) when (WindowsTaskStore.IsComFailure(ex)) { }
        StatusText.Text = _saved.Kind switch
        {
            ScheduleKind.AtStartup => Loc.T("SdNextStartup"),
            ScheduleKind.AtLogon => Loc.T("SdNextLogon"),
            _ => next is { } n ? Loc.F("SdNext", Loc.FormatDate(n)) : Loc.T("SdNextNone"),
        };
    }

    private ScheduledTasks CheckedTasks() =>
        (TaskJunk.IsChecked == true ? ScheduledTasks.Junk : 0) | (TaskRegistry.IsChecked == true ? ScheduledTasks.Registry : 0) |
        (TaskHistory.IsChecked == true ? ScheduledTasks.History : 0) | (TaskShortcuts.IsChecked == true ? ScheduledTasks.Shortcuts : 0);

    private void UpdateControls()
    {
        SaveButton.IsEnabled = CheckedTasks() != ScheduledTasks.None;     // the original enables Schedule only with a task checked
        TimeRow.IsEnabled = KindStartup.IsChecked != true && KindLogon.IsChecked != true;
        MonthNote.Visibility = KindMonthly.IsChecked == true && DayOfMonthCombo.SelectedIndex >= 28 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Task_Click(object sender, RoutedEventArgs e) => UpdateControls();
    private void Kind_Checked(object sender, RoutedEventArgs e) => UpdateControls();
    private void DayOfMonth_Changed(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private static string Digits(string text) => Loc.Digits(text);

    private static int? Number(string text) =>
        int.TryParse(Digits(text), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The schedule shown, or null after telling the user what to correct.</summary>
    private Schedule? Read()
    {
        var tasks = CheckedTasks();
        var time = new TimeSpan(Math.Max(HourCombo.SelectedIndex, 0), Math.Max(MinuteCombo.SelectedIndex, 0), 0);
        var start = DateTime.Today + time;
        Schedule s;
        if (KindOnce.IsChecked == true)
        {
            if (!DateTime.TryParseExact(Digits(DateBox.Text), ["yyyy-MM-dd", "yyyy-M-d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return Refuse(Loc.F("SdBadDate", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), DateBox);
            s = new Schedule(tasks, ScheduleKind.Once, day.Date + time);
            if (s.Start <= DateTime.Now) return Refuse(Loc.T("SdPast"), DateBox);
        }
        else if (KindDaily.IsChecked == true)
        {
            var days = Number(DaysBox.Text);
            if (days is not (>= 1 and <= Schedule.MaxDays)) return Refuse(Loc.F("SdBadInterval", Loc.N0(Schedule.MaxDays)), DaysBox);
            s = new Schedule(tasks, ScheduleKind.Daily, start, days.Value);
        }
        else if (KindWeekly.IsChecked == true)
        {
            var weeks = Number(WeeksBox.Text);
            if (weeks is not (>= 1 and <= Schedule.MaxWeeks)) return Refuse(Loc.F("SdBadInterval", Loc.N0(Schedule.MaxWeeks)), WeeksBox);
            s = new Schedule(tasks, ScheduleKind.Weekly, start, weeks.Value, Days[Math.Max(DayOfWeekCombo.SelectedIndex, 0)]);
        }
        else if (KindMonthly.IsChecked == true)
            s = new Schedule(tasks, ScheduleKind.Monthly, start, DayOfMonth: Math.Max(DayOfMonthCombo.SelectedIndex, 0) + 1);
        else
            s = new Schedule(tasks, KindStartup.IsChecked == true ? ScheduleKind.AtStartup : ScheduleKind.AtLogon, start);
        if (s.Problem() == "no-tasks") return Refuse(Loc.T("SdNoTasks"), TaskJunk);
        return s;
    }

    private Schedule? Refuse(string message, Control focus)
    {
        Loc.Show(Owner, message, Loc.T("SdSave"), MessageBoxButton.OK, MessageBoxImage.Information);
        focus.Focus();
        return null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Read() is not { } s) return;
        try
        {
            _schedule.Save(s);
        }
        catch (Exception ex) when (WindowsTaskStore.IsComFailure(ex))
        {
            var text = Loc.F("SdFailed", ex.Message);
            Loc.Show(Owner, text, Loc.T("SdSave"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Reload();
        Bidi.SetText(StatusText, Loc.T("SdSaved") + " " + Bidi.GetText(StatusText));
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Loc.Show(Owner, Loc.F("SdRemoveConfirm", Loc.Ltr(_schedule.Name)), Loc.T("SdRemove"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _schedule.Remove(); }
        catch (Exception ex) when (WindowsTaskStore.IsComFailure(ex))
        {
            Loc.Show(Owner, Loc.F("SdFailed", ex.Message), Loc.T("SdRemove"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Reload();
        Bidi.SetText(StatusText, Loc.T("SdRemoved") + " " + Bidi.GetText(StatusText));
    }
}
