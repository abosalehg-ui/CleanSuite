using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Erase history, Firefox: the statements that clean places.sqlite. Temp sandboxes and a private registry hive only.</summary>
public class FirefoxHistoryTests
{
    [Fact]
    public void Firefox_history_cleaning_keeps_place_queries_and_drops_unvisited_pages()
    {
        using var sb = new Sandbox();
        var db = Path.Combine(sb.Root, "places.sqlite");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT, frecency INTEGER DEFAULT -1, visit_count INTEGER DEFAULT 0);
                CREATE TABLE moz_historyvisits (id INTEGER PRIMARY KEY, place_id INTEGER);
                CREATE TABLE moz_bookmarks (id INTEGER PRIMARY KEY, fk INTEGER, parent INTEGER);
                CREATE TABLE moz_anno_attributes (id INTEGER PRIMARY KEY, name TEXT);
                CREATE TABLE moz_annos (id INTEGER PRIMARY KEY, place_id INTEGER, anno_attribute_id INTEGER, mime_type TEXT, content TEXT,
                    flags INTEGER, expiration INTEGER, type INTEGER, dateAdded INTEGER, lastModified INTEGER);
                CREATE TABLE moz_items_annos (id INTEGER PRIMARY KEY, item_id INTEGER, anno_attribute_id INTEGER, mime_type TEXT, content TEXT,
                    flags INTEGER, expiration INTEGER, type INTEGER, dateAdded INTEGER, lastModified INTEGER);
                CREATE TABLE moz_inputhistory (place_id INTEGER, input TEXT, use_count INTEGER);
                CREATE TABLE moz_hosts (id INTEGER PRIMARY KEY, host TEXT);
                INSERT INTO moz_places (id, url) VALUES (1, 'place:sort=8&maxResults=10'), (2, 'https://example.org/'), (3, 'https://kept.example/');
                INSERT INTO moz_historyvisits (place_id) VALUES (2);
                INSERT INTO moz_bookmarks (fk, parent) VALUES (3, 1);
                INSERT INTO moz_hosts (host) VALUES ('example.org');
                """;
            cmd.ExecuteNonQuery();
            foreach (var statement in CleanSuite.History.FirefoxCleaner.PlacesStatements)
            {
                cmd.CommandText = statement;
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText = "SELECT group_concat(id) FROM (SELECT id FROM moz_places ORDER BY id)";
            Assert.Equal("1,3", cmd.ExecuteScalar());                                // the query and the bookmark stay, the visited page goes
            cmd.CommandText = "SELECT COUNT(*) FROM moz_historyvisits";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
