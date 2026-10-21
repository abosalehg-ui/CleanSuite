using System.Runtime.InteropServices;
using System.Text;

namespace CleanSuite.Safety;

/// <summary>
/// NT device paths to drive paths through the DOS device names (QueryDosDevice over A:–Z:). The original falls back to guessing
/// the drive letter from the volume number ('B' + N); that guess can name the wrong volume and is not used here. Shared by
/// the registry scanner (sound and AutoPlay handlers) and the Registry Compactor (hive list).
/// </summary>
public static class DevicePaths
{
    /// <param name="devices">"\Device\HarddiskVolume3" → "C:".</param>
    public static string? ToDosPath(string ntPath, IReadOnlyDictionary<string, string> devices)
    {
        if (ntPath.StartsWith(@"\??\", StringComparison.Ordinal)) ntPath = ntPath[4..];
        if (ntPath.Length >= 3 && char.IsAsciiLetter(ntPath[0]) && ntPath[1] == ':' && ntPath[2] == '\\') return ntPath;
        string? best = null;
        foreach (var device in devices.Keys)
            if (ntPath.Length > device.Length && ntPath[device.Length] == '\\' && ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase)
                && (best == null || device.Length > best.Length))
                best = device;
        return best == null ? null : devices[best] + ntPath[best.Length..];
    }

    /// <summary>The device behind every drive letter of this session: "\Device\HarddiskVolume3" → "C:".</summary>
    public static IReadOnlyDictionary<string, string> Current()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new StringBuilder(1024);
        for (char c = 'A'; c <= 'Z'; c++)
        {
            var drive = c + ":";
            if (QueryDosDeviceW(drive, buffer, buffer.Capacity) == 0) continue;
            map.TryAdd(buffer.ToString(), drive);                  // first name only; the call returns a list separated by NUL
        }
        return map;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string device, StringBuilder target, int max);
}
