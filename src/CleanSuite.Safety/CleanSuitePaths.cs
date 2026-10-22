namespace CleanSuite.Safety;

/// <summary>
/// Where CleanSuite keeps its own data, all under %LocalAppData%\CleanSuite (per user, no administrator rights needed). The one
/// place these folders are named, so the tools, the Restore page and the uninstaller agree.
/// </summary>
public static class CleanSuitePaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSuite");

    /// <summary>Zip backups ("Jnk …", "Dup …"), registry backups ("Reg ….reg"), shortcut and startup copies, and the ledger.</summary>
    public static string Backups => Path.Combine(Root, "Backups");

    /// <summary>One folder per modifying session with its journal.jsonl.</summary>
    public static string Journal => Path.Combine(Root, "Journal");

    /// <summary>Unexpected errors of the desktop program (errors.log).</summary>
    public static string Logs => Path.Combine(Root, "Logs");

    /// <summary>The user's own junk ignore lists.</summary>
    public static string IgnoreJunk => Path.Combine(Root, "Ignore_Junk");

    /// <summary>The user's own registry ignore lists.</summary>
    public static string IgnoreReg => Path.Combine(Root, "Ignore_Reg");

    /// <summary>One line per step of every scheduled run.</summary>
    public static string ScheduledLog => Path.Combine(Root, "Scheduled.log");
}
