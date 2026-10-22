using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Installed programs: how uninstall commands are resolved before they are started. Temp sandboxes and a private registry hive only.</summary>
public class UninstallCommandTests
{
    [Fact]
    public void Uninstall_commands_use_the_full_msiexec_path_and_only_real_product_codes()
    {
        CleanSuite.Uninstall.UninstallEntry Msi(string key) => new()
        {
            Name = "P", SubKey = key, Hive = RegHive.LocalMachine, ParentKey = "p", Flags = CleanSuite.Uninstall.UninstallFlags.WindowsInstaller,
        };
        var ok = Msi("{12345678-1234-1234-1234-1234567890AB}");
        Assert.Equal(CleanSuite.Uninstall.UninstallEntry.Msiexec + " /X {12345678-1234-1234-1234-1234567890AB}", ok.UninstallCommand);
        Assert.StartsWith("\"" + Environment.SystemDirectory, ok.RepairCommand);
        var forged = Msi("x /i http://host/a.msi /qn");
        Assert.Equal("", forged.UninstallCommand);                                  // no arguments smuggled through the key name
        Assert.Equal("", forged.ModifyCommand);
        Assert.Equal("", forged.RepairCommand);
    }

    [Fact]
    public void An_unquoted_path_resolves_to_the_longest_existing_program()
    {
        using var sb = new Sandbox();
        var planted = sb.File(@"My.exe", 1);                                        // what Windows would run for "…\My App\…"
        var real = sb.File(@"My App\unins.exe", 1);
        var r = CleanSuite.Uninstall.UninstallManager.ResolveCommand(Path.Combine(sb.Root, @"My App\unins.exe") + " /S /x");
        Assert.Equal((real, "/S /x"), r);
        Assert.Equal((planted, "/q"), CleanSuite.Uninstall.UninstallManager.ResolveCommand("\"" + planted + "\" /q"));
        Assert.Null(CleanSuite.Uninstall.UninstallManager.ResolveCommand(Path.Combine(sb.Root, @"Gone\setup.exe /u")));
        Assert.Null(CleanSuite.Uninstall.UninstallManager.ResolveCommand("msiexec /X {x}"));      // relative: never searched for
    }
}
