using System.Text;

namespace CleanSuite.Scheduler;

/// <summary>
/// Tools a schedule can run (the original's /tasks letters: j junk, r registry, h history, s shortcuts). Duplicate files and
/// the other tools are not schedulable, as in the original.
/// </summary>
[Flags]
public enum ScheduledTasks
{
    None = 0,
    Junk = 1,
    Registry = 2,
    History = 8,
    Shortcuts = 0x40,
}

public static class TaskLetters
{
    /// <summary>Run order of a scheduled cleanup, fixed as in the original: junk, registry, history, shortcuts.</summary>
    public static readonly (ScheduledTasks Task, char Letter)[] Order =
    [
        (ScheduledTasks.Junk, 'j'), (ScheduledTasks.Registry, 'r'), (ScheduledTasks.History, 'h'), (ScheduledTasks.Shortcuts, 's'),
    ];

    public static string Format(ScheduledTasks tasks)
    {
        var sb = new StringBuilder();
        foreach (var (t, c) in Order) if ((tasks & t) != 0) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Letters in any order and case; other characters are ignored (the original's loader accepts either case).</summary>
    public static ScheduledTasks Parse(string? letters)
    {
        var r = ScheduledTasks.None;
        foreach (var ch in letters ?? "")
            foreach (var (t, c) in Order)
                if (char.ToLowerInvariant(ch) == c) r |= t;
        return r;
    }
}

/// <summary>Frequency radio group of the original's Scheduler page (TS 1.0 trigger types 0, 1, 2, 3, 6 and 7).</summary>
public enum ScheduleKind { Once, Daily, Weekly, Monthly, AtStartup, AtLogon }

/// <param name="Start">Local date and time of the first run; only the time of day matters except for <see cref="ScheduleKind.Once"/>.</param>
/// <param name="Interval">Every N days (daily) or N weeks (weekly).</param>
/// <param name="DayOfWeek">Weekday of a weekly schedule (the original allows one).</param>
/// <param name="DayOfMonth">1..31 for a monthly schedule; months without that day are skipped by Task Scheduler.</param>
public sealed record Schedule(ScheduledTasks Tasks, ScheduleKind Kind, DateTime Start, int Interval = 1,
    DayOfWeek DayOfWeek = DayOfWeek.Sunday, int DayOfMonth = 1)
{
    public const int MaxDays = 365, MaxWeeks = 52;

    /// <summary>Null when valid, otherwise a problem code: no-tasks, interval, day.</summary>
    public string? Problem() =>
        Tasks == ScheduledTasks.None ? "no-tasks"
        : Kind == ScheduleKind.Daily && Interval is < 1 or > MaxDays ? "interval"
        : Kind == ScheduleKind.Weekly && Interval is < 1 or > MaxWeeks ? "interval"
        : Kind == ScheduleKind.Monthly && DayOfMonth is < 1 or > 31 ? "day"
        : null;

    public bool IsTimed => Kind is not (ScheduleKind.AtStartup or ScheduleKind.AtLogon);
}
