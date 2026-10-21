namespace CleanSuite.History;

/// <summary>
/// Cookie whitelist ("Cookie Manager", HKCU\…\Ace Utilities\Cookies\&lt;entry&gt;). Hosts, origins and storage keys from
/// every browser are normalised to ".host" and kept when an entry is a case-insensitive prefix
/// of that form, e.g. ".google." keeps ".google.de".
/// Extension (on by default, keeps more, never deletes more): an entry ".example.com" also keeps its subdomains
/// (".mail.example.com"), which the original's pure prefix test does not.
/// </summary>
public sealed class CookieWhitelist
{
    private readonly List<string> _entries;

    public CookieWhitelist(IEnumerable<string> entries, bool matchSubdomains = true)
    {
        _entries = entries.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        MatchSubdomains = matchSubdomains;
    }

    public static CookieWhitelist Empty { get; } = new(Array.Empty<string>());
    public IReadOnlyList<string> Entries => _entries;
    public bool MatchSubdomains { get; }

    /// <summary>True when the host/origin/key must be kept. Values that cannot be normalised are kept (never deleted).</summary>
    public bool Keeps(string raw)
    {
        var n = Normalize(raw);
        if (n == null) return true;
        foreach (var e in _entries)
        {
            if (n.StartsWith(e, StringComparison.OrdinalIgnoreCase)) return true;
            if (MatchSubdomains && e.StartsWith('.') && !e.EndsWith('.') && n.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// ".host" form of a cookie host, origin or storage key, or null when the value is not a web origin
    /// (internal pages such as "chrome…", "moz-…", "opera_…", "&lt;&lt;…&gt;&gt;", "about:" scopes).
    /// Handles Chromium "https_host_0" folder names, Firefox "https+++host" storage folders, reversed webappsstore
    /// scopes ("moc.elpmaxe.:http:80"), partition keys and first-party domains.
    /// </summary>
    public static string? Normalize(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith("<<", StringComparison.Ordinal) && s.EndsWith(">>", StringComparison.Ordinal)) return null;
        foreach (var internalPrefix in new[] { "chrome", "moz-", "opera_" })
            if (s.StartsWith(internalPrefix, StringComparison.OrdinalIgnoreCase)) return null;

        // Firefox storage folder names "http+++host" / "https+++host".
        if (s.StartsWith("http+++", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        else if (s.StartsWith("https+++", StringComparison.OrdinalIgnoreCase)) s = s[8..];

        // "…^partitionKey=%28http%2Csite%29" → "site", then drop "^firstPartyDomain…".
        const string partition = "^partitionKey=%28http%2C";
        int p = s.IndexOf(partition, StringComparison.Ordinal);
        if (p >= 0)
        {
            s = s[(p + partition.Length)..];
            int end = s.IndexOf("%29", StringComparison.Ordinal);
            if (end >= 0) s = s[..end];
        }
        int fpd = s.IndexOf("^firstPartyDomain", StringComparison.Ordinal);
        if (fpd >= 0) s = s[..fpd];

        if (s.StartsWith("DOMStore:", StringComparison.OrdinalIgnoreCase)) s = s[9..];
        if (s.StartsWith("http@", StringComparison.OrdinalIgnoreCase)) s = s[5..];
        int at = s.IndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        int sf = s.IndexOf(".safeframe.", StringComparison.Ordinal);
        if (sf >= 0) s = s[(sf + ".safeframe.".Length)..];

        // Reversed Firefox DOM-storage scope "moc.elgoog.www.:http:80".
        int rev = s.IndexOf(".:http", StringComparison.Ordinal);
        if (rev >= 0)
        {
            var arr = s[..rev].ToCharArray();
            Array.Reverse(arr);
            s = new string(arr);
        }
        else if (s.Contains(".:about", StringComparison.Ordinal)) return null;

        // Chromium storage folder "https_www.example.com_0" → "www.example.com".
        if (s.StartsWith("http_", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https_", StringComparison.OrdinalIgnoreCase))
        {
            s = s[(s.IndexOf('_') + 1)..];
            int u = s.IndexOf('_');
            if (u >= 0) s = s[..u];
            s = s.Replace('_', '.');
        }

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Host.Length > 0 && s.Contains("://", StringComparison.Ordinal))
            s = uri.Host;
        foreach (var scheme in new[] { "http://", "https://" })
            if (s.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) s = s[scheme.Length..];
        s = s.TrimEnd('/').Trim('.');
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];                                        // port of an origin
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        s = s.Trim('.');
        return s.Length == 0 ? null : "." + s.ToLowerInvariant();
    }
}
