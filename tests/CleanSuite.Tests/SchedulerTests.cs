using System.Xml.Linq;
using CleanSuite.Scheduler;

namespace CleanSuite.Tests;

/// <summary>
/// Scheduler: task letters, the Task Scheduler definition and its reverse, the scheduled runner. No task is ever registered:
/// the store is in memory, and the one check against Windows only parses a definition in memory (ITaskDefinition.XmlText).
/// </summary>
public class SchedulerTests
{
    private const string Exe = @"C:\Program Files\CleanSuite\CleanSuite.App.exe";
    private const string User = @"PC\tester";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly DateTime At = new(2026, 10, 21, 21, 30, 0);

    private sealed class MemoryStore : ITaskStore
    {
        public readonly Dictionary<string, string> Tasks = new();
        public string? GetXml(string name) => Tasks.GetValueOrDefault(name);
        public DateTime? NextRun(string name) => null;
        public void Register(string name, string xml) => Tasks[name] = xml;
        public bool Delete(string name) => Tasks.Remove(name);
        public string? Validate(string xml) => null;
    }

    public static TheoryData<Schedule> AllKinds() => new()
    {
        new Schedule(ScheduledTasks.Junk, ScheduleKind.Once, At),
        new Schedule(ScheduledTasks.Junk | ScheduledTasks.Registry, ScheduleKind.Daily, At, Interval: 3),
        new Schedule(ScheduledTasks.History, ScheduleKind.Weekly, At, Interval: 2, DayOfWeek: DayOfWeek.Friday),
        new Schedule(ScheduledTasks.Shortcuts | ScheduledTasks.Junk, ScheduleKind.Monthly, At, DayOfMonth: 31),
        new Schedule(ScheduledTasks.Registry, ScheduleKind.AtStartup, At),
        new Schedule(ScheduledTasks.Junk | ScheduledTasks.Registry | ScheduledTasks.History | ScheduledTasks.Shortcuts, ScheduleKind.AtLogon, At),
    };

    [Fact]
    public void Letters_follow_the_fixed_run_order_and_parse_either_case()
    {
        Assert.Equal("jrhs", TaskLetters.Format(ScheduledTasks.Shortcuts | ScheduledTasks.History | ScheduledTasks.Registry | ScheduledTasks.Junk));
        Assert.Equal("js", TaskLetters.Format(ScheduledTasks.Shortcuts | ScheduledTasks.Junk));
        Assert.Equal(ScheduledTasks.Junk | ScheduledTasks.Shortcuts, TaskLetters.Parse("Sjx"));     // unknown letters ignored
        Assert.Equal(ScheduledTasks.None, TaskLetters.Parse(null));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Definition_reads_back_as_the_same_schedule(Schedule s)
    {
        var back = TaskXml.Parse(TaskXml.Build(s, Exe, User));
        Assert.NotNull(back);
        Assert.Equal(s.Tasks, back!.Tasks);
        Assert.Equal(s.Kind, back.Kind);
        if (s.IsTimed) Assert.Equal(s.Start, back.Start);
        if (s.Kind is ScheduleKind.Daily or ScheduleKind.Weekly) Assert.Equal(s.Interval, back.Interval);
        if (s.Kind == ScheduleKind.Weekly) Assert.Equal(s.DayOfWeek, back.DayOfWeek);
        if (s.Kind == ScheduleKind.Monthly) Assert.Equal(s.DayOfMonth, back.DayOfMonth);
    }

    [Fact]
    public void Definition_runs_only_while_logged_on_and_not_on_batteries()
    {
        var x = XElement.Parse(TaskXml.Build(new Schedule(ScheduledTasks.Junk | ScheduledTasks.History, ScheduleKind.Daily, At), Exe, User));
        string V(params string[] path) => path.Aggregate(x, (e, n) => e.Element(Ns + n)!).Value;
        Assert.Equal("InteractiveToken", V("Principals", "Principal", "LogonType"));
        Assert.Equal("LeastPrivilege", V("Principals", "Principal", "RunLevel"));
        Assert.Equal(User, V("Principals", "Principal", "UserId"));
        Assert.Equal("true", V("Settings", "DisallowStartIfOnBatteries"));
        Assert.Equal("true", V("Settings", "StopIfGoingOnBatteries"));
        Assert.Equal("IgnoreNew", V("Settings", "MultipleInstancesPolicy"));
        Assert.Equal("\"" + Exe + "\"", V("Actions", "Exec", "Command"));                      // path with spaces is quoted
        Assert.Equal("--tasks jh", V("Actions", "Exec", "Arguments"));
        Assert.Equal("2026-10-21T21:30:00", V("Triggers", "CalendarTrigger", "StartBoundary"));  // invariant, local time
        Assert.Single(x.Element(Ns + "Triggers")!.Elements());                                    // one trigger, as in the original
    }

    [Fact]
    public void Monthly_definition_covers_every_month()
    {
        var x = XElement.Parse(TaskXml.Build(new Schedule(ScheduledTasks.Junk, ScheduleKind.Monthly, At, DayOfMonth: 15), Exe, User));
        var month = x.Descendants(Ns + "ScheduleByMonth").Single();
        Assert.Equal("15", month.Element(Ns + "DaysOfMonth")!.Element(Ns + "Day")!.Value);
        Assert.Equal(12, month.Element(Ns + "Months")!.Elements().Count());
    }

    [Fact]
    public void Invalid_schedules_are_refused_and_foreign_tasks_are_not_read()
    {
        Assert.Equal("no-tasks", new Schedule(ScheduledTasks.None, ScheduleKind.Daily, At).Problem());
        Assert.Equal("interval", new Schedule(ScheduledTasks.Junk, ScheduleKind.Daily, At, Interval: 0).Problem());
        Assert.Equal("interval", new Schedule(ScheduledTasks.Junk, ScheduleKind.Weekly, At, Interval: 53).Problem());
        Assert.Equal("day", new Schedule(ScheduledTasks.Junk, ScheduleKind.Monthly, At, DayOfMonth: 32).Problem());
        Assert.Null(new Schedule(ScheduledTasks.Junk, ScheduleKind.Monthly, At, Interval: 0, DayOfMonth: 5).Problem());   // interval unused
        Assert.Throws<ArgumentException>(() => TaskXml.Build(new Schedule(ScheduledTasks.None, ScheduleKind.Once, At), Exe, User));

        var other = TaskXml.Build(new Schedule(ScheduledTasks.Junk, ScheduleKind.Once, At), Exe, User).Replace("--tasks j", "/update");
        Assert.Null(TaskXml.Parse(other));
        Assert.Null(TaskXml.Parse("not xml"));
        var idle = TaskXml.Build(new Schedule(ScheduledTasks.Junk, ScheduleKind.AtLogon, At), Exe, User).Replace("LogonTrigger", "IdleTrigger");
        Assert.Null(TaskXml.Parse(idle));
    }

    [Fact]
    public void Start_with_a_written_offset_is_read_as_local_time()
    {
        var xml = TaskXml.Build(new Schedule(ScheduledTasks.Junk, ScheduleKind.Once, At), Exe, User)
            .Replace("2026-10-21T21:30:00", "2026-10-21T18:30:00Z");
        Assert.Equal(new DateTime(2026, 10, 21, 18, 30, 0, DateTimeKind.Utc).ToLocalTime(), TaskXml.Parse(xml)!.Start);
    }

    [Fact]
    public void Saving_replaces_the_one_task_and_removing_deletes_it()
    {
        var store = new MemoryStore();
        var schedule = new TaskSchedule(store, Exe);
        Assert.Equal((false, (Schedule?)null), schedule.Load());
        schedule.Save(new Schedule(ScheduledTasks.Junk, ScheduleKind.Daily, At), User);
        schedule.Save(new Schedule(ScheduledTasks.Registry, ScheduleKind.AtLogon, At), User);
        Assert.Equal(TaskSchedule.DefaultName, Assert.Single(store.Tasks).Key);
        var (exists, loaded) = schedule.Load();
        Assert.True(exists);
        Assert.Equal(ScheduleKind.AtLogon, loaded!.Kind);
        Assert.Equal(ScheduledTasks.Registry, loaded.Tasks);

        store.Tasks[TaskSchedule.DefaultName] = "<Task/>";                                       // replaced by hand: exists, unreadable
        Assert.Equal((true, (Schedule?)null), schedule.Load());
        Assert.True(schedule.Remove());
        Assert.False(schedule.Remove());
        Assert.Empty(store.Tasks);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Task_Scheduler_accepts_the_definition_without_registering_it(Schedule s)
    {
        Assert.Null(new WindowsTaskStore().Validate(TaskXml.Build(s, Exe, TaskSchedule.CurrentUser)));
    }

    [Fact]
    public void Task_Scheduler_check_rejects_a_broken_definition()
    {
        var bad = TaskXml.Build(new Schedule(ScheduledTasks.Junk, ScheduleKind.Daily, At), Exe, TaskSchedule.CurrentUser)
            .Replace("<DaysInterval>1</DaysInterval>", "<DaysInterval>often</DaysInterval>");
        Assert.NotNull(new WindowsTaskStore().Validate(bad));
    }

    [Fact]
    public void Missing_task_reads_as_absent()
    {
        var store = new WindowsTaskStore();                                                       // read-only calls only
        var name = "CleanSuite test " + Guid.NewGuid().ToString("N");
        Assert.Null(store.GetXml(name));
        Assert.Null(store.NextRun(name));
    }

    [Fact]
    public void Runner_keeps_the_order_isolates_errors_and_stops_after_a_cancel()
    {
        var ran = new List<ScheduledTasks>();
        using var cts = new CancellationTokenSource();
        StepResult Ok(ScheduledTasks t) { ran.Add(t); return new StepResult(t, 2, 2, 0); }
        var runner = new ScheduledRunner(new Dictionary<ScheduledTasks, Func<CancellationToken, StepResult>>
        {
            [ScheduledTasks.Shortcuts] = _ => Ok(ScheduledTasks.Shortcuts),
            [ScheduledTasks.History] = _ => Ok(ScheduledTasks.History),
            [ScheduledTasks.Registry] = _ => throw new InvalidOperationException("boom"),
            [ScheduledTasks.Junk] = _ => Ok(ScheduledTasks.Junk),
        });
        var started = new List<ScheduledTasks>();
        var all = runner.Run(ScheduledTasks.Shortcuts | ScheduledTasks.Registry | ScheduledTasks.Junk | ScheduledTasks.History, started.Add);
        Assert.Equal(new[] { ScheduledTasks.Junk, ScheduledTasks.Registry, ScheduledTasks.History, ScheduledTasks.Shortcuts }, started);
        Assert.Equal("boom", all[1].Error);
        Assert.Equal(new[] { ScheduledTasks.Junk, ScheduledTasks.History, ScheduledTasks.Shortcuts }, ran);    // went on after the error

        ran.Clear();
        var cancelling = new ScheduledRunner(new Dictionary<ScheduledTasks, Func<CancellationToken, StepResult>>
        {
            [ScheduledTasks.Junk] = _ => { cts.Cancel(); throw new OperationCanceledException(); },
            [ScheduledTasks.Registry] = _ => Ok(ScheduledTasks.Registry),
        });
        var res = cancelling.Run(ScheduledTasks.Junk | ScheduledTasks.Registry, ct: cts.Token);
        Assert.True(Assert.Single(res).Cancelled);
        Assert.Empty(ran);
    }

    [Fact]
    public void Log_lines_are_invariant_and_appended()
    {
        using var sb = new Sandbox();
        var log = Path.Combine(sb.Root, @"sub\Scheduled.log");
        var when = new DateTime(2026, 10, 21, 3, 4, 5);
        ScheduledRunner.AppendLog([new StepResult(ScheduledTasks.Junk, 10, 9, 1, 1234567, @"C:\b\Jnk.zip")], when, log);
        ScheduledRunner.AppendLog([new StepResult(ScheduledTasks.Registry, 0, 0, 0, Error: "line1\nline2")], when, log);
        ScheduledRunner.AppendLog([], when, log);
        var lines = File.ReadAllLines(log);
        Assert.Equal(3, lines.Length);
        Assert.Equal(@"2026-10-21 03:04:05  Junk  found=10 done=9 failed=1 freed=1234567  backup=C:\b\Jnk.zip", lines[0]);
        Assert.EndsWith("error=line1 line2", lines[1]);
        Assert.EndsWith("nothing ran", lines[2]);
    }
}
