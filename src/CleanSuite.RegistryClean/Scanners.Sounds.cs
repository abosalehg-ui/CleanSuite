using System.Runtime.InteropServices;
using CleanSuite.Safety;

namespace CleanSuite.RegistryClean;

/// <summary>Sounds (0x20000) and History Lists (0x40000).</summary>
public sealed partial class RegistryScanner
{
    /// <summary>The Windows folder (replaceable in tests).</summary>
    public string WindowsFolder { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    // ---------------------------------------------------------------- 0x20000 Sounds

    private void ScanSounds(RegCategory cat)
    {
        ScanEventSounds(cat);
        ScanAudioPropertyStore(cat);
        ScanAutoplay(cat);
    }

    /// <summary>
    /// AppEvents\Schemes\Apps\&lt;app&gt;\&lt;event&gt;\.current: a sound given without a drive (relative to Windows\Media)
    /// that does not exist is an "Invalid Sound". Absolute paths are left to the generic scan of User Entries.
    /// </summary>
    private void ScanEventSounds(RegCategory cat)
    {
        const string apps = @"AppEvents\Schemes\Apps";
        using var a = Lease(RegHive.CurrentUser, apps);
        if (a.Key == null) return;
        var media = WindowsFolder.TrimEnd('\\') + @"\Media\";
        foreach (var app in SubKeys(a.Key))
        {
            _ct.ThrowIfCancellationRequested();
            using var al = Lease(RegHive.CurrentUser, apps + "\\" + app);
            if (al.Key == null) continue;
            foreach (var ev in SubKeys(al.Key))
            {
                var key = apps + "\\" + app + "\\" + ev + @"\.current";
                var sound = ReadExpanded(RegHive.CurrentUser, key, "");
                if (sound.Length == 0 || sound.Contains(@":\", StringComparison.Ordinal)) continue;
                if (!Paths.Exists(media + sound, commandMode: true))
                    Report(cat, RegHive.CurrentUser, key, "", sound, RegTargetKind.Key, "InvalidSound", "Invalid Sound");
            }
        }
    }

    /// <summary>Per-application audio settings whose program ("{id}|\Device\HarddiskVolumeN\…%b…") no longer exists ("Invalid File").</summary>
    private void ScanAudioPropertyStore(RegCategory cat)
    {
        const string store = @"Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore";
        using var s = Lease(RegHive.CurrentUser, store);
        if (s.Key == null) return;
        IReadOnlyDictionary<string, string>? devices = null;               // read once, on the first entry that needs it
        foreach (var sub in SubKeys(s.Key))
        {
            var v = ReadString(RegHive.CurrentUser, store + "\\" + sub, "");
            if (string.IsNullOrEmpty(v)) continue;
            int bar = v.IndexOf('|');
            if (bar < 0) continue;
            v = v[(bar + 1)..];
            int b = v.IndexOf("%b", StringComparison.Ordinal);
            if (b < 0) continue;
            v = v[..b];
            var path = DevicePaths.ToDosPath(v, devices ??= DevicePaths.Current());
            if (path != null && !Paths.Exists(path, commandMode: true))
                Report(cat, RegHive.CurrentUser, store + "\\" + sub, "", path, RegTargetKind.Key, "InvalidFile", "Invalid File");
        }
    }

    /// <summary>Microsoft, eHome and MagicUSBCable handlers are never checked.</summary>
    private static bool AutoplaySkipped(string name) =>
        name.StartsWith("MS", StringComparison.OrdinalIgnoreCase) || name.StartsWith("EHOME", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("MagicUSBCable", StringComparison.OrdinalIgnoreCase);

    /// <summary>AutoPlay: event entries naming an unregistered handler, and handlers whose ProgID/CLSID/DropTarget is gone.</summary>
    private void ScanAutoplay(RegCategory cat)
    {
        const RegHive lm = RegHive.LocalMachine, cr = RegHive.ClassesRoot;
        const string handlers = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers\Handlers";
        const string events = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers\EventHandlers";
        using (var e = Lease(lm, events))
            if (e.Key != null)
                foreach (var ev in SubKeys(e.Key))
                {
                    using var el = Lease(lm, events + "\\" + ev);
                    if (el.Key == null) continue;
                    foreach (var h in ValueNames(el.Key))
                        if (h.Length > 0 && !AutoplaySkipped(h) && !KeyPresent(lm, handlers + "\\" + h))
                            Report(cat, lm, events + "\\" + ev, h, "", RegTargetKind.Value, "InvalidAutoplayEventHandler",
                                "Invalid Autoplay Event Handler", raw: RawOf(el.Key, h));
                }

        using var hl = Lease(lm, handlers);
        if (hl.Key == null) return;
        foreach (var h in SubKeys(hl.Key))
        {
            if (AutoplaySkipped(h)) continue;
            var hk = handlers + "\\" + h;
            var invoke = ReadExpanded(lm, hk, "InvokeProgID");
            var progid = ReadExpanded(lm, hk, "ProgID");
            var clsid = ReadExpanded(lm, hk, "CLSID");
            var provider = ReadExpanded(lm, hk, "Provider");
            bool bad;
            if (invoke.Length == 0)
            {
                if (provider.Length == 0 && progid.Length > 0) provider = ReadExpanded(cr, progid, "");
                if (clsid.Length == 0 && progid.Length > 0) clsid = ReadExpanded(cr, progid + @"\CLSID", "");
                bad = clsid.Length != 0 && !ClsidExists(clsid);
                if (progid.Length != 0 && !KeyPresent(cr, progid)) bad = true;
                if (clsid.Length == 0 && progid.Length == 0) bad = true;
            }
            else
            {
                var verb = ReadExpanded(lm, hk, "InvokeVerb");
                bad = false;
                if (verb.Length != 0)
                {
                    var drop = ReadExpanded(cr, invoke + @"\shell\" + verb + @"\DropTarget", "CLSID");
                    bad = drop.Length != 0 && !ClsidExists(drop);
                }
                if (progid.Length == 0 && clsid.Length == 0 && !KeyPresent(cr, invoke)) bad = true;
            }
            if (bad) Report(cat, lm, hk, "", provider, RegTargetKind.Key, "InvalidAutoplayHandler", "Invalid Autoplay Handler");
        }
    }

    // ---------------------------------------------------------------- 0x40000 History Lists

    private const string UserAssist = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\";

    internal static string Rot13(string s) => string.Create(s.Length, s, (span, src) =>
    {
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            span[i] = c is >= 'A' and <= 'Z' ? (char)('A' + (c - 'A' + 13) % 26)
                    : c is >= 'a' and <= 'z' ? (char)('a' + (c - 'a' + 13) % 26) : c;
        }
    });

    /// <summary>UserAssist entries (ROT13 names) pointing at programs or shortcuts that no longer exist ("Invalid History").</summary>
    private void ScanHistoryLists(RegCategory cat)
    {
        var programs = SpecialFolder(Environment.SpecialFolder.Programs).TrimEnd('\\');
        var common = SpecialFolder(Environment.SpecialFolder.CommonPrograms).TrimEnd('\\');
        if (programs.Length == 0) return;
        const RegHive cu = RegHive.CurrentUser;

        var classic = UserAssist + @"{75048700-EF1F-11D0-9888-006097DEACF9}\Count";
        using (var l = Lease(cu, classic))
            if (l.Key != null)
                foreach (var name in ValueNames(l.Key))
                {
                    _ct.ThrowIfCancellationRequested();
                    if (name.StartsWith(@"HRZR_EHACVQY:%pfvqy2%\", StringComparison.Ordinal))
                    {
                        var rel = Rot13(name[@"HRZR_EHACVQY:%pfvqy2%\".Length..]);
                        if (!Paths.FileProbe.HasAttributes(programs + "\\" + rel) && !Paths.FileProbe.HasAttributes(common + "\\" + rel))
                            Report(cat, cu, classic, name, rel, RegTargetKind.Value, "InvalidHistory", "Invalid History", raw: RawOf(l.Key, name));
                    }
                    else if (name.StartsWith("HRZR_EHACNGU:", StringComparison.Ordinal))
                    {
                        var path = Rot13(name["HRZR_EHACNGU:".Length..]);
                        if (path.Length > 3 && path[1] == ':' && path[2] == '\\' && !Paths.FileProbe.HasAttributes(path))
                            Report(cat, cu, classic, name, path, RegTargetKind.Value, "InvalidHistory", "Invalid History", raw: RawOf(l.Key, name));
                    }
                }

        var modern = UserAssist + @"{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count";
        using var m = Lease(cu, modern);
        if (m.Key == null) return;
        foreach (var name in ValueNames(m.Key))
        {
            var d = Rot13(name);
            if (d.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) continue;      // packaged application id
            int open = d.IndexOf('{'), close = d.IndexOf('}');
            if (open >= 0 && close > open && Guid.TryParse(d[..(close + 1)], out var folderId) && KnownFolderPath(folderId) is { } folder)
                d = folder + d[(close + 1)..];
            d = d.Trim('"', ' ');
            if (d.Length > 3 && d[1] == ':' && d[2] == '\\' && !Paths.FileProbe.HasAttributes(d))
                Report(cat, cu, modern, name, d, RegTargetKind.Value, "InvalidHistory", "Invalid History", raw: RawOf(m.Key, name));
        }
    }

    /// <summary>Known-folder resolution for "{GUID}\rest" names (replaceable in tests).</summary>
    public Func<Guid, string?> KnownFolderPath { get; init; } = DefaultKnownFolder;

    private static string? DefaultKnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
