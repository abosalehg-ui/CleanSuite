using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CleanSuite.Startup;

public enum EntryState
{
    Enabled,
    /// <summary>Disabled through StartupApproved (Windows 8+, Task Manager, the original on Windows 8+).</summary>
    DisabledApproved,
    /// <summary>Disabled the old way: value moved to the sibling key "&lt;RunKey&gt;-" (the original, Autoruns-style).</summary>
    DisabledLegacy,
}

/// <param name="Name">Registry value name, or file name in a startup folder.</param>
/// <param name="Command">Command line (registry), or the full path of the startup-folder file.</param>
public sealed record StartupEntry(StartupLocation Location, string Name, string Command, EntryState State, DateTime? DisabledSinceUtc)
{
    public bool Enabled => State == EntryState.Enabled;

    /// <summary>Executable or document the entry starts (shortcuts resolved), or null when it cannot be determined.</summary>
    public string? TargetPath { get; init; }
    public bool TargetExists => TargetPath != null && (File.Exists(TargetPath) || Directory.Exists(TargetPath));
    public string? Company { get; init; }
    public string? Description { get; init; }
    public bool IsMicrosoft => Company?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
    /// <summary>The entry was disabled earlier (by CleanSuite or the original) and is enabled again now.</summary>
    public bool ReEnabledItself { get; init; }

    /// <summary>Stable identity for monitoring: location id + name.</summary>
    public string Identity => Location.Id + "|" + Name;
}

/// <summary>Finds the program a startup command runs.</summary>
public static class CommandLineTarget
{
    public static string? Resolve(string command)
    {
        var cmd = Environment.ExpandEnvironmentVariables(command.Trim());
        if (cmd.Length == 0) return null;
        string exe, args;
        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            exe = end > 0 ? cmd[1..end] : cmd[1..];
            args = end > 0 ? cmd[(end + 1)..].Trim() : "";
        }
        else
        {
            // Unquoted paths with spaces: grow token by token until an existing file is found (CreateProcess behaviour).
            var parts = cmd.Split(' ');
            exe = parts[0];
            args = string.Join(' ', parts.Skip(1));
            for (int i = 1; i <= parts.Length; i++)
            {
                var candidate = string.Join(' ', parts.Take(i));
                if (Existing(candidate) is { } found) { exe = found; args = string.Join(' ', parts.Skip(i)); break; }
            }
        }
        var path = Existing(exe) ?? (Path.IsPathRooted(exe) ? exe : null);
        if (path == null) return null;
        // rundll32 <dll>,<entry>: the DLL is what actually starts.
        if (Path.GetFileName(path).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) && args.Length > 0)
        {
            var dll = args.Trim('"');
            int comma = dll.IndexOf(',');
            if (comma > 0) dll = dll[..comma].Trim().Trim('"');
            return Existing(dll) ?? dll;
        }
        return path;
    }

    private static string? Existing(string p)
    {
        p = p.Trim().Trim('"');
        if (p.Length == 0) return null;
        // CreateProcess appends ".exe" when the module name has no extension.
        foreach (var c in new[] { Path.HasExtension(p) ? p : p + ".exe" })
        {
            if (Path.IsPathRooted(c)) { if (File.Exists(c)) return Path.GetFullPath(c); continue; }
            foreach (var dir in SearchDirs())
            {
                var full = Path.Combine(dir, c);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private static IEnumerable<string> SearchDirs()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return d.Trim();
    }

    /// <summary>Company and description from the file's version resource (null when unavailable).</summary>
    public static (string? Company, string? Description) VersionInfo(string? path)
    {
        if (path == null || !File.Exists(path)) return (null, null);
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return (string.IsNullOrWhiteSpace(v.CompanyName) ? null : v.CompanyName.Trim(),
                    string.IsNullOrWhiteSpace(v.FileDescription) ? null : v.FileDescription.Trim());
        }
        catch (FileNotFoundException) { return (null, null); }
    }
}
