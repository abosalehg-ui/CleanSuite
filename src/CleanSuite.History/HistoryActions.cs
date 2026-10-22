using System.Runtime.InteropServices;
using System.Text;
using CleanSuite.Safety;

namespace CleanSuite.History;

/// <summary>Everything one action needs while executing: the journal, the delete guard and cancellation.</summary>
public sealed class ExecutionContext
{
    public required OperationJournal Journal { get; init; }
    public required ProtectedPaths Protected { get; init; }
    public CancellationToken Cancellation { get; init; }
    /// <summary>Registry roots registry actions apply to.</summary>
    public IRegistryRoots Registry { get; init; } = WindowsRegistryRoots.Instance;
    /// <summary>Receives a .reg export of every key or value before it is deleted.</summary>
    public RegBackupWriter? RegistryBackup { get; init; }
}

public sealed record ActionResult(bool Success, string? Error = null, long BytesFreed = 0)
{
    public static readonly ActionResult Ok = new(true);
    public static ActionResult Fail(string error) => new(false, error);
}

/// <summary>
/// One reviewable step of a History clean. Analysis builds a list of actions without modifying anything; the clean step
/// executes exactly the reviewed actions, re-checking each target first.
/// </summary>
public abstract class HistoryAction
{
    /// <summary>Catalog key of the item that produced the action (e.g. "chrome.cookies", "win.prefetch").</summary>
    public required string ItemKey { get; init; }
    /// <summary>File, folder, database or INI file the action applies to.</summary>
    public required string Target { get; init; }
    /// <summary>Bytes the action is expected to free (0 when unknown, e.g. SQL).</summary>
    public long Size { get; init; }
    /// <summary>Short machine-readable kind for the UI and the journal.</summary>
    public abstract string Kind { get; }
    /// <summary>Human-readable detail (SQL statement, INI section…), may be null.</summary>
    public virtual string? Detail => null;

    public abstract ActionResult Execute(ExecutionContext ctx);
}

/// <summary>Deletes one file if it is still the file that was analysed (same size and time stamp).</summary>
public sealed class DeleteFileAction : HistoryAction
{
    public DateTime LastWriteUtc { get; init; }
    public override string Kind => "delete";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        if (ctx.Protected.CheckResolved(Target) is { } why) return Record(ctx, ActionResult.Fail(why));
        var fi = new FileInfo(Target);
        if (!fi.Exists) return ActionResult.Ok;                                       // already gone: nothing to do
        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) return Record(ctx, ActionResult.Fail("reparse point"));
        if (FileIdentity.ChangedSince(fi, Size, LastWriteUtc) is { } changed) return Record(ctx, ActionResult.Fail(changed));
        try
        {
            if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes &= ~FileAttributes.ReadOnly;
            fi.Delete();
            return Record(ctx, File.Exists(Target) ? ActionResult.Fail("still exists") : new ActionResult(true, null, Size));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Record(ctx, ActionResult.Fail(e.Message));
        }
    }

    private ActionResult Record(ExecutionContext ctx, ActionResult r)
    {
        ctx.Journal.Record("delete", Target, r.Success, error: r.Error);
        return r;
    }
}

/// <summary>Removes a folder if it is empty when the action runs (/R, and the folders emptied by /E).</summary>
public sealed class RemoveFolderIfEmptyAction : HistoryAction
{
    public override string Kind => "rmdir";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        if (!Directory.Exists(Target)) return ActionResult.Ok;
        if (ctx.Protected.CheckResolved(Target, isDirectory: true) is { } why) return ActionResult.Fail(why);
        try
        {
            if ((File.GetAttributes(Target) & FileAttributes.ReparsePoint) != 0) return ActionResult.Fail("reparse point");
            if (Directory.EnumerateFileSystemEntries(Target).Any()) return ActionResult.Ok;   // not empty: kept, as in the original
            Directory.Delete(Target, false);
            ctx.Journal.Record("rmdir", Target, true);
            return ActionResult.Ok;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Journal.Record("rmdir", Target, false, error: e.Message);
            return ActionResult.Fail(e.Message);
        }
    }
}

/// <summary>/C: removes empty subfolders bottom-up (never the folder itself, never through reparse points).</summary>
public sealed class RemoveEmptySubfoldersAction : HistoryAction
{
    public override string Kind => "rmdir-empty";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        if (!Directory.Exists(Target)) return ActionResult.Ok;
        void Walk(string dir, bool isRoot)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            List<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var s in subs)
            {
                try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                Walk(s, false);
            }
            if (isRoot || ctx.Protected.CheckResolved(dir, true) != null) return;
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
                Directory.Delete(dir, false);
                ctx.Journal.Record("rmdir", dir, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ctx.Journal.Record("rmdir", dir, false, error: e.Message); }
        }
        Walk(Target.TrimEnd('\\'), true);
        return ActionResult.Ok;
    }
}

/// <summary>INIFILE rule: deletes a key, or the whole section (WritePrivateProfileStringW(section, key|NULL, NULL, file)).</summary>
public sealed class IniDeleteAction : HistoryAction
{
    public required string Section { get; init; }
    public string? Key { get; init; }
    public override string Kind => "ini";
    public override string? Detail => Key == null ? $"[{Section}]" : $"[{Section}] {Key}";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        if (!File.Exists(Target)) return ActionResult.Ok;
        if (ctx.Protected.CheckResolved(Target) is { } why) return ActionResult.Fail(why);
        bool ok = WritePrivateProfileStringW(Section, Key, null, Target);
        var err = ok ? null : new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
        ctx.Journal.Record("ini-delete", Target, ok, backupRef: Detail, error: err);
        return ok ? ActionResult.Ok : ActionResult.Fail(err!);
    }

    /// <summary>True when the section (and key, if given) is present — analysis reports only entries that exist.</summary>
    public static bool Exists(IniTarget t)
    {
        if (!File.Exists(t.File)) return false;
        var buf = new char[32768];
        if (t.Key == null)
            return GetPrivateProfileSectionW(t.Section, buf, buf.Length, t.File) > 0 || SectionHeaderPresent(t);
        const string missing = "\u0001\u0002missing";
        int n = GetPrivateProfileStringW(t.Section, t.Key, missing, buf, buf.Length, t.File);
        return new string(buf, 0, n) != missing;
    }

    private static bool SectionHeaderPresent(IniTarget t)
    {
        try
        {
            return File.ReadLines(t.File, Encoding.Default).Any(l =>
                l.Trim().Equals("[" + t.Section + "]", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WritePrivateProfileStringW(string section, string? key, string? value, string file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPrivateProfileStringW(string section, string key, string def, char[] buf, int size, string file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPrivateProfileSectionW(string section, char[] buf, int size, string file);
}
