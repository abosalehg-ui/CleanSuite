using CleanSuite.Safety;

namespace CleanSuite.Startup;

public enum LocationKind { RegistryRun, StartupFolder }

/// <param name="Type">Location type of the original, kept for compatibility (Auto\Md5 names).</param>
/// <param name="Key">Run key, or for startup folders the folder path.</param>
/// <param name="ApprovedKey">StartupApproved key (same hive) used to disable on Windows 8+, or null: the original then moves the
/// value to the sibling key "&lt;Key&gt;-".</param>
public sealed record StartupLocation(int Type, string Id, LocationKind Kind, RegHive Hive, string Key, string? ApprovedKey, bool PerUser);

public static class StartupLocations
{
    private const string Cv = @"Software\Microsoft\Windows\CurrentVersion";
    private const string Wow = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion";
    private const string Ts = @"Software\Microsoft\Windows NT\CurrentVersion\Terminal Server\Install\Software\Microsoft\Windows\CurrentVersion";
    public const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Run-type locations of the original, in its type numbering (Phase 2 report §5.2).</summary>
    public static readonly IReadOnlyList<StartupLocation> RegistryLocations = new[]
    {
        new StartupLocation(1, "hkcu.run", LocationKind.RegistryRun, RegHive.CurrentUser, Cv + @"\Run", Approved + @"\Run", true),
        new StartupLocation(2, "hkcu.runonce", LocationKind.RegistryRun, RegHive.CurrentUser, Cv + @"\RunOnce", null, true),
        new StartupLocation(3, "hkcu.policies", LocationKind.RegistryRun, RegHive.CurrentUser, Cv + @"\Policies\Explorer\Run", null, true),
        new StartupLocation(0x51, "hkcu.run32", LocationKind.RegistryRun, RegHive.CurrentUser, Wow + @"\Run", Approved + @"\Run32", true),
        new StartupLocation(0x50, "hkcu.runonce32", LocationKind.RegistryRun, RegHive.CurrentUser, Wow + @"\RunOnce", null, true),
        new StartupLocation(0x48, "hkcu.ts.run", LocationKind.RegistryRun, RegHive.CurrentUser, Ts + @"\Run", null, true),
        new StartupLocation(0x4A, "hkcu.ts.runonce", LocationKind.RegistryRun, RegHive.CurrentUser, Ts + @"\Runonce", null, true),
        new StartupLocation(4, "hklm.run", LocationKind.RegistryRun, RegHive.LocalMachine, Cv + @"\Run", Approved + @"\Run", false),
        new StartupLocation(5, "hklm.runservices", LocationKind.RegistryRun, RegHive.LocalMachine, Cv + @"\RunServices", null, false),
        new StartupLocation(6, "hklm.runonce", LocationKind.RegistryRun, RegHive.LocalMachine, Cv + @"\RunOnce", null, false),
        new StartupLocation(8, "hklm.runservicesonce", LocationKind.RegistryRun, RegHive.LocalMachine, Cv + @"\RunServicesOnce", null, false),
        new StartupLocation(9, "hklm.policies", LocationKind.RegistryRun, RegHive.LocalMachine, Cv + @"\Policies\Explorer\Run", null, false),
        new StartupLocation(0x32, "hklm.run32", LocationKind.RegistryRun, RegHive.LocalMachine, Wow + @"\Run", Approved + @"\Run32", false),
        new StartupLocation(0x38, "hklm.runonce32", LocationKind.RegistryRun, RegHive.LocalMachine, Wow + @"\RunOnce", null, false),
        new StartupLocation(0x47, "hklm.ts.run", LocationKind.RegistryRun, RegHive.LocalMachine, Ts + @"\Run", null, false),
        new StartupLocation(0x49, "hklm.ts.runonce", LocationKind.RegistryRun, RegHive.LocalMachine, Ts + @"\Runonce", null, false),
    };

    /// <summary>Startup folders: 11 = current user (HKCU StartupFolder), 10 = all users (HKLM StartupFolder).</summary>
    public static IReadOnlyList<StartupLocation> FolderLocations(string userStartup, string commonStartup) => new[]
    {
        new StartupLocation(11, "user.startup", LocationKind.StartupFolder, RegHive.CurrentUser, userStartup, Approved + @"\StartupFolder", true),
        new StartupLocation(10, "common.startup", LocationKind.StartupFolder, RegHive.LocalMachine, commonStartup, Approved + @"\StartupFolder", false),
    };

    public static IReadOnlyList<StartupLocation> FolderLocations() => FolderLocations(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
}
