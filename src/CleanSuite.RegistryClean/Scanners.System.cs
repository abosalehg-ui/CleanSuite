using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.RegistryClean;

/// <summary>
/// Application Paths (0x100), Add/Remove (0x200), Shared DLLs (0x400), Fonts (0x800), Help files (0x1000), Shared folders
/// (0x80000) and device drivers (System Entries, Thorough).
/// </summary>
public sealed partial class RegistryScanner
{
    private const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
    private const string SharedDllsKey = @"Software\Microsoft\Windows\CurrentVersion\SharedDLLs";

    /// <summary>
    /// The names the validator accepts in command mode: bare file names of SharedDLLs
    /// entries, App Paths names whose target exists, and every HKCR\Applications name.
    /// </summary>
    private IEnumerable<string> KnownCommandNames()
    {
        var names = new List<string>();
        names.AddRange(AppPathsWithTarget());
        using (var l = Lease(RegHive.LocalMachine, SharedDllsKey))
            if (l.Key != null)
                foreach (var n in ValueNames(l.Key))
                    if (n.Length > 2 && n.Substring(1, 2) == @":\" && Kind(l.Key, n) == RegistryValueKind.DWord)
                        names.Add(n.ToUpperInvariant()[(n.LastIndexOf('\\') + 1)..]);
        using (var a = Lease(RegHive.ClassesRoot, "Applications"))
            if (a.Key != null) names.AddRange(SubKeys(a.Key));
        return names;
    }

    private List<string> AppPathsWithTarget()
    {
        var list = new List<string>();
        using var l = Lease(RegHive.LocalMachine, AppPaths);
        if (l.Key == null) return list;
        foreach (var n in SubKeys(l.Key))
        {
            var p = ReadExpanded(RegHive.LocalMachine, AppPaths + "\\" + n, "");
            if (p.Length != 0 && Paths.FileProbe.HasAttributes(p)) { list.Add(n); continue; }
            var p2 = ReadExpanded(RegHive.LocalMachine, AppPaths + "\\" + n, "PATH");
            if (p2.Length != 0 && Paths.FileProbe.HasAttributes(p2)) list.Add(n);
        }
        return list;
    }

    private static RegistryValueKind Kind(RegistryKey k, string name)
    {
        try { return k.GetValueKind(name); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return RegistryValueKind.Unknown; }
    }

    // ---------------------------------------------------------------- 0x100 Application Paths

    private void ScanApplicationPaths(RegCategory cat)
    {
        GenericScan(cat, RegHive.LocalMachine, @"Software\Classes\Applications", false, true);
        var withTarget = new HashSet<string>(AppPathsWithTarget(), StringComparer.OrdinalIgnoreCase);
        UselessApplications(cat, RegHive.ClassesRoot, "Applications", withTarget);
        ScanAppPaths(cat, AppPaths, @"Software\classes\Applications", withTarget);
        ScanAppPaths(cat, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\App Paths", @"Software\Wow6432Node\classes\Applications", withTarget);
        ScanFirewallRules(cat);
    }

    /// <summary>
    /// "*.exe" application keys with no values anywhere in their tree, for a
    /// program that cannot be found and is not registered in App Paths ("Useless Key").
    /// </summary>
    private void UselessApplications(RegCategory cat, RegHive hive, string root, ISet<string> withTarget)
    {
        using var l = Lease(hive, root);
        if (l.Key == null) return;
        foreach (var n in SubKeys(l.Key))
        {
            _ct.ThrowIfCancellationRequested();
            var key = root + "\\" + n;
            if (SubKeyCount(hive, key) > 0 && ShellCheck(cat, hive, key)) continue;
            if (withTarget.Contains(n)) continue;
            if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || n.Equals("Outlook.EXE", StringComparison.OrdinalIgnoreCase)
                || n.Equals("winword.EXE", StringComparison.OrdinalIgnoreCase) || n.Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (Paths.FileExists(n)) continue;
            if (ValueState(hive, key, "NoOpenWith") == 2 || ValueState(hive, key, "NoStartPage") == 2) continue;
            if (IsEmptyTree(l.Key, n, true, false, true))
                Report(cat, hive, key, n, "", RegTargetKind.Key, "UselessKey", "Useless Key", 0xA185, applyIgnore: false);
        }
    }

    /// <summary>App Paths entries with neither a default value nor "path" and no content ("Empty key").</summary>
    private void ScanAppPaths(RegCategory cat, string appPaths, string applications, ISet<string> withTarget)
    {
        const RegHive lm = RegHive.LocalMachine;
        using (var l = Lease(lm, appPaths))
            if (l.Key != null)
                foreach (var n in SubKeys(l.Key))
                {
                    var key = appPaths + "\\" + n;
                    if (ReadExpanded(lm, key, "").Length != 0 || ReadExpanded(lm, key, "path").Length != 0) continue;
                    if (IsEmptyTree(l.Key, n, true, false, true))
                        Report(cat, lm, key, n, "", RegTargetKind.Key, "EmptyKey", "Empty key", 0xA17C, applyIgnore: false);
                }
        UselessApplications(cat, lm, applications, withTarget);
    }

    /// <summary>Firewall rules (ControlSet001–004) whose "App=" program no longer exists.</summary>
    private void ScanFirewallRules(RegCategory cat)
    {
        const RegHive lm = RegHive.LocalMachine;
        for (int n = 1; n <= 4; n++)
        {
            var key = $@"SYSTEM\ControlSet{n:000}\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
            using var l = Lease(lm, key);
            if (l.Key == null) continue;
            foreach (var rule in ValueNames(l.Key))
            {
                _ct.ThrowIfCancellationRequested();
                var data = ReadString(l.Key, rule);
                if (data == null || (data.Length + 1) * 2 > MaxDataBytes) continue;
                string? app = null;
                foreach (var token in data.Split('|'))
                {
                    if (token.Length == 0) break;
                    if (token.StartsWith("App=", StringComparison.OrdinalIgnoreCase)) { app = token[(token.IndexOf('=') + 1)..]; break; }
                }
                if (app == null) continue;
                var path = Expand(app);
                if (path.Length is 0 or > 0x103) continue;
                if (Paths.Exists(path)) continue;
                if (rule.StartsWith("MCX-", StringComparison.OrdinalIgnoreCase) || rule.StartsWith("Collab-P2PHost-", StringComparison.OrdinalIgnoreCase)) continue;
                Report(cat, lm, key, rule, path, RegTargetKind.Value, "InvalidFirewallRule", "Invalid Firewall Rule", applyIgnore: false,
                    raw: RawOf(l.Key, rule));
            }
        }
    }

    // ---------------------------------------------------------------- 0x200 Add/Remove

    private void ScanAddRemove(RegCategory cat)
    {
        foreach (var hive in new[] { RegHive.LocalMachine, RegHive.CurrentUser })
        {
            const string arp = @"Software\Microsoft\Windows\CurrentVersion\App Management\ARPCache";
            using var l = Lease(hive, arp);
            if (l.Key == null) continue;
            foreach (var n in SubKeys(l.Key))
                if (!KeyPresent(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + n)
                    && !KeyPresent(RegHive.LocalMachine, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + n))
                    Report(cat, hive, arp + "\\" + n, n, "", RegTargetKind.Key, "ObsoleteUninstallEntry", "Obsolete Uninstall Entry", 0xA18B);
        }
        const string folders = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders";
        using var f = Lease(RegHive.LocalMachine, folders);
        if (f.Key == null) return;
        foreach (var p in ValueNames(f.Key))
            if (p.Length > 0 && !Paths.Exists(p))
                Report(cat, RegHive.LocalMachine, folders, p, "", RegTargetKind.Value, "InvalidInstallerReference", "Invalid Installer Reference",
                    raw: RawOf(f.Key, p));
    }

    // ---------------------------------------------------------------- 0x400 Shared DLLs

    private void ScanSharedDlls(RegCategory cat)
    {
        foreach (var key in new[] { SharedDllsKey, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs" })
        {
            using var l = Lease(RegHive.LocalMachine, key);
            if (l.Key == null) continue;
            foreach (var name in ValueNames(l.Key))
            {
                _ct.ThrowIfCancellationRequested();
                if (name.Length > MaxNameChars) continue;
                var raw = RawOf(l.Key, name);
                string data;
                switch (Kind(l.Key, name))
                {
                    case RegistryValueKind.DWord when raw != null: data = DwordText(raw); break;
                    case RegistryValueKind.Binary: data = "<BINARY>"; break;
                    case RegistryValueKind.String when raw is string str: data = str; break;
                    default: continue;
                }
                if (name.Length > 2 && name.Substring(1, 2) == @":\" && !Paths.DataIsValid(name.ToUpperInvariant()))
                    Report(cat, RegHive.LocalMachine, key, name, data, RegTargetKind.Value, "InvalidEntry", "Invalid Entry: %2", InvalidEntryId, raw: raw);
            }
        }
        const string approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";
        using var a = Lease(RegHive.LocalMachine, approved);
        if (a.Key == null) return;
        foreach (var clsid in ValueNames(a.Key))
            if (clsid.Length > 0 && !KeyPresent(RegHive.ClassesRoot, @"CLSID\" + clsid))
                Report(cat, RegHive.LocalMachine, approved, clsid, ReadString(a.Key, clsid) ?? "", RegTargetKind.Value, "InvalidApprovedShellExt",
                    "Invalid Approved ShellExt", raw: RawOf(a.Key, clsid));
    }

    // ---------------------------------------------------------------- 0x800 Fonts

    private void ScanFonts(RegCategory cat)
    {
        var fonts = SpecialFolder(Environment.SpecialFolder.Fonts).TrimEnd('\\');
        if (fonts.Length == 0 || !Paths.Exists(fonts)) return;
        var win = WindowsFolder.TrimEnd('\\', ' ');
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
        using var l = Lease(RegHive.LocalMachine, key);
        if (l.Key == null) return;
        foreach (var name in ValueNames(l.Key))
        {
            var data = ReadString(l.Key, name);
            if (data == null) continue;
            if (Paths.Exists(fonts + "\\" + data) || Paths.Exists(data, commandMode: true) || Paths.Exists(win + @"\Fonts\" + data)) continue;
            Report(cat, RegHive.LocalMachine, key, name, data, RegTargetKind.Value, "FileMissing", fonts + "\\" + data + " doesn't exist", 0xA18E,
                applyIgnore: false, raw: RawOf(l.Key, name));
        }
    }

    // ---------------------------------------------------------------- 0x1000 Help files

    private void ScanHelpFiles(RegCategory cat)
    {
        foreach (var key in new[]
                 {
                     @"Software\Microsoft\Windows\Help", @"Software\Wow6432Node\Microsoft\Windows\Help",
                     @"Software\Microsoft\Windows\HTML Help", @"Software\Wow6432Node\Microsoft\Windows\HTML Help",
                 })
        {
            using var l = Lease(RegHive.LocalMachine, key);
            if (l.Key == null) continue;
            foreach (var name in ValueNames(l.Key))
            {
                var dir = ReadString(l.Key, name);
                if (string.IsNullOrEmpty(dir) || !Paths.Exists(dir)) continue;        // a missing folder is not reported
                var full = dir.TrimEnd('\\') + "\\" + name;
                if (!Paths.Exists(full))
                    Report(cat, RegHive.LocalMachine, key, name, dir, RegTargetKind.Value, "FileMissing", full + " doesn't exist", 0xA18E,
                        applyIgnore: false, raw: RawOf(l.Key, name));
                else if (IsZeroSize(full))
                    Report(cat, RegHive.LocalMachine, key, name, dir, RegTargetKind.Value, "FileDamaged", full + " is damaged (zero size)",
                        applyIgnore: false, raw: RawOf(l.Key, name));
            }
        }
    }

    private static bool IsZeroSize(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length == 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---------------------------------------------------------------- 0x80000 Shared folders

    private void ScanSharedFolders(RegCategory cat)
    {
        const string shares = @"SYSTEM\CurrentControlSet\Services\LanmanServer\Shares";
        using var l = Lease(RegHive.LocalMachine, shares);
        if (l.Key == null) return;
        foreach (var s in ValueNames(l.Key))
        {
            if (RawOf(l.Key, s) is not string[] lines) continue;
            string Field(string prefix) => lines.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } x
                ? x[(x.IndexOf('=') + 1)..].Trim() : "";
            var path = Field("Path=");
            var typeText = Field("Type=");
            uint type = uint.TryParse(typeText, out var t) ? t : uint.MaxValue;
            if (type != 0 || path.Length == 0 || Paths.Exists(path)) continue;
            Report(cat, RegHive.LocalMachine, shares, s, path, RegTargetKind.Value, "FileMissing", path + " doesn't exist", 0xA18E, raw: RawOf(l.Key, s));
            using var sec = Lease(RegHive.LocalMachine, shares + @"\Security");
            if (sec.Key != null && ValueState(sec.Key, s) == 2)
                Report(cat, RegHive.LocalMachine, shares + @"\Security", s, path, RegTargetKind.Value, "FileMissing", path + " doesn't exist", 0xA18E,
                    raw: RawOf(sec.Key, s));
        }
    }

    // ---------------------------------------------------------------- drivers (System Entries, Thorough)

    private void ScanDrivers(RegCategory cat)
    {
        const string vxd = @"System\CurrentControlSet\Services\VxD";
        using var l = Lease(RegHive.LocalMachine, vxd);
        if (l.Key == null) return;
        foreach (var n in SubKeys(l.Key))
        {
            var key = vxd + "\\" + n;
            using var k = Lease(RegHive.LocalMachine, key);
            if (k.Key == null || ValueState(k.Key, "StaticVxD") != 2) continue;
            var v = ReadExpanded(RegHive.LocalMachine, key, "StaticVxD");
            if (v.Contains('*')) continue;
            if (v.Length == 0 || !Paths.DataIsValid(v, true))
                Report(cat, RegHive.LocalMachine, key, "StaticVxD", v, RegTargetKind.Value, "InvalidDeviceDriver", "Invalid Device Driver",
                    raw: RawOf(k.Key, "StaticVxD"));
        }
    }
}
