using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CleanSuite.Scheduler;

/// <summary>Where a schedule lives: one task in the root folder of Windows Task Scheduler (tests use an in-memory store).</summary>
public interface ITaskStore
{
    /// <summary>Task definition XML, or null when there is no task with this name.</summary>
    string? GetXml(string name);
    /// <summary>Next run time, or null when Task Scheduler reports none (event triggers, past "once" times, disabled task).</summary>
    DateTime? NextRun(string name);
    void Register(string name, string xml);
    /// <summary>Removes the task; false when it did not exist.</summary>
    bool Delete(string name);
    /// <summary>Validates a definition without registering anything; null when Task Scheduler accepts it, else its message.</summary>
    string? Validate(string xml);
}

/// <summary>
/// Task Scheduler 2.0 through its scripting object (Schedule.Service). The original uses the TS 1.0 COM API
/// (ITaskScheduler/ITask), which Windows now emulates on top of 2.0.
/// </summary>
public sealed class WindowsTaskStore : ITaskStore
{
    private const int CreateOrUpdate = 6, InteractiveToken = 3;
    private const int FileNotFound = unchecked((int)0x80070002);

    /// <summary>
    /// A failure reported by Task Scheduler. The late-bound COM binder turns HRESULTs into exceptions of matching types
    /// (0x80070002 FileNotFoundException, 0x80070005 UnauthorizedAccessException, others COMException or ArgumentException).
    /// </summary>
    public static bool IsComFailure(Exception e) =>
        e is COMException or UnauthorizedAccessException or IOException or ArgumentException or FormatException or InvalidCastException;

    private static dynamic Service()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("Task Scheduler is not available.");
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        return svc;
    }

    private static dynamic Folder() => Service().GetFolder("\\");

    private static dynamic? Find(string name)
    {
        try { return Folder().GetTask(name); }
        catch (Exception e) when (e.HResult == FileNotFound) { return null; }
    }

    public string? GetXml(string name)
    {
        dynamic? t = Find(name);
        return t == null ? null : (string)t.Xml;
    }

    public DateTime? NextRun(string name)
    {
        dynamic? t = Find(name);
        if (t == null) return null;
        DateTime next = t.NextRunTime;
        return next.Year < 1900 ? null : next;          // "no next run" comes back as the zero OLE date (1899-12-30)
    }

    public void Register(string name, string xml) => Folder().RegisterTask(name, xml, CreateOrUpdate, null, null, InteractiveToken, null);

    public bool Delete(string name)
    {
        try { Folder().DeleteTask(name, 0); return true; }
        catch (Exception e) when (e.HResult == FileNotFound) { return false; }
    }

    public string? Validate(string xml)
    {
        try
        {
            dynamic def = Service().NewTask(0);
            def.XmlText = xml;                             // parsed and checked in memory; nothing is registered
            return null;
        }
        catch (Exception e) when (IsComFailure(e)) { return e.Message; }
    }
}

/// <summary>The CleanSuite schedule: one task, read and written as a <see cref="Schedule"/>.</summary>
public sealed class TaskSchedule(ITaskStore store, string exePath, string? taskName = null)
{
    public const string DefaultName = "CleanSuite Maintenance";
    public string Name { get; } = taskName ?? DefaultName;

    public static string CurrentUser => WindowsIdentity.GetCurrent().Name;

    /// <summary>The saved schedule; Exists is true also for a task of this name this program cannot read back.</summary>
    public (bool Exists, Schedule? Schedule) Load()
    {
        var xml = store.GetXml(Name);
        return xml == null ? (false, null) : (true, TaskXml.Parse(xml));
    }

    public DateTime? NextRun() => store.NextRun(Name);

    /// <summary>Creates or replaces the task (the original deletes and recreates it).</summary>
    public void Save(Schedule s, string? user = null) => store.Register(Name, TaskXml.Build(s, exePath, user ?? CurrentUser));

    public bool Remove() => store.Delete(Name);
}
