using CleanSuite.Junk;
using Microsoft.Win32;

namespace CleanSuite.Compat;

/// <summary>Read-only view of a registry key's values (lets tests inject data without touching the real registry).</summary>
public interface IKeyReader
{
    /// <summary>Values of the subkey, or null if the key does not exist.</summary>
    IReadOnlyDictionary<string, object>? ReadValues(string subKey);
}

/// <summary>Reads HKCU\Software\Acelogix\Ace Utilities with KEY_READ only. Never writes.</summary>
public sealed class AceRegistryReader : IKeyReader
{
    public const string Root = @"Software\Acelogix\Ace Utilities";

    public IReadOnlyDictionary<string, object>? ReadValues(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Root + "\\" + subKey, writable: false);
        if (key == null) return null;
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in key.GetValueNames())
            if (key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } v) d[n] = v;
        return d;
    }
}

/// <summary>Junk settings as stored by the original.</summary>
public sealed class AceJunkSettings
{
    public uint SettingsJnk { get; init; } = JunkDefaults.SettingsJnk;
    public bool ExcludeFilesModifiedLately { get; init; }
    public RemovalMode DelJunkToBin { get; init; } = RemovalMode.RecycleBin;
    public string JunkbakFolder { get; init; } = "";
    /// <summary>Bitmask, bit n = drive 'A'+n; null = "all fixed drives" (original default).</summary>
    public uint? DriveJunkFiles { get; init; }
    public string ActiveProfile { get; init; } = "Default";
    public IReadOnlyDictionary<string, string> Profiles { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> DefaultExt { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> UserExt { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Exclusions { get; init; } = JunkDefaults.Exclusions;
    public bool FoundInRegistry { get; init; }

    public JunkOptions ToOptions(bool expert = false) => JunkOptions.FromSettingsJnk(SettingsJnk, ExcludeFilesModifiedLately, expert);

    public PatternSet ToPatternSet()
    {
        if (!Profiles.TryGetValue(ActiveProfile, out var list) && !Profiles.TryGetValue("Default", out list))
            return PatternSet.BuildDefault();
        var masks = list.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var set = PatternSet.Build(masks,
            DefaultExt.Count > 0 ? DefaultExt : JunkDefaults.Patterns.ToDictionary(p => p.Mask, p => p.Category, StringComparer.OrdinalIgnoreCase),
            UserExt);
        return set.Patterns.Count > 0 ? set : PatternSet.BuildDefault();
    }

    public static IEnumerable<string> DrivesFromMask(uint mask)
    {
        for (int i = 0; i < 26; i++) if ((mask & (1u << i)) != 0) yield return $"{(char)('A' + i)}:";
    }

    public static AceJunkSettings Load(IKeyReader reader)
    {
        var junk = reader.ReadValues("Junk");
        var profiles = reader.ReadValues(@"Junk\Profiles");
        var defExt = reader.ReadValues(@"Junk\DefaultExt");
        var userExt = reader.ReadValues(@"Junk\UserExt");
        var exclude = reader.ReadValues(@"Junk\Exclude");

        static Dictionary<string, string> Strings(IReadOnlyDictionary<string, object>? d) =>
            d == null ? new(StringComparer.OrdinalIgnoreCase)
                      : d.Where(kv => kv.Key.Length > 0 && kv.Value is string)
                         .ToDictionary(kv => kv.Key, kv => (string)kv.Value, StringComparer.OrdinalIgnoreCase);

        uint? Dword(string name) => junk != null && junk.TryGetValue(name, out var v) && v is int i ? unchecked((uint)i) : null;
        string? Str(string name) => junk != null && junk.TryGetValue(name, out var v) ? v as string : null;

        string active = "Default";
        if (profiles != null && profiles.TryGetValue("", out var dv) && dv is string s && s.Length > 0) active = s;

        IReadOnlyList<string> exclusions = JunkDefaults.Exclusions;
        if (exclude != null)
            exclusions = exclude.Where(kv => kv.Value is int i && i != 0 && !kv.Key.StartsWith('\\'))
                                .Select(kv => kv.Key).ToList();

        var delMode = Dword("DelJunkToBin") ?? 1;
        return new AceJunkSettings
        {
            FoundInRegistry = junk != null || profiles != null,
            SettingsJnk = Dword("SettingsJnk") ?? JunkDefaults.SettingsJnk,
            ExcludeFilesModifiedLately = (Dword("ExcludeFilesModifiedLately") ?? 0) != 0,
            DelJunkToBin = delMode <= 2 ? (RemovalMode)delMode : RemovalMode.Permanent,
            JunkbakFolder = Str("JunkbakFolder") ?? "",
            DriveJunkFiles = Dword("DriveJunkFiles"),
            ActiveProfile = active,
            Profiles = Strings(profiles).Where(kv => kv.Key.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            DefaultExt = Strings(defExt),
            UserExt = Strings(userExt),
            Exclusions = exclusions,
        };
    }
}
