using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Registry: CleanSuite's built-in protection list. Temp sandboxes and a private registry hive only.</summary>
public class RegistryProtectionTests
{
    [Fact]
    public void Built_in_registry_protection_list_is_loaded_and_leaves_the_scanned_areas_alone()
    {
        var protective = CleanSuite.RegistryClean.IgnoreWords.Protective;
        Assert.Contains(@"\COMPONENT BASED SERVICING\", protective);
        Assert.Contains(@"\CURRENTVERSION\APPX\", protective);
        Assert.Contains(@"\APPMODEL\STATEREPOSITORY\", protective);
        Assert.All(protective, w => Assert.Equal(w, w.Trim().ToUpperInvariant()));
        Assert.DoesNotContain(protective, w => w.Contains("//"));

        string[] scannedOnPurpose =
        [
            @"HKEY_LOCAL_MACHINE\SOFTWARE\MICROSOFT\WINDOWS\CURRENTVERSION\INSTALLER\FOLDERS\C:\GONE\\\INVALID INSTALLER REFERENCE",
            @"HKEY_LOCAL_MACHINE\SYSTEM\CURRENTCONTROLSET\SERVICES\LANMANSERVER\SHARES\DATA\PATH=D:\GONE\INVALID SHARE",
            @"HKEY_LOCAL_MACHINE\SYSTEM\CONTROLSET001\SERVICES\SHAREDACCESS\PARAMETERS\FIREWALLPOLICY\FIREWALLRULES\{X}\C:\GONE\APP.EXE\INVALID PATH",
            @"HKEY_CURRENT_USER\SOFTWARE\MICROSOFT\WINDOWS\CURRENTVERSION\EXPLORER\RUNMRU\A\C:\GONE\X.EXE\INVALID HISTORY",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\MICROSOFT\WINDOWS\CURRENTVERSION\APP PATHS\GONE.EXE\\C:\GONE\GONE.EXE\INVALID PATH",
        ];
        foreach (var composite in scannedOnPurpose)
            Assert.DoesNotContain(protective, w => composite.Contains(w, StringComparison.Ordinal));

        var user = CleanSuite.RegistryClean.IgnoreWords.UserFolder;
        Assert.EndsWith(@"CleanSuite\Ignore_Reg", user);
        Assert.DoesNotContain(CleanSuite.RegistryClean.IgnoreWords.DefaultFolders(@"Z:\does\not\exist"), f => f.StartsWith("Z:", StringComparison.OrdinalIgnoreCase));
    }
}
