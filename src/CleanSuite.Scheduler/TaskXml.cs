using System.Globalization;
using System.Xml.Linq;

namespace CleanSuite.Scheduler;

/// <summary>
/// Task Scheduler 2.0 definition of a CleanSuite schedule, and the reverse. The original creates the task through the TS 1.0
/// API with flags 0x20C1: run only while the user is logged on, interactive, do not start on batteries and stop when going
/// on batteries; one trigger. The same is expressed here as XML (logon type InteractiveToken, least privilege, as the app
/// itself runs as invoker). The command is CleanSuite itself with "--tasks &lt;letters&gt;"; no launcher program is needed.
/// "When Windows starts" is a logon trigger with a short delay: a boot trigger fires before anyone is logged on, when a task
/// that runs only while its user is logged on cannot start (and registering one needs administrator rights), so it never ran.
/// Tasks saved by earlier versions with a boot trigger are still read as "When Windows starts" and rewritten on the next save.
/// </summary>
public static class TaskXml
{
    public const string Switch = "--tasks";

    /// <summary>Delay after signing in for "When Windows starts", so the run does not compete with the start of the desktop.</summary>
    public const string StartupDelay = "PT2M";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly string[] Months =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <param name="exePath">Program to start (CleanSuite.App.exe).</param>
    /// <param name="user">DOMAIN\user the task runs as, only while that user is logged on.</param>
    public static string Build(Schedule s, string exePath, string user)
    {
        if (s.Problem() is { } p) throw new ArgumentException("Invalid schedule: " + p, nameof(s));
        var start = new XElement(Ns + "StartBoundary", s.Start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        XElement trigger = s.Kind switch
        {
            ScheduleKind.Once => new XElement(Ns + "TimeTrigger", start),
            ScheduleKind.Daily => new XElement(Ns + "CalendarTrigger", start,
                new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", s.Interval))),
            ScheduleKind.Weekly => new XElement(Ns + "CalendarTrigger", start,
                new XElement(Ns + "ScheduleByWeek",
                    new XElement(Ns + "DaysOfWeek", new XElement(Ns + s.DayOfWeek.ToString())),
                    new XElement(Ns + "WeeksInterval", s.Interval))),
            ScheduleKind.Monthly => new XElement(Ns + "CalendarTrigger", start,
                new XElement(Ns + "ScheduleByMonth",
                    new XElement(Ns + "DaysOfMonth", new XElement(Ns + "Day", s.DayOfMonth)),
                    new XElement(Ns + "Months", Months.Select(m => new XElement(Ns + m))))),
            ScheduleKind.AtStartup => new XElement(Ns + "LogonTrigger", new XElement(Ns + "UserId", user), new XElement(Ns + "Delay", StartupDelay)),
            _ => new XElement(Ns + "LogonTrigger", new XElement(Ns + "UserId", user)),
        };
        trigger.Add(new XElement(Ns + "Enabled", "true"));

        var doc = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Author", user),
                new XElement(Ns + "Description", "CleanSuite scheduled cleanup")),
            new XElement(Ns + "Triggers", trigger),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", user),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "true"),
                new XElement(Ns + "StopIfGoingOnBatteries", "true"),
                new XElement(Ns + "StartWhenAvailable", "false"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "ExecutionTimeLimit", "PT4H")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", Quote(exePath)),
                    new XElement(Ns + "Arguments", Switch + " " + TaskLetters.Format(s.Tasks)))));
        return new XDeclaration("1.0", "UTF-16", null) + Environment.NewLine + doc;
    }

    /// <summary>
    /// The schedule stored in a task definition, or null when the task is not a CleanSuite schedule this program can show
    /// (another command, no "--tasks", no trigger or a trigger kind it does not create).
    /// </summary>
    public static Schedule? Parse(string xml)
    {
        XElement root;
        try { root = XElement.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }
        var args = root.Element(Ns + "Actions")?.Element(Ns + "Exec")?.Element(Ns + "Arguments")?.Value ?? "";
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = Array.IndexOf(parts, Switch);
        var tasks = i >= 0 && i + 1 < parts.Length ? TaskLetters.Parse(parts[i + 1]) : ScheduledTasks.None;
        if (tasks == ScheduledTasks.None) return null;

        var trigger = root.Element(Ns + "Triggers")?.Elements().FirstOrDefault();
        if (trigger == null) return null;
        var startText = trigger.Element(Ns + "StartBoundary")?.Value;
        DateTime start = DateTime.Today;
        // A written offset ("Z", "+03:00") is converted to local time by the parser.
        if (startText != null && !DateTime.TryParse(startText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out start)) return null;

        int Int(XElement? e, int fallback) => int.TryParse(e?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        switch (trigger.Name.LocalName)
        {
            case "TimeTrigger":
                return new Schedule(tasks, ScheduleKind.Once, start);
            case "BootTrigger":
                return new Schedule(tasks, ScheduleKind.AtStartup, start);
            case "LogonTrigger":
                return new Schedule(tasks, trigger.Element(Ns + "Delay") != null ? ScheduleKind.AtStartup : ScheduleKind.AtLogon, start);
            case "CalendarTrigger":
                if (trigger.Element(Ns + "ScheduleByDay") is { } day)
                    return new Schedule(tasks, ScheduleKind.Daily, start, Int(day.Element(Ns + "DaysInterval"), 1));
                if (trigger.Element(Ns + "ScheduleByWeek") is { } week)
                {
                    var dow = week.Element(Ns + "DaysOfWeek")?.Elements().Select(e => Enum.TryParse<DayOfWeek>(e.Name.LocalName, out var d) ? d : (DayOfWeek?)null)
                        .FirstOrDefault(d => d != null);
                    return new Schedule(tasks, ScheduleKind.Weekly, start, Int(week.Element(Ns + "WeeksInterval"), 1), dow ?? start.DayOfWeek);
                }
                if (trigger.Element(Ns + "ScheduleByMonth") is { } month)
                    return new Schedule(tasks, ScheduleKind.Monthly, start,
                        DayOfMonth: Int(month.Element(Ns + "DaysOfMonth")?.Element(Ns + "Day"), start.Day));
                return null;
            default:
                return null;
        }
    }

    private static string Quote(string path) => path.Contains(' ') ? "\"" + path + "\"" : path;
}
