using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.Restore;
using CleanSuite.Scheduler;

namespace CleanSuite.App;

/// <summary>
/// Home page: free space of the fixed drives, the newest backup, the scheduled cleanup, and buttons that open a tool. It only
/// reads; nothing is scanned, cleaned or changed from here. Refreshed every time the page is shown.
/// </summary>
public partial class HomeView : UserControl
{
    private int _refreshVersion, _shownVersion;

    /// <summary>A refresh is still reading (used by the snapshot aid).</summary>
    public bool Busy => _shownVersion != _refreshVersion;

    public HomeView()
    {
        InitializeComponent();
        Loc.Instance.LanguageChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    private void View_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) Refresh();
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && Window.GetWindow(this) is MainWindow w) w.Open(key);
    }

    /// <summary>What the page shows, read on a background thread (drives, backup folders, Task Scheduler, the log).</summary>
    private sealed record HomeData(List<DriveRow> Drives, BackupItem? Newest, int BackupCount, ScheduleState Schedule, DateTime? LastRun);

    private sealed record ScheduleState(bool Exists, Schedule? Settings, string Name, DateTime? Next, string? Error);

    private async void Refresh()
    {
        int version = ++_refreshVersion;
        HomeData data;
        try { data = await Task.Run(Read); }
        catch (Exception ex) { UiErrors.Log(ex, "home page"); _shownVersion = version; return; }
        if (version != _refreshVersion) return;                     // a newer refresh (page shown again, language) is on its way
        _shownVersion = version;

        DriveList.ItemsSource = data.Drives;
        Bidi.SetText(BackupText, data.Newest == null ? Loc.T("HmBackupNone")
            : Loc.F("HmBackupLast", Loc.T("RsKind_" + data.Newest.Kind), Loc.Ltr(Loc.FormatDate(data.Newest.Created))));
        BackupCount.Text = data.BackupCount > 0 ? Loc.F("RsCount", Loc.N0(data.BackupCount)) : "";
        Bidi.SetText(ScheduleText, ScheduleSummary(data.Schedule));
        LastRunText.Text = data.LastRun is { } last ? Loc.F("HmLastRun", Loc.FormatDate(last)) : Loc.T("HmNeverRun");
    }

    private static HomeData Read()
    {
        var drives = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.DriveType == DriveType.Fixed && d.IsReady) drives.Add(new DriveRow(d)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // a drive that went away in between
        }
        var backups = BackupCatalog.List(BackupCatalog.DefaultFolders());
        return new HomeData(drives, backups.OrderByDescending(b => b.Created).FirstOrDefault(), backups.Count, ReadSchedule(), LastRun());
    }

    private static ScheduleState ReadSchedule()
    {
        var schedule = new TaskSchedule(new WindowsTaskStore(), Environment.ProcessPath ?? "CleanSuite.App.exe");
        try
        {
            var (exists, s) = schedule.Load();
            var next = exists && s != null && s.Kind is not (ScheduleKind.AtStartup or ScheduleKind.AtLogon) ? schedule.NextRun() : null;
            return new ScheduleState(exists, s, schedule.Name, next, null);
        }
        catch (Exception ex) when (WindowsTaskStore.IsComFailure(ex))
        {
            return new ScheduleState(false, null, schedule.Name, null, ex.Message);
        }
    }

    private static string ScheduleSummary(ScheduleState st)
    {
        if (st.Error != null) return Loc.F("SdUnavailable", st.Error);
        if (!st.Exists) return Loc.T("SdNone");
        if (st.Settings == null) return Loc.F("SdForeign", Loc.Ltr(st.Name));
        return st.Settings.Kind switch
        {
            ScheduleKind.AtStartup => Loc.T("SdNextStartup"),
            ScheduleKind.AtLogon => Loc.T("SdNextLogon"),
            _ => st.Next is { } n ? Loc.F("SdNext", Loc.FormatDate(n)) : Loc.T("SdNextNone"),
        };
    }

    /// <summary>
    /// Start time of the newest line of Scheduled.log ("yyyy-MM-dd HH:mm:ss  …"). Only the last 16 KB are read: the log
    /// grows with every scheduled run and is never trimmed.
    /// </summary>
    internal static DateTime? LastRun(string? path = null)
    {
        path ??= ScheduledRunner.DefaultLogPath;
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int Tail = 16 * 1024;
            if (fs.Length > Tail) fs.Seek(-Tail, SeekOrigin.End);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = reader.ReadToEnd().Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length >= 19 && DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    return t;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

public sealed class DriveRow(DriveInfo d)
{
    private readonly long _total = d.TotalSize, _free = d.AvailableFreeSpace;
    public string Name { get; } = string.IsNullOrEmpty(d.VolumeLabel) ? d.Name.TrimEnd('\\') : $"{d.Name.TrimEnd('\\')}  {d.VolumeLabel}";
    public string FreeText => Loc.F("HmFree", Loc.FormatSize(_free), Loc.FormatSize(_total));
    public GridLength UsedWidth => new(Math.Max(_total - _free, 1), GridUnitType.Star);
    public GridLength FreeWidth => new(Math.Max(_free, 0), GridUnitType.Star);
    /// <summary>Accent colour, or the danger colour when less than 10 % is free.</summary>
    public Brush Bar => (Brush)Application.Current.FindResource(_total > 0 && _free * 10 < _total ? "DangerFg" : "AccentBg");
}
