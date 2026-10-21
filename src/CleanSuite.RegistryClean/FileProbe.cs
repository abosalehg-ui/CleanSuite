using System.Runtime.InteropServices;
using CleanSuite.Safety;

namespace CleanSuite.RegistryClean;

/// <summary>
/// The file-system questions the path validator asks. Abstracted so tests can simulate removable and CD drives; the
/// production implementation calls the same Win32 functions as the original (FindFirstFileW, GetFileAttributesW,
/// SearchPathW, GetDriveTypeW). Nothing is ever written.
/// </summary>
public interface IFileProbe
{
    /// <summary>FindFirstFileW succeeds (wildcards allowed, as in the original).</summary>
    bool FindFirst(string path);
    /// <summary>GetFileAttributesW does not fail.</summary>
    bool HasAttributes(string path);
    /// <summary>SearchPathW finds the bare file name on the default search path.</summary>
    bool SearchPath(string fileName);
    DriveKind DriveType(string root);
}

public sealed class WindowsFileProbe : IFileProbe
{
    public static WindowsFileProbe Instance { get; } = new();

    /// <summary>
    /// FindFirstFileW succeeds. Deliberate safety deviations (see <see cref="PathProbe"/>): access denied and a drive that
    /// cannot be read (BitLocker-locked, not ready) count as existing, so such entries are never reported.
    /// </summary>
    public bool FindFirst(string path) => PathProbe.FindFirst(path);

    public bool HasAttributes(string path) => PathProbe.HasAttributes(path);

    public bool SearchPath(string fileName)
    {
        var buf = new char[260];
        return SearchPathW(null, fileName, null, (uint)buf.Length, buf, IntPtr.Zero) != 0;
    }

    public DriveKind DriveType(string root) => PathProbe.DriveType(root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SearchPathW(string? path, string fileName, string? ext, uint bufLen, [Out] char[] buffer, IntPtr filePart);
}
