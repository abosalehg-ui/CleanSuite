using Microsoft.Win32;

namespace CleanSuite.Junk;

/// <summary>System facts the junk engine reads. Abstracted so tests never depend on the real machine.</summary>
public interface ISystemEnvironment
{
    string GetTempPath();
    string GetWindowsDirectory();
    /// <summary>Expanded value of an environment variable stored in the registry (HKLM Session Manager or HKCU\Environment).</summary>
    string? GetRegistryEnvironment(bool machine, string name);
    IEnumerable<string> GetUserProfileDirectories();
    DateTime UtcNow { get; }
}

public sealed class WindowsEnvironment : ISystemEnvironment
{
    public string GetTempPath()
    {
        var p = Path.GetTempPath();
        try { return Path.GetFullPath(p); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

    public string GetWindowsDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public string? GetRegistryEnvironment(bool machine, string name)
    {
        using var key = machine
            ? Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", false)
            : Registry.CurrentUser.OpenSubKey("Environment", false);
        if (key?.GetValue(name) is not string v || v.Length == 0) return null;
        return Environment.ExpandEnvironmentVariables(v);
    }

    public IEnumerable<string> GetUserProfileDirectories()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", false);
        if (key == null) yield break;
        foreach (var sid in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(sid, false);
            if (sub?.GetValue("ProfileImagePath") is string p && p.Length > 0)
                yield return Environment.ExpandEnvironmentVariables(p);
        }
    }

    public DateTime UtcNow => DateTime.UtcNow;
}
