using CleanSuite.Matching;

namespace CleanSuite.Junk;

/// <summary>Active junk patterns and the classifier.</summary>
public sealed class PatternSet
{
    private readonly List<JunkPattern> _patterns = new();
    public IReadOnlyList<JunkPattern> Patterns => _patterns;

    /// <param name="masks">Masks of the active profile.</param>
    /// <param name="defaultExt">Junk\DefaultExt: mask -> category.</param>
    /// <param name="userExt">Junk\UserExt: mask -> category.</param>
    public static PatternSet Build(IEnumerable<string> masks,
        IReadOnlyDictionary<string, string> defaultExt, IReadOnlyDictionary<string, string> userExt)
    {
        var set = new PatternSet();
        foreach (var raw in masks)
        {
            var mask = raw.Trim().ToUpperInvariant();
            if (mask.Length == 0) continue;
            bool isDefault = true;
            if (!TryGet(defaultExt, mask, out var category))
            {
                isDefault = false;
                TryGet(userExt, mask, out category);
            }
            bool hasBackslash = mask.Contains('\\');
            int s = mask.IndexOf("/S", StringComparison.Ordinal);
            bool fullPath = s >= 0;
            if (fullPath) mask = mask[..s];
            set._patterns.Add(new JunkPattern(mask, category ?? "", isDefault, hasBackslash, fullPath));
        }
        return set;
    }

    /// <summary>Fallback used when no profile entry exists: built-in Default profile.</summary>
    public static PatternSet BuildDefault()
    {
        var ext = JunkDefaults.Patterns.ToDictionary(p => p.Mask, p => p.Category, StringComparer.OrdinalIgnoreCase);
        return Build(JunkDefaults.DefaultProfileMasks, ext, new Dictionary<string, string>());
    }

    /// <summary>Returns the matching pattern's category, or null.</summary>
    /// <param name="name">File name.</param>
    /// <param name="fullPath">Full path ("C:\dir\name").</param>
    public string? Classify(string name, string fullPath)
    {
        foreach (var p in _patterns)
        {
            if (!p.HasBackslash)
            {
                if (AceWildcard.IsMatch(name, p.Mask)) return p.Category;
            }
            else if (!p.FullPath)
            {
                // Name part matched against the file name, folder part against the parent folder.
                int mi = p.Mask.LastIndexOf('\\');
                int fi = fullPath.LastIndexOf('\\');
                if (mi < 0 || fi < 0) continue;
                if (AceWildcard.IsMatch(name, p.Mask[(mi + 1)..]) && AceWildcard.IsMatch(fullPath[..fi], p.Mask[..mi]))
                    return p.Category;
            }
            else if (AceWildcard.IsMatch(fullPath, p.Mask))
            {
                return p.Category;
            }
        }
        return null;
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> d, string key, out string? value)
    {
        foreach (var kv in d)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        value = null;
        return false;
    }
}
