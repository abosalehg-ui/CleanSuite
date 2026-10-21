using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using CleanSuite.Safety;
using Microsoft.Win32.SafeHandles;

namespace CleanSuite.RegistryCompact;

/// <summary>The hives Windows has loaded, from HKLM\SYSTEM\CurrentControlSet\Control\hivelist (read only).</summary>
public sealed class WindowsHiveSource : IHiveSource
{
    public IReadOnlyList<HiveListEntry> Entries()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\hivelist");
        if (key == null) return Array.Empty<HiveListEntry>();
        return key.GetValueNames().Where(n => n.Length > 0)
            .Select(n => new HiveListEntry(n, key.GetValue(n, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? ""))
            .ToList();
    }

    public IReadOnlyDictionary<string, string> Devices() => DevicePaths.Current();
}

/// <summary>
/// RegSaveKeyEx with REG_LATEST_FORMAT (RegSaveKey for UsrClass.dat), the hive root opened with REG_OPTION_BACKUP_RESTORE, as
/// the original does. Backup rights are enabled first and checked: the original ignores ERROR_NOT_ALL_ASSIGNED.
/// </summary>
public sealed class WindowsHiveSaver : IHiveSaver
{
    private const int RegLatestFormat = 2, RegOptionBackupRestore = 4, KeyQueryValue = 1, ErrorNotAllAssigned = 1300;

    public IDisposable Prepare()
    {
        SetPrivilege("SeBackupPrivilege", enable: true);
        return new Disabler();
    }

    private sealed class Disabler : IDisposable
    {
        public void Dispose()
        {
            try { SetPrivilege("SeBackupPrivilege", enable: false); }
            catch (Exception e) when (e is Win32Exception or PrivilegeNotHeldException) { }
        }
    }

    public int Save(HiveCandidate hive, string file)
    {
        var root = new SafeRegistryHandle(hive.Root == HiveRoot.LocalMachine ? unchecked((nint)0x80000002) : unchecked((nint)0x80000003), false);
        int err = RegCreateKeyExW(root, hive.SubKey, 0, null, RegOptionBackupRestore, KeyQueryValue, IntPtr.Zero, out var key, out _);
        if (err != 0) return err;
        using (key)
        {
            if (hive.LatestFormat)
            {
                err = RegSaveKeyExW(key, file, IntPtr.Zero, RegLatestFormat);
                if (err == 0) return 0;
                if (File.Exists(file)) File.Delete(file);
            }
            return RegSaveKeyW(key, file, IntPtr.Zero);
        }
    }

    /// <summary>Enables or disables the privilege for this process; throws when the token does not have it (not elevated).</summary>
    public static void SetPrivilege(string name, bool enable)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8 /* ADJUST_PRIVILEGES | QUERY */, out var token)) throw new Win32Exception();
        using (token)
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) throw new Win32Exception();
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = enable ? 2 /* SE_PRIVILEGE_ENABLED */ : 0 };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned) throw new PrivilegeNotHeldException(name);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(SafeRegistryHandle key, string subKey, int reserved, string? cls, int options, int sam,
        IntPtr security, out SafeRegistryHandle result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSaveKeyExW(SafeRegistryHandle key, string file, IntPtr security, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSaveKeyW(SafeRegistryHandle key, string file, IntPtr security);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges state, int length,
        IntPtr previous, IntPtr returnLength);
}

/// <summary>
/// File size read like the original: the file is opened with no access rights and full sharing, which works for hive
/// files that are in use and whose folder a standard user cannot list.
/// </summary>
public static class FileSizes
{
    public static long? Of(string path)
    {
        using var h = CreateFileW(path, 0, 7 /* READ | WRITE | DELETE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x02000000 /* BACKUP_SEMANTICS */, IntPtr.Zero);
        return !h.IsInvalid && GetFileSizeEx(h, out var size) ? size : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(SafeFileHandle file, out long size);
}
