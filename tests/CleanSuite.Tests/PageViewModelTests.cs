using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>The view models of the tool pages: one operation at a time, errors reported, rows updated after a clean. Temp sandboxes and a private registry hive only.</summary>
public class PageViewModelTests
{
    private static CleanSuite.RegistryClean.RegFinding Finding(string key) => new()
    {
        Category = CleanSuite.RegistryClean.RegCategory.UserEntries, Hive = RegHive.CurrentUser, Key = key, ValueName = "v", Data = @"C:\gone.exe",
        Target = CleanSuite.RegistryClean.RegTargetKind.Value, Reason = "InvalidPath", ReasonText = "missing",
    };

    [Fact]
    public async Task While_a_scan_runs_nothing_else_can_start_and_errors_end_up_in_the_Error_event()
    {
        using var gate = new ManualResetEventSlim();
        int scans = 0, cleans = 0;
        var vm = new CleanSuite.App.ViewModels.RegistryViewModel(() => new CleanSuite.RegistryClean.RegScanOptions(),
            scan: (_, _) =>
            {
                Interlocked.Increment(ref scans);
                gate.Wait();
                return new CleanSuite.RegistryClean.RegScanResult { Findings = [Finding("a"), Finding("b")], Ignored = [], Unreadable = [] };
            },
            clean: (f, _) =>
            {
                Interlocked.Increment(ref cleans);
                if (f.Count == 1) throw new IOException("disk gone");
                return new CleanSuite.RegistryClean.RegCleanResult { Removed = f.Count, JournalPath = "j" };
            });
        var errors = new List<Exception>();
        vm.Error += errors.Add;
        vm.Confirm = _ => true;

        var first = vm.ScanCommand.ExecuteAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.SelectAllCommand.CanExecute(null));
        await vm.ScanCommand.ExecuteAsync();                                        // a second click while busy is ignored
        gate.Set();
        await first;
        Assert.Equal(1, scans);
        Assert.False(vm.IsBusy);
        Assert.Equal(2, vm.Rows.Count);
        Assert.True(vm.CanClean);

        vm.Rows[1].Selected = false;                                                // one row: the fake cleaner throws
        await vm.CleanCommand.ExecuteAsync();
        Assert.IsType<IOException>(Assert.Single(errors));
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsChanging);
        Assert.Equal(1, cleans);
        Assert.Equal(2, vm.Rows.Count);                                             // nothing reported as removed
    }

    [Fact]
    public async Task Shortcuts_view_model_keeps_failed_rows_and_drops_removed_ones()
    {
        static CleanSuite.Shortcuts.ShortcutItem Item(string name) => new()
        {
            Kind = CleanSuite.Shortcuts.ShortcutItemKind.InvalidShortcut, Name = name + ".lnk", Folder = @"C:\Links",
            Target = @"C:\Gone\" + name + ".exe", Reason = CleanSuite.Shortcuts.ShortcutReason.TargetMissing,
        };
        var items = new[] { Item("a"), Item("b"), Item("c") };
        int cleans = 0;
        var vm = new CleanSuite.App.ViewModels.ShortcutsViewModel(() => new CleanSuite.Shortcuts.ShortcutScanOptions(),
            scan: (_, _) => new CleanSuite.App.ViewModels.ShortcutsViewModel.ScanOutcome(items, 10, 20),
            clean: (chosen, _) =>
            {
                cleans++;
                return new CleanSuite.Shortcuts.ShortcutRunResult
                {
                    Succeeded = chosen.Count - 1, Failed = [(chosen.First(i => i.Name == "b.lnk"), "in use")], JournalPath = "j",
                };
            });
        vm.Confirm = _ => true;

        await vm.ScanCommand.ExecuteAsync();
        Assert.Equal(3, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.True(r.Selected));                 // checked by default, as in the original
        Assert.False(vm.CancelCommand.CanExecute(null));                    // the scanner cannot be interrupted

        vm.Rows[2].Selected = false;                                        // "c" is kept out of the removal
        await vm.CleanCommand.ExecuteAsync();
        Assert.Equal(1, cleans);
        Assert.Equal(["b.lnk", "c.lnk"], vm.Rows.Select(r => r.Item.Name));  // "a" removed, "b" failed, "c" not chosen
        Assert.Equal(1, vm.LastClean!.Succeeded);

        vm.Retargeted(vm.Rows[0]);
        Assert.Equal(["c.lnk"], vm.Rows.Select(r => r.Item.Name));

        vm.SelectNoneCommand.Execute(null);
        Assert.False(vm.CanClean);
        await vm.CleanCommand.ExecuteAsync();                               // nothing checked: nothing runs
        Assert.Equal(1, cleans);
    }
}
