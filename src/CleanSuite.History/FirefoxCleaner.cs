namespace CleanSuite.History;

/// <summary>Bits of History\firefox. Default 0x15F.</summary>
[Flags]
public enum FirefoxItems : uint
{
    None = 0,
    History = 0x1,
    Cache = 0x2,
    Cookies = 0x4,
    Forms = 0x8,
    SearchHistory = 0x10,
    Passwords = 0x20,
    Downloads = 0x40,
    Compact = 0x100,
    Sessions = 0x1000,
    /// <summary>OfflineCache (application cache). Firefox removed it in version 84; not implemented.</summary>
    OfflineCache = 0x2000,
    SitePreferences = 0x10000,
    CrashReports = 0x20000,
    Default = 0x15F,
}

/// <summary>
/// Firefox-family cleaner (Firefox, Waterfox, Pale Moon, SeaMonkey, IceDragon, Cyberfox), with the original's behaviour:
/// profiles.ini discovery, per-profile files, the places.sqlite statements, and cookies and site storage filtered by the
/// whitelist.
/// Analysis only: returns the actions to review.
/// </summary>
public sealed class FirefoxCleaner
{
    /// <summary>Base folders under %APPDATA% (profiles.ini) and %LOCALAPPDATA% (cache profiles), in the original's order.</summary>
    public static readonly IReadOnlyList<(string Folder, string Exe)> Families = new[]
    {
        (@"Mozilla\Firefox", "firefox.exe"),
        (@"Mozilla\waterfox", "waterfox.exe"),
        (@"Moonchild Productions\Pale Moon", "palemoon.exe"),
        (@"Mozilla\SeaMonkey", "seamonkey.exe"),
        (@"Comodo\IceDragon", "icedragon.exe"),
        (@"8pecxstudios\Cyberfox", "cyberfox.exe"),
    };

    /// <summary>
    /// The 12 statements the original runs on places.sqlite (EXPIRE_NEVER = 4, EXPIRE_SESSION = 0), with one correction: the original tests
    /// SUBSTR(h.url,0,6), but SQLite counts from 1, so that reads five characters and never equals "place:" — saved queries
    /// ("place:" URLs, the bookmark toolbar's smart folders) were deleted. SUBSTR(h.url,1,6) is what Firefox itself uses.
    /// </summary>
    public static readonly IReadOnlyList<string> PlacesStatements = new[]
    {
        "UPDATE moz_places SET frecency = -MAX(visit_count, 1) WHERE id IN(SELECT h.id FROM moz_places h WHERE EXISTS (SELECT id FROM moz_bookmarks WHERE fk = h.id) OR EXISTS (SELECT id FROM moz_annos WHERE place_id = h.id AND expiration = 4));",
        "DELETE FROM moz_historyvisits;",
        "DELETE FROM moz_places WHERE id IN (SELECT h.id FROM moz_places h LEFT OUTER JOIN moz_historyvisits v ON h.id = v.place_id LEFT OUTER JOIN moz_bookmarks b ON h.id = b.fk WHERE v.id IS NULL AND b.id IS NULL AND SUBSTR(h.url,1,6) <> 'place:' );",
        "DELETE FROM moz_annos WHERE expiration = 0;",
        "DELETE FROM moz_annos WHERE id IN (SELECT a.id FROM moz_annos a LEFT OUTER JOIN moz_places p ON a.place_id = p.id LEFT OUTER JOIN moz_historyvisits v ON a.place_id = v.place_id WHERE p.id IS NULL OR (v.id IS NULL AND a.expiration != 4 ));",
        "INSERT OR REPLACE INTO moz_annos SELECT null, b.fk, t.anno_attribute_id, t.mime_type, t.content, t.flags, t.expiration, t.type, t.dateAdded, t.lastModified FROM moz_items_annos t JOIN moz_anno_attributes n ON t.anno_attribute_id = n.id JOIN moz_bookmarks b ON b.id = t.item_id WHERE n.name = 'URIProperties/characterSet' GROUP BY b.fk, t.anno_attribute_id;",
        "DELETE FROM moz_items_annos WHERE id IN (SELECT t.id FROM moz_items_annos t JOIN moz_anno_attributes n ON t.anno_attribute_id = n.id WHERE n.name = 'URIProperties/characterSet' );",
        "DELETE FROM moz_items_annos WHERE id IN (SELECT a.id FROM moz_items_annos a LEFT OUTER JOIN moz_bookmarks b ON a.item_id = b.id WHERE b.id IS NULL);",
        "DELETE FROM moz_anno_attributes WHERE id NOT IN (SELECT DISTINCT a.id FROM moz_anno_attributes a JOIN moz_annos b ON b.anno_attribute_id = a.id JOIN moz_places p ON b.place_id = p.id) AND id NOT IN (SELECT DISTINCT a.id FROM moz_anno_attributes a JOIN moz_items_annos c ON c.anno_attribute_id = a.id JOIN moz_bookmarks p ON c.item_id = p.id);",
        "DELETE FROM moz_inputhistory WHERE place_id IN (SELECT i.place_id FROM moz_inputhistory i LEFT OUTER JOIN moz_places h ON i.place_id = h.id WHERE h.id IS NULL);",
        "UPDATE moz_places SET frecency = 0 WHERE id IN (SELECT h.id FROM moz_places h LEFT OUTER JOIN moz_bookmarks b ON h.id = b.fk WHERE frecency < 0 AND (b.parent IN (SELECT annos.item_id FROM moz_anno_attributes attrs JOIN moz_items_annos annos ON attrs.id = annos.anno_attribute_id WHERE attrs.name = 'livemark/feedURI') AND visit_count = 0) OR SUBSTR(h.url,1,6) = 'place:');",
        "delete from moz_hosts;",
    };

    private readonly RulePlanner _files;
    private readonly IProcessProbe _processes;

    public FirefoxCleaner(RulePlanner files, IProcessProbe? processes = null)
    {
        _files = files;
        _processes = processes ?? new SystemProcessProbe();
    }

    public sealed record Profile(string Family, string Name, string Roaming, string? Local);

    /// <summary>profiles.ini discovery: Profile0, Profile1… until Path or Name is missing; relative paths also get a local twin.</summary>
    public static List<(string Family, string Exe, string CrashBase, List<Profile> Profiles)> Discover(UserFolders users)
    {
        var result = new List<(string, string, string, List<Profile>)>();
        foreach (var (folder, exe) in Families)
            foreach (var (roaming, local) in users.Users)
            {
                var baseRoaming = Path.Combine(roaming, folder);
                var baseLocal = Path.Combine(local, folder);
                var ini = Path.Combine(baseRoaming, "profiles.ini");
                var profiles = new List<Profile>();
                if (File.Exists(ini))
                {
                    var sections = IniFile.Read(ini);
                    for (int i = 0; ; i++)
                    {
                        if (!sections.TryGetValue($"Profile{i}", out var sec)) break;
                        if (!sec.TryGetValue("Path", out var path) || !sec.TryGetValue("Name", out var name)) break;
                        path = path.Replace('/', '\\').Trim('\\');
                        bool relative = sec.TryGetValue("IsRelative", out var rel) && int.TryParse(rel, out var r)
                            ? r != 0 : !Path.IsPathRooted(path);
                        var roamingPath = relative ? Path.Combine(baseRoaming, path) : path;
                        var localPath = relative ? Path.Combine(baseLocal, path) : null;
                        profiles.Add(new Profile(folder, name, roamingPath, localPath));
                    }
                }
                result.Add((folder, exe, baseRoaming, profiles));
            }
        return result;
    }

    public HistoryPlan Plan(FirefoxItems items, CookieWhitelist whitelist, UserFolders users, CancellationToken ct = default)
    {
        var plan = new HistoryPlan();
        var vacuum = new List<string>();
        foreach (var (family, exe, crashBase, profiles) in Discover(users))
        {
            ct.ThrowIfCancellationRequested();
            if (profiles.Count == 0 && !Directory.Exists(crashBase)) continue;
            if (_processes.IsRunning(exe))
            {
                plan.Notes.Add(new HistoryNote("firefox", "browser-running", $"{family}: {exe}"));
                continue;
            }
            foreach (var p in profiles)
            {
                PlanProfile(plan, p.Roaming, items, whitelist, vacuum, ct);
                if (p.Local != null && (items & FirefoxItems.Cache) != 0)
                    PlanProfile(plan, p.Local, FirefoxItems.Cache, whitelist, vacuum, ct);
            }
            if ((items & FirefoxItems.CrashReports) != 0)
                _files.AddEmptyFolder(plan.Actions, "firefox.crash", Path.Combine(crashBase, "Crash Reports"), removeFolder: false, ct);
        }
        if ((items & FirefoxItems.Compact) != 0)
            foreach (var db in vacuum.Distinct(StringComparer.OrdinalIgnoreCase))
                plan.Actions.Add(new VacuumAction { ItemKey = "firefox.compact", Target = db });
        return plan;
    }

    private void PlanProfile(HistoryPlan plan, string profile, FirefoxItems items, CookieWhitelist whitelist,
        List<string> vacuum, CancellationToken ct, int depth = 0)
    {
        if (!Directory.Exists(profile)) return;
        string P(string rel) => Path.Combine(profile, rel);
        void File_(string key, string rel) => _files.AddFileIfExists(plan.Actions, key, P(rel));
        void Compact(string rel) { if ((items & FirefoxItems.Compact) != 0 && File.Exists(P(rel))) vacuum.Add(P(rel)); }

        if ((items & FirefoxItems.Cache) != 0)
        {
            const string k = "firefox.cache";
            _files.AddEmptyFolder(plan.Actions, k, P("Cache"), removeFolder: true, ct);
            foreach (var dir in new[] { "thumbnails", "cache2", "jumpListCache", "minidumps", "startupCache" })
                _files.AddEmptyFolder(plan.Actions, k, P(dir), removeFolder: false, ct);
            if (File.Exists(P("places.sqlite-wal")))
                plan.Actions.Add(new WalCheckpointAction { ItemKey = k, Target = P("places.sqlite") });
            // A custom disk cache location (browser.cache.disk.parent_directory in user.js, else prefs.js) is cleaned too.
            if (depth == 0 && CacheParentDirectory(profile) is { } custom && !custom.Equals(profile, StringComparison.OrdinalIgnoreCase))
                PlanProfile(plan, custom, FirefoxItems.Cache, whitelist, vacuum, ct, depth + 1);
        }
        if (depth > 0) return;

        if ((items & FirefoxItems.Cookies) != 0)
        {
            const string k = "firefox.cookies";
            Compact("cookies.sqlite");
            Compact("webappsstore.sqlite");
            if (SqlDeleteKeysAction.Plan(k, P("cookies.sqlite"), "moz_cookies", "host", whitelist) is { } c) plan.Actions.Add(c);
            if (SqlDeleteKeysAction.Plan(k, P("webappsstore.sqlite"), "webappsstore2", "scope", whitelist) is { } w) plan.Actions.Add(w);
            foreach (var store in new[] { @"storage\persistent", @"storage\default", @"storage\permanent", @"storage\temporary" })
            {
                var root = P(store);
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> origins;
                try { origins = Directory.EnumerateDirectories(root).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var o in origins)
                {
                    var name = Path.GetFileName(o);
                    if (name.StartsWith("file", StringComparison.OrdinalIgnoreCase) || CookieWhitelist.Normalize(name) == null) continue;
                    if (!whitelist.Keeps(name)) _files.AddEmptyFolder(plan.Actions, k, o, removeFolder: true, ct);
                }
            }
            if (FilterLinesAction.PlanHostLines(k, P("SiteSecurityServiceState.txt"), whitelist) is { } hsts) plan.Actions.Add(hsts);
        }
        if ((items & FirefoxItems.SitePreferences) != 0)
        {
            File_("firefox.siteprefs", "content-prefs.sqlite");
            File_("firefox.siteprefs", "permissions.sqlite");
        }
        if ((items & FirefoxItems.History) != 0)
        {
            const string k = "firefox.history";
            File_(k, "history.dat");
            File_(k, "search-history.txt");
            Compact("places.sqlite");
            Compact("favicons.sqlite");
            if (File.Exists(P("places.sqlite")) && PlacesHasHistory(P("places.sqlite")))
                plan.Actions.Add(new SqlBatchAction
                {
                    ItemKey = k, Target = P("places.sqlite"), Statements = PlacesStatements,
                    Description = "history (bookmarks kept)",
                });
            if (SqlDeleteKeysAction.Plan(k, P("favicons.sqlite"), "moz_icons", "icon_url", whitelist) is { } icons) plan.Actions.Add(icons);
        }
        if ((items & FirefoxItems.Forms) != 0)
        {
            File_("firefox.forms", "formhistory.dat");
            File_("firefox.forms", "formhistory.sqlite");
        }
        if ((items & FirefoxItems.SearchHistory) != 0)
            File_("firefox.search", @"googletoolbardata\searchhistory.xml");
        if ((items & FirefoxItems.Passwords) != 0)
        {
            File_("firefox.passwords", "signons.txt");
            File_("firefox.passwords", "signons.sqlite");
            File_("firefox.passwords", "logins.json");
        }
        if ((items & FirefoxItems.Downloads) != 0)
        {
            File_("firefox.downloads", "downloads.rdf");
            File_("firefox.downloads", "downloads.sqlite");
        }
        if ((items & FirefoxItems.Sessions) != 0)
        {
            const string k = "firefox.sessions";
            File_(k, "sessionstore.js");
            File_(k, "sessionstore.bak");
            File_(k, "sessionCheckpoints.json");
            File_(k, "sessionstore.jsonlz4");          // current Firefox name of sessionstore.js (extension)
            _files.AddEmptyFolder(plan.Actions, k, P("sessionstore-backups"), removeFolder: false, ct);
        }
    }

    private static bool PlacesHasHistory(string db)
    {
        try
        {
            using var c = Sqlite.Open(db, readOnly: true);
            return Sqlite.Count(c, "moz_historyvisits") > 0 || Sqlite.Count(c, "moz_inputhistory") > 0;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return false; }
    }

    /// <summary>Value of user_pref("browser.cache.disk.parent_directory", "…") from user.js, else prefs.js.</summary>
    public static string? CacheParentDirectory(string profile)
    {
        const string pref = "user_pref(\"browser.cache.disk.parent_directory\"";
        foreach (var file in new[] { "user.js", "prefs.js" })
        {
            var path = Path.Combine(profile, file);
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var t = line.Trim();
                    if (!t.StartsWith(pref, StringComparison.Ordinal)) continue;
                    var v = t[pref.Length..].Trim('\\', ',', '"', ' ', '\t', ')', ';').Replace(@"\\", @"\");
                    return v.Length > 0 ? v : null;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}

/// <summary>Minimal INI reader (sections and key=value lines, case-insensitive, first value wins).</summary>
public static class IniFile
{
    public static Dictionary<string, Dictionary<string, string>> Read(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        IEnumerable<string> lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result; }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!result.TryGetValue(name, out current)) result[name] = current = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || current == null) continue;
            current.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        return result;
    }
}
