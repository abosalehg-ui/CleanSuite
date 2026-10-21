namespace CleanSuite.History;

/// <summary>INIFILE|&lt;file&gt;|&lt;section&gt;[|&lt;key&gt;]: delete a key, or the whole section when no key is given.</summary>
public sealed record IniTarget(string File, string Section, string? Key);

/// <summary>
/// One path rule of the original rule engine, as used by the History items and plug-in File# entries:
/// <c>&lt;dir&gt;\&lt;mask&gt;[|&lt;mask2&gt;…] [/X:"&lt;pat&gt;|&lt;pat&gt;"] [/S] [/E] [/R] [/C]</c> or <c>INIFILE|&lt;ini&gt;|&lt;section&gt;[|&lt;key&gt;]</c>.
/// </summary>
public sealed class PathRule
{
    public required string Raw { get; init; }
    /// <summary>Expanded rule text without flags (used as the folder by /E).</summary>
    public required string Expanded { get; init; }
    /// <summary>Part before the last '\' (may contain wildcards).</summary>
    public required string Directory { get; init; }
    /// <summary>Masks after the last '\', split on '|', "*.*" turned into "*".</summary>
    public required IReadOnlyList<string> Masks { get; init; }
    public IReadOnlyList<string> Exclusions { get; init; } = Array.Empty<string>();
    /// <summary>/S: include subfolders.</summary>
    public bool Recurse { get; init; }
    /// <summary>/E: empty the folder completely (refused for ?:\Windows and ?:\Windows\system32).</summary>
    public bool EmptyFolder { get; init; }
    /// <summary>/R: remove the folder itself when it is empty afterwards.</summary>
    public bool RemoveFolderIfEmpty { get; init; }
    /// <summary>/C: remove empty subfolders afterwards.</summary>
    public bool RemoveEmptySubfolders { get; init; }
    public IniTarget? Ini { get; init; }

    /// <summary>One mask without wildcards in a folder without wildcards: a single file is deleted directly.</summary>
    public bool IsSingleFile =>
        Ini == null && !EmptyFolder && Masks.Count == 1 && !HasWildcard(Masks[0]) && !HasWildcard(Directory);

    internal static bool HasWildcard(string s) => s.IndexOfAny(new[] { '*', '?' }) >= 0;

    /// <summary>
    /// Parses and expands a rule. Returns null when the rule is invalid (no '\') or uses a token that cannot be resolved,
    /// e.g. %PATH% of a plug-in whose install folder is unknown; such rules are skipped, as in the original.
    /// </summary>
    public static PathRule? Parse(string rule, TokenExpander tokens)
    {
        var text = rule.Trim();

        // /X:"a|b" — cut out first (it may contain '/'), then split on '|'.
        var exclusions = new List<string>();
        int x = text.IndexOf("/X", StringComparison.Ordinal);
        if (x >= 0)
        {
            int close = x + 4 <= text.Length ? text.IndexOf('"', x + 4) : -1;
            var token = close >= 0 ? text[x..(close + 1)] : text[x..];
            text = text.Replace(token, "", StringComparison.Ordinal);
            var body = token[2..].TrimStart('/', ':').Trim('"');
            exclusions.AddRange(body.Split('|').Select(e => e.Trim()).Where(e => e.Length > 0));
        }

        // Flags are upper case and may appear anywhere (wcsstr); every occurrence is removed.
        bool Take(ref string s, string flag)
        {
            if (!s.Contains(flag, StringComparison.Ordinal)) return false;
            s = s.Replace(flag, "", StringComparison.Ordinal);
            return true;
        }
        bool recurse = Take(ref text, "/S");
        bool empty = Take(ref text, "/E");
        bool removeIfEmpty = Take(ref text, "/R");
        bool removeEmptySubs = Take(ref text, "/C");
        text = text.Trim();

        var expanded = tokens.Expand(text);
        if (expanded == null) return null;
        exclusions = exclusions.Select(e => tokens.Expand(e) ?? e).ToList();

        int slash = expanded.LastIndexOf('\\');
        if (slash < 0) return null;
        var dir = expanded[..slash];
        var masks = expanded[(slash + 1)..].Replace("*.*", "*", StringComparison.Ordinal)
            .Split('|').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();

        IniTarget? ini = null;
        if (expanded.StartsWith("INIFILE", StringComparison.OrdinalIgnoreCase))
        {
            var parts = expanded.Split('|');
            if (parts.Length < 3) return null;
            var file = parts[1].Trim();
            var section = parts[2].Trim();
            var key = parts.Length > 3 && parts[3].Trim().Length > 0 ? parts[3].Trim() : null;
            if (file.Length == 0 || section.Length == 0) return null;
            ini = new IniTarget(file, section, key);
        }

        return new PathRule
        {
            Raw = rule, Expanded = expanded, Directory = dir, Masks = masks, Exclusions = exclusions,
            Recurse = recurse, EmptyFolder = empty, RemoveFolderIfEmpty = removeIfEmpty, RemoveEmptySubfolders = removeEmptySubs,
            Ini = ini,
        };
    }
}
