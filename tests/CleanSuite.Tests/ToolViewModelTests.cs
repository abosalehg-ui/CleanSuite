using CleanSuite.App.ViewModels;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>
/// The page view models introduced with the second review: junk files, duplicates, the list pages, and the shared selection
/// count. Scanners and removers are replaced by fakes; nothing on the computer is touched.
/// </summary>
public class ToolViewModelTests
{
    private static JunkItem Junk(string name, long size) => new(@"C:\nowhere\" + name, name, @"C:\nowhere", size, JunkReason.JunkType, "Temp", 0);

    private static JunkScanResult Result(params JunkItem[] items) => new() { Items = items, FilesSearched = items.Length };

    [Fact]
    public async Task Checking_fifty_thousand_rows_is_linear_and_the_totals_follow()
    {
        var items = Enumerable.Range(0, 50_000).Select(i => Junk($"f{i}.tmp", 10)).ToArray();
        int changed = 0;
        var vm = new JunkViewModel(() => new JunkInput([@"C:\"], new JunkOptions(), RemovalMode.RecycleBin),
            scan: (_, _, _) => (Result(items), Array.Empty<string>()));
        await vm.ScanCommand.ExecuteAsync();
        Assert.Equal(50_000, vm.SelectedCount);
        Assert.Equal(500_000, vm.SelectedBytes);
        vm.Changed += () => changed++;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        vm.SelectNoneCommand.Execute(null);
        vm.SelectAllCommand.Execute(null);
        vm.InvertCommand.Execute(null);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal(0, vm.SelectedBytes);
        Assert.Equal(3, changed);                                                    // one announcement per bulk change, not per row
        vm.Rows[7].Selected = true;
        Assert.Equal(1, vm.SelectedCount);
        Assert.Equal(10, vm.SelectedBytes);
        Assert.True(vm.CanClean);
    }

    [Fact]
    public async Task Junk_clean_drops_the_removed_rows_and_asks_first()
    {
        using var sb = new Sandbox();
        var keep = sb.File("keep.tmp", 3);
        var go = sb.File("go.tmp", 5);
        JunkItem Item(string p) => new(p, Path.GetFileName(p), sb.Root, new FileInfo(p).Length, JunkReason.JunkType, "Temp", 0);
        JunkRemovalRequest? seen = null;
        var vm = new JunkViewModel(() => new JunkInput([sb.Root], new JunkOptions { CleanTempFolders = false }, RemovalMode.ZipBackup),
            scan: (_, _, _) => (Result(Item(keep), Item(go)), Array.Empty<string>()),
            clean: (req, _) =>
            {
                seen = req;
                File.Delete(go);
                return new JunkRemovalResult { Deleted = 1, BytesFreed = 5, JournalPath = "j" };
            });
        bool nothing = false;
        vm.NothingToScan += () => nothing = true;
        await vm.ScanCommand.ExecuteAsync();
        Assert.False(nothing);
        vm.Confirm = _ => false;
        await vm.CleanCommand.ExecuteAsync();
        Assert.Null(seen);                                                           // declined: nothing ran
        vm.Confirm = _ => true;
        vm.Rows.Single(r => r.Name == "keep.tmp").Selected = false;
        await vm.CleanCommand.ExecuteAsync();
        Assert.Equal(RemovalMode.ZipBackup, seen!.Mode);
        Assert.Equal([go], seen.Items.Select(i => i.FullPath));
        Assert.Equal(["keep.tmp"], vm.Rows.Select(r => r.Name));
        Assert.Equal(5, vm.LastClean!.BytesFreed);
    }

    [Fact]
    public async Task A_junk_scan_with_nothing_chosen_says_so_instead_of_scanning()
    {
        bool scanned = false, told = false;
        var vm = new JunkViewModel(() => new JunkInput([], new JunkOptions { CleanTempFolders = false }, RemovalMode.RecycleBin),
            scan: (_, _, _) => { scanned = true; return (Result(), Array.Empty<string>()); });
        vm.NothingToScan += () => told = true;
        await vm.ScanCommand.ExecuteAsync();
        Assert.True(told);
        Assert.False(scanned);
    }

    [Fact]
    public async Task Duplicates_check_one_per_group_and_a_clean_drops_groups_left_with_one_file()
    {
        using var sb = new Sandbox();
        var a1 = sb.File(@"a\1.bin", 4);
        var a2 = sb.File(@"b\1.bin", 4);
        var b1 = sb.File(@"a\2.bin", 6);
        var b2 = sb.File(@"b\2.bin", 6);
        var b3 = sb.File(@"c\2.bin", 6);
        DupFile F(string p) => new(p, new FileInfo(p).Length, File.GetLastWriteTimeUtc(p));
        var groups = new[] { new DupGroup(1, 8, [F(a1), F(a2)]), new DupGroup(2, 18, [F(b1), F(b2), F(b3)]) };
        bool? whole = null;
        var vm = new DuplicatesViewModel(() => new DupOptions { Roots = [sb.Root], Patterns = DupProfiles.AllFiles }, () => RemovalMode.RecycleBin,
            scan: (_, _, _) => new DupScanResult { Groups = groups, FilesSearched = 5 },
            clean: (g, selected, mode, allowWhole, _) =>
            {
                whole = allowWhole;
                foreach (var p in selected) File.Delete(p);
                return new DupRemovalResult { Deleted = selected.Count, JournalPath = "j" };
            });
        await vm.ScanCommand.ExecuteAsync();
        Assert.Equal(0, vm.SelectedCount);                                           // nothing checked after a scan, as in the original
        vm.CheckOneCommand.Execute(null);
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(10, vm.SelectedBytes);
        vm.Confirm = _ => { vm.AllowWholeGroups = true; return true; };
        await vm.CleanCommand.ExecuteAsync();
        Assert.True(whole);
        Assert.False(vm.AllowWholeGroups);                                           // never carried over to the next clean
        Assert.Equal([b2, b3], vm.Rows.Select(r => r.File.FullPath));               // group 1 has a single file left: gone from the list
    }

    private sealed class CountingList(Func<int> read) : ListPageViewModel<int>
    {
        public int Reads { get; private set; }
        protected override IReadOnlyList<int> Read(CancellationToken ct) { Reads++; return Enumerable.Range(0, read()).ToList(); }
        public Task<string?> Change(Func<string> change) => ChangeAsync(_ => change());
    }

    [Fact]
    public async Task A_list_page_reads_again_after_a_change_and_never_overlaps()
    {
        int n = 2;
        var vm = new CountingList(() => n);
        await vm.RefreshAsync();
        Assert.Equal(2, vm.Items.Count);
        Assert.True(vm.Loaded);
        var gate = new TaskCompletionSource();
        var change = vm.Change(() => { gate.Task.Wait(); n = 5; return "done"; });
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsChanging);
        Assert.False(vm.RefreshCommand.CanExecute(null));
        await vm.RefreshAsync();                                                     // ignored while the change runs
        gate.SetResult();
        Assert.Equal("done", await change);
        Assert.Equal(5, vm.Items.Count);
        Assert.Equal(2, vm.Reads);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task An_error_in_a_list_page_goes_to_the_Error_event()
    {
        var vm = new CountingList(() => throw new IOException("disk gone"));
        Exception? seen = null;
        vm.Error += e => seen = e;
        await vm.RefreshAsync();
        Assert.IsType<IOException>(seen);
        Assert.False(vm.IsBusy);
    }
}
