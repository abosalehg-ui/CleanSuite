using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.DiskUsage;

namespace CleanSuite.App;

/// <summary>
/// Disk analysis tab: analyse a drive or folder read-only, browse the folder tree by size, see
/// folder contents, file types and the 100 largest files. Items can be opened in Explorer or sent to the Recycle Bin.
/// </summary>
public partial class DiskView : UserControl
{
    private DiskFolder? _root;
    private DiskFolder? _current;
    private CancellationTokenSource? _cts;
    private bool _autoStarted;

    private sealed record Target(string Path, string Label)
    {
        public override string ToString() => Label;
    }

    public DiskView()
    {
        InitializeComponent();
        LoadDrives();
        Loc.Instance.LanguageChanged += (_, _) => { LoadDrives(); RenderAll(); };
        ThemeManager.ThemeChanged += (_, _) => { if (_current != null) RenderAll(); };    // chart colours are stepped per theme
        // Two charts side by side, or one under the other when the page is narrow.
        ChartsPanel.SizeChanged += (_, e) => ChartsPanel.Columns = e.NewSize.Width < 620 ? 1 : 2;
        StatusText.Text = Loc.T("DaIntro");
        // Developer aid: "--auto-scan" with "--scan <folder>" analyses that folder when the tab is shown (for --snapshot checks);
        // "--disk-view <n>" opens that view (0 contents, 1 file types, 2 charts, 3 largest files, 4 empty folders).
        Loaded += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            int view = Array.IndexOf(args, "--disk-view");
            if (view >= 0 && view + 1 < args.Length && int.TryParse(args[view + 1], out var n) && n >= 0 && n < Views.Items.Count) Views.SelectedIndex = n;
            if (_autoStarted || !IsVisible || !args.Contains("--auto-scan")) return;
            _autoStarted = true;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--scan" && Directory.Exists(args[i + 1])) AddTarget(Path.GetFullPath(args[i + 1]).TrimEnd('\\'));
            Analyze_Click(this, new RoutedEventArgs());
        };
    }

    /// <summary>True while an analysis runs (the snapshot aid waits for it).</summary>
    public bool Busy => _busy;

    /// <summary>True while items are being sent to the Recycle Bin; the window waits for it before closing.</summary>
    public bool IsChanging { get; private set; }

    private bool _busy;

    /// <summary>One flag for every operation of the page: analysis, empty-folder search and the two deletes never overlap.</summary>
    private void SetBusy(bool busy, bool cancellable = false)
    {
        _busy = busy;
        AnalyzeButton.IsEnabled = FindEmptyButton.IsEnabled = TargetCombo.IsEnabled = AddFolderButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy && cancellable;
        ReportButton.IsEnabled = !busy && _root != null;
        DeleteEmptyButton.IsEnabled = !busy && _empty.Any(x => x.Selected);
        UpdateButtons();
    }

    private Window Owner => Window.GetWindow(this)!;

    private void LoadDrives()
    {
        var drives = DiskScanner.Drives();
        DrivesList.ItemsSource = drives.Select(d => new DriveCard(d)).ToList();
        var selected = (TargetCombo.SelectedItem as Target)?.Path;
        var folders = TargetCombo.Items.OfType<Target>().Where(t => t.Path.Length > 2).ToList();
        TargetCombo.Items.Clear();
        foreach (var d in drives)
            TargetCombo.Items.Add(new Target(d.Root, $"{d.Root} {d.Label}  ·  {Loc.F("DaFree", Loc.FormatSize(d.Free))}"));
        foreach (var f in folders) TargetCombo.Items.Add(f);
        TargetCombo.SelectedItem = TargetCombo.Items.OfType<Target>().FirstOrDefault(t => t.Path == selected) ?? TargetCombo.Items.OfType<Target>().FirstOrDefault();
    }

    private void AddTarget(string path)
    {
        var t = TargetCombo.Items.OfType<Target>().FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? new Target(path, path);
        if (!TargetCombo.Items.Contains(t)) TargetCombo.Items.Add(t);
        TargetCombo.SelectedItem = t;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog(Owner) == true) AddTarget(dlg.FolderName.TrimEnd('\\'));
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || TargetCombo.SelectedItem is not Target t) return;
        _cts = new CancellationTokenSource();
        SetBusy(true, cancellable: true);
        var ct = _cts.Token;
        StatusText.Text = Loc.T("DaAnalyzing");
        var sw = Stopwatch.StartNew();
        try
        {
            var root = await Task.Run(() => DiskScanner.Scan(t.Path,
                n => Dispatcher.BeginInvoke(() => { if (Busy) StatusText.Text = Loc.F("DaAnalyzingCount", Loc.N0(n)); }), ct), ct);
            _root = root;
            BuildTree();
            StatusText.Text = Loc.F("DaDone", Loc.N0(root.FileCount), Loc.N0(root.FolderCount), Loc.FormatSize(root.Size),
                sw.Elapsed.TotalSeconds.ToString("0.0", Loc.Instance.NumberCulture));
        }
        catch (OperationCanceledException) { StatusText.Text = Loc.T("ScanCancelled"); }
        catch (Exception ex) { StatusText.Text = ""; UiErrors.Show(Owner, ex, "disk analysis"); }
        finally { SetBusy(false); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------------------------------------------------------------- tree

    private static readonly object Placeholder = new();

    private TreeViewItem Node(DiskFolder f)
    {
        var item = new TreeViewItem { Header = NodeHeader(f), Tag = f, ToolTip = f.FullPath };
        if (f.Folders.Count > 0) item.Items.Add(Placeholder);
        item.Expanded += (_, _) =>
        {
            if (item.Items.Count == 1 && item.Items[0] == Placeholder)
            {
                item.Items.Clear();
                foreach (var c in f.Folders.OrderByDescending(c => c.Size)) item.Items.Add(Node(c));
            }
        };
        return item;
    }

    /// <summary>Name (left-to-right, as paths) and size as separate elements, so right-to-left layout keeps them in order.</summary>
    private static object NodeHeader(DiskFolder f)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = f.Name, FlowDirection = FlowDirection.LeftToRight });
        var size = Loc.FormatSize(f.Size) + (f.IsRoot ? "" : "  ·  " + Pct(f.ShareOfParent)) + (f.Inaccessible ? "  ·  " + Loc.T("DaInaccessible") : "");
        panel.Children.Add(new TextBlock { Text = size, Margin = new Thickness(10, 0, 10, 0), Opacity = 0.7 });
        return panel;
    }

    private void BuildTree()
    {
        Tree.Items.Clear();
        if (_root == null) return;
        var top = Node(_root);
        Tree.Items.Add(top);
        top.IsExpanded = true;
        top.IsSelected = true;
        _current = _root;
        RenderAll();
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: DiskFolder f }) { _current = f; RenderAll(); }
    }

    private static string Pct(double share) => (share * 100).ToString("0.0", Loc.Instance.NumberCulture) + "%";

    // ---------------------------------------------------------------- views

    private void RenderAll()
    {
        if (_current == null) { ContentsGrid.ItemsSource = null; CategoryGrid.ItemsSource = null; ExtensionGrid.ItemsSource = null; TopGrid.ItemsSource = null; return; }
        var f = _current;
        ContentsGrid.ItemsSource = f.Folders.OrderByDescending(c => c.Size).Select(c => DiskRow.ForFolder(c, f))
            .Concat(f.Files.OrderByDescending(x => x.Size).Select(x => DiskRow.ForFile(x, f))).ToList();
        var cats = Enumerable.Range(0, FileCategories.Count)
            .Select(i => new CategoryRow((FileCategory)i, Loc.T("DaCat_" + (FileCategory)i), f.CategorySize[i], f.CategoryCount[i], f.Size)).ToList();
        CategoryGrid.ItemsSource = cats;
        CategoryGrid.SelectedIndex = 0;
        var top = DiskScanner.Largest(f);
        TopGrid.ItemsSource = top.Select(x => DiskRow.ForFile(x, f, location: true)).ToList();
        RenderCharts(f, cats);
        UpdateButtons();
    }

    /// <summary>
    /// The Charts tab: the selected folder by subfolder (the six largest, the files directly in it, then "Other") and by
    /// file type (each type keeps its colour whatever the folder).
    /// </summary>
    private void RenderCharts(DiskFolder f, IReadOnlyList<CategoryRow> cats)
    {
        string Pct(long v) => (f.Size > 0 ? v * 100.0 / f.Size : 0).ToString("0.0", Loc.Instance.NumberCulture) + "%";
        var folders = f.Folders.Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList();
        var parts = new List<ChartSlice>();
        foreach (var (c, i) in folders.Take(6).Select((c, i) => (c, i)))
            parts.Add(new ChartSlice(c.Name, c.Size, Loc.FormatSize(c.Size), Pct(c.Size), DonutChart.SlotBrush(i)));
        long direct = f.Files.Sum(x => x.Size);
        if (direct > 0) parts.Add(new ChartSlice(Loc.T("DaChartFiles"), direct, Loc.FormatSize(direct), Pct(direct), DonutChart.SlotBrush(6)));
        long rest = folders.Skip(6).Sum(c => c.Size);
        if (rest > 0) parts.Add(new ChartSlice(Loc.F("DaChartOther", Loc.N0(folders.Count - 6)), rest, Loc.FormatSize(rest), Pct(rest), DonutChart.OtherBrush));
        FolderChart.Slices = parts;
        FolderChart.CenterText = Loc.FormatSize(f.Size);
        FolderLegend.ItemsSource = parts;

        var types = cats.Where(c => c.Size > 0)
            .Select(c => new ChartSlice(c.Name, c.Size, Loc.FormatSize(c.Size), c.PercentText,
                c.Category == FileCategory.Others ? DonutChart.OtherBrush : DonutChart.SlotBrush((int)c.Category)))
            .OrderByDescending(s => s.Value).ToList();
        TypeChart.Slices = types;
        TypeChart.CenterText = Loc.F("DaChartFileCount", Loc.N0(f.FileCount));
        TypeLegend.ItemsSource = types;
    }

    private void Category_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_current == null || CategoryGrid.SelectedItem is not CategoryRow c) { ExtensionGrid.ItemsSource = null; return; }
        ExtensionGrid.ItemsSource = DiskScanner.Extensions(_current, c.Category)
            .Select(x => new ExtensionRow(x.Extension.Length == 0 ? Loc.T("DaNoExtension") : x.Extension, x.Count, x.Size, c.Size)).ToList();
    }

    private DataGrid? ActiveGrid => Views.SelectedItem == ContentsGrid.Parent ? ContentsGrid : Views.SelectedItem == TopGrid.Parent ? TopGrid : null;

    private List<DiskRow> SelectedRows() => ActiveGrid?.SelectedItems.OfType<DiskRow>().ToList() ?? new List<DiskRow>();

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var rows = SelectedRows();
        OpenButton.IsEnabled = rows.Count == 1 || (rows.Count == 0 && _current != null);
        DeleteButton.IsEnabled = rows.Count > 0 && !_busy;
    }

    private void Contents_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ContentsGrid.SelectedItem is DiskRow { Folder: { } f }) SelectInTree(f);
    }

    private void Top_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Open_Click(sender, e);

    private void SelectInTree(DiskFolder target)
    {
        var chain = new Stack<DiskFolder>();
        for (var x = target; x != null; x = x.Parent) chain.Push(x);
        ItemsControl parent = Tree;
        TreeViewItem? node = null;
        foreach (var f in chain)
        {
            node = parent.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag == f);
            if (node == null) return;
            node.IsExpanded = true;
            parent = node;
        }
        if (node != null) { node.IsSelected = true; node.BringIntoView(); }
    }

    // ---------------------------------------------------------------- actions

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        string? path = rows.Count == 1 ? rows[0].FullPath : _current?.FullPath;
        if (path == null) return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : Directory.Exists(path) ? $"\"{path}\"" : null;
        if (args != null) Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _root == null) return;
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        var blocked = rows.Select(r => (r, Why: DiskScanner.DeleteBlocked(r.FullPath, r.Attributes, r.Folder?.IsRoot == true))).Where(x => x.Why != null).ToList();
        if (blocked.Count == rows.Count)
        {
            Loc.Show(Owner, Loc.F("DaDeleteBlocked", Loc.Ltr(blocked[0].r.FullPath)), Loc.T("TabDisk"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var size = rows.Sum(r => r.Size);
        if (Loc.Show(Owner, Loc.F("DaDeleteConfirm", Loc.N0(rows.Count), Loc.FormatSize(size)), Loc.T("DaDeleteTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var items = rows.Select(r => (r.FullPath, r.Attributes, r.Folder?.IsRoot == true)).ToList();
        IReadOnlyList<string> removed;
        IReadOnlyList<(string Path, string Reason)> failed;
        SetBusy(true);
        IsChanging = true;
        StatusText.Text = Loc.T("DaDeleting");
        try { (removed, failed) = await Task.Run(() => DiskScanner.Recycle(items)); }
        catch (Exception ex) { UiErrors.Show(Owner, ex, "disk analysis delete"); return; }
        finally { IsChanging = false; SetBusy(false); StatusText.Text = ""; }
        DiskScanner.Forget(_root, removed);
        var keep = _current;
        BuildTree();
        if (keep != null && !removed.Contains(keep.FullPath, StringComparer.OrdinalIgnoreCase)) SelectInTree(keep);
        var msg = Loc.F("DaDeleted", Loc.N0(removed.Count));
        if (failed.Count > 0) msg += "\n" + Loc.F("DaNotDeleted", Loc.N0(failed.Count), Loc.Reason(failed[0].Reason));
        Loc.Show(Owner, msg, Loc.T("TabDisk"), MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    // ---------------------------------------------------------------- empty folders (the original "Find Empty Folders")

    private List<EmptyRow> _empty = new();

    private async void FindEmpty_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || TargetCombo.SelectedItem is not Target t) return;
        SetBusy(true);
        StatusText.Text = Loc.T("DaFindingEmpty");
        try
        {
            var found = await Task.Run(() => EmptyFolderFinder.Find(t.Path, EmptyFolderFinder.DefaultExcludes));
            _empty = found.Select(p => new EmptyRow(p)).ToList();
            foreach (var r in _empty) r.PropertyChanged += (_, _) => DeleteEmptyButton.IsEnabled = !_busy && _empty.Any(x => x.Selected);
            EmptyGrid.ItemsSource = _empty;
            StatusText.Text = Loc.F("DaEmptyFound", Loc.N0(_empty.Count));
        }
        catch (Exception ex) { StatusText.Text = ""; UiErrors.Show(Owner, ex, "empty folders"); }
        finally { SetBusy(false); }
    }

    private async void DeleteEmpty_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _empty.Where(r => r.Selected).ToList();
        if (_busy || chosen.Count == 0) return;
        if (Loc.Show(Owner, Loc.F("DaDeleteEmptyConfirm", Loc.N0(chosen.Count)), Loc.T("DaDeleteTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        // Re-checked right before removal: a folder that received files since the search is left alone.
        List<EmptyRow> still;
        IReadOnlyList<string> removed;
        IReadOnlyList<(string Path, string Reason)> failed;
        SetBusy(true);
        IsChanging = true;
        try
        {
            (still, removed, failed) = await Task.Run(() =>
            {
                var s = chosen.Where(r => EmptyFolderFinder.IsEmptyTree(r.Path)).ToList();
                var (rm, fl) = DiskScanner.Recycle(s.Select(r => (r.Path, FileAttributes.Directory, false)).ToList());
                return (s, rm, fl);
            });
        }
        catch (Exception ex) { UiErrors.Show(Owner, ex, "empty folders delete"); return; }
        finally { IsChanging = false; SetBusy(false); }
        var gone = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
        _empty = _empty.Where(r => !gone.Contains(r.Path)).ToList();
        EmptyGrid.ItemsSource = _empty;
        int notEmpty = chosen.Count - still.Count;
        var msg = Loc.F("DaDeleted", Loc.N0(removed.Count));
        if (notEmpty > 0) msg += Environment.NewLine + Loc.F("DaNoLongerEmpty", Loc.N0(notEmpty));
        if (failed.Count > 0) msg += Environment.NewLine + Loc.F("DaNotDeleted", Loc.N0(failed.Count), Loc.Reason(failed[0].Reason));
        Loc.Show(Owner, msg, Loc.T("TabDisk"), MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        DeleteEmptyButton.IsEnabled = _empty.Any(x => x.Selected);
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_root == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "report.txt", Filter = Loc.T("DaReportFilter") + " (*.txt)|*.txt" };
        if (dlg.ShowDialog(Owner) != true) return;
        var drives = DiskScanner.Drives().Where(d => new DriveInfo(d.Root).DriveType == DriveType.Fixed);
        var text = DiskScanner.Report(drives, [_current ?? _root], Loc.FormatSize);
        try { File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Loc.Show(Owner, Loc.F("SaveFailed", ex.Message), Loc.T("TabDisk"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Loc.Show(Owner, Loc.F("DaReportSaved", Loc.Ltr(dlg.FileName)), Loc.T("TabDisk"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

public sealed class DriveCard(DriveSpace d)
{
    public string Title => $"{d.Root}  {d.Label}";
    public int UsedPercent => d.UsedPercent;
    public string UsedText => Loc.F("DaUsedOf", Loc.FormatSize(d.Used), d.UsedPercent);
    public string FreeText => Loc.F("DaFreeOf", Loc.FormatSize(d.Free), Loc.FormatSize(d.Total));
}

/// <summary>A folder or file row (Folder contents / Top 100).</summary>
public sealed class DiskRow
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required long Size { get; init; }
    public required double Percent { get; init; }
    public required FileAttributes Attributes { get; init; }
    public DiskFolder? Folder { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public string Items { get; init; } = "";
    public string Files { get; init; } = "";
    public string Folders { get; init; } = "";
    public string Location { get; init; } = "";

    public string SizeText => Loc.FormatSize(Size);
    public string PercentText => Percent.ToString("0.0", Loc.Instance.NumberCulture) + "%";
    public string ModifiedText => LastWriteUtc == default ? "" : Loc.FormatDate(LastWriteUtc.ToLocalTime());
    /// <summary>H, S, R as in the original (read when shown).</summary>
    public string Attrib
    {
        get
        {
            var a = Attributes;
            try { a = File.GetAttributes(FullPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return ((a & FileAttributes.Hidden) != 0 ? "H" : "") + ((a & FileAttributes.System) != 0 ? "S" : "") + ((a & FileAttributes.ReadOnly) != 0 ? "R" : "");
        }
    }

    public static DiskRow ForFolder(DiskFolder c, DiskFolder parent) => new()
    {
        Name = c.Name, FullPath = c.FullPath, Size = c.Size, Percent = DiskFolder.Share(c.Size, parent) * 100, Attributes = c.Attributes,
        Folder = c, LastWriteUtc = c.LastWriteUtc, Items = Loc.N0(c.ItemCount), Files = Loc.N0(c.FileCount), Folders = Loc.N0(c.FolderCount),
    };

    public static DiskRow ForFile(DiskFile f, DiskFolder scope, bool location = false) => new()
    {
        Name = f.Name, FullPath = f.FullPath, Size = f.Size, Percent = DiskFolder.Share(f.Size, scope) * 100, Attributes = f.Attributes,
        LastWriteUtc = f.LastWriteUtc, Location = location ? f.Parent.FullPath : "",
    };
}

public sealed record CategoryRow(FileCategory Category, string Name, long Size, long Count, long Total)
{
    public string SizeText => Loc.FormatSize(Size);
    public double Percent => Total > 0 ? Size * 100.0 / Total : 0;
    public string PercentText => Percent.ToString("0.0", Loc.Instance.NumberCulture) + "%";
}

public sealed record ExtensionRow(string Name, long CountValue, long Size, long Total)
{
    public string Count => Loc.N0(CountValue);
    public string SizeText => Loc.FormatSize(Size);
    public double Percent => Total > 0 ? Size * 100.0 / Total : 0;
    public string PercentText => Percent.ToString("0.0", Loc.Instance.NumberCulture) + "%";
}

/// <summary>An empty folder found by the search; checked by default (the original's default).</summary>
public sealed class EmptyRow(string path) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _selected = true;
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    }
}
