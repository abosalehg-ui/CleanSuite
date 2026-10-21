using CleanSuite.Safety;

namespace CleanSuite.RegistryClean;

/// <summary>Options of the path validator (Settings\SettingsReg bits 0 and 1, Thorough, expert mode).</summary>
public sealed record PathValidatorOptions
{
    /// <summary>SettingsReg bit0 "Include removable media": paths on removable drives are checked instead of assumed valid.</summary>
    public bool IncludeRemovable { get; init; }
    /// <summary>SettingsReg bit1 "Include CD drives".</summary>
    public bool IncludeCd { get; init; }
    public bool Thorough { get; init; }
    /// <summary>The original's expert mode (only changes the verdict for paths on drives that do not exist).</summary>
    public bool Expert { get; init; }
}

/// <summary>
/// Folders the original uses: path prefixes it rewrites before GetFileAttributesW (WOW64 file-system views), and the
/// folders tried for a bare "name.ext" that SearchPathW did not find.
/// </summary>
public sealed record SystemFolders(IReadOnlyList<(string From, string To)> Redirects, IReadOnlyList<string> BareNameDirs)
{
    public static SystemFolders Empty { get; } = new(Array.Empty<(string, string)>(), Array.Empty<string>());
}

/// <summary>
/// The original's path judgement, step for step:
/// <list type="bullet">
/// <item><see cref="DataIsValid"/> (find up to 9 "X:\" paths inside a text).</item>
/// <item><see cref="Exists"/> (one path, with the original's trimming and fallbacks).</item>
/// <item><see cref="FileExists"/> (FindFirstFile, SearchPath, WOW64 views).</item>
/// </list>
/// Everything is read-only.
/// </summary>
public sealed class PathValidator
{
    private readonly PathValidatorOptions _o;
    private readonly IFileProbe _fs;
    private readonly SystemFolders _folders;
    private HashSet<string> _knownCommands;

    /// <summary>Replaces the names accepted in command mode (ctx+0xC80 and +0xCA8, built at scan start).</summary>
    public void SetKnownCommands(IEnumerable<string> names) => _knownCommands = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>The file-system probe the validator uses.</summary>
    public IFileProbe FileProbe => _fs;

    /// <param name="knownCommands">Names accepted as valid in command mode without being found on disk (ctx+0xC80/+0xCA8).</param>
    public PathValidator(PathValidatorOptions options, IFileProbe? fs = null, SystemFolders? folders = null,
        IEnumerable<string>? knownCommands = null)
    {
        _o = options;
        _fs = fs ?? WindowsFileProbe.Instance;
        _folders = folders ?? SystemFolders.Empty;
        _knownCommands = new HashSet<string>(knownCommands ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the text holds no "X:\" path, or when one of its first candidates exists. In command mode
    /// a text without "/", "?" or "@" is checked as one command line instead.
    /// </summary>
    public bool DataIsValid(string text, bool commandMode = false)
    {
        if (commandMode && text.IndexOfAny(['/', '?', '@']) < 0) return Exists(text, true);
        var u = text.ToUpperInvariant();
        bool tried = false;
        int pos = 0;
        for (int i = 0; i < 9; i++)
        {
            int idx = pos <= u.Length ? u.IndexOf(@":\", pos, StringComparison.Ordinal) : -1;
            if (idx < 0) return !tried;
            if (tried && idx < 5) return true;
            if (idx > 0 && u[idx - 1] is >= 'A' and <= 'Z')
            {
                if (Exists(u[(idx - 1)..], false)) return true;
                tried = true;
            }
            pos = idx + 1;
        }
        return true;
    }

    /// <summary>Does the path (or command line, in command mode) point at something that exists?</summary>
    public bool Exists(string path, bool commandMode = false)
    {
        if (path.Contains(@":\\", StringComparison.Ordinal)) return true;
        var p = path.ToUpperInvariant();
        var original = p;
        bool cmd = commandMode;
        if (commandMode)
        {
            if (p.IndexOfAny(['/', '?', '@']) >= 0) cmd = false;
            if (p.StartsWith('@')) p = p[1..];
        }
        p = p.Trim('"');

        if (p.StartsWith(@"A:\", StringComparison.Ordinal) && _fs.DriveType(@"A:\") == DriveKind.Removable) return !_o.IncludeRemovable;
        if (FileExists(p)) return true;

        int cut = p.IndexOfAny(['/', '*', '?', '|', '<', '>', '"']);
        if (cut >= 0) p = p[..cut].TrimEnd();
        if (FileExists(p)) return true;

        foreach (var c in ";,%^-")
        {
            int i = p.LastIndexOf(c);                                     // wcsrchr in the original
            if (i >= 0 && FileExists(p[..i].TrimEnd())) return true;
        }
        cut = p.IndexOfAny([';', ',', '%', '^']);
        if (cut >= 0) p = p[..cut].TrimEnd();
        p = p.TrimStart('"', ' ', '\t').TrimEnd('"', '\\', ' ', '\t');

        if (!cmd)
        {
            bool drivePath = p.Length > 3 && p.Substring(1, 2) == @":\" && !p.Contains(@"\ ", StringComparison.Ordinal)
                             && !p.Contains(@" \", StringComparison.Ordinal) && p.IndexOf(':', 2) < 0;
            if (!drivePath) return true;
            switch (_fs.DriveType(p[..3]))
            {
                case DriveKind.Removable: return !_o.IncludeRemovable;
                case DriveKind.Fixed: break;                                   // checked below
                case DriveKind.Remote: return true;
                case DriveKind.CdRom: return !_o.IncludeCd;
                default: return !(_o.Expert && _o.Thorough && _o.IncludeRemovable);
            }
        }
        return ExistsDeep(p, original, cmd);
    }

    /// <summary>The deeper check: fixed drives and command lines.</summary>
    private bool ExistsDeep(string p, string original, bool cmd)
    {
        if (p.Length == 0 || FileExists(p)) return true;
        foreach (var ext in new[] { ".EXE", ".DLL", ".OCX" })
        {
            int i = p.IndexOf(ext, StringComparison.Ordinal);
            if (i >= 0) { p = p[..(i + 4)]; break; }
        }
        int dash = p.IndexOfAny(['-', '!']);
        if (dash >= 0 && FileExists(p[..dash].TrimEnd())) return true;
        if (cmd && _knownCommands.Contains(p)) return true;
        if (FileExists(p)) return true;

        if (!p.Contains('.') && (FileExists(p + ".exe") || FileExists(p + ".lnk"))) return true;

        var name = original[(original.LastIndexOf('\\') + 1)..];
        int odash = original.LastIndexOf('-');
        if (odash >= 0 && !name.Contains('.'))
        {
            p = original[..odash].TrimEnd();
            if (FileExists(p) || FileExists(p + ".exe")) return true;
        }
        return p.Contains("RUNDLL", StringComparison.Ordinal) || p.Contains("SHELL32", StringComparison.Ordinal)
               || p.Contains("REGSVR", StringComparison.Ordinal);
    }

    /// <summary>File or folder exists (after removing trailing "\" and spaces).</summary>
    public bool FileExists(string path)
    {
        var p = path.TrimEnd('\\', ' ');
        if (p.Length == 0) return false;
        if (_fs.FindFirst(p)) return true;
        if (p.Contains(@":\", StringComparison.Ordinal))
        {
            foreach (var (from, to) in _folders.Redirects)
                if (from.Length > 0 && p.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                    return _fs.HasAttributes(to + p[from.Length..]);
            return false;
        }
        var name = p[(p.LastIndexOf('\\') + 1)..];
        if (!name.Contains('.')) return false;
        if (_fs.SearchPath(name)) return true;
        foreach (var dir in _folders.BareNameDirs)
            if (_fs.HasAttributes(Path.Join(dir, name))) return true;
        return false;
    }
}
