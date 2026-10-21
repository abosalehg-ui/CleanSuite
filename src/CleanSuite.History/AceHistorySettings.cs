using CleanSuite.Safety;

namespace CleanSuite.History;

/// <summary>
/// The original's Erase History selection, read with read access only from HKCU\Software\Acelogix\Ace Utilities:
/// History\HistorySettings2 (one byte per Windows/Start-menu item), History\firefox (FirefoxItems bits),
/// History\Users\Cookies or Cache (all users' profiles) and the Cookies whitelist (value names). Nothing is written.
/// </summary>
public sealed class AceHistorySettings
{
    public const string Root = @"Software\Acelogix\Ace Utilities";

    public required IReadOnlySet<int> WindowsItems { get; init; }
    public FirefoxItems Firefox { get; init; } = FirefoxItems.Default;
    public bool AllUsers { get; init; }
    public IReadOnlyList<string> CookieWhitelist { get; init; } = Array.Empty<string>();
    public bool FoundInRegistry { get; init; }

    public static AceHistorySettings Read(IRegistryRoots? roots = null)
    {
        var hkcu = (roots ?? WindowsRegistryRoots.Instance).Root(RegHive.CurrentUser);
        try
        {
            using var h = hkcu.OpenSubKey(Root + @"\History", writable: false);
            using var users = hkcu.OpenSubKey(Root + @"\History\Users", writable: false);
            using var cookies = hkcu.OpenSubKey(Root + @"\Cookies", writable: false);
            var whitelist = cookies?.GetValueNames().Where(n => n.Length > 0).ToList() ?? new List<string>();
            bool Flag(string name) => users?.GetValue(name) is int v && v != 0;
            return new AceHistorySettings
            {
                WindowsItems = WindowsHistoryCatalog.FromHistorySettings2(h?.GetValue("HistorySettings2") as byte[]),
                Firefox = h?.GetValue("firefox") is int f ? (FirefoxItems)unchecked((uint)f) : FirefoxItems.Default,
                AllUsers = Flag("Cookies") || Flag("Cache"),
                CookieWhitelist = whitelist,
                FoundInRegistry = h != null,
            };
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new AceHistorySettings { WindowsItems = WindowsHistoryCatalog.FromHistorySettings2(null) };
        }
    }
}
