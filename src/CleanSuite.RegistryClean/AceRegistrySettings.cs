using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.RegistryClean;

/// <summary>
/// The original's registry-cleaner settings, read with read access only from
/// HKCU\Software\Acelogix\Ace Utilities. Nothing is ever written there.
/// </summary>
public sealed class AceRegistrySettings
{
    public const string Root = @"Software\Acelogix\Ace Utilities";

    public uint SettingsReg { get; init; } = 0xC;
    public uint RegScanOption { get; init; } = 0xFFFFF;
    public bool Thorough { get; init; }
    public bool Expert { get; init; }
    /// <summary>Settings\reg_bak_format: 0 = .reg, 1 = zip, 2 = none (CleanSuite always writes a .reg backup).</summary>
    public uint BackupFormat { get; init; }
    /// <summary>.dat ignore lists switched off in IgnoreListReg (value 0).</summary>
    public IReadOnlySet<string> DisabledDatLists { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool FoundInRegistry { get; init; }

    public static AceRegistrySettings Read(IRegistryRoots? roots = null)
    {
        var hkcu = (roots ?? WindowsRegistryRoots.Instance).Root(RegHive.CurrentUser);
        try
        {
            using var s = hkcu.OpenSubKey(Root + @"\Settings", writable: false);
            using var ign = hkcu.OpenSubKey(Root + @"\IgnoreListReg", writable: false);
            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ign != null)
                foreach (var n in ign.GetValueNames())
                    if (ign.GetValue(n) is int v && v == 0) disabled.Add(n);
            if (s == null) return new AceRegistrySettings { DisabledDatLists = disabled, FoundInRegistry = ign != null };
            uint U(string name, uint def) => s.GetValue(name) is int i ? unchecked((uint)i) : def;
            return new AceRegistrySettings
            {
                SettingsReg = U("SettingsReg", 0xC), RegScanOption = U("RegScanOption", 0xFFFFF), Thorough = U("registryscanthorough", 0) != 0,
                Expert = U("Expert", 0) != 0, BackupFormat = U("reg_bak_format", 0), DisabledDatLists = disabled, FoundInRegistry = true,
            };
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new AceRegistrySettings();
        }
    }

    /// <summary>Scan options from these settings; <paramref name="ignoreFolder"/> is the original's Data\Ignore_Reg (read only).</summary>
    public RegScanOptions ToOptions(string? ignoreFolder) => RegScanOptions.FromSettingsReg(SettingsReg, RegScanOption, Thorough) with
    {
        Expert = Expert,
        IgnoreFolders = ignoreFolder != null && Directory.Exists(ignoreFolder) ? [ignoreFolder] : Array.Empty<string>(),
        DisabledDatLists = DisabledDatLists,
    };

    /// <summary>Data\Ignore_Reg of an installation folder, when present.</summary>
    public static string? IgnoreFolder(string? installDir)
    {
        if (installDir == null) return null;
        var p = Path.Combine(installDir, "Data", "Ignore_Reg");
        return Directory.Exists(p) ? p : null;
    }
}
