using CleanSuite.Safety;
using System.Text;

namespace CleanSuite.History;

/// <summary>
/// Rewrites a text file keeping only some lines (e.g. SiteSecurityServiceState.txt: one "host:…" line per site).
/// The decision is fixed at analysis time: the lines to drop are listed, and the file is only
/// rewritten if it is unchanged since then. The new content is written next to the file and swapped in atomically.
/// </summary>
public sealed class FilterLinesAction : HistoryAction
{
    public required IReadOnlyList<string> LinesToDrop { get; init; }
    public DateTime LastWriteUtc { get; init; }
    /// <summary>File length at analysis time (Size stays 0: rewriting frees almost nothing).</summary>
    public long ExpectedLength { get; init; }
    public override string Kind => "filter-lines";
    public override string? Detail => $"{LinesToDrop.Count} lines";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        var fi = new FileInfo(Target);
        if (!fi.Exists) return ActionResult.Ok;
        if (ctx.Protected.CheckResolved(Target) is { } why) return ActionResult.Fail(why);
        if (fi.LastWriteTimeUtc != LastWriteUtc || fi.Length != ExpectedLength) return ActionResult.Fail(FileIdentity.ChangedReason);
        try
        {
            var drop = new HashSet<string>(LinesToDrop, StringComparer.Ordinal);
            var kept = File.ReadAllLines(Target, Encoding.UTF8).Where(l => !drop.Contains(l)).ToList();
            var tmp = Target + ".cleansuite.tmp";
            File.WriteAllLines(tmp, kept, new UTF8Encoding(false));
            File.Move(tmp, Target, overwrite: true);
            ctx.Journal.Record("filter-lines", Target, true, backupRef: $"{LinesToDrop.Count} lines removed");
            return ActionResult.Ok;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Journal.Record("filter-lines", Target, false, error: e.Message);
            return ActionResult.Fail(e.Message);
        }
    }

    /// <summary>Plans the filter: lines whose host (text before the first ':') the whitelist does not keep.</summary>
    public static FilterLinesAction? PlanHostLines(string itemKey, string file, CookieWhitelist whitelist)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists) return null;
        List<string> lines;
        try { lines = File.ReadAllLines(file, Encoding.UTF8).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var drop = lines.Where(l =>
        {
            int c = l.IndexOf(':');
            var host = c >= 0 ? l[..c] : l;
            return host.Trim().Length > 0 && !whitelist.Keeps(host);
        }).ToList();
        return drop.Count == 0 ? null : new FilterLinesAction
        {
            ItemKey = itemKey, Target = file, ExpectedLength = fi.Length, LastWriteUtc = fi.LastWriteTimeUtc, LinesToDrop = drop,
        };
    }
}
