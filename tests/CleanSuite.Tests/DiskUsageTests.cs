using CleanSuite.DiskUsage;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Disk Analysis on temp sandboxes.</summary>
public class DiskUsageTests
{
    private sealed class FakeBin : IRecycleBin
    {
        public IReadOnlyList<string> Recycle(IReadOnlyList<string> paths, bool silent)
        {
            foreach (var p in paths)
                if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p);
            return Array.Empty<string>();
        }
    }

    private static void Write(Sandbox sb, string rel, int size)
    {
        var p = Path.Combine(sb.Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[size]);
    }

    [Fact]
    public void Totals_shares_and_categories_roll_up()
    {
        using var sb = new Sandbox();
        Write(sb, @"Music\a.mp3", 300);
        Write(sb, @"Music\Live\b.FLAC", 100);
        Write(sb, @"Docs\c.pdf", 400);
        Write(sb, @"Docs\noext", 200);
        Directory.CreateDirectory(Path.Combine(sb.Root, "Empty"));

        var root = DiskScanner.Scan(sb.Root);
        Assert.Equal(1000, root.Size);
        Assert.Equal(4, root.FileCount);
        Assert.Equal(4, root.FolderCount);                                          // Music, Live, Docs, Empty
        Assert.Equal(3, root.ItemCount);
        var music = root.Folders.Single(f => f.Name == "Music");
        Assert.Equal(400, music.Size);
        Assert.Equal(0.4, music.ShareOfParent, 3);
        Assert.Equal(400, root.CategorySize[(int)FileCategory.Music]);
        Assert.Equal(400, root.CategorySize[(int)FileCategory.Documents]);
        Assert.Equal(200, root.CategorySize[(int)FileCategory.Others]);
        Assert.Equal(1.0, root.ShareOfParent);
        Assert.Equal(0.0, root.Folders.Single(f => f.Name == "Empty").ShareOfParent);   // 0 of a 1000-byte parent
    }

    [Fact]
    public void Junctions_are_not_followed()
    {
        using var sb = new Sandbox();
        Write(sb, @"real\big.bin", 5000);
        var link = Path.Combine(sb.Root, "link");
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                   $"/c mklink /J \"{link}\" \"{Path.Combine(sb.Root, "real")}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(link));
        var root = DiskScanner.Scan(sb.Root);
        Assert.Equal(5000, root.Size);
        Assert.DoesNotContain(root.Folders, f => f.Name == "link");
    }

    [Fact]
    public void Largest_files_and_extension_stats()
    {
        using var sb = new Sandbox();
        for (int i = 1; i <= 5; i++) Write(sb, $@"d{i % 2}\f{i}.jpg", i * 10);
        Write(sb, @"d0\x.txt", 25);
        var root = DiskScanner.Scan(sb.Root);
        var top = DiskScanner.Largest(root, 3);
        Assert.Equal(new long[] { 50, 40, 30 }, top.Select(f => f.Size));
        var ext = DiskScanner.Extensions(root);
        Assert.Equal(".jpg", ext[0].Extension);
        Assert.Equal(5, ext[0].Count);
        Assert.Equal(150, ext[0].Size);
        Assert.Equal(FileCategory.Images, ext[0].Category);
        Assert.Single(DiskScanner.Extensions(root, FileCategory.Documents));
    }

    [Fact]
    public void Delete_refuses_drives_and_hidden_system_items_and_recycles_the_rest()
    {
        Assert.NotNull(DiskScanner.DeleteBlocked(@"C:\", FileAttributes.Directory, true));
        Assert.NotNull(DiskScanner.DeleteBlocked(@"D:\pagefile.sys", FileAttributes.Hidden | FileAttributes.System, false));
        Assert.NotNull(DiskScanner.DeleteBlocked(@"E:\$Recycle.Bin\S-1-5", FileAttributes.Directory, false));
        Assert.Null(DiskScanner.DeleteBlocked(@"E:\Data\old.iso", FileAttributes.Archive, false));

        using var sb = new Sandbox();
        Write(sb, @"keep\a.bin", 10);
        Write(sb, @"old\b.bin", 20);
        var root = DiskScanner.Scan(sb.Root);
        var old = root.Folders.Single(f => f.Name == "old");
        var (removed, failed) = DiskScanner.Recycle([(old.FullPath, old.Attributes, false), (root.FullPath, root.Attributes, true)], new FakeBin(),
            new ProtectedPaths(), Path.Combine(sb.Root, "..", "_j" + Guid.NewGuid().ToString("N")));
        Assert.Equal(new[] { old.FullPath }, removed);
        Assert.Single(failed);
        DiskScanner.Forget(root, removed);
        Assert.Equal(10, root.Size);
        Assert.DoesNotContain(root.Folders, f => f.Name == "old");
    }

    [Fact]
    public void Report_lists_drives_and_subfolders()
    {
        using var sb = new Sandbox();
        Write(sb, @"A\x.bin", 2048);
        var root = DiskScanner.Scan(sb.Root);
        var text = DiskScanner.Report([new DriveSpace("C:", "System", 1000, 400)], [root], s => s + " B");
        Assert.Contains("C:\\ System Capacity: 1000 B, Used: 600 B, Free: 400 B", text);
        Assert.Contains($"<Folder(s) of {root.FullPath}>", text);
        Assert.Contains("A [2 KB, 1 File(s), 0 Folder(s)]", text);
    }

    [Fact]
    public void Empty_folder_trees_are_found_once_at_their_top()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sb.Root, @"a\b\c"));                 // empty tree -> reported as "a"
        Write(sb, @"full\x.txt", 1);
        Directory.CreateDirectory(Path.Combine(sb.Root, @"full\emptychild"));
        var hidden = Directory.CreateDirectory(Path.Combine(sb.Root, "hidden"));
        hidden.Attributes |= FileAttributes.Hidden;
        var found = EmptyFolderFinder.Find(sb.Root, protectedPaths: new ProtectedPaths());
        Assert.Equal(new[] { Path.Combine(sb.Root, "a"), Path.Combine(sb.Root, @"full\emptychild") }.OrderBy(x => x), found.OrderBy(x => x));
        Assert.Empty(EmptyFolderFinder.Find(sb.Root, [Path.Combine(sb.Root, "*")], new ProtectedPaths()));
        Assert.False(EmptyFolderFinder.IsEmptyTree(Path.Combine(sb.Root, "full")));
    }
}
