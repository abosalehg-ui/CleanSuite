using CleanSuite.History;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Path rule engine on synthetic folders.</summary>
public class HistoryRuleTests
{
    private static TokenExpander Tokens(Sandbox sb) => new(new Dictionary<string, string>
    {
        ["APPDATA"] = Path.Combine(sb.Root, "Roaming"),
        ["LOCALAPPDATA"] = Path.Combine(sb.Root, "Local"),
    }, s => s);   // no process environment: unknown tokens stay unresolved

    private static RulePlanner Planner(Sandbox sb, IReadOnlyList<string>? never = null) => new(Tokens(sb), never);

    private static HistoryRunResult Run(Sandbox sb, IReadOnlyList<HistoryAction> actions) =>
        new HistoryExecutor(new ProtectedPaths(), Path.Combine(sb.Root, "_journal")).Execute(actions);

    [Fact]
    public void Parses_flags_masks_and_exclusions()
    {
        using var sb = new Sandbox();
        var r = PathRule.Parse(@"%APPDATA%\App\*.log|*.tmp /S /C /R /X:""keep*.log|*\Sub\x.tmp""", Tokens(sb))!;
        Assert.Equal(Path.Combine(sb.Root, @"Roaming\App"), r.Directory);
        Assert.Equal(new[] { "*.log", "*.tmp" }, r.Masks);
        Assert.Equal(new[] { "keep*.log", @"*\Sub\x.tmp" }, r.Exclusions);
        Assert.True(r.Recurse && r.RemoveEmptySubfolders && r.RemoveFolderIfEmpty);
        Assert.False(r.EmptyFolder);
        Assert.Equal(new[] { "*" }, PathRule.Parse(@"%APPDATA%\App\*.*", Tokens(sb))!.Masks);   // "*.*" → "*"
        Assert.True(PathRule.Parse(@"%APPDATA%\App\settings.dat", Tokens(sb))!.IsSingleFile);
    }

    [Fact]
    public void Unresolvable_or_invalid_rules_are_skipped()
    {
        using var sb = new Sandbox();
        Assert.Null(PathRule.Parse(@"%PATH%\cache\*.*", Tokens(sb)));      // plug-in install folder unknown
        Assert.Null(PathRule.Parse(@"%NOPE%\x\*.*", Tokens(sb)));
        Assert.Null(PathRule.Parse("no-backslash.txt", Tokens(sb)));
        Assert.Empty(Planner(sb).Plan("t", @"%NOPE%\x\*.*"));
    }

    [Fact]
    public void Masks_recursion_and_exclusions_select_the_right_files()
    {
        using var sb = new Sandbox();
        sb.File(@"Roaming\App\a.log");
        sb.File(@"Roaming\App\keep1.log");
        sb.File(@"Roaming\App\b.txt");
        sb.File(@"Roaming\App\Sub\c.tmp");
        sb.File(@"Roaming\App\Sub\x.tmp");
        var flat = Planner(sb).Plan("t", @"%APPDATA%\App\*.log|*.tmp /X:""keep*""");
        Assert.Equal(new[] { "a.log" }, flat.Select(a => Path.GetFileName(a.Target)));
        var deep = Planner(sb).Plan("t", @"%APPDATA%\App\*.log|*.tmp /S /X:""keep*|*\Sub\x.tmp""");
        Assert.Equal(new[] { "a.log", "c.tmp" }, deep.Select(a => Path.GetFileName(a.Target)).OrderBy(n => n));
    }

    [Fact]
    public void Wildcard_folders_are_resolved()
    {
        using var sb = new Sandbox();
        sb.File(@"Roaming\Mozilla\Profiles\abc.default\cache2\e1");
        sb.File(@"Roaming\Mozilla\Profiles\xyz.dev\cache2\e2");
        sb.File(@"Roaming\Mozilla\Profiles\xyz.dev\other\e3");
        var acts = Planner(sb).Plan("t", @"%APPDATA%\Mozilla\Profiles\*\cache2\*.* /S");
        Assert.Equal(new[] { "e1", "e2" }, acts.Select(a => Path.GetFileName(a.Target)).OrderBy(n => n));
    }

    [Fact]
    public void Single_file_rule_and_never_delete_list()
    {
        using var sb = new Sandbox();
        sb.File(@"Local\App\state.dat", 7);
        sb.File(@"Local\App\tool.exe", 7);
        Assert.Single(Planner(sb).Plan("t", @"%LOCALAPPDATA%\App\state.dat"));
        Assert.Empty(Planner(sb).Plan("t", @"%LOCALAPPDATA%\App\missing.dat"));
        var acts = Planner(sb, new[] { "*.exe", "*.dll" }).Plan("t", @"%LOCALAPPDATA%\App\*.*");
        Assert.Equal("state.dat", Path.GetFileName(Assert.Single(acts).Target));
    }

    [Fact]
    public void Empty_folder_rule_clears_contents_keeps_folder_unless_R()
    {
        using var sb = new Sandbox();
        sb.File(@"Local\Cache\a.bin");
        sb.File(@"Local\Cache\d1\d2\b.bin");
        var dir = Path.Combine(sb.Root, @"Local\Cache");
        var res = Run(sb, Planner(sb).Plan("t", @"%LOCALAPPDATA%\Cache\ /E"));
        Assert.Empty(res.Failed);
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));

        sb.File(@"Local\Cache2\x.bin");
        Run(sb, Planner(sb).Plan("t", @"%LOCALAPPDATA%\Cache2\*.* /E /R"));
        Assert.False(Directory.Exists(Path.Combine(sb.Root, @"Local\Cache2")));
    }

    [Theory]
    [InlineData(@"C:\Windows\ /E")]
    [InlineData(@"C:\Windows\system32\*.* /E")]
    public void Empty_folder_rule_refuses_windows_and_system32(string rule)
    {
        using var sb = new Sandbox();
        Assert.Empty(Planner(sb).Plan("t", rule));
    }

    [Fact]
    public void C_and_R_remove_empty_folders_after_cleaning()
    {
        using var sb = new Sandbox();
        sb.File(@"Roaming\App\logs\old\a.log");
        sb.File(@"Roaming\App\logs\b.log");
        var res = Run(sb, Planner(sb).Plan("t", @"%APPDATA%\App\logs\*.log /S /C /R"));
        Assert.Empty(res.Failed);
        Assert.False(Directory.Exists(Path.Combine(sb.Root, @"Roaming\App\logs")));
        Assert.True(Directory.Exists(Path.Combine(sb.Root, @"Roaming\App")));
    }

    [Fact]
    public void Ini_rule_deletes_key_or_section_only_when_present()
    {
        using var sb = new Sandbox();
        var ini = sb.File(@"Roaming\App\app.ini");
        File.WriteAllText(ini, "[Recent]\r\nFile1=a\r\nFile2=b\r\n[Keep]\r\nx=1\r\n");
        var planner = Planner(sb);
        Assert.Empty(planner.Plan("t", @"INIFILE|%APPDATA%\App\app.ini|Recent|NoSuchKey"));
        Run(sb, planner.Plan("t", @"INIFILE|%APPDATA%\App\app.ini|Recent|File1"));
        Assert.DoesNotContain("File1", File.ReadAllText(ini));
        Assert.Contains("File2", File.ReadAllText(ini));
        Run(sb, planner.Plan("t", @"INIFILE|%APPDATA%\App\app.ini|Recent"));
        var text = File.ReadAllText(ini);
        Assert.DoesNotContain("[Recent]", text);
        Assert.Contains("[Keep]", text);
    }

    [Fact]
    public void Analysis_never_modifies_and_changed_files_are_skipped()
    {
        using var sb = new Sandbox();
        var f = sb.File(@"Roaming\App\a.log", 10);
        var acts = Planner(sb).Plan("t", @"%APPDATA%\App\*.log");
        Assert.True(File.Exists(f));                                   // planning is read-only
        File.WriteAllBytes(f, new byte[20]);                           // modified after analysis
        var res = Run(sb, acts);
        Assert.True(File.Exists(f));
        Assert.Equal("changed since scan", Assert.Single(res.Failed).Reason);
    }

    [Fact]
    public void Junctions_are_not_followed()
    {
        using var sb = new Sandbox();
        using var target = new Sandbox();
        target.File("inside.log");
        Directory.CreateDirectory(Path.Combine(sb.Root, @"Roaming\App"));
        var link = Path.Combine(sb.Root, @"Roaming\App\link");
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target.Root}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        try
        {
            Assert.Empty(Planner(sb).Plan("t", @"%APPDATA%\App\*.log /S"));
            Assert.Empty(Planner(sb).Plan("t", @"%APPDATA%\App\link\*.log"));
            Assert.Empty(Planner(sb).Plan("t", @"%APPDATA%\App\ /E"));
        }
        finally { Directory.Delete(link); }
        Assert.True(File.Exists(Path.Combine(target.Root, "inside.log")));
    }
}
