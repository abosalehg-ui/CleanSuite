using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.RegistryClean;

/// <summary>Start Menu (0x4), Obsolete Software (0x8), File Extensions (0x10) and File Associations (0x20).</summary>
public sealed partial class RegistryScanner
{
    /// <summary>Special-folder lookup (replaceable in tests).</summary>
    public Func<Environment.SpecialFolder, string> SpecialFolder { get; init; } = f => Environment.GetFolderPath(f);

    private const string MenuOrder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\MenuOrder";

    // ---------------------------------------------------------------- 0x4 Start Menu

    /// <summary>MenuOrder keys whose Start-menu folder no longer exists (user or all-users Programs), and empty MenuOrder keys.</summary>
    private void ScanStartMenu(RegCategory cat)
    {
        var programs = SpecialFolder(Environment.SpecialFolder.Programs).TrimEnd('\\');
        var common = SpecialFolder(Environment.SpecialFolder.CommonPrograms).TrimEnd('\\');
        StartMenuWalk(cat, MenuOrder + @"\Start Menu\Programs", "", programs, common);
        StartMenuWalk(cat, MenuOrder + @"\Start Menu2\Programs", "", programs, common);

        using var fav = Lease(RegHive.CurrentUser, MenuOrder + @"\Favorites");
        if (fav.Key == null) return;
        foreach (var name in SubKeys(fav.Key))
            if (IsEmptyTree(fav.Key, name, true, false, true))
                Report(cat, RegHive.CurrentUser, MenuOrder + @"\Favorites\" + name, name, "", RegTargetKind.Key, "EmptyUselessKey",
                    "Empty Useless Key", 0xA17B, IgnoreUnlessMruExempt);
    }

    /// <summary>The folder paths are built literally (a doubled "\" at the first level, as in the original).</summary>
    private void StartMenuWalk(RegCategory cat, string regPath, string rel, string programs, string common)
    {
        _ct.ThrowIfCancellationRequested();
        using var l = Lease(RegHive.CurrentUser, regPath);
        if (l.Key == null) return;
        foreach (var name in SubKeys(l.Key))
        {
            var key = regPath + "\\" + name;
            if (IsEmptyTree(l.Key, name, true, false, true))
            {
                Report(cat, RegHive.CurrentUser, key, name, "", RegTargetKind.Key, "EmptyUselessKey", "Empty Useless Key", 0xA17B, IgnoreUnlessMruExempt);
                continue;
            }
            var user = programs + "\\" + rel + "\\" + name;
            var all = common + "\\" + rel + "\\" + name;
            if (programs.Length > 0 && !Paths.DataIsValid(user, true) && (common.Length == 0 || !Paths.DataIsValid(all, true)))
                Report(cat, RegHive.CurrentUser, key, name, "", RegTargetKind.Key, "ObsoleteStartMenuItem", "Obsolete Start Menu Item", 0xA184,
                    IgnoreUnlessMruExempt);
            StartMenuWalk(cat, key, rel.Length == 0 ? name : rel + "\\" + name, programs, common);
        }
    }

    // ---------------------------------------------------------------- 0x8 Obsolete Software

    private static readonly HashSet<string> UninstallKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fontcore", "AddressBook", "DirectAnimation", "DirectDrawEx", "DXM_Runtime", "IEData", "IE5BAKEX", "IE4Data", "IE40",
        "MobileOptionPack", "NetMeeting", "OutlookExpress", "PCHealth", "SchedulingAgent", "ICW", "MPlayer2", "InstallShield Uninstall Information",
    };

    /// <summary>Vendor/application keys that are empty trees ("Obsolete key").</summary>
    private void ScanObsoleteSoftware(RegCategory cat)
    {
        void Each(RegHive hive, string root, ISet<string> skip, bool allowDefault, bool recurse, bool ignore)
        {
            using var l = Lease(hive, root);
            if (l.Key == null) return;
            foreach (var n in SubKeys(l.Key))
            {
                _ct.ThrowIfCancellationRequested();
                if (skip.Contains(n)) continue;
                if (IsEmptyTree(l.Key, n, true, allowDefault, recurse))
                    Report(cat, hive, root + "\\" + n, "", "", RegTargetKind.Key, "ObsoleteKey", "Obsolete key", 0xA188, ignore);
            }
        }
        var softwareSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Classes", "Windows 3.1 Migration Status" };
        Each(RegHive.LocalMachine, "Software", softwareSkip, true, _o.Thorough, true);
        Each(RegHive.LocalMachine, @"Software\Wow6432Node", softwareSkip, true, _o.Thorough, true);
        Each(RegHive.CurrentUser, "Software", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Classes" }, true, true, true);
        Each(RegHive.Users, @".Default\Software", new HashSet<string>(), true, true, true);
        Each(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", UninstallKeep, false, true, false);
        Each(RegHive.LocalMachine, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall", UninstallKeep, false, true, false);
    }

    // ---------------------------------------------------------------- 0x10 File Extensions

    private const string FileExts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    /// <summary>Explorer's per-user FileExts: empty keys, Open-with entries whose program is gone, unknown ProgIDs.</summary>
    private void ScanFileExtensions(RegCategory cat)
    {
        using var fe = Lease(RegHive.CurrentUser, FileExts);
        if (fe.Key == null) return;
        bool ign = IgnoreUnlessMruExempt;
        foreach (var ext in SubKeys(fe.Key))
        {
            _ct.ThrowIfCancellationRequested();
            var extKey = FileExts + "\\" + ext;
            if (IsEmptyTree(fe.Key, ext, true, false, true) && ext != "OpenWithList")
            {
                Report(cat, RegHive.CurrentUser, extKey, ext, "", RegTargetKind.Key, "EmptyKey", "Empty Key", 0xA170, ign);
                continue;
            }

            int nonMru = 0;
            bool opened;
            using (var owl = Lease(RegHive.CurrentUser, extKey + @"\openwithlist"))
            {
                opened = owl.Key != null;
                if (owl.Key != null)
                    foreach (var name in ValueNames(owl.Key))
                    {
                        if (!name.Equals("MRUList", StringComparison.Ordinal)) nonMru++;
                        if (name.Length != 1) continue;
                        var data = ReadString(owl.Key, name) ?? "";
                        int bang = data.IndexOf('!');
                        if (bang >= 0 && KeyPresent(RegHive.ClassesRoot,
                                @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\" + data[..bang]))
                            continue;                                            // packaged (Store) application
                        if (!Paths.Exists(data, commandMode: true))
                            Report(cat, RegHive.CurrentUser, extKey + @"\openwithlist", name, data, RegTargetKind.Value, "OpenWithAppMissing",
                                "Open With App doesn't Exist", 0xA187, ign, raw: RawOf(owl.Key, name));
                    }
            }
            if ((!opened || nonMru == 0) && _o.Thorough && IsEmptyTree(fe.Key, ext + @"\openwithlist", false, false, true))
                Report(cat, RegHive.CurrentUser, extKey + @"\openwithlist", ext, "", RegTargetKind.Key, "EmptyKey", "Empty Key", 0xA170, ign);

            using var owp = Lease(RegHive.CurrentUser, extKey + @"\openwithprogids");
            if (owp.Key == null) continue;
            foreach (var progid in ValueNames(owp.Key))
                if (progid.Length > 0 && !KeyPresent(RegHive.ClassesRoot, progid) && !KeyPresent(RegHive.ClassesRoot, @"Wow6432Node\" + progid))
                    Report(cat, RegHive.CurrentUser, extKey + @"\openwithprogids", progid, "", RegTargetKind.Value, "InvalidProgid",
                        "Invalid Progid", 0, ign, raw: RawOf(owp.Key, progid));
        }
    }

    // ---------------------------------------------------------------- 0x20 File Associations

    private static readonly string[] ClassPrefixesSkipped =
        ["SystemFileAssociations", "CLSID", "TYPELIB", "APPLICATIONS", "MIME", "INTERFACE", "APPID", "INSTALLER", "Local Settings"];

    private void ScanFileAssociations(RegCategory cat)
    {
        foreach (var root in new[] { "", "SystemFileAssociations", "Wow6432Node" })
            ScanAssociationRoot(cat, root);
    }

    private void ScanAssociationRoot(RegCategory cat, string root)
    {
        const RegHive cr = RegHive.ClassesRoot;
        var prefix = root.Length == 0 ? "" : root + "\\";
        using var rl = Lease(cr, root);
        if (rl.Key == null) return;
        foreach (var name in SubKeys(rl.Key))
        {
            _ct.ThrowIfCancellationRequested();
            var path = prefix + name;
            using var hl = Lease(cr, path);
            var hk = hl.Key;
            if (hk == null) continue;

            // DefaultIcon of every class
            if (KeyPresent(cr, path + @"\DefaultIcon"))
            {
                var icon = ReadExpanded(cr, path + @"\DefaultIcon", "");
                if (icon.Length == 0)
                    Report(cat, cr, path + @"\DefaultIcon", "", "", RegTargetKind.Key, "EmptyIconPath", "Empty default icon path", 0xA171);
                else if (!Paths.Exists(icon, commandMode: true))
                    Report(cat, cr, path + @"\DefaultIcon", "", icon, RegTargetKind.Key, "InvalidIconPath", "Invalid icon path in %1", 0xA279);
            }

            if (name.StartsWith('.'))
            {
                ShellCheck(cat, cr, path);
                OpenWithCheck(cat, path);
                var progid = ReadExpanded(cr, path, "");
                bool typed = ValueState(hk, "Content Type") == 2 || ValueState(hk, "PerceivedType") == 2;
                if (progid.Length == 0)
                {
                    if (typed) continue;
                    if (SubKeys(hk).Length != 0) GenericScan(cat, cr, path, false, true);
                    else if (!root.Equals("SystemFileAssociations", StringComparison.OrdinalIgnoreCase))
                        Report(cat, cr, path, "", "", RegTargetKind.Key, "UselessFileExtension", "Useless File Extension", 0xA16F);
                }
                else if (KeyPresent(cr, prefix + progid))
                {
                    var cmdKey = prefix + progid + @"\shell\open\command";
                    if (!KeyPresent(cr, cmdKey)) continue;
                    var cmd = ReadExpanded(cr, cmdKey, "");
                    if (cmd.Length != 0 && !Paths.DataIsValid(cmd))
                        Report(cat, cr, path, progid, cmd, RegTargetKind.Key, "InvalidPath", "Invalid Path: %1", InvalidPathId);
                    else if (SubKeys(hk).Length > 0) GenericScan(cat, cr, path, false, true);
                }
                else
                {
                    int n = SubKeys(hk).Length;
                    if (n >= 1 && !IsEmptyTree(hk, "", false, true, true))
                    {
                        if (n == 1 && KeyPresent(cr, path + @"\PersistentHandler"))
                            Report(cat, cr, path, "", progid, RegTargetKind.Value, "MissingProgid", "File association pointing to missing key : %1",
                                0xA278, raw: RawOf(hk, ""));
                        GenericScan(cat, cr, path, false, true);
                    }
                    else if (!typed)
                        Report(cat, cr, path, "", progid, RegTargetKind.Key, "MissingProgid", "File association pointing to missing key : %1", 0xA278);
                }
                continue;
            }

            if (name.Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase) || root.Equals("SystemFileAssociations", StringComparison.OrdinalIgnoreCase)
                || name.Equals("VirtualStore", StringComparison.OrdinalIgnoreCase))
                continue;
            ClsidCheck(cat, path);
            if (IsEmptyTree(hk, "", true, false, true))
            {
                Report(cat, cr, path, "", "", RegTargetKind.Key, "EmptyKey", "Empty Key", 0xA170);
                continue;
            }
            if (ClassPrefixesSkipped.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            ShellCheck(cat, cr, path);
            GenericScan(cat, cr, path, false, true);
        }
    }

    /// <summary>A class's CLSID value that names a class that is not registered.</summary>
    private void ClsidCheck(RegCategory cat, string path)
    {
        using var l = Lease(RegHive.ClassesRoot, path + @"\CLSID");
        if (l.Key == null) return;
        var v = ReadString(l.Key, "");
        if (v == null) return;
        var id = v.StartsWith('{') ? v : "{" + v + "}";
        if (!ClsidExists(id))
            Report(cat, RegHive.ClassesRoot, path + @"\CLSID", "", v, RegTargetKind.Key, "InvalidEntry", "Invalid Entry: %1", 0xA174);
    }

    /// <summary>
    /// Returns true when it reported something: empty shell/verb keys, empty or invalid commands, DropTarget classes that are not registered; the
    /// shellex part runs only with the Shell Extensions category, Thorough mode and expert mode.
    /// </summary>
    private bool ShellCheck(RegCategory cat, RegHive hive, string path)
    {
        if (hive is not (RegHive.ClassesRoot or RegHive.LocalMachine)) return false;
        int before = _findings.Count + _ignored.Count;
        var shell = path + @"\shell";
        using (var sl = Lease(hive, shell))
        {
            if (sl.Key != null)
            {
                if (IsEmptyTree(sl.Key, "", false, true, true) && ValueState(sl.Key, "FriendlyCache") == 1)
                    Report(cat, hive, shell, "", "", RegTargetKind.Key, "EmptyShellKey", "Empty shell key", 0xA18C);
                else
                    foreach (var verb in SubKeys(sl.Key))
                    {
                        var vk = shell + "\\" + verb;
                        // Deliberate safety deviation: the original ignores the verb's own values here, so modern verbs
                        // defined only by values (ExplorerCommandHandler, MUIVerb…) looked empty; they are kept.
                        if (IsEmptyTree(sl.Key, verb, true, true, true))
                        {
                            Report(cat, hive, vk, "", "", RegTargetKind.Key, "EmptyShellKey", "Empty shell key", 0xA18C);
                            continue;
                        }
                        var cmdKey = vk + @"\command";
                        if (KeyPresent(hive, cmdKey))
                        {
                            var cmd = ReadExpanded(hive, cmdKey, "");
                            if (cmd.Length == 0 && ValueState(hive, cmdKey, "command") != 2 && ValueState(hive, cmdKey, "DelegateExecute") != 2)
                                Report(cat, hive, cmdKey, "", "", RegTargetKind.Key, "EmptyCommandKey", "Empty command key", 0xA18D);
                            if (cmd.Length != 0 && !Paths.DataIsValid(cmd) && ValueCount(hive, cmdKey) == 1)
                                Report(cat, hive, vk, "", cmd, RegTargetKind.Key, "InvalidCommand", "Invalid Command");
                        }
                        var drop = ReadExpanded(hive, vk + @"\DropTarget", "clsid");
                        if (drop.Length != 0 && !ClsidExists(drop))
                            Report(cat, hive, vk + @"\DropTarget", "", "", RegTargetKind.Key, "InvalidDropTarget", "Invalid DropTarget Clsid");
                    }
            }
        }
        if ((_o.Categories & RegCategory.ShellExtensions) != 0 && _o.Thorough && _o.Expert) ShellexCheck(cat, hive, path + @"\shellex");
        return _findings.Count + _ignored.Count != before;
    }

    private static readonly HashSet<string> ShellexKept = new(StringComparer.OrdinalIgnoreCase)
        { "ExtShellFolderViews", "MayChangeDefaultMenu", "{BB2E617C-0920-11d1-9A0B-00C04FC2D6C1}", "NoRecentDocs" };

    private void ShellexCheck(RegCategory cat, RegHive hive, string shellex)
    {
        using var l = Lease(hive, shellex);
        if (l.Key == null) return;
        foreach (var s in SubKeys(l.Key))
        {
            if (ShellexKept.Contains(s)) continue;
            var sp = shellex + "\\" + s;
            if (SubKeyCount(hive, sp) == 0)
            {
                var d = ReadExpanded(hive, sp, "");
                if (d.Length == 0 && IsEmptyTree(l.Key, s, true, false, true) && !s.StartsWith('{'))
                    Report(RegCategory.ShellExtensions, hive, sp, "", "", RegTargetKind.Key, "EmptyShellexKey", "Empty shellex key");
                if (d.StartsWith('{') && !ClsidExists(d))
                    Report(RegCategory.ShellExtensions, hive, sp, "", "", RegTargetKind.Key, "InvalidShellext", "Invalid Shellext");
                continue;
            }
            using var sl = Lease(hive, sp);
            if (sl.Key == null) continue;
            foreach (var t in SubKeys(sl.Key))
            {
                var tp = sp + "\\" + t;
                var d = ReadExpanded(hive, tp, "");
                if (d.Length == 0 && t.StartsWith('{') && !ClsidExists(t))
                    Report(RegCategory.ShellExtensions, hive, tp, "", "", RegTargetKind.Key, "InvalidShellexKey", "Invalid shellex key");
                if (d.StartsWith('{') && !ClsidExists(d) && !ClsidExists(t))
                    Report(RegCategory.ShellExtensions, hive, tp, "", "", RegTargetKind.Key, "InvalidShellext", "Invalid Shellext");
            }
        }
    }

    /// <summary>Open-with application keys whose program is gone, Open-with ProgIDs that are not registered.</summary>
    private void OpenWithCheck(RegCategory cat, string path)
    {
        const RegHive cr = RegHive.ClassesRoot;
        using (var l = Lease(cr, path + @"\openwithlist"))
            if (l.Key != null)
                foreach (var app in SubKeys(l.Key))
                    if (SubKeyCount(cr, path + @"\openwithlist\" + app) == 0 && !Paths.Exists(app, commandMode: true)
                        && IsEmptyTree(l.Key, app, true, true, true))
                        Report(cat, cr, path + @"\OpenWithList\" + app, "", "", RegTargetKind.Key, "UselessOpenWithApp", "Useless Open-with App Key");
        using var p = Lease(cr, path + @"\OpenWithProgids");
        if (p.Key == null) return;
        foreach (var progid in ValueNames(p.Key))
            if (progid.Length > 0 && !KeyPresent(cr, progid))
                Report(cat, cr, path + @"\OpenWithProgids", progid, "", RegTargetKind.Value, "InvalidProgid", "Invalid Progid", 0,
                    IgnoreUnlessMruExempt, raw: RawOf(p.Key, progid));
    }
}
