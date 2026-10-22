using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Duplicate files: never the only copy, never a file as a copy of itself, ignore lists in any encoding, the partial pre-check. Temp sandboxes and a private registry hive only.</summary>
public class DuplicateSafetyTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    private static DupOptions DupOpt(params string[] roots) =>
        new() { Roots = roots, Patterns = DupProfiles.AllFiles, MinSize = 0, Excludes = Array.Empty<string>() };

    /// <summary>Deletes what it is given; refuses (like a drive without a Recycle Bin) every path containing "nobin".</summary>
    private sealed class PickyBin : IRecycleBin
    {
        public readonly List<string> Sent = new();
        public IReadOnlyList<string> Recycle(IReadOnlyList<string> paths, bool silent)
        {
            foreach (var p in paths) { Sent.Add(p); File.Delete(p); }
            return Array.Empty<string>();
        }
        public string? WhyNotRecyclable(string path) => path.Contains("nobin") ? "drive without a Recycle Bin (not deleted)" : null;
    }

    private static JunkItem Item(string path) => new(path, Path.GetFileName(path), Path.GetDirectoryName(path)!, new FileInfo(path).Length,
        JunkReason.JunkType, "Temp", 1);

    [Fact]
    public void Duplicate_ignore_lists_in_UTF16_or_UTF8_match_Arabic_names()
    {
        using var sb = new Sandbox();
        var dir = Path.Combine(sb.Root, "Ignore_Dup");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "تقرير*.pdf  // comment", System.Text.Encoding.Unicode);
        File.WriteAllText(Path.Combine(dir, "b.txt"), "صورة.jpg", new System.Text.UTF8Encoding(true));
        Assert.Equal(new[] { "تقرير*.PDF", "صورة.JPG" }, CleanSuite.Duplicates.IgnoreFile.Load(dir));
    }

    [Fact]
    public void A_hard_link_or_a_nested_root_is_not_a_duplicate_of_itself()
    {
        using var sb = new Sandbox();
        var file = sb.File(@"data\song.mp3", 500);
        var link = Path.Combine(sb.Root, @"other\song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Assert.True(CreateHardLinkW(link, file, IntPtr.Zero));                       // one file, two names

        var res = new DuplicateScanner(DupOpt(sb.Root, Path.Combine(sb.Root, "data"), sb.Root + "\\")).Scan();
        Assert.Empty(res.Groups);
        Assert.Equal(2, res.FilesSearched);                                         // the nested root is walked once

        Assert.Equal(new[] { sb.Root }, DuplicateScanner.DistinctRoots([sb.Root, sb.Root + @"\data", sb.Root.ToUpperInvariant() + "\\"]));
    }

    [Fact]
    public void A_duplicate_is_kept_when_the_other_copy_changed_or_is_the_same_file()
    {
        using var sb = new Sandbox();
        var a = sb.File(@"a\x.bin", 64);
        var b = sb.File(@"b\x.bin", 64);
        var groups = new DuplicateScanner(DupOpt(sb.Root)).Scan().Groups;
        Assert.Single(groups);
        File.WriteAllBytes(a, Enumerable.Repeat((byte)7, 64).ToArray());          // the kept copy changed after the scan
        var res = new CleanSuite.Duplicates.DuplicateRemover(new PickyBin(), new ProtectedPaths(), Path.Combine(sb.Root, "_j"))
            .Remove(groups, new HashSet<string>([b], StringComparer.OrdinalIgnoreCase), RemovalMode.RecycleBin);
        Assert.Equal(0, res.Deleted);
        Assert.True(File.Exists(b));
        Assert.Contains(res.Failed, f => f.Path == b && f.Reason.Contains("same content"));
    }

    [Fact]
    public void Duplicate_pre_check_never_merges_files_that_differ_only_in_the_middle()
    {
        using var sb = new Sandbox();
        var root = Path.Combine(sb.Root, "d");
        Directory.CreateDirectory(Path.Combine(root, "x"));
        Directory.CreateDirectory(Path.Combine(root, "y"));
        Directory.CreateDirectory(Path.Combine(root, "z"));
        Directory.CreateDirectory(Path.Combine(root, "w"));
        var data = new byte[300 * 1024];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(Path.Combine(root, "x", "big.bin"), data);
        File.WriteAllBytes(Path.Combine(root, "y", "big.bin"), data);                       // identical copy
        var middle = (byte[])data.Clone(); middle[150 * 1024] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(root, "z", "big.bin"), middle);                     // same ends, different middle
        var head = (byte[])data.Clone(); head[0] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(root, "w", "big.bin"), head);                       // differs in the first block

        Assert.Equal(DuplicateScanner.PartialHash(Path.Combine(root, "x", "big.bin")), DuplicateScanner.PartialHash(Path.Combine(root, "z", "big.bin")));
        Assert.NotEqual(DuplicateScanner.PartialHash(Path.Combine(root, "x", "big.bin")), DuplicateScanner.PartialHash(Path.Combine(root, "w", "big.bin")));

        foreach (var content in new[] { ContentCompare.Md5, ContentCompare.Crc32 })
        {
            var res = new DuplicateScanner(new DupOptions { Roots = [root], Content = content, Patterns = DupProfiles.AllFiles, MinSize = 0, Excludes = Array.Empty<string>() }).Scan();
            var group = Assert.Single(res.Groups);
            Assert.Equal(new[] { "x", "y" }, group.Files.Select(f => Path.GetFileName(Path.GetDirectoryName(f.FullPath)!)).Order());
        }
    }
}
