using CleanSuite.Safety;
namespace CleanSuite.RegistryCompact;

/// <summary>One value of HKLM\SYSTEM\CurrentControlSet\Control\hivelist: registry path and NT file path (empty for volatile hives).</summary>
public sealed record HiveListEntry(string RegistryPath, string NtFilePath);

public enum HiveRoot { LocalMachine, Users }

public enum HiveSkip
{
    None,
    /// <summary>No backing file (volatile hive such as HARDWARE).</summary>
    Volatile,
    /// <summary>Not under \REGISTRY\MACHINE or \REGISTRY\USER (application and silo hives).</summary>
    OtherNamespace,
    /// <summary>SAM, SECURITY, BCD and DRIVERS, never touched by the original either.</summary>
    Excluded,
    /// <summary>The NT path could not be mapped to a drive letter.</summary>
    UnmappedPath,
    /// <summary>256 KiB or smaller (the original's threshold).</summary>
    Small,
}

/// <param name="SubKey">Key under the root, e.g. "SOFTWARE" or "S-1-5-21-…_Classes".</param>
/// <param name="FilePath">Hive file as a drive path; null when it could not be mapped.</param>
public sealed record HiveCandidate(HiveRoot Root, string SubKey, string RegistryPath, string? FilePath, long? FileSize, HiveSkip Skip)
{
    /// <summary>File name of the hive, or the last part of its registry path when it has no file (e.g. "Silo89a8…").</summary>
    public string Name => FilePath != null ? Path.GetFileName(FilePath) : RegistryPath[(RegistryPath.LastIndexOf('\\') + 1)..];

    /// <summary>
    /// REG_LATEST_FORMAT for every hive except UsrClass.dat on Windows 10 and later, as the original does
    ///; the older format list is not needed because CleanSuite requires Windows 10.
    /// </summary>
    public bool LatestFormat => !string.Equals(Name, "UsrClass.dat", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Which hivelist entries are measured.</summary>
public static class HiveSelection
{
    public const long SmallHiveLimit = 0x40000;

    /// <summary>Exact subkey names never processed. The original lists "userdifr", a typo that matches nothing; it is left out.</summary>
    public static readonly IReadOnlySet<string> ExcludedNames =
        new HashSet<string>(["SECURITY", "SAM", "BCD", "BCD00000000", "DRIVERS"], StringComparer.OrdinalIgnoreCase);

    /// <param name="toDosPath">NT path to drive path, or null when unknown.</param>
    /// <param name="fileSize">Size of the hive file, or null when it cannot be read.</param>
    public static IReadOnlyList<HiveCandidate> Select(IEnumerable<HiveListEntry> entries, Func<string, string?> toDosPath, Func<string, long?> fileSize)
    {
        var list = new List<HiveCandidate>();
        foreach (var e in entries)
        {
            HiveRoot root;
            string sub;
            if (StartsWith(e.RegistryPath, @"\REGISTRY\MACHINE\", out var rest)) (root, sub) = (HiveRoot.LocalMachine, rest);
            else if (StartsWith(e.RegistryPath, @"\REGISTRY\USER\", out rest)) (root, sub) = (HiveRoot.Users, rest);
            else
            {
                list.Add(new HiveCandidate(HiveRoot.LocalMachine, e.RegistryPath, e.RegistryPath, null, null, HiveSkip.OtherNamespace));
                continue;
            }
            if (string.IsNullOrWhiteSpace(e.NtFilePath)) { list.Add(new HiveCandidate(root, sub, e.RegistryPath, null, null, HiveSkip.Volatile)); continue; }
            if (ExcludedNames.Contains(sub)) { list.Add(new HiveCandidate(root, sub, e.RegistryPath, null, null, HiveSkip.Excluded)); continue; }
            var path = toDosPath(Environment.ExpandEnvironmentVariables(e.NtFilePath));
            if (path == null) { list.Add(new HiveCandidate(root, sub, e.RegistryPath, null, null, HiveSkip.UnmappedPath)); continue; }
            var size = fileSize(path);
            // As in the original, a file whose size cannot be read is not treated as small.
            list.Add(new HiveCandidate(root, sub, e.RegistryPath, path, size, size is <= SmallHiveLimit ? HiveSkip.Small : HiveSkip.None));
        }
        return list;
    }

    private static bool StartsWith(string s, string prefix, out string rest)
    {
        bool ok = s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && s.Length > prefix.Length;
        rest = ok ? s[prefix.Length..] : "";
        return ok;
    }
}

