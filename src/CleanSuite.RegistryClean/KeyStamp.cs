using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CleanSuite.RegistryClean;

/// <summary>
/// Fingerprint of a key: number of subkeys and values and the last write time. Taken when a key finding is reported and
/// compared right before the key is deleted, so a key that changed after the scan (a program reinstalled in between) is
/// left alone, like changed values already are.
/// </summary>
public static class KeyStamp
{
    /// <summary>"subkeys|values|lastWrite", or null when the key cannot be opened.</summary>
    public static string? Of(RegistryKey root, string path)
    {
        try
        {
            using var k = path.Length == 0 ? null : root.OpenSubKey(path, writable: false);
            var key = path.Length == 0 ? root : k;
            if (key == null) return null;
            return RegQueryInfoKeyW(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var subKeys, IntPtr.Zero, IntPtr.Zero,
                       out var values, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var lastWrite) == 0
                ? $"{subKeys}|{values}|{lastWrite}"
                : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKeyW(SafeRegistryHandle key, IntPtr cls, IntPtr clsLen, IntPtr reserved, out uint subKeys,
        IntPtr maxSubKeyLen, IntPtr maxClassLen, out uint values, IntPtr maxValueNameLen, IntPtr maxValueLen, IntPtr securityDescriptor,
        out long lastWriteTime);
}
