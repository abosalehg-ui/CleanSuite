using System.Diagnostics;

namespace CleanSuite.History;

/// <summary>Result of an analysis: the actions to review, plus notes such as "skipped: browser is running".</summary>
public sealed class HistoryPlan
{
    public List<HistoryAction> Actions { get; } = new();
    public List<HistoryNote> Notes { get; } = new();
    public long TotalSize => Actions.Sum(a => a.Size);

    public void Add(HistoryPlan other)
    {
        Actions.AddRange(other.Actions);
        Notes.AddRange(other.Notes);
    }
}

/// <param name="Code">Stable code for the UI ("browser-running", "not-installed"…).</param>
public sealed record HistoryNote(string ItemKey, string Code, string Text);

/// <summary>Detects running processes (abstracted so tests do not depend on what runs on the machine).</summary>
public interface IProcessProbe
{
    bool IsRunning(string exeName);
}

public sealed class SystemProcessProbe : IProcessProbe
{
    public bool IsRunning(string exeName)
    {
        var name = Path.GetFileNameWithoutExtension(exeName);
        var procs = Process.GetProcessesByName(name);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}

/// <summary>
/// Per-user base folders the browser cleaners look in. "All users" adds every profile folder under the users directory
/// (History\Users\Cookies / Cache = 1 in the original); only folders that exist and are readable are used.
/// </summary>
public sealed class UserFolders
{
    public required IReadOnlyList<(string Roaming, string Local)> Users { get; init; }

    public static UserFolders CurrentUser() => new()
    {
        Users = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
             Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        },
    };

    public static UserFolders AllUsers()
    {
        var list = new List<(string, string)>(CurrentUser().Users);
        var profileRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (profileRoot != null && Directory.Exists(profileRoot))
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(profileRoot).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { dirs = Array.Empty<string>(); }
            foreach (var d in dirs)
            {
                var roaming = Path.Combine(d, "AppData", "Roaming");
                var local = Path.Combine(d, "AppData", "Local");
                if (Directory.Exists(roaming) && !list.Any(u => u.Item1.Equals(roaming, StringComparison.OrdinalIgnoreCase)))
                    list.Add((roaming, local));
            }
        }
        return new UserFolders { Users = list };
    }
}
