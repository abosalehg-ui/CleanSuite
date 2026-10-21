using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CleanSuite.Safety;

namespace CleanSuite.Uninstall;

/// <summary>Starts a command line. Abstracted so tests never start real uninstallers.</summary>
public interface IProcessLauncher
{
    /// <summary>Starts the command without waiting; throws <see cref="Win32Exception"/> on failure.</summary>
    void Start(string commandLine);
}

/// <summary>
/// CreateProcessW with the full command line, as the original. When the uninstaller needs
/// administrator rights (ERROR_ELEVATION_REQUIRED) it is started again through ShellExecute "runas", which the original
/// does not do (its CreateProcess call simply fails).
/// </summary>
public sealed class Win32ProcessLauncher : IProcessLauncher
{
    /// <summary>
    /// Starts the program named by the command line. Its path is resolved first and passed as lpApplicationName, so Windows
    /// never guesses where an unquoted path with spaces ends (C:\Program.exe for C:\Program Files\…). A command whose
    /// program does not exist is refused.
    /// </summary>
    public void Start(string commandLine)
    {
        var (exe, args) = UninstallManager.ResolveCommand(commandLine) ?? throw new Win32Exception(2 /* ERROR_FILE_NOT_FOUND */);
        commandLine = "\"" + exe + "\"" + (args.Length > 0 ? " " + args : "");
        var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        var buf = new StringBuilder(commandLine, Math.Max(commandLine.Length + 1, 32768));
        if (CreateProcessW(exe, buf, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out var pi))
        {
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return;
        }
        int err = Marshal.GetLastWin32Error();
        if (err != 740 /* ERROR_ELEVATION_REQUIRED */) throw new Win32Exception(err);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" })?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? dir, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}

public sealed record UninstallActionResult(bool Success, string? Error = null, string? BackupPath = null);

/// <summary>
/// Uninstall Manager actions: Uninstall, Change, Repair (start the program's own commands), Remove Entry (export the
/// key to a .reg backup, verify it, then delete), and the list report. Every action is journaled.
/// </summary>
public sealed class UninstallManager
{
    private readonly IRegistryRoots _roots;
    private readonly IProcessLauncher _launcher;
    private readonly string? _journalBase;
    private readonly string _backupFolder;

    public UninstallManager(IRegistryRoots? roots = null, IProcessLauncher? launcher = null, string? journalBase = null, string? backupFolder = null)
    {
        _roots = roots ?? WindowsRegistryRoots.Instance;
        _launcher = launcher ?? new Win32ProcessLauncher();
        _journalBase = journalBase;
        _backupFolder = backupFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSuite", "Backups");
    }

    /// <summary>Like the original refuses to uninstall itself ("Ace Utilities"), CleanSuite refuses its own entry.</summary>
    public static bool IsSelf(UninstallEntry e) => e.Name.Equals("CleanSuite", StringComparison.OrdinalIgnoreCase);

    public UninstallActionResult Uninstall(UninstallEntry e)
    {
        if (IsSelf(e)) return new(false, "self");
        if (!e.CanUninstall) return new(false, "not allowed");
        return Launch("uninstall", e, e.UninstallCommand);
    }

    public UninstallActionResult Modify(UninstallEntry e) =>
        e.CanModify ? Launch("modify", e, e.ModifyCommand) : new(false, "not allowed");

    public UninstallActionResult Repair(UninstallEntry e) =>
        e.CanRepair ? Launch("repair", e, e.RepairCommand) : new(false, "not allowed");

    private UninstallActionResult Launch(string action, UninstallEntry e, string cmd)
    {
        using var journal = new OperationJournal("uninstall", _journalBase);
        if (cmd.Length == 0) { journal.Record(action, e.KeyDisplay, false, error: "no command"); return new(false, "no command"); }
        try
        {
            _launcher.Start(cmd);
            journal.Record(action, e.Name + " :: " + cmd, true);
            return new(true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            journal.Record(action, e.Name + " :: " + cmd, false, error: ex.Message);
            return new(false, ex.Message);
        }
    }

    /// <summary>
    /// Removes the program's Uninstall key (only the list entry; the program's files stay). The key is exported to
    /// "Backups\Reg … .reg" first and the export is checked; without a backup nothing is deleted. Windows Installer
    /// entries cannot be removed (as in the original).
    /// </summary>
    public UninstallActionResult RemoveEntry(UninstallEntry e)
    {
        using var journal = new OperationJournal("uninstall", _journalBase);
        if (!e.CanRemoveEntry) { journal.Record("remove-entry", e.KeyDisplay, false, error: "windows installer"); return new(false, "windows installer"); }
        var root = _roots.Root(e.Hive);
        try
        {
            using var backup = new RegBackupWriter(_backupFolder);
            if (!backup.ExportKey(root, e.Hive, e.KeyPath))
            {
                journal.Record("remove-entry", e.KeyDisplay, false, error: "no longer exists");
                return new(false, "no longer exists");
            }
            backup.Dispose();
            var text = File.ReadAllText(backup.FilePath!);
            if (!text.Contains("[" + e.KeyDisplay + "]", StringComparison.OrdinalIgnoreCase))
            {
                journal.Record("remove-entry", e.KeyDisplay, false, backupRef: backup.FilePath, error: "backup failed");
                return new(false, "backup failed", backup.FilePath);
            }
            root.DeleteSubKeyTree(e.KeyPath, throwOnMissingSubKey: false);
            journal.Record("remove-entry", e.KeyDisplay, true, backupRef: backup.FilePath);
            return new(true, null, backup.FilePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            var msg = ex is IOException ? ex.Message : "requires administrator rights";
            journal.Record("remove-entry", e.KeyDisplay, false, error: msg);
            return new(false, msg);
        }
    }

    /// <summary>
    /// The list report: plain text with a header, one block per program and a total; or tab-delimited
    /// rows without header. Written as UTF-8 with BOM; dates are ISO (yyyy-MM-dd).
    /// </summary>
    public static void WriteReport(string file, IReadOnlyList<UninstallEntry> entries, bool tabDelimited, IReadOnlyList<string> headers)
    {
        var sb = new StringBuilder();
        static string[] Cells(UninstallEntry e) =>
        [
            e.Name, e.Publisher, e.InstalledOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            e.SizeKb is { } kb ? FormatSize(kb) : "", e.Version,
        ];
        if (tabDelimited)
        {
            foreach (var e in entries) sb.Append(string.Join('\t', Cells(e))).Append("\r\n");
        }
        else
        {
            sb.Append("Report created on ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" by CleanSuite\r\n\r\n");
            for (int i = 0; i < headers.Count; i++) sb.Append($"Header {i + 1}: {headers[i]}\r\n");
            var line = new string('-', 60);
            sb.Append(line).Append("\r\n").Append(line).Append("\r\n");
            foreach (var e in entries)
            {
                foreach (var c in Cells(e)) sb.Append(c).Append("\r\n");
                sb.Append(line).Append("\r\n");
            }
            sb.Append($"Total {entries.Count} item(s)\r\n");
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>StrFormatByteSize-style size of a KB count ("12.3 MB"), with invariant digits.</summary>
    public static string FormatSize(long kb)
    {
        double bytes = kb * 1024.0;
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return u == 0 ? $"{bytes:0} bytes" : bytes.ToString(bytes >= 100 ? "0" : bytes >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + units[u];
    }

    /// <summary>Program and arguments of a command line ("C:\a b\x.exe" /s → (C:\a b\x.exe, /s)); unquoted paths with spaces are found up to ".exe".</summary>
    /// <summary>
    /// The existing program a command line names, and its arguments; null when no candidate exists. A quoted path is taken
    /// as written. For an unquoted one every space is a possible end of the path, and the longest candidate that is an
    /// existing file wins, so "C:\Program Files\My App\unins.exe /S" is never read as C:\Program.exe.
    /// Environment variables are expanded; relative names are refused.
    /// </summary>
    public static (string File, string Args)? ResolveCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            var (f, a) = SplitCommand(cmd);
            return Path.IsPathFullyQualified(f) && File.Exists(f) ? (f, a) : null;
        }
        var spaces = new List<int>();
        for (int i = 0; i < cmd.Length; i++) if (cmd[i] == ' ') spaces.Add(i);
        spaces.Add(cmd.Length);
        for (int k = spaces.Count - 1; k >= 0; k--)
        {
            var candidate = cmd[..spaces[k]];
            foreach (var c in new[] { candidate, candidate + ".exe" })
                if (Path.IsPathFullyQualified(c) && File.Exists(c)) return (c, cmd[spaces[k]..].Trim());
        }
        return null;
    }

    public static (string File, string Args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end < 0 ? (cmd.Trim('"'), "") : (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0) return (cmd[..(exe + 4)], cmd[(exe + 4)..].Trim());
        int sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }
}
