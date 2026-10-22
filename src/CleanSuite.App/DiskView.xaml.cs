using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;
using CleanSuite.DiskUsage;

namespace CleanSuite.App;

/// <summary>
/// Disk Analysis, laid out like the original: a folder tree that starts at "This PC" with every drive (and folders the user
/// adds), and the views of the selected item. Selecting This PC shows every drive's capacity; selecting a drive or folder
/// analyses it at once (read-only, in the background, while the rest of the tree stays usable) and then shows its Overview
/// (largest folders and files, size by file type, both as pie charts), Folder contents, File types and Top 100 files. An
/// analysed drive is kept, so returning to it is instant; Analyze runs it again. Back, Forward and Up move through the tree.
/// The views of a folder are computed on a background thread and kept per folder, so selecting a large drive never blocks
/// the window.
/// </summary>
public partial class DiskView : UserControl, IToolPage
{
    /// <summary>The "This PC" node.</summary>
    private sealed class ComputerNode;

    /// <summary>A drive or a folder the user added: what can be analysed, and its result once it is.</summary>
    private sealed class TargetNode(string path)
    {
        public string Path { get; } = path;
        public DriveSpace? Space { get; set; }
        public DiskFolder? Root { get; set; }
        public TreeViewItem Item { get; set; } = null!;
        public bool Analyzing { get; set; }
        public bool Stopped { get; set; }
        public long Progress { get; set; }
        public bool IsDrive => Path.Length == 2 && Path[1] == ':';
    }

    private readonly ComputerNode _computer = new();
    private readonly TreeViewItem _computerItem = new();
    private readonly List<TargetNode> _targets = new();
    /// <summary>Views of a folder, computed once (off the UI thread) and kept until its drive is analysed again or items are removed.</summary>
    private readonly Dictionary<DiskFolder, FolderSnapshot> _snapshots = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _history = new();
    private int _historyIndex = -1;
    private bool _navigating;
    private object? _selected;
    private DiskFolder? _current;
    private FolderSnapshot? _shown;
    private TargetNode? _analyzing;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _emptyCts;
    private int _renders;
    private int _renderVersion;
    private bool _operation;
    private bool _started;

    private sealed record FolderSnapshot(IReadOnlyList<DiskRow> Contents, IReadOnlyList<DiskRow> Top, IReadOnlyList<DiskScanner.ExtensionStat> Extensions);

    public DiskView()
    {
        InitializeComponent();
        _computerItem.Tag = _computer;
        Tree.Items.Add(_computerItem);
        LoadDrives();
        Loc.Instance.LanguageChanged += (_, _) => { RefreshHeaders(); if (_selected != null) Show(_selected, record: false); };
        ThemeManager.ThemeChanged += (_, _) => { if (_selected != null) Show(_selected, record: false); };    // chart colours are stepped per theme
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            if (_started) return;
            _started = true;
            _computerItem.IsExpanded = true;
            _computerItem.IsSelected = true;
            // Developer aids: "--auto-scan" with "--scan <folder>" adds and analyses that folder when the tab is shown (for
            // --snapshot checks); "--disk-view <n>" opens that view (0 overview, 1 contents, 2 file types, 3 largest files, 4 empty folders).
            var args = Environment.GetCommandLineArgs();
            if (args.Contains("--auto-scan"))
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "--scan" && Directory.Exists(args[i + 1])) { AddTarget(Path.GetFullPath(args[i + 1]).TrimEnd('\\')); break; }
            int view = Array.IndexOf(args, "--disk-view");
            if (view >= 0 && view + 1 < args.Length && int.TryParse(args[view + 1], out var n) && n >= 0 && n < Views.Items.Count)
                Dispatcher.BeginInvoke(() => SelectView(n), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }

    // ---------------------------------------------------------------- IToolPage

    /// <summary>True while an analysis, a view computation or a delete runs (the snapshot aid waits for it).</summary>
    public bool IsBusy => _analyzing != null || _renders > 0 || _operation;

    /// <summary>True while items are being sent to the Recycle Bin; the window waits for it before closing.</summary>
    public bool IsChanging { get; private set; }

    public Button? ScanButton => AnalyzeButton;
    Button? IToolPage.CancelButton => CancelButton;
    public void CancelWork() { _analysisCts?.Cancel(); _emptyCts?.Cancel(); }

    private Window Owner => Window.GetWindow(this)!;

    private void SelectView(int index)
    {
        if (Views.Items[index] is TabItem { Visibility: Visibility.Visible } tab) tab.IsSelected = true;
    }

    // ---------------------------------------------------------------- tree

    /// <summary>(Re)reads the drives under This PC; drives already analysed keep their result.</summary>
    private void LoadDrives()
    {
        var drives = DiskScanner.Drives();
        var folders = _targets.Where(t => !t.IsDrive).ToList();
        var old = _targets.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        _targets.Clear();
        foreach (var d in drives)
        {
            var t = old.TryGetValue(d.Root, out var o) ? o : new TargetNode(d.Root);
            t.Space = d;
            _targets.Add(t);
        }
        _targets.AddRange(folders);
        var selected = _selected;
        _computerItem.Items.Clear();
        foreach (var t in _targets)
        {
            t.Item = TargetItem(t);
            _computerItem.Items.Add(t.Item);
        }
        RefreshHeaders();
        if (selected is TargetNode st && !_targets.Contains(st)) _computerItem.IsSelected = true;
        else if (selected is TargetNode keep) keep.Item.IsSelected = true;
    }

    private TreeViewItem TargetItem(TargetNode t)
    {
        var item = new TreeViewItem { Tag = t, ToolTip = t.Path };
        if (t.Root != null) FillChildren(item, t.Root);
        return item;
    }

    private void AddTarget(string path)
    {
        var t = _targets.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (t == null)
        {
            t = new TargetNode(path);
            _targets.Add(t);
            t.Item = TargetItem(t);
            _computerItem.Items.Add(t.Item);
            t.Item.Header = TargetHeader(t);
        }
        _computerItem.IsExpanded = true;
        t.Item.IsSelected = true;
        t.Item.BringIntoView();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog(Owner) == true) AddTarget(dlg.FolderName.TrimEnd('\\'));
    }

    private static readonly object Placeholder = new();

    private static void FillChildren(TreeViewItem item, DiskFolder f)
    {
        item.Items.Clear();
        foreach (var c in f.Folders.OrderByDescending(c => c.Size)) item.Items.Add(FolderItem(c));
    }

    private static TreeViewItem FolderItem(DiskFolder f)
    {
        var item = new TreeViewItem { Header = FolderHeader(f), Tag = f, ToolTip = f.FullPath };
        if (f.Folders.Count > 0) item.Items.Add(Placeholder);
        item.Expanded += (_, _) =>
        {
            if (item.Items.Count == 1 && item.Items[0] == Placeholder) FillChildren(item, f);
        };
        return item;
    }

    /// <summary>Icon, name and, once known, size, each its own element so right-to-left layout keeps them in order.</summary>
    private static StackPanel Header(string iconKey, string name, string? detail, string? suffix = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new ContentControl { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) };
        icon.SetResourceReference(StyleProperty, "Icon");
        icon.SetResourceReference(TagProperty, iconKey);
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = name, FlowDirection = DirectionOf(name), VerticalAlignment = VerticalAlignment.Center });
        if (suffix != null) panel.Children.Add(new TextBlock { Text = suffix, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        if (detail != null)
        {
            var d = new TextBlock { Text = detail, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            d.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedFg");
            panel.Children.Add(d);
        }
        return panel;
    }

    /// <summary>A name in Arabic script reads right-to-left, any other name left-to-right (folder and file names keep their order).</summary>
    internal static FlowDirection DirectionOf(string s) =>
        s.Any(c => c is >= (char)0x0590 and <= (char)0x08FF or >= (char)0xFB1D and <= (char)0xFEFC) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    private static object FolderHeader(DiskFolder f) =>
        Header("Icon.Folder", f.Name, Loc.FormatSize(f.Size) + "  ·  " + Pct(f.ShareOfParent) + (f.Inaccessible ? "  ·  " + Loc.T("DaInaccessible") : ""));

    private static object TargetHeader(TargetNode t)
    {
        var detail = t.Root != null ? Loc.FormatSize(t.Root.Size) : t.Analyzing ? Loc.T("DaAnalyzingShort") : null;
        // An added folder shows its name (the full path is the tool tip and the caption), so the tree stays narrow.
        return t.IsDrive ? Header("Icon.Drive", DriveLabel(t), detail, "(" + t.Path + ")") : Header("Icon.Folder", Path.GetFileName(t.Path), detail);
    }

    /// <summary>The drive's label, or what Explorer calls an unlabelled drive of its type.</summary>
    private static string DriveLabel(TargetNode t) => t.Space is { Label.Length: > 0 } s ? s.Label : Loc.T(t.Space?.Type switch
    {
        DriveType.Removable => "DaRemovable",
        DriveType.Network => "DaNetworkDrive",
        DriveType.CDRom => "DaCdDrive",
        _ => "DaLocalDisk",
    });

    private static string Title(TargetNode t) => t.IsDrive ? $"{DriveLabel(t)} ({t.Path})" : t.Path;

    private void RefreshHeaders()
    {
        _computerItem.Header = Header("Icon.Computer", Loc.T("DaComputer"), null);
        foreach (var t in _targets)
        {
            t.Item.Header = TargetHeader(t);
            RefreshFolderHeaders(t.Item);
        }
    }

    private static void RefreshFolderHeaders(TreeViewItem parent)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is DiskFolder f) item.Header = FolderHeader(f);
            RefreshFolderHeaders(item);
        }
    }

    private TargetNode? TargetOf(DiskFolder f)
    {
        var root = f;
        while (root.Parent != null) root = root.Parent;
        return _targets.FirstOrDefault(t => ReferenceEquals(t.Root, root));
    }

    /// <summary>False for a folder of an analysis that was replaced (Back/Forward skip it).</summary>
    private bool IsAlive(object node) => node switch
    {
        ComputerNode => true,
        TargetNode t => _targets.Contains(t),
        DiskFolder f => TargetOf(f) != null,
        _ => false,
    };

    private void Select(object node)
    {
        switch (node)
        {
            case ComputerNode: _computerItem.IsSelected = true; _computerItem.BringIntoView(); break;
            case TargetNode t: _computerItem.IsExpanded = true; t.Item.IsSelected = true; t.Item.BringIntoView(); break;
            case DiskFolder f: SelectInTree(f); break;
        }
    }

    private void SelectInTree(DiskFolder target)
    {
        var t = TargetOf(target);
        if (t == null) return;
        if (ReferenceEquals(target, t.Root)) { Select(t); return; }
        var chain = new Stack<DiskFolder>();
        for (var x = target; x.Parent != null; x = x.Parent) chain.Push(x);
        _computerItem.IsExpanded = true;
        ItemsControl parent = t.Item;
        TreeViewItem? node = null;
        foreach (var f in chain)
        {
            ((TreeViewItem)parent).IsExpanded = true;
            node = parent.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag == f);
            if (node == null) return;
            parent = node;
        }
        if (node != null) { node.IsSelected = true; node.BringIntoView(); }
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: { } tag }) Show(tag, record: !_navigating);
    }

    // ---------------------------------------------------------------- navigation (Back, Forward, Up)

    private void Record(object node)
    {
        if (_historyIndex >= 0 && ReferenceEquals(_history[_historyIndex], node)) return;
        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(node);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    private void Go(int step)
    {
        int i = _historyIndex + step;
        while (i >= 0 && i < _history.Count && !IsAlive(_history[i])) i += step;
        if (i < 0 || i >= _history.Count) return;
        _historyIndex = i;
        _navigating = true;
        try { Select(_history[i]); }
        finally { _navigating = false; }
        UpdateNavButtons();
    }

    private object? ParentOf(object? node) => node switch
    {
        DiskFolder { Parent: { } p } f => p.Parent == null ? TargetOf(f) : p,
        DiskFolder f => (object?)TargetOf(f) is { } t ? _computer : null,
        TargetNode => _computer,
        _ => null,
    };

    private void Back_Click(object sender, RoutedEventArgs e) => Go(-1);
    private void Forward_Click(object sender, RoutedEventArgs e) => Go(+1);
    private void Up_Click(object sender, RoutedEventArgs e) { if (ParentOf(_selected) is { } p) Select(p); }

    private void UpdateNavButtons()
    {
        BackButton.IsEnabled = Enumerable.Range(0, Math.Max(0, _historyIndex)).Any(i => IsAlive(_history[i]));
        ForwardButton.IsEnabled = Enumerable.Range(_historyIndex + 1, Math.Max(0, _history.Count - _historyIndex - 1)).Any(i => IsAlive(_history[i]));
        UpButton.IsEnabled = ParentOf(_selected) != null;
    }

    /// <summary>Alt+← / Alt+→ / Alt+↑ move through the tree as in Explorer (the arrows follow the reading direction); Delete removes the selected rows.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Left or Key.Right or Key.Up)
        {
            bool rtl = Loc.Instance.IsRtl;
            if (key == Key.Up) Up_Click(this, e);
            else Go((key == Key.Left) != rtl ? -1 : +1);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Delete && e.OriginalSource is DependencyObject d
                 && (IsInside(d, ContentsGrid) || IsInside(d, TopGrid)) && DeleteButton.IsEnabled)
        {
            Delete_Click(this, e);
            e.Handled = true;
        }
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == ancestor) return true;
        return false;
    }

    // ---------------------------------------------------------------- showing the selected item

    private void Show(object node, bool record)
    {
        _selected = node;
        if (record) Record(node);
        _current = null;
        _shown = null;
        switch (node)
        {
            case ComputerNode: ShowComputer(); break;
            case TargetNode t when t.Root != null: ShowFolder(t.Root, t); break;
            case TargetNode t:
                ShowPending(t);
                if (!t.Analyzing && !t.Stopped) StartAnalysis(t);
                break;
            case DiskFolder f: ShowFolder(f, TargetOf(f)); break;
        }
        UpdateNavButtons();
        UpdateButtons();
    }

    /// <summary>Which groups and tabs the selected kind of item has: This PC and a pending drive only show the Overview.</summary>
    private void Layout(bool computer, bool pending, bool folder, bool hasPath)
    {
        DrivesGroup.Visibility = computer ? Visibility.Visible : Visibility.Collapsed;
        PendingGroup.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        LargestGroup.Visibility = TypesGroup.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tab in new[] { ContentsTab, TypesTab, TopTab }) tab.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        EmptyTab.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        if (Views.SelectedItem is TabItem { Visibility: not Visibility.Visible } || Views.SelectedItem == null) OverviewTab.IsSelected = true;
    }

    private void SetFacts(params (string Label, string Value)[] facts)
    {
        var labels = new[] { Fact1Label, Fact2Label, Fact3Label };
        var values = new[] { Fact1Value, Fact2Value, Fact3Value };
        for (int i = 0; i < 3; i++)
        {
            bool on = i < facts.Length;
            labels[i].Visibility = values[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) continue;
            labels[i].Text = facts[i].Label;
            values[i].Text = facts[i].Value;
        }
    }

    private void ShowComputer()
    {
        Layout(computer: true, pending: false, folder: false, hasPath: false);
        CaptionText.Text = Loc.T("DaComputer");
        var drives = _targets.Where(t => t.Space != null).Select(t => t.Space!).ToList();
        long total = drives.Sum(d => d.Total), used = drives.Sum(d => d.Used), free = drives.Sum(d => d.Free);
        SetFacts((Loc.T("DaTotalSize"), Loc.FormatSize(total)),
                 (Loc.T("DaOccupied"), Loc.F("DaUsedValue", Loc.FormatSize(used), Percent(used, total))),
                 (Loc.T("DaDrivesLabel"), Loc.N0(drives.Count)));
        DrivesGrid.ItemsSource = _targets.Where(t => t.Space != null).Select(t => new DiskDriveRow(t.Path, DriveLabel(t), t.Space!)).ToList();
        SpaceChart.Slices = SpaceSlices(used, free);
        SpaceText.Text = Loc.F("DaFreeTotal", Loc.FormatSize(free), Loc.FormatSize(total));
        SetStatus(Loc.T("DaComputerStatus"));
    }

    private static IReadOnlyList<ChartSlice> SpaceSlices(long used, long free)
    {
        long total = used + free;
        return
        [
            new ChartSlice(Loc.T("DaUsedSlice"), used, Loc.FormatSize(used), Percent(used, total) + "%", DonutChart.SlotBrush(0)),
            new ChartSlice(Loc.T("DaFreeSlice"), free, Loc.FormatSize(free), Percent(free, total) + "%", DonutChart.SlotBrush(3)),
        ];
    }

    private static string Percent(long part, long whole) =>
        (whole > 0 ? part * 100.0 / whole : 0).ToString("0.0", Loc.Instance.NumberCulture);

    private void ShowPending(TargetNode t)
    {
        Layout(computer: false, pending: true, folder: false, hasPath: true);
        CaptionText.Text = Title(t);
        if (t.Space is { } s)
        {
            SetFacts((Loc.T("DaPath"), t.Path + "\\"), (Loc.T("DaColCapacity"), Loc.FormatSize(s.Total)),
                     (Loc.T("DaOccupied"), Loc.F("DaUsedValue", Loc.FormatSize(s.Used), Percent(s.Used, s.Total))));
            PendingChart.Slices = SpaceSlices(s.Used, s.Free);
            PendingChart.Visibility = Visibility.Visible;
            SpaceText.Text = Loc.F("DaFreeTotal", Loc.FormatSize(s.Free), Loc.FormatSize(s.Total));
        }
        else
        {
            SetFacts((Loc.T("DaPath"), t.Path));
            PendingChart.Visibility = Visibility.Collapsed;
            SpaceText.Text = SpaceOf(t.Path);
        }
        UpdatePending(t);
    }

    private void UpdatePending(TargetNode t)
    {
        PendingProgress.Visibility = t.Analyzing ? Visibility.Visible : Visibility.Collapsed;
        PendingText.Text = t.Analyzing ? (t.Progress > 0 ? Loc.F("DaPendingCount", Loc.N0(t.Progress)) : Loc.T("DaPendingStart"))
                         : t.Stopped ? Loc.T("DaPendingStopped") : Loc.T("DaPendingStart");
        SetStatus(t.Analyzing ? Loc.F("DaAnalyzingCount", Loc.N0(t.Progress)) : t.Stopped ? Loc.T("ScanCancelled") : "");
    }

    /// <summary>Free and total space of the drive a path is on, for the status bar.</summary>
    private string SpaceOf(string path)
    {
        var root = Path.GetPathRoot(path)?.TrimEnd('\\');
        var s = _targets.FirstOrDefault(t => t.IsDrive && t.Path.Equals(root, StringComparison.OrdinalIgnoreCase))?.Space;
        return s == null ? "" : Loc.F("DaFreeTotal", Loc.FormatSize(s.Free), Loc.FormatSize(s.Total));
    }

    private void ShowFolder(DiskFolder f, TargetNode? t)
    {
        Layout(computer: false, pending: false, folder: true, hasPath: true);
        _current = f;
        bool top = t != null && ReferenceEquals(f, t.Root);
        CaptionText.Text = top ? Title(t!) : f.Name;
        // As the original: a drive's occupied space is its used share of the capacity; a folder's is its share of the parent.
        string pct = top && t!.Space is { } s ? Percent(s.Used, s.Total) : (f.ShareOfParent * 100).ToString("0.0", Loc.Instance.NumberCulture);
        SetFacts((Loc.T("DaPath"), f.FullPath),
                 (Loc.T("DaSizeLabel"), Loc.FormatSize(f.Size)),
                 (Loc.T("DaOccupied"), Loc.F("DaOccupiedValue", pct, Loc.N0(f.FileCount), Loc.N0(f.FolderCount))));
        SpaceText.Text = SpaceOf(f.FullPath);
        SetStatus(f.Inaccessible ? Loc.T("DaInaccessibleFolder") : Loc.F("DaDone", Loc.N0(f.FileCount), Loc.N0(f.FolderCount), Loc.FormatSize(f.Size)));
        RenderFolder(f);
    }

    /// <summary>Computes the folder's views on a background thread (the whole subtree is walked for Top 100 and the extensions) and shows them.</summary>
    private async void RenderFolder(DiskFolder f)
    {
        int version = ++_renderVersion;
        if (!_snapshots.TryGetValue(f, out var snap))
        {
            ClearViews();
            _renders++;
            UpdateBusy();
            try { snap = await Task.Run(() => BuildSnapshot(f)); }
            catch (Exception ex) { UiErrors.Show(Owner, ex, "disk analysis view"); return; }
            finally { _renders--; UpdateBusy(); }
            if (TargetOf(f) != null) _snapshots[f] = snap;
        }
        if (version != _renderVersion || !ReferenceEquals(_current, f)) return;
        _shown = snap;
        ContentsGrid.ItemsSource = snap.Contents;
        TopGrid.ItemsSource = snap.Top;
        var cats = Enumerable.Range(0, FileCategories.Count)
            .Select(i => new CategoryRow((FileCategory)i, Loc.T("DaCat_" + (FileCategory)i), f.CategorySize[i], f.CategoryCount[i], f.Size)).ToList();
        CategoryGrid.ItemsSource = cats;
        CategoryGrid.SelectedItem = cats.OrderByDescending(c => c.Size).First();
        RenderOverview(f, snap, cats);
        UpdateButtons();
    }

    private static FolderSnapshot BuildSnapshot(DiskFolder f) => new(
        f.Folders.OrderByDescending(c => c.Size).Select(c => DiskRow.ForFolder(c, f))
            .Concat(f.Files.OrderByDescending(x => x.Size).Select(x => DiskRow.ForFile(x, f))).ToList(),
        DiskScanner.Largest(f).Select(x => DiskRow.ForFile(x, f, location: true)).ToList(),
        DiskScanner.Extensions(f));

    private void ClearViews()
    {
        ContentsGrid.ItemsSource = TopGrid.ItemsSource = CategoryGrid.ItemsSource = ExtensionGrid.ItemsSource = null;
        FolderLegend.ItemsSource = TypeLegend.ItemsSource = null;
        FolderChart.Slices = TypeChart.Slices = null;
    }

    /// <summary>
    /// Overview: the largest folders and files (the seven largest items with their colours, the rest as "Other") and the size
    /// by file type, each a list next to a pie chart; every type keeps its colour whatever the folder.
    /// </summary>
    private void RenderOverview(DiskFolder f, FolderSnapshot snap, IReadOnlyList<CategoryRow> cats)
    {
        string Share(long v) => Percent(v, f.Size) + "%";
        var items = snap.Contents.Where(r => r.Size > 0).ToList();
        var parts = new List<ChartSlice>();
        int shown = Math.Min(items.Count, items.Count > DonutChart.SlotCount ? DonutChart.SlotCount - 1 : DonutChart.SlotCount);
        for (int i = 0; i < shown; i++)
        {
            var r = items[i];
            parts.Add(new ChartSlice(r.Name, r.Size, Loc.FormatSize(r.Size), Share(r.Size), DonutChart.SlotBrush(i))
            { Icon = r.Icon, Target = r.Folder, CountText = r.Folder != null ? Loc.F("DaFilesShort", Loc.N0(r.Folder.FileCount)) : "" });
        }
        var rest = items.Skip(shown).ToList();
        if (rest.Count > 0)
        {
            long size = rest.Sum(r => r.Size);
            parts.Add(new ChartSlice(Loc.F("DaOtherItems", Loc.N0(rest.Count)), size, Loc.FormatSize(size), Share(size), DonutChart.OtherBrush));
        }
        FolderChart.Slices = parts;
        FolderLegend.ItemsSource = parts;

        var types = cats.Where(c => c.Size > 0).OrderByDescending(c => c.Size)
            .Select(c => new ChartSlice(c.Name, c.Size, Loc.FormatSize(c.Size), c.PercentText,
                c.Category == FileCategory.Others ? DonutChart.OtherBrush : DonutChart.SlotBrush((int)c.Category))
            { CountText = Loc.F("DaFilesShort", Loc.N0(c.Count)), Target = c.Category })
            .ToList();
        TypeChart.Slices = types;
        TypeLegend.ItemsSource = types;
    }

    private void Legend_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderLegend.SelectedItem is ChartSlice { Target: DiskFolder f }) SelectInTree(f);
    }

    private void Legend_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FolderLegend.SelectedItem is ChartSlice { Target: DiskFolder f }) { SelectInTree(f); e.Handled = true; }
    }

    private void TypeLegend_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TypeLegend.SelectedItem is not ChartSlice { Target: FileCategory c }) return;
        TypesTab.IsSelected = true;
        CategoryGrid.SelectedItem = CategoryGrid.Items.OfType<CategoryRow>().FirstOrDefault(r => r.Category == c);
    }

    private void Category_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_shown == null || CategoryGrid.SelectedItem is not CategoryRow c) { ExtensionGrid.ItemsSource = null; return; }
        ExtensionGrid.ItemsSource = _shown.Extensions.Where(x => x.Category == c.Category)
            .Select(x => new ExtensionRow(x.Extension.Length == 0 ? Loc.T("DaNoExtension") : x.Extension, x.Count, x.Size, c.Size)).ToList();
    }

    private void Drives_DoubleClick(object sender, MouseButtonEventArgs e) => OpenDriveRow();

    private void Drives_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenDriveRow();
        e.Handled = true;
    }

    private void OpenDriveRow()
    {
        if (DrivesGrid.SelectedItem is DiskDriveRow r && _targets.FirstOrDefault(t => t.Path == r.Root) is { } t) Select(t);
    }

    private void Views_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Views)) UpdateButtons();
    }

    // ---------------------------------------------------------------- analysis

    /// <summary>
    /// Analyses a drive or folder in the background. One analysis runs at a time: choosing another drive that is not analysed
    /// yet stops the running one (it can be started again with Analyze). The rest of the page stays usable meanwhile.
    /// </summary>
    private async void StartAnalysis(TargetNode t)
    {
        if (_analyzing == t) return;
        _analysisCts?.Cancel();
        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        _analysisCts = cts;
        _analyzing = t;
        t.Analyzing = true;
        t.Stopped = false;
        t.Progress = 0;
        t.Item.Header = TargetHeader(t);
        UpdateBusy();
        if (_selected == t) UpdatePending(t);
        var sw = Stopwatch.StartNew();
        var path = t.Path;
        try
        {
            var root = await Task.Run(() => DiskScanner.Scan(path, n => Dispatcher.BeginInvoke(() =>
            {
                if (!t.Analyzing || ct.IsCancellationRequested) return;
                t.Progress = n;
                if (_selected == t) UpdatePending(t);
            }), ct), ct);
            ForgetSnapshots(t.Root);
            t.Root = root;
            t.Analyzing = false;
            FillChildren(t.Item, root);
            t.Item.Header = TargetHeader(t);
            if (_selected == t) Show(t, record: false);
            if (ReferenceEquals(_selected, t) || _selected is DiskFolder sf && TargetOf(sf) == t)
                SetStatus(Loc.F("DaDoneTime", Loc.N0(root.FileCount), Loc.N0(root.FolderCount), Loc.FormatSize(root.Size),
                    sw.Elapsed.TotalSeconds.ToString("0.0", Loc.Instance.NumberCulture)));
        }
        catch (OperationCanceledException) { t.Stopped = true; }
        catch (Exception ex) { t.Stopped = true; UiErrors.Show(Owner, ex, "disk analysis"); }
        finally
        {
            t.Analyzing = false;
            if (_analyzing == t) { _analyzing = null; _analysisCts = null; }
            cts.Dispose();
            t.Item.Header = TargetHeader(t);
            if (_selected == t && t.Root == null) UpdatePending(t);
            UpdateBusy();
        }
    }

    /// <summary>Analyze: This PC reads the drives again; a drive or folder (or a folder inside one) is analysed again.</summary>
    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        switch (_selected)
        {
            case ComputerNode or null:
                LoadDrives();
                ShowComputer();
                break;
            case TargetNode t: Reanalyze(t); break;
            case DiskFolder f when TargetOf(f) is { } t: Reanalyze(t); break;
        }
    }

    private void Reanalyze(TargetNode t)
    {
        if (t.Analyzing) return;
        ForgetSnapshots(t.Root);
        t.Root = null;
        t.Stopped = false;
        t.Item.IsSelected = true;               // a folder of the old result disappears with it
        t.Item.Items.Clear();
        if (t.IsDrive) t.Space = DiskScanner.Drives().FirstOrDefault(d => d.Root == t.Path) ?? t.Space;
        Show(t, record: false);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelWork();

    private void ForgetSnapshots(DiskFolder? root)
    {
        if (root == null) return;
        foreach (var k in _snapshots.Keys.Where(k => { var r = k; while (r.Parent != null) r = r.Parent; return ReferenceEquals(r, root); }).ToList())
            _snapshots.Remove(k);
    }

    // ---------------------------------------------------------------- busy state and buttons

    private void UpdateBusy()
    {
        Progress.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = _analyzing != null || _emptyCts != null;
        AnalyzeButton.IsEnabled = !_operation && !(_selected is TargetNode { Analyzing: true });
        AddFolderButton.IsEnabled = !_operation;
        FindEmptyButton.IsEnabled = !_operation && _emptyCts == null;
        DeleteEmptyButton.IsEnabled = !_operation && _emptyCts == null && _empty.Any(x => x.Selected);
        UpdateButtons();
    }

    private void SetStatus(string text) => Bidi.SetText(StatusText, text);

    private DataGrid? ActiveGrid => ContentsTab.IsSelected ? ContentsGrid : TopTab.IsSelected ? TopGrid : null;

    private List<DiskRow> SelectedRows() => ActiveGrid?.SelectedItems.OfType<DiskRow>().ToList() ?? new List<DiskRow>();

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var rows = SelectedRows();
        OpenButton.IsEnabled = rows.Count == 1 || (rows.Count == 0 && CurrentPath() != null);
        DeleteButton.IsEnabled = rows.Count > 0 && !_operation;
        ReportButton.IsEnabled = _current != null && !_operation;
        AnalyzeButton.IsEnabled = !_operation && !(_selected is TargetNode { Analyzing: true });
    }

    /// <summary>The folder or drive the page shows (null for This PC).</summary>
    private string? CurrentPath() => _selected switch
    {
        TargetNode t => t.IsDrive ? t.Path + "\\" : t.Path,
        DiskFolder f => f.FullPath,
        _ => null,
    };

    private void Contents_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ContentsGrid.SelectedItem is DiskRow { Folder: { } f }) SelectInTree(f);
        else if (ContentsGrid.SelectedItem is DiskRow) Open_Click(sender, e);
    }

    private void Top_DoubleClick(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

    // ---------------------------------------------------------------- actions

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        string? path = rows.Count == 1 ? rows[0].FullPath : CurrentPath();
        if (path == null) return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : Directory.Exists(path) ? $"\"{path}\"" : null;
        if (args != null) Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_operation || _current == null || TargetOf(_current) is not { Root: { } root }) return;
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
        _operation = true;
        IsChanging = true;
        UpdateBusy();
        SetStatus(Loc.T("DaDeleting"));
        try { (removed, failed) = await Task.Run(() => DiskScanner.Recycle(items)); }
        catch (Exception ex) { SetStatus(""); UiErrors.Show(Owner, ex, "disk analysis delete"); return; }
        finally { IsChanging = false; _operation = false; UpdateBusy(); }
        var keep = _current;
        DiskScanner.Forget(root, removed);
        ForgetSnapshots(root);
        var t = TargetOf(keep)!;
        FillChildren(t.Item, root);
        RefreshHeaders();
        if (ReferenceEquals(keep, root)) Show(t, record: false); else SelectInTree(keep);
        var msg = Loc.F("DaDeleted", Loc.N0(removed.Count));
        if (failed.Count > 0) msg += "\n" + Loc.F("DaNotDeleted", Loc.N0(failed.Count), Loc.Reason(failed[0].Reason));
        Loc.Show(Owner, msg, Loc.T("TabDisk"), MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    // ---------------------------------------------------------------- empty folders (the original "Find Empty Folders")

    private List<EmptyRow> _empty = new();

    private async void FindEmpty_Click(object sender, RoutedEventArgs e)
    {
        if (_operation || _emptyCts != null || CurrentPath() is not { } path) return;
        var cts = new CancellationTokenSource();
        _emptyCts = cts;
        UpdateBusy();
        SetStatus(Loc.T("DaFindingEmpty"));
        try
        {
            var found = await Task.Run(() => EmptyFolderFinder.Find(path, EmptyFolderFinder.DefaultExcludes, ct: cts.Token), cts.Token);
            _empty = found.Select(p => new EmptyRow(p)).ToList();
            foreach (var r in _empty) r.PropertyChanged += (_, _) => DeleteEmptyButton.IsEnabled = !_operation && _empty.Any(x => x.Selected);
            EmptyGrid.ItemsSource = _empty;
            SetStatus(Loc.F("DaEmptyFound", Loc.N0(_empty.Count)));
        }
        catch (OperationCanceledException) { SetStatus(Loc.T("ScanCancelled")); }
        catch (Exception ex) { SetStatus(""); UiErrors.Show(Owner, ex, "empty folders"); }
        finally { _emptyCts = null; cts.Dispose(); UpdateBusy(); }
    }

    private async void DeleteEmpty_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _empty.Where(r => r.Selected).ToList();
        if (_operation || chosen.Count == 0) return;
        if (Loc.Show(Owner, Loc.F("DaDeleteEmptyConfirm", Loc.N0(chosen.Count)), Loc.T("DaDeleteTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        // Re-checked right before removal: a folder that received files since the search is left alone.
        List<EmptyRow> still;
        IReadOnlyList<string> removed;
        IReadOnlyList<(string Path, string Reason)> failed;
        _operation = true;
        IsChanging = true;
        EmptyGrid.IsReadOnly = true;
        UpdateBusy();
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
        finally { IsChanging = false; _operation = false; EmptyGrid.IsReadOnly = false; UpdateBusy(); }
        var gone = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
        _empty = _empty.Where(r => !gone.Contains(r.Path)).ToList();
        EmptyGrid.ItemsSource = _empty;
        int notEmpty = chosen.Count - still.Count;
        var msg = Loc.F("DaDeleted", Loc.N0(removed.Count));
        if (notEmpty > 0) msg += Environment.NewLine + Loc.F("DaNoLongerEmpty", Loc.N0(notEmpty));
        if (failed.Count > 0) msg += Environment.NewLine + Loc.F("DaNotDeleted", Loc.N0(failed.Count), Loc.Reason(failed[0].Reason));
        Loc.Show(Owner, msg, Loc.T("TabDisk"), MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        UpdateBusy();
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "report.txt", Filter = Loc.T("DaReportFilter") + " (*.txt)|*.txt" };
        if (dlg.ShowDialog(Owner) != true) return;
        var drives = _targets.Where(t => t.Space is { Type: DriveType.Fixed }).Select(t => t.Space!);
        var text = DiskScanner.Report(drives, [_current], Loc.FormatSize);
        try { File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Loc.Show(Owner, Loc.F("SaveFailed", ex.Message), Loc.T("TabDisk"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Loc.Show(Owner, Loc.F("DaReportSaved", Loc.Ltr(dlg.FileName)), Loc.T("TabDisk"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Pct(double share) => (share * 100).ToString("0.0", Loc.Instance.NumberCulture) + "%";
}

/// <summary>A drive row of This PC's overview.</summary>
public sealed class DiskDriveRow(string root, string label, DriveSpace d)
{
    public string Root { get; } = root;
    public string Label { get; } = label;
    public string Letter => "(" + Root + ")";
    public string Title => Label + " " + Letter;
    public FlowDirection LabelDirection { get; } = DiskView.DirectionOf(label);
    public long Total => d.Total;
    public long Used => d.Used;
    public long Free => d.Free;
    public double Percent => d.Total > 0 ? d.Used * 100.0 / d.Total : 0;
    public string CapacityText => Loc.FormatSize(d.Total);
    public string UsedText => Loc.FormatSize(d.Used);
    public string FreeText => Loc.FormatSize(d.Free);
    public string PercentText => Percent.ToString("0.0", Loc.Instance.NumberCulture) + "%";
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

    /// <summary>Folder or file icon (looked up when shown, on the UI thread).</summary>
    public object Icon => Application.Current.FindResource(Folder != null ? "Icon.Folder" : "Icon.File");
    public string SizeText => Loc.FormatSize(Size);
    public string PercentText => Percent.ToString("0.0", Loc.Instance.NumberCulture) + "%";
    public string ModifiedText => LastWriteUtc == default ? "" : Loc.FormatDate(LastWriteUtc.ToLocalTime());

    /// <summary>Hidden, system and read-only, spelled out in the UI language (the original shows "H S R").</summary>
    public string Attrib
    {
        get
        {
            var parts = new List<string>();
            if ((Attributes & FileAttributes.Hidden) != 0) parts.Add(Loc.T("DaAttr_Hidden"));
            if ((Attributes & FileAttributes.System) != 0) parts.Add(Loc.T("DaAttr_System"));
            if ((Attributes & FileAttributes.ReadOnly) != 0) parts.Add(Loc.T("DaAttr_ReadOnly"));
            return string.Join(Loc.Instance.IsRtl ? "، " : ", ", parts);
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
public sealed class EmptyRow(string path) : ViewModels.SelectableRow(true)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
}
