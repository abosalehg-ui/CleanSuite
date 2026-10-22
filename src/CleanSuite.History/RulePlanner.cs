using CleanSuite.Safety;
using CleanSuite.Matching;

namespace CleanSuite.History;

/// <summary>
/// Turns path rules into actions (the analysis half of the original). Read-only: nothing is modified here.
/// Differences from the original, all on the safe side: analysis lists the exact files that match (the original only
/// reports "something to clean" when the folder is not empty), links and junctions are never followed, and an optional
/// never-delete list (Data\Ignore_Hst\plugins.ini: *.exe, *.dll) is applied to every file.
/// </summary>
public sealed class RulePlanner
{
    private readonly TokenExpander _tokens;
    private readonly IReadOnlyList<string> _neverDelete;

    /// <summary>Files no history rule deletes, whatever folder it empties: programs, libraries and drivers.</summary>
    public static readonly IReadOnlyList<string> DefaultNeverDelete = ["*.exe", "*.dll", "*.sys"];

    /// <param name="neverDelete">Masks of files that are never deleted (matched against the file name).</param>
    public RulePlanner(TokenExpander tokens, IReadOnlyList<string>? neverDelete = null)
    {
        _tokens = tokens;
        _neverDelete = neverDelete ?? Array.Empty<string>();
    }

    /// <summary>Plans one rule. Invalid or unresolvable rules yield no actions.</summary>
    public IReadOnlyList<HistoryAction> Plan(string itemKey, string rule, CancellationToken ct = default)
    {
        var r = PathRule.Parse(rule, _tokens);
        return r == null ? Array.Empty<HistoryAction>() : Plan(itemKey, r, ct);
    }

    public IReadOnlyList<HistoryAction> Plan(string itemKey, PathRule rule, CancellationToken ct = default)
    {
        var actions = new List<HistoryAction>();
        if (rule.Ini is { } ini)
        {
            if (IniDeleteAction.Exists(ini))
                actions.Add(new IniDeleteAction { ItemKey = itemKey, Target = ini.File, Section = ini.Section, Key = ini.Key });
            return actions;
        }

        if (rule.EmptyFolder)
        {
            var folder = EmptyTarget(rule.Expanded);
            foreach (var dir in ResolveFolders(folder, ct))
            {
                if (IsWindowsOrSystem32(dir)) continue;                      // the original refuses these two
                AddFiles(actions, itemKey, dir, new[] { "*" }, Array.Empty<string>(), recurse: true, ct);
                AddSubfolderRemovals(actions, itemKey, dir, ct);
                if (rule.RemoveFolderIfEmpty) actions.Add(new RemoveFolderIfEmptyAction { ItemKey = itemKey, Target = dir });
            }
            return actions;
        }

        if (rule.IsSingleFile)
        {
            var path = Path.Combine(rule.Directory, rule.Masks[0]);
            if (File.Exists(path)) AddFile(actions, itemKey, new FileInfo(path));
        }
        else
        {
            foreach (var dir in ResolveFolders(rule.Directory, ct))
            {
                AddFiles(actions, itemKey, dir, rule.Masks, rule.Exclusions, rule.Recurse, ct);
                if (rule.RemoveEmptySubfolders) actions.Add(new RemoveEmptySubfoldersAction { ItemKey = itemKey, Target = dir });
                if (rule.RemoveFolderIfEmpty) actions.Add(new RemoveFolderIfEmptyAction { ItemKey = itemKey, Target = dir });
            }
            return actions;
        }
        if (rule.RemoveEmptySubfolders && Directory.Exists(rule.Directory))
            actions.Add(new RemoveEmptySubfoldersAction { ItemKey = itemKey, Target = rule.Directory });
        if (rule.RemoveFolderIfEmpty && Directory.Exists(rule.Directory))
            actions.Add(new RemoveFolderIfEmptyAction { ItemKey = itemKey, Target = rule.Directory });
        return actions;
    }

    /// <summary>/E uses the whole rule as the folder; a trailing "\", "\*" or "\*.*" is tolerated.</summary>
    private static string EmptyTarget(string expanded)
    {
        var f = expanded.TrimEnd();
        foreach (var tail in new[] { "\\*.*", "\\*" })
            if (f.EndsWith(tail, StringComparison.Ordinal)) f = f[..^tail.Length];
        return f.TrimEnd('\\');
    }

    private static bool IsWindowsOrSystem32(string dir) =>
        AceWildcard.IsMatch(dir, @"?:\Windows") || AceWildcard.IsMatch(dir, @"?:\Windows\system32");

    /// <summary>Expands wildcard folder segments ("…\Profiles\*\cache2") into the existing folders, skipping links.</summary>
    public static IEnumerable<string> ResolveFolders(string pattern, CancellationToken ct = default)
    {
        pattern = pattern.TrimEnd('\\');
        if (!PathRule.HasWildcard(pattern))
        {
            if (Directory.Exists(pattern) && !IsLink(pattern)) yield return pattern;
            yield break;
        }
        var parts = pattern.Split('\\');
        int first = Array.FindIndex(parts, PathRule.HasWildcard);
        if (first == 0) yield break;                                         // wildcard in the drive part: not supported
        var current = new List<string> { string.Join('\\', parts[..first]) + (first == 1 ? "\\" : "") };
        for (int i = first; i < parts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var next = new List<string>();
            foreach (var c in current)
            {
                if (!PathRule.HasWildcard(parts[i]))
                {
                    var p = Path.Combine(c, parts[i]);
                    if (Directory.Exists(p) && !IsLink(p)) next.Add(p);
                    continue;
                }
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(c).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                next.AddRange(subs.Where(s => AceWildcard.IsMatch(Path.GetFileName(s), parts[i]) && !IsLink(s)));
            }
            current = next;
        }
        foreach (var c in current.Distinct(StringComparer.OrdinalIgnoreCase)) yield return c.TrimEnd('\\');
    }

    /// <summary>Plans "empty this folder" (all files, then the emptied subfolders deepest first; the folder itself only when asked).</summary>
    public void AddEmptyFolder(List<HistoryAction> actions, string itemKey, string dir, bool removeFolder, CancellationToken ct = default)
    {
        if (!Directory.Exists(dir) || IsLink(dir) || IsWindowsOrSystem32(dir)) return;
        AddFiles(actions, itemKey, dir, new[] { "*" }, Array.Empty<string>(), recurse: true, ct);
        AddSubfolderRemovals(actions, itemKey, dir, ct);
        if (removeFolder) actions.Add(new RemoveFolderIfEmptyAction { ItemKey = itemKey, Target = dir });
    }

    /// <summary>Plans the deletion of one file when it exists (and is not on the never-delete list).</summary>
    public void AddFileIfExists(List<HistoryAction> actions, string itemKey, string path)
    {
        var fi = new FileInfo(path);
        if (fi.Exists && (fi.Attributes & FileAttributes.ReparsePoint) == 0) AddFile(actions, itemKey, fi);
    }

    /// <summary>The files of <paramref name="dir"/> (and below, when <paramref name="recurse"/>) that match; links never followed (<see cref="SafeWalk"/>).</summary>
    private void AddFiles(List<HistoryAction> actions, string itemKey, string dir, IReadOnlyList<string> masks,
        IReadOnlyList<string> exclusions, bool recurse, CancellationToken ct)
    {
        dir = dir.TrimEnd('\\');
        SafeWalk.Walk(dir, d => recurse || d.Equals(dir, StringComparison.OrdinalIgnoreCase), (folder, entries) =>
        {
            foreach (var e in entries)
            {
                if ((e.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) continue;
                if (!masks.Any(m => AceWildcard.IsMatch(e.Name, m))) continue;
                var full = folder + "\\" + e.Name;
                if (exclusions.Any(x => AceWildcard.IsMatch(x.Contains('\\') ? full : e.Name, x))) continue;
                AddFile(actions, itemKey, (FileInfo)e);
            }
        }, ct: ct);
    }

    private void AddSubfolderRemovals(List<HistoryAction> actions, string itemKey, string dir, CancellationToken ct)
    {
        // Folders emptied by /E are removed deepest first; the folder itself stays (unless /R).
        List<string> subs;
        try
        {
            subs = new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions
            { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false })
                .Select(d => d.FullName).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var s in subs)
        {
            ct.ThrowIfCancellationRequested();
            AddSubfolderRemovals(actions, itemKey, s, ct);
            actions.Add(new RemoveFolderIfEmptyAction { ItemKey = itemKey, Target = s });
        }
    }

    private void AddFile(List<HistoryAction> actions, string itemKey, FileInfo f)
    {
        if (_neverDelete.Any(m => AceWildcard.IsMatch(f.Name, m))) return;
        actions.Add(new DeleteFileAction { ItemKey = itemKey, Target = f.FullName, Size = f.Length, LastWriteUtc = f.LastWriteTimeUtc });
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }
}
