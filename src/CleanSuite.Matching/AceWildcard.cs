namespace CleanSuite.Matching;

/// <summary>
/// Wildcard matcher compatible with the original junk/ignore matcher:
/// classic iterative wildcmp over upper-cased strings.
/// '*' matches any run of characters INCLUDING '\'; '?' matches exactly one character;
/// no Windows special-casing (so "*.*" requires a literal dot).
/// Called for every file against every pattern of a scan, so it allocates nothing for ordinary names: both strings are
/// upper-cased into stack buffers (same invariant casing as string.ToUpperInvariant).
/// </summary>
public static class AceWildcard
{
    private const int StackLimit = 512;

    public static bool IsMatch(string text, string pattern)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pattern);
        Span<char> s = text.Length <= StackLimit ? stackalloc char[text.Length] : new char[text.Length];
        Span<char> w = pattern.Length <= StackLimit ? stackalloc char[pattern.Length] : new char[pattern.Length];
        text.AsSpan().ToUpperInvariant(s);
        pattern.AsSpan().ToUpperInvariant(w);
        return Match(s, w);
    }

    private static bool Match(ReadOnlySpan<char> s, ReadOnlySpan<char> w)
    {
        int si = 0, wi = 0;
        // Leading part before the first '*'.
        while (si < s.Length && wi < w.Length && w[wi] != '*')
        {
            if (w[wi] != s[si] && w[wi] != '?') return false;
            si++; wi++;
        }
        if (wi >= w.Length) return si >= s.Length;

        int mp = -1, cp = -1;
        while (si < s.Length)
        {
            if (wi < w.Length && w[wi] == '*')
            {
                wi++;
                if (wi >= w.Length) return true;
                mp = wi;
                cp = si + 1;
            }
            else if (wi < w.Length && (w[wi] == s[si] || w[wi] == '?'))
            {
                wi++; si++;
            }
            else
            {
                if (mp < 0) return false;
                wi = mp;
                si = cp++;
            }
        }
        while (wi < w.Length && w[wi] == '*') wi++;
        return wi >= w.Length;
    }
}
