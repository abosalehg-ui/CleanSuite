using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CleanSuite.Safety;

public enum BackupTrust
{
    /// <summary>Written by CleanSuite and unchanged since (SHA-256 matches the ledger).</summary>
    Verified,
    /// <summary>Written by CleanSuite but changed afterwards.</summary>
    Modified,
    /// <summary>Not written by CleanSuite (the original program's backups, or a file placed in the folder).</summary>
    Unknown,
}

/// <summary>
/// Record of the backups CleanSuite writes (zip and .reg), one line per file with its SHA-256, in "ledger.tsv" next to the
/// backups (normally %LocalAppData%\CleanSuite\Backups). The Restore page uses it to tell its own untouched backups from
/// anything else in the backup folders, warns before restoring the rest, and refuses some destinations for them.
/// </summary>
public static class BackupLedger
{
    public const string FileName = "ledger.tsv";

    private static string LedgerFor(string backupPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backupPath))!, FileName);

    public static void Record(string backupPath, string? ledgerPath = null)
    {
        if (!File.Exists(backupPath) || Hash(backupPath) is not { } hash) return;
        ledgerPath ??= LedgerFor(backupPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            File.AppendAllText(ledgerPath, hash + "\t" + Path.GetFullPath(backupPath) + "\t"
                                           + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static BackupTrust Status(string backupPath, string? ledgerPath = null)
    {
        string full;
        try { full = Path.GetFullPath(backupPath); ledgerPath ??= LedgerFor(full); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return BackupTrust.Unknown; }
        string? recorded = null;
        try
        {
            if (!File.Exists(ledgerPath)) return BackupTrust.Unknown;
            foreach (var line in File.ReadLines(ledgerPath))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 2 && parts[1].Equals(full, StringComparison.OrdinalIgnoreCase)) recorded = parts[0];   // the last line wins
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return BackupTrust.Unknown; }
        if (recorded == null) return BackupTrust.Unknown;
        return string.Equals(Hash(full), recorded, StringComparison.OrdinalIgnoreCase) ? BackupTrust.Verified : BackupTrust.Modified;
    }

    private static string? Hash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
