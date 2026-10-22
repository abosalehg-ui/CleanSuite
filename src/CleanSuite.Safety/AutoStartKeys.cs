namespace CleanSuite.Safety;

/// <summary>Registry keys a restore from an untrusted backup must not write.</summary>
public static class AutoStartKeys
{
    /// <summary>Reason recorded for a key kept out of a restore.</summary>
    public const string Refused = "starts programs; not restored from a backup CleanSuite did not make";

    /// <summary>
    /// Keys through which Windows starts programs or loads code: Run and RunOnce, policies, Winlogon, Image File Execution
    /// Options, AppInit (CurrentVersion\Windows), services, Session Manager, Active Setup, shell folders, shell hooks, the task
    /// cache and the command processor. A registry backup that CleanSuite did not write, or that changed since, never writes there:
    /// a file placed in the backup folder could otherwise install a program that runs at every sign-in.
    /// </summary>
    public static bool IsMatch(string key)
    {
        var k = "\\" + key.Replace('/', '\\').Trim('\\') + "\\";
        return Parts.Any(p => k.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] Parts =
    [
        @"\CurrentVersion\Run\", @"\CurrentVersion\RunOnce\", @"\CurrentVersion\RunOnceEx\", @"\CurrentVersion\RunServices\",
        @"\CurrentVersion\RunServicesOnce\", @"\CurrentVersion\Policies\", @"\Windows NT\CurrentVersion\Winlogon\",
        @"\Windows NT\CurrentVersion\Image File Execution Options\", @"\Windows NT\CurrentVersion\Windows\",
        @"\Windows NT\CurrentVersion\SilentProcessExit\", @"\Windows NT\CurrentVersion\Schedule\TaskCache\",
        @"\CurrentControlSet\Services\", @"\ControlSet001\Services\", @"\ControlSet002\Services\", @"\Control\Session Manager\",
        @"\Software\Policies\", @"\Active Setup\Installed Components\", @"\Explorer\Shell Folders\", @"\Explorer\User Shell Folders\",
        @"\Explorer\ShellServiceObjectDelayLoad\", @"\Explorer\Browser Helper Objects\", @"\Terminal Server\Install\",
        @"\Command Processor\",
    ];

    /// <summary>
    /// The key filter for importing <paramref name="regFile"/>: null (everything allowed) for a backup CleanSuite wrote and that is
    /// unchanged since (<see cref="BackupLedger"/>), otherwise <see cref="IsMatch"/>.
    /// </summary>
    public static Func<string, bool>? FilterFor(string regFile) => BackupLedger.Status(regFile) == BackupTrust.Verified ? null : IsMatch;
}
