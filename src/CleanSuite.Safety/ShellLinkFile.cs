using System.Runtime.InteropServices;
using System.Text;

namespace CleanSuite.Safety;

/// <summary>What a .lnk points to, read without resolving it (no IShellLink::Resolve, so no search and no UI).</summary>
public sealed record LinkTarget(string Target, string Arguments, bool Advertised, bool EnvironmentExpanded);

/// <summary>
/// Reads, creates and retargets .lnk files through IShellLinkW / IPersistFile.
/// MSI advertised shortcuts are read with MsiGetShortcutTarget + MsiGetComponentPath first.
/// </summary>
public static class ShellLinkFile
{
    private const uint SLGP_RAWPATH = 4;
    private const int INSTALLSTATE_LOCAL = 3;

    /// <summary>The target and arguments (environment variables expanded), or null when the file cannot be loaded.</summary>
    public static LinkTarget? Read(string lnkPath)
    {
        var msi = MsiTarget(lnkPath);
        if (msi != null) return new LinkTarget(msi, "", true, false);
        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sl = (IShellLinkW)link;
            var sb = new StringBuilder(1024);
            sl.GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            var raw = sb.ToString();
            sb.Clear();
            sl.GetArguments(sb, sb.Capacity);
            var rawArgs = sb.ToString();
            var target = Environment.ExpandEnvironmentVariables(raw);
            var args = Environment.ExpandEnvironmentVariables(rawArgs);
            return new LinkTarget(target, args, false, target.Length != raw.Length || args.Length != rawArgs.Length);
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or FileNotFoundException or InvalidCastException or IOException)
        {
            return null;
        }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    /// <summary>Installed path of an MSI advertised shortcut, or null (not advertised, or the component is not installed locally).</summary>
    private static string? MsiTarget(string lnkPath)
    {
        try
        {
            var product = new StringBuilder(39);
            var component = new StringBuilder(39);
            if (MsiGetShortcutTargetW(lnkPath, product, null, component) != 0) return null;
            var path = new StringBuilder(260);
            uint len = (uint)path.Capacity;
            return MsiGetComponentPathW(product.ToString(), component.ToString(), path, ref len) == INSTALLSTATE_LOCAL
                ? path.ToString() : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>Creates a shortcut (used for retarget tests and by the app's own tests; nothing else writes .lnk files).</summary>
    public static void Create(string lnkPath, string target, string arguments = "", string workingDirectory = "")
    {
        object link = new ShellLinkCoClass();
        try
        {
            var sl = (IShellLinkW)link;
            sl.SetPath(target);
            if (arguments.Length > 0) sl.SetArguments(arguments);
            if (workingDirectory.Length > 0) sl.SetWorkingDirectory(workingDirectory);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    /// <summary>
    /// Points an existing shortcut at a new target and saves it. Like the original, every other field (arguments,
    /// working folder, icon, description, hotkey, show command) is kept as it was.
    /// </summary>
    public static void Retarget(string lnkPath, string newTarget)
    {
        object link = new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(lnkPath, 2 /* STGM_READWRITE */);
            var sl = (IShellLinkW)link;
            // Read every field first and put it back after SetPath, as the original's wrapper does.
            var sb = new StringBuilder(1024);
            sl.GetArguments(sb, sb.Capacity); var args = sb.ToString(); sb.Clear();
            sl.GetWorkingDirectory(sb, sb.Capacity); var dir = sb.ToString(); sb.Clear();
            sl.GetDescription(sb, sb.Capacity); var desc = sb.ToString(); sb.Clear();
            sl.GetIconLocation(sb, sb.Capacity, out int icon); var iconPath = sb.ToString();
            sl.GetHotkey(out short hotkey);
            sl.GetShowCmd(out int show);
            sl.SetPath(newTarget);
            sl.SetArguments(args);
            sl.SetWorkingDirectory(dir);
            sl.SetDescription(desc);
            if (iconPath.Length > 0) sl.SetIconLocation(iconPath, icon);
            sl.SetHotkey(hotkey);
            sl.SetShowCmd(show);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetShortcutTargetW(string target, StringBuilder product, StringBuilder? feature, StringBuilder component);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetComponentPathW(string product, string component, StringBuilder path, ref uint len);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid cls);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? file, [MarshalAs(UnmanagedType.Bool)] bool remember);
    }
}
