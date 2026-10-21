using System.Runtime.InteropServices;

namespace CleanSuite.History;

/// <summary>Empties the clipboard (item 9; OpenClipboard(NULL) + EmptyClipboard).</summary>
public sealed class ClearClipboardAction : HistoryAction
{
    public override string Kind => "clipboard";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    bool ok = EmptyClipboard();
                    ctx.Journal.Record("clipboard", "clipboard", ok);
                    return ok ? ActionResult.Ok : ActionResult.Fail("clipboard could not be emptied");
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(50);                                  // another program holds the clipboard
        }
        return ActionResult.Fail("clipboard is in use");
    }

    public static bool HasContent() => CountClipboardFormats() > 0;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern int CountClipboardFormats();
}

/// <summary>Clears one event log channel (item 45; EvtClearLog). Most channels require administrator rights.</summary>
public sealed class ClearEventLogAction : HistoryAction
{
    public override string Kind => "eventlog";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        bool ok = EventLogs.EvtClearLog(IntPtr.Zero, Target, null, 0);
        var err = ok ? null : Marshal.GetLastWin32Error() == 5 ? "requires administrator rights" : new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
        ctx.Journal.Record("eventlog-clear", Target, ok, error: err);
        return ok ? ActionResult.Ok : ActionResult.Fail(err!);
    }
}

/// <summary>Event log channels with records (EvtOpenChannelEnum / EvtNextChannelPath / EvtGetLogInfo).</summary>
public static class EventLogs
{
    public static List<(string Channel, long Records)> ChannelsWithRecords()
    {
        var result = new List<(string, long)>();
        var e = EvtOpenChannelEnum(IntPtr.Zero, 0);
        if (e == IntPtr.Zero) return result;
        try
        {
            var buf = new char[512];
            while (true)
            {
                if (!EvtNextChannelPath(e, buf.Length, buf, out int used))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 122) { buf = new char[used]; continue; }       // ERROR_INSUFFICIENT_BUFFER
                    break;                                                    // ERROR_NO_MORE_ITEMS or failure
                }
                var name = new string(buf, 0, Math.Max(0, used - 1));
                var log = EvtOpenLog(IntPtr.Zero, name, 1 /* EvtOpenChannelPath */);
                if (log == IntPtr.Zero) continue;                             // not readable without elevation
                try
                {
                    var v = new byte[24];
                    if (EvtGetLogInfo(log, 5 /* EvtLogNumberOfLogRecords */, v.Length, v, out _))
                    {
                        long n = BitConverter.ToInt64(v, 0);
                        if (n > 0) result.Add((name, n));
                    }
                }
                finally { EvtClose(log); }
            }
        }
        finally { EvtClose(e); }
        return result;
    }

    [DllImport("wevtapi.dll", SetLastError = true)] private static extern IntPtr EvtOpenChannelEnum(IntPtr session, int flags);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EvtNextChannelPath(IntPtr e, int size, [Out] char[] buffer, out int used);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtOpenLog(IntPtr session, string path, int flags);
    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtGetLogInfo(IntPtr log, int property, int size, byte[] buffer, out int used);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool EvtClearLog(IntPtr session, string channel, string? backup, int flags);
    [DllImport("wevtapi.dll")] private static extern bool EvtClose(IntPtr handle);
}

/// <summary>
/// Closes Explorer gracefully (WM_QUIT to "Progman", as the original) so it does not rewrite the tray icon history
/// while it is being deleted; <see cref="StartExplorerAction"/> starts it again afterwards.
/// </summary>
public sealed class StopExplorerAction : HistoryAction
{
    public override string Kind => "explorer-stop";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        var w = FindWindowW("Progman", null);
        if (w == IntPtr.Zero) return ActionResult.Ok;
        PostMessageW(w, 0x12 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < 40 && FindWindowW("Progman", null) != IntPtr.Zero; i++) Thread.Sleep(100);
        bool stopped = FindWindowW("Progman", null) == IntPtr.Zero;
        ctx.Journal.Record("explorer-stop", "explorer.exe", stopped);
        return stopped ? ActionResult.Ok : ActionResult.Fail("Explorer did not close");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}

public sealed class StartExplorerAction : HistoryAction
{
    public override string Kind => "explorer-start";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        if (StopExplorerAction.FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero) return ActionResult.Ok;   // already running
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
            ctx.Journal.Record("explorer-start", exe, true);
            return ActionResult.Ok;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            ctx.Journal.Record("explorer-start", exe, false, error: e.Message);
            return ActionResult.Fail(e.Message);
        }
    }
}
