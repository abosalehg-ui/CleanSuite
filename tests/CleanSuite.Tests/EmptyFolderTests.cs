using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Disk analysis: where the empty-folder search never looks. Temp sandboxes and a private registry hive only.</summary>
public class EmptyFolderTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Vendor\Plugins", true)]
    [InlineData(@"C:\ProgramData\Vendor\Cache", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Vendor\Logs", true)]
    [InlineData(@"C:\Users\u\Desktop\New folder", true)]
    [InlineData(@"C:\Users\u\OneDrive\Empty", true)]
    [InlineData(@"C:\Windows\Temp\x", true)]
    [InlineData(@"D:\Data\Old project\empty", false)]
    [InlineData(@"C:\Users\u\Documents\empty", false)]
    public void Default_empty_folder_exclusions_cover_program_and_user_places(string folder, bool excluded)
    {
        Assert.Equal(excluded, CleanSuite.DiskUsage.EmptyFolderFinder.DefaultExcludes.Any(p => CleanSuite.Matching.AceWildcard.IsMatch(folder, p)));
        Assert.Contains("*.exe", CleanSuite.History.RulePlanner.DefaultNeverDelete);
        Assert.Contains("*.dll", CleanSuite.History.RulePlanner.DefaultNeverDelete);
    }
}
