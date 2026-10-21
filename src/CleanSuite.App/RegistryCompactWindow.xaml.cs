using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.RegistryCompact;

namespace CleanSuite.App;

/// <summary>
/// Registry compactor, analysis only (the original's Registry Defragmenter &amp; Compact Optimization page): measures how much
/// each loaded hive would shrink. The original then schedules RegReplaceKey for every hive and forces an immediate restart;
/// CleanSuite stops after measuring, so nothing in Windows is changed.
/// </summary>
public partial class RegistryCompactWindow : Window
{
    private readonly CompactReport? _preview;
    private CompactReport? _report;
    private CancellationTokenSource? _cts;
    private string? _progress;

    /// <param name="preview">Developer aid (--snapshot): show this report; nothing is analysed.</param>
    public RegistryCompactWindow(CompactReport? preview = null)
    {
        InitializeComponent();
        _preview = preview;
        _report = preview;
        ThemeManager.ApplyTitleBar(this);
        AdminText.Visibility = Environment.IsPrivilegedProcess || preview != null ? Visibility.Collapsed : Visibility.Visible;
        AnalyseButton.IsEnabled = Environment.IsPrivilegedProcess && preview == null;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => { Loc.Instance.LanguageChanged -= OnLanguageChanged; _cts?.Cancel(); };
        Render();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        Grid.ItemsSource = _report?.Ordered().Select(r => new CompactRow(r)).ToList();
        ReportButton.IsEnabled = _report != null && _cts == null;
        Bidi.SetText(StatusText, _cts != null ? _progress ?? ""
            : _report == null ? ""
            : _report.Cancelled ? Loc.T("RcCancelled")
            : (_report.TotalSaving > 0 ? Loc.F("RcSummary", Loc.FormatSize(_report.TotalSaving), Loc.FormatSize(_report.TotalCurrent), Loc.FormatSize(_report.TotalCompact))
               : Loc.T("RcNothing"))
              + (_report.LeftOver.Count > 0 ? Environment.NewLine + Loc.F("RcLeftOver", Loc.N0(_report.LeftOver.Count), Loc.Ltr(Path.GetDirectoryName(_report.LeftOver[0]) ?? "")) : ""));
    }

    private async void Analyse_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        AnalyseButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.Visibility = Visibility.Visible;
        _report = null;
        _progress = null;
        Render();
        var ct = _cts.Token;
        try
        {
            _report = await Task.Run(() => new CompactAnalyzer(new WindowsHiveSource(), new WindowsHiveSaver()).Analyze(
                (i, n, h) => Dispatcher.BeginInvoke(() => { _progress = Loc.F("RcProgress", Loc.N0(i), Loc.N0(n), Loc.Ltr(h.Name)); Render(); }), ct));
        }
        catch (PrivilegeNotHeldException)
        {
            AdminText.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Loc.Show(this, ex.Message, Loc.T("RcTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { UiErrors.Show(this, ex, "registry compactor"); }
        finally
        {
            _cts = null;
            CancelButton.IsEnabled = false;
            AnalyseButton.IsEnabled = Environment.IsPrivilegedProcess;
            Progress.Visibility = Visibility.Collapsed;
            Render();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "registry-compact-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".txt",
            Filter = Loc.T("RcReportFilter") + " (*.txt)|*.txt",
        };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("RcTitle") + " — " + Loc.FormatDate(DateTime.Now)).AppendLine();
        foreach (var r in _report.Ordered().Select(r => new CompactRow(r)))
            sb.Append(r.Name).Append('\t').Append(r.CurrentText).Append('\t').Append(r.CompactText).Append('\t').Append(r.SavingText)
              .Append('\t').Append(r.StatusText).Append('\t').Append(r.FileText).AppendLine();
        sb.AppendLine().AppendLine(StatusText.Text);
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            Bidi.SetText(StatusText, Loc.F("RcSaved", Loc.Ltr(dlg.FileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Loc.Show(this, ex.Message, Loc.T("RcSaveReport"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Developer aid: a typical report for "--open-compactor --snapshot"; nothing is analysed.</summary>
    internal static CompactReport PreviewReport()
    {
        HiveCandidate H(HiveRoot root, string sub, string file, long size, HiveSkip skip = HiveSkip.None) =>
            new(root, sub, (root == HiveRoot.LocalMachine ? @"\REGISTRY\MACHINE\" : @"\REGISTRY\USER\") + sub, file, size, skip);
        const string cfg = @"C:\Windows\System32\config\";
        return new CompactReport
        {
            Rows =
            [
                new(H(HiveRoot.LocalMachine, "SOFTWARE", cfg + "SOFTWARE", 142_606_336), HiveStatus.Measured, 142_606_336, 118_489_088),
                new(H(HiveRoot.LocalMachine, "SYSTEM", cfg + "SYSTEM", 31_457_280), HiveStatus.Measured, 31_457_280, 27_262_976),
                new(H(HiveRoot.Users, "S-1-5-21-1001", @"C:\Users\me\NTUSER.DAT", 8_650_752), HiveStatus.NoGain, 8_650_752, 8_912_896),
                new(H(HiveRoot.Users, "S-1-5-21-1001_Classes", @"C:\Users\me\AppData\Local\Microsoft\Windows\UsrClass.dat", 5_505_024),
                    HiveStatus.SaveFailed, 5_505_024, null, 32),
                new(H(HiveRoot.LocalMachine, "SAM", cfg + "SAM", 65_536, HiveSkip.Excluded), HiveStatus.Skipped, 65_536, null),
                new(H(HiveRoot.LocalMachine, "ELAM", cfg + "ELAM", 32_768, HiveSkip.Small), HiveStatus.Skipped, 32_768, null),
            ],
        };
    }
}

public sealed class CompactRow(HiveRow row)
{
    public HiveRow Row { get; } = row;
    public string Name => Row.Hive.Name;
    public string FileText => Row.Hive.FilePath ?? Row.Hive.RegistryPath;
    public string CurrentText => Row.CurrentSize is long c ? Loc.FormatSize(c) : "";
    public string CompactText => Row.CompactSize is long c ? Loc.FormatSize(c) : "";
    public string SavingText => Row.Status == HiveStatus.Measured ? Loc.FormatSize(Row.Saving) : "";
    public string ChipText => StatusText;
    public string Tone => Row.Status switch
    {
        HiveStatus.Measured => "success",
        HiveStatus.NoGain => "neutral",
        HiveStatus.Skipped => "neutral",
        HiveStatus.Cancelled => "warning",
        _ => "danger",
    };
    public string StatusText => Row.Status switch
    {
        HiveStatus.Skipped => Loc.T("RcSkip_" + Row.Hive.Skip),
        HiveStatus.SaveFailed => Loc.F("RcSt_SaveFailed", Row.Error.ToString(CultureInfo.InvariantCulture)),
        _ => Loc.T("RcSt_" + Row.Status),
    };
}
