using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.Startup;

/// <summary>
/// Notices entries that were disabled and have enabled themselves again ("Program(s) that were previously disabled, have
/// re-enabled themselves again"). Sources: CleanSuite's own record of entries it disabled
/// (%LocalAppData%\CleanSuite\startup-monitor.json) and, read-only, the original's HKCU\…\Ace Utilities\Auto\Md5 values
/// ("[&lt;user&gt;\]&lt;md5&gt;-&lt;type&gt;" = DWORD, 0 = disabled; MD5 of the lower-cased ANSI name+command, Phase 2 report §5.4).
/// </summary>
public sealed class StartupMonitor
{
    private readonly string _file;
    private readonly IRegistryRoots _roots;
    private readonly HashSet<string> _disabled;

    public StartupMonitor(string? file = null, IRegistryRoots? roots = null)
    {
        _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSuite", "startup-monitor.json");
        _roots = roots ?? WindowsRegistryRoots.Instance;
        _disabled = Load();
    }

    private HashSet<string> Load()
    {
        try
        {
            if (File.Exists(_file))
                return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_disabled.OrderBy(s => s).ToList()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Record(StartupEntry e, string tool)
    {
        if (tool == "startup-disable") _disabled.Add(e.Identity);
        else _disabled.Remove(e.Identity);                                   // enabled or deleted on purpose
        Save();
    }

    public IReadOnlyList<StartupEntry> Annotate(IReadOnlyList<StartupEntry> entries)
    {
        var ace = AceDisabledHashes();
        return entries.Select(e => e.Enabled && (_disabled.Contains(e.Identity) || ace.Contains(AceKey(e))) ? e with { ReEnabledItself = true } : e).ToList();
    }

    /// <summary>"&lt;md5&gt;-&lt;type&gt;" (without the user prefix) of every entry the original recorded as disabled.</summary>
    private HashSet<string> AceDisabledHashes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = _roots.Root(RegHive.CurrentUser).OpenSubKey(@"Software\Acelogix\Ace Utilities\Auto\Md5", writable: false);
            if (k == null) return set;
            foreach (var n in k.GetValueNames())
                if (k.GetValue(n) is int v && v == 0) set.Add(n.Contains('\\') ? n[(n.LastIndexOf('\\') + 1)..] : n);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
        return set;
    }

    public static string AceKey(StartupEntry e) => AceMd5(e.Name, e.Command) + "-" + e.Location.Type;

    public static string AceMd5(string name, string command)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(0).GetBytes((name + command).ToLowerInvariant());
        return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    }
}
