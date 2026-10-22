using Microsoft.Data.Sqlite;

namespace CleanSuite.History;

/// <summary>Opens browser databases without pooling, so no handle stays open after an action.</summary>
public static class Sqlite
{
    public static SqliteConnection Open(string path, bool readOnly)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }

    public static bool TableExists(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() != null;
    }

    public static long Count(SqliteConnection c, string table)
    {
        if (!TableExists(c, table)) return 0;
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Distinct values of a column, or empty when the table/column is missing or the file is not a database.</summary>
    public static List<string> Distinct(string db, string table, string column)
    {
        var list = new List<string>();
        try
        {
            using var c = Open(db, readOnly: true);
            if (!TableExists(c, table)) return list;
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT {Quote(column)} FROM {Quote(table)}";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0)) list.Add(r.GetValue(0).ToString() ?? "");
        }
        catch (SqliteException) { }
        return list;
    }

    /// <summary>A table or column name as a quoted SQL identifier (names cannot be parameters; inner quotes are doubled).</summary>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    internal static bool IsMissingObject(SqliteException e) =>
        e.SqliteErrorCode == 1 && (e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
                                    e.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Runs a fixed list of statements in one transaction (the original sends "begin transaction;" … "commit transaction;").
/// Statements that reference a table or column the browser version does not have are skipped, as the original's
/// statement-by-statement execution does; any other error rolls the whole batch back.
/// </summary>
public sealed class SqlBatchAction : HistoryAction
{
    public required IReadOnlyList<string> Statements { get; init; }
    public required string Description { get; init; }
    public override string Kind => "sql";
    public override string? Detail => Description;

    public override ActionResult Execute(ExecutionContext ctx)
    {
        if (!File.Exists(Target)) return ActionResult.Ok;
        if (ctx.Protected.CheckResolved(Target) is { } why) return ActionResult.Fail(why);
        try
        {
            using var c = Sqlite.Open(Target, readOnly: false);
            using var tx = c.BeginTransaction();
            int skipped = 0;
            foreach (var sql in Statements)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                try { cmd.ExecuteNonQuery(); }
                catch (SqliteException e) when (Sqlite.IsMissingObject(e)) { skipped++; }
            }
            tx.Commit();
            ctx.Journal.Record("sql", Target, true, backupRef: $"{Description} ({Statements.Count - skipped}/{Statements.Count} statements)");
            return ActionResult.Ok;
        }
        catch (SqliteException e)
        {
            ctx.Journal.Record("sql", Target, false, backupRef: Description, error: e.Message);
            return ActionResult.Fail(e.Message);
        }
    }
}

/// <summary>
/// Deletes the rows whose key column holds one of the given values (values chosen at analysis time from
/// "select … group by &lt;column&gt;" minus the cookie whitelist). Uses parameters instead of the
/// original's string formatting, so a stored value cannot alter the statement.
/// </summary>
public sealed class SqlDeleteKeysAction : HistoryAction
{
    public required string Table { get; init; }
    public required string Column { get; init; }
    public required IReadOnlyList<string> Keys { get; init; }
    public override string Kind => "sql-delete";
    public override string? Detail => $"{Table}: {Keys.Count} {Column}";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        if (!File.Exists(Target) || Keys.Count == 0) return ActionResult.Ok;
        if (ctx.Protected.CheckResolved(Target) is { } why) return ActionResult.Fail(why);
        try
        {
            using var c = Sqlite.Open(Target, readOnly: false);
            if (!Sqlite.TableExists(c, Table)) return ActionResult.Ok;
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {Sqlite.Quote(Table)} WHERE {Sqlite.Quote(Column)} = $k";
            var p = cmd.Parameters.Add("$k", SqliteType.Text);
            long rows = 0;
            foreach (var k in Keys)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                p.Value = k;
                rows += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            ctx.Journal.Record("sql-delete", Target, true, backupRef: $"{Table}.{Column}: {Keys.Count} keys, {rows} rows");
            return ActionResult.Ok;
        }
        catch (SqliteException e)
        {
            ctx.Journal.Record("sql-delete", Target, false, backupRef: Table, error: e.Message);
            return ActionResult.Fail(e.Message);
        }
    }

    /// <summary>Plans the delete: distinct keys of the column that the whitelist does not keep; null when nothing to do.</summary>
    public static SqlDeleteKeysAction? Plan(string itemKey, string db, string table, string column, CookieWhitelist whitelist)
    {
        if (!File.Exists(db)) return null;
        var keys = Sqlite.Distinct(db, table, column).Where(k => !whitelist.Keeps(k)).ToList();
        return keys.Count == 0 ? null : new SqlDeleteKeysAction { ItemKey = itemKey, Target = db, Table = table, Column = column, Keys = keys };
    }
}

/// <summary>"Compact databases": VACUUM.</summary>
public sealed class VacuumAction : HistoryAction
{
    public override string Kind => "vacuum";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        if (!File.Exists(Target)) return ActionResult.Ok;
        if (ctx.Protected.CheckResolved(Target) is { } why) return ActionResult.Fail(why);
        long before = new FileInfo(Target).Length;
        try
        {
            using (var c = Sqlite.Open(Target, readOnly: false))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "VACUUM;";
                cmd.ExecuteNonQuery();
            }
            long freed = Math.Max(0, before - new FileInfo(Target).Length);
            ctx.Journal.Record("vacuum", Target, true, backupRef: $"{freed} bytes");
            return new ActionResult(true, null, freed);
        }
        catch (SqliteException e)
        {
            ctx.Journal.Record("vacuum", Target, false, error: e.Message);
            return ActionResult.Fail(e.Message);
        }
    }
}

/// <summary>
/// Folds a database's write-ahead log back into it (PRAGMA wal_checkpoint(TRUNCATE)). Used instead of the original's
/// deletion of "places.sqlite-wal"/"-shm", which discards history and bookmarks that were not checkpointed yet.
/// </summary>
public sealed class WalCheckpointAction : HistoryAction
{
    public override string Kind => "wal-checkpoint";

    public override ActionResult Execute(ExecutionContext ctx)
    {
        if (!File.Exists(Target)) return ActionResult.Ok;
        try
        {
            using var c = Sqlite.Open(Target, readOnly: false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
            ctx.Journal.Record("wal-checkpoint", Target, true);
            return ActionResult.Ok;
        }
        catch (SqliteException e)
        {
            ctx.Journal.Record("wal-checkpoint", Target, false, error: e.Message);
            return ActionResult.Fail(e.Message);
        }
    }
}
