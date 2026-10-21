using CleanSuite.Safety;

namespace CleanSuite.RegistryClean;

/// <summary>User, All Users and System entries.</summary>
public sealed partial class RegistryScanner
{
    private const string StartupApproved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>0x1: generic scan of all of HKCU, value names of the MUI caches and AppCompatFlags, notifications, StartupApproved.</summary>
    private void ScanUserEntries(RegCategory cat)
    {
        GenericScan(cat, RegHive.CurrentUser, "", skipRoots: false, expand: true);
        foreach (var k in new[]
                 {
                     @"Software\Microsoft\Windows\ShellNoRoam\MUICache",
                     @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
                     @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Persisted",
                     @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                 })
            ScanValueNamesAsPaths(cat, RegHive.CurrentUser, k);
        ScanNotifications(cat);
        ScanStartupApproved(cat, RegHive.CurrentUser, StartupApproved + @"\Run");
        ScanStartupApproved(cat, RegHive.CurrentUser, StartupApproved + @"\Run32");
    }

    /// <summary>0x2: generic scan of all of HKEY_USERS and the .DEFAULT MUI cache.</summary>
    private void ScanAllUsersEntries(RegCategory cat)
    {
        GenericScan(cat, RegHive.Users, "", skipRoots: false, expand: true);
        ScanValueNamesAsPaths(cat, RegHive.Users, @".DEFAULT\Software\Microsoft\Windows\ShellNoRoam\MUICache");
    }

    /// <summary>
    /// 0x80: HKLM\Software without the class roots (environment strings expanded only in Thorough mode),
    /// HKLM\System\CurrentControlSet, drivers (Thorough), Windows Installer classes, machine StartupApproved.
    /// </summary>
    private void ScanSystemEntries(RegCategory cat)
    {
        GenericScan(cat, RegHive.LocalMachine, "Software", skipRoots: true, expand: _o.Thorough);
        GenericScan(cat, RegHive.LocalMachine, @"System\CurrentControlSet", skipRoots: false, expand: _o.Thorough);
        if (_o.Thorough) ScanDrivers(cat);
        GenericScan(cat, RegHive.LocalMachine, @"Software\Classes\Installer", skipRoots: false, expand: _o.Thorough);
        ScanStartupApproved(cat, RegHive.LocalMachine, StartupApproved + @"\Run");
        ScanStartupApproved(cat, RegHive.LocalMachine, StartupApproved + @"\Run32");
    }

    /// <summary>
    /// A StartupApproved\Run(32) value whose program is no longer in the matching Run key
    /// is an "Invalid Startup Reference".
    /// </summary>
    private void ScanStartupApproved(RegCategory cat, RegHive hive, string key)
    {
        string? run = key.EndsWith(@"\Run32", StringComparison.OrdinalIgnoreCase) ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"
                    : key.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase) ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" : null;
        if (run == null) return;
        using var l = Lease(hive, key);
        if (l.Key == null) return;
        HashSet<string> present;
        using (var r = Lease(hive, run))
            present = r.Key == null ? new(StringComparer.OrdinalIgnoreCase) : new(ValueNames(r.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var name in ValueNames(l.Key))
            if (!present.Contains(name))
                Report(cat, hive, key, name, "", RegTargetKind.Value, "InvalidStartupReference", "Invalid Startup Reference",
                    raw: RawOf(l.Key, name));
    }

    /// <summary>Push-notification registrations whose LocalPath (checked as a command line) no longer exists.</summary>
    private void ScanNotifications(RegCategory cat)
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications\wpnidm";
        using var l = Lease(RegHive.CurrentUser, root);
        if (l.Key == null) return;
        foreach (var sub in SubKeys(l.Key))
        {
            using var s = l.Key.OpenSubKey(sub, writable: false);
            if (s == null) continue;
            var file = ReadString(s, "FileName");
            var local = ReadString(s, "LocalPath");
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(local)) continue;
            if (!Paths.Exists(local, commandMode: true))
                Report(cat, RegHive.CurrentUser, root + "\\" + sub, "", local, RegTargetKind.Key, "InvalidNotification", "Invalid Notification");
        }
    }
}
