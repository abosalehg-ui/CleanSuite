using System.Runtime.InteropServices;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.RegistryClean;

/// <summary>Helpers shared by the category scanners.</summary>
public sealed partial class RegistryScanner
{
    /// <summary>Applies the ignore words to Explorer findings unless MRU/History lists are exempt (the original passes ctx+0xC50 == 0).</summary>
    private bool IgnoreUnlessMruExempt => !_o.NoIgnoreForMru;

    /// <summary>
    /// The key below <paramref name="parent"/> is an empty tree. With <paramref name="checkValues"/> it must
    /// have no values (or, with <paramref name="allowDefaultOnly"/>, only an unnamed default value with no data). Subkeys are
    /// allowed only with <paramref name="recurse"/>, and then each must itself be empty (values checked, default not allowed).
    /// </summary>
    internal static bool IsEmptyTree(RegistryKey parent, string name, bool checkValues, bool allowDefaultOnly, bool recurse)
    {
        RegistryKey? k;
        try { k = name.Length == 0 ? parent : parent.OpenSubKey(name, writable: false); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        if (k == null) return false;
        try
        {
            if (checkValues)
            {
                if (!QueryInfo(k, out int values, out int maxName, out int maxData)) return false;
                if (!(values == 0 || (values == 1 && allowDefaultOnly && maxName == 0 && maxData == 0))) return false;
            }
            var subs = SubKeys(k);
            if (subs.Length > 0 && !recurse) return false;
            foreach (var s in subs)
                if (!IsEmptyTree(k, s, true, false, true)) return false;
            return true;
        }
        finally { if (!ReferenceEquals(k, parent)) k.Dispose(); }
    }

    private static bool QueryInfo(RegistryKey k, out int values, out int maxValueName, out int maxValueData)
    {
        values = maxValueName = maxValueData = 0;
        try
        {
            int err = RegQueryInfoKeyW(k.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out _, out _, out _, out uint v, out uint mn, out uint md, IntPtr.Zero, IntPtr.Zero);
            if (err != 0) return false;
            values = (int)v; maxValueName = (int)mn; maxValueData = (int)md;
            return true;
        }
        catch (ObjectDisposedException) { return false; }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKeyW(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, IntPtr cls, IntPtr clsLen, IntPtr reserved,
        out uint subKeys, out uint maxSubKeyLen, out uint maxClassLen, out uint values, out uint maxValueNameLen, out uint maxValueLen,
        IntPtr securityDescriptor, IntPtr lastWrite);

    /// <summary>0 = key cannot be opened, 1 = value absent, 2 = value exists.</summary>
    internal int ValueState(RegHive hive, string key, string value)
    {
        using var l = Lease(hive, key);
        if (l.Key == null) return 0;
        return ValueNames(l.Key).Contains(value, StringComparer.OrdinalIgnoreCase) ? 2 : 1;
    }

    private static int ValueState(RegistryKey k, string value) => ValueNames(k).Contains(value, StringComparer.OrdinalIgnoreCase) ? 2 : 1;

    /// <summary>The key opens (an access-denied key also counts as existing).</summary>
    internal bool KeyPresent(RegHive hive, string path)
    {
        try
        {
            using var k = path.Length == 0 ? null : _roots.Root(hive).OpenSubKey(path, writable: false);
            return path.Length == 0 || k != null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return true; }
        catch (IOException) { return false; }
    }

    /// <summary>HKCR\CLSID\{x} or HKCR\Wow6432Node\CLSID\{x} exists.</summary>
    internal bool ClsidExists(string clsid) =>
        KeyPresent(RegHive.ClassesRoot, @"CLSID\" + clsid) || KeyPresent(RegHive.ClassesRoot, @"Wow6432Node\CLSID\" + clsid);

    /// <summary>Number of subkeys, 0 when the key cannot be opened.</summary>
    private int SubKeyCount(RegHive hive, string path)
    {
        using var l = Lease(hive, path);
        try { return l.Key?.SubKeyCount ?? 0; }
        catch (IOException) { return 0; }
    }

    /// <summary>REG_SZ or REG_EXPAND_SZ (expanded) value, "" when missing or of another type.</summary>
    private string ReadExpanded(RegHive hive, string key, string value)
    {
        using var l = Lease(hive, key);
        if (l.Key == null) return "";
        object? v;
        try { v = l.Key.GetValue(value, null); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ""; }
        return v as string ?? "";
    }

    private int ValueCount(RegHive hive, string key)
    {
        using var l = Lease(hive, key);
        try { return l.Key?.ValueCount ?? 0; }
        catch (IOException) { return 0; }
    }
}
