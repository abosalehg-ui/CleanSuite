using System.Text.Encodings.Web;
using System.Text.Json;

namespace CleanSuite.Safety;

public sealed record JournalEntry(DateTime Utc, string Action, string Target, bool Success, string? BackupRef = null, string? Error = null);

/// <summary>
/// Append-only record of every modifying operation of one session (JSON lines, flushed per entry),
/// written BEFORE results are reported so an interrupted session still leaves a trace.
/// </summary>
public sealed class OperationJournal : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public string SessionFolder { get; }
    public string FilePath { get; }

    public OperationJournal(string tool, string? baseFolder = null)
    {
        baseFolder ??= DefaultBaseFolder;
        // Invariant culture: with e.g. ar-SA the current calendar is Hijri and "yyyy" would not be the Gregorian year.
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(baseFolder);
        // Two sessions started within the same millisecond get "_2", "_3", … instead of colliding.
        for (int n = 1; ; n++)
        {
            var folder = Path.Combine(baseFolder, n == 1 ? $"{stamp}_{tool}" : $"{stamp}_{tool}_{n}");
            var file = Path.Combine(folder, "journal.jsonl");
            Directory.CreateDirectory(folder);
            try
            {
                _writer = new StreamWriter(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            }
            catch (IOException) when (File.Exists(file)) { continue; }
            SessionFolder = folder;
            FilePath = file;
            break;
        }
    }

    public static string DefaultBaseFolder =>
        CleanSuitePaths.Journal;

    public void Record(JournalEntry entry)
    {
        lock (_lock) _writer.WriteLine(JsonSerializer.Serialize(entry, Json));
    }

    public void Record(string action, string target, bool success, string? backupRef = null, string? error = null) =>
        Record(new JournalEntry(DateTime.UtcNow, action, target, success, backupRef, error));

    /// <summary>The entries of a journal, also one that a running session is still writing (it is opened for reading with sharing).</summary>
    public static IReadOnlyList<JournalEntry> Read(string journalFile)
    {
        using var fs = new FileStream(journalFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        var list = new List<JournalEntry>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) list.Add(JsonSerializer.Deserialize<JournalEntry>(line, Json)!);
        return list;
    }

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }
}
