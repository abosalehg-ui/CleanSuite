using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Junk files: temp folders that must never be cleaned, files that changed after the scan, the built-in protection list, the wildcard matcher. Temp sandboxes and a private registry hive only.</summary>
public class JunkSafetyTests
{
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

    private sealed class EnvWithTemp(string userTemp) : ISystemEnvironment
    {
        public string GetTempPath() => @"C:\Users\u\AppData\Local\Temp\";
        public string GetWindowsDirectory() => @"C:\Windows";
        public string? GetRegistryEnvironment(bool machine, string name) => machine ? @"C:\Windows\TEMP" : userTemp;
        public IEnumerable<string> GetUserProfileDirectories() => [@"C:\Users\u", @"C:\Users\tmpuser"];
        public DateTime UtcNow => DateTime.UtcNow;
    }

    [Theory]
    [InlineData(@"D:\Downloads")]          // no temp in the name (the original only checked GetTempPath)
    [InlineData(@"C:\")]                   // a drive root
    [InlineData(@"C:\Users\tmpuser")]      // a profile folder whose name contains "tmp"
    [InlineData(@"D:\")]
    public void Unsafe_temp_variables_are_never_used(string userTemp)
    {
        var temps = TempFolderLocator.Locate(new EnvWithTemp(userTemp));
        Assert.DoesNotContain(temps, t => t.Equals(userTemp.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(@"C:\Windows\Temp", temps);
        Assert.False(TempFolderLocator.IsSafeTempFolder(@"C:\Temp-and-Users", [@"C:\Temp-and-Users\u"], @"C:\Windows"));   // contains a profile
        Assert.True(TempFolderLocator.IsSafeTempFolder(@"D:\Tmp", [@"C:\Users\u"], @"C:\Windows"));
    }

    [Fact]
    public void A_junk_file_rewritten_with_the_same_size_is_kept()
    {
        using var sb = new Sandbox();
        var f = sb.File("cache.tmp", 32, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var item = Item(f) with { LastWriteUtc = File.GetLastWriteTimeUtc(f) };
        File.WriteAllBytes(f, Enumerable.Repeat((byte)1, 32).ToArray());           // same size, new content and time
        var res = new JunkRemover(new PickyBin(), new ProtectedPaths(), Path.Combine(sb.Root, "_j"))
            .Remove(new JunkRemovalRequest { Items = [item], Mode = RemovalMode.RecycleBin });
        Assert.Equal(0, res.Deleted);
        Assert.True(File.Exists(f));
    }

    [Theory]
    [InlineData("edb.chk", @"C:\Users\u\AppData\Local\Packages\App\AppData\Indexed DB\edb.chk", true)]
    [InlineData("MSS.chk", @"C:\ProgramData\Microsoft\Search\Data\Applications\Windows\MSS.chk", true)]
    [InlineData("ChkList.tcl", @"C:\Python311\tcl\tix8.4.3\ChkList.tcl", true)]
    [InlineData("tool.exe", @"D:\Tools\tool.exe", true)]
    [InlineData("FILE0000.CHK", @"C:\FOUND.000\FILE0000.CHK", false)]
    [InlineData("report.tmp", @"C:\Work\report.tmp", false)]
    [InlineData("setup.log", @"C:\Work\setup.log", false)]
    public void Built_in_junk_protection_list_keeps_program_and_database_files(string name, string path, bool kept)
    {
        Assert.Equal(kept, CleanSuite.Junk.IgnoreList.BuiltIn.Match(name, path, CleanSuite.Junk.ScanMode.Drive) != null);
    }

    [Theory]
    [InlineData("file.INI", "*.ini", true)]
    [InlineData("Ünïcode-ß.txt", "ünÏ*.TXT", true)]
    [InlineData("a\\b\\c.tmp", "*\\B\\*.tmp", true)]
    [InlineData("abc", "a?d", false)]
    public void Wildcard_matches_without_allocating_the_same_as_before(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, CleanSuite.Matching.AceWildcard.IsMatch(text, pattern));
        var longName = new string('x', 2000) + ".log";                                     // beyond the stack buffers
        Assert.True(CleanSuite.Matching.AceWildcard.IsMatch(longName, "*.LOG"));
    }
}
