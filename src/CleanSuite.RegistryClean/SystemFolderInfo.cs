namespace CleanSuite.RegistryClean;

/// <summary>
/// The folders the original uses: an absolute path under System32, Program Files or Common
/// Files that does not exist is retried in its 32-bit twin (SysWOW64, Program Files (x86), Common Files (x86)) — the
/// registry of a 64-bit system holds paths written by 32-bit programs. A bare "name.ext" not found by SearchPath is tried
/// in the same three 32-bit folders.
/// </summary>
public static class SystemFolderInfo
{
    public static SystemFolders Current { get; } = Build();

    /// <summary>The same WOW64 table the shortcut and uninstall scanners use (<see cref="CleanSuite.Safety.PathProbe"/>).</summary>
    private static SystemFolders Build()
    {
        if (Environment.GetFolderPath(Environment.SpecialFolder.Windows).Length == 0) return SystemFolders.Empty;
        return new SystemFolders(CleanSuite.Safety.PathProbe.Wow64Redirects, CleanSuite.Safety.PathProbe.Wow64Folders);
    }
}
