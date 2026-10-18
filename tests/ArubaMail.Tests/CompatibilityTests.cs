using ArubaMail;
using Microsoft.Data.Sqlite;

namespace ArubaMail.Tests;

public class CompatibilityTests
{
    [Fact]
    public void ReadsPythonV1HistoryAndReplaysItsRequestWithoutDuplication()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aruba-compat-" + Guid.NewGuid()); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "register.sqlite3");
        try
        {
            using (var db = new SqliteConnection("Data Source=" + path))
            {
                db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-v1.sql")); cmd.ExecuteNonQuery();
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var store = new FollowupStore(path); var replay = store.Create("Città di prova", "Collegamento verificato", "2026-10-20T10:00:00+02:00", "python-create", ["A", "B"], note: "Prova sintetica");
            Assert.True(replay["replayed"]!.GetValue<bool>()); var id = replay["followup"]!["id"]!.ToString();
            Assert.Equal("1", store.List()["total"]!.ToString()); Assert.Equal("1", store.Get(id)["history_total"]!.ToString());
            store.Change(id, 1, "csharp-note", note: "Aggiornato da C#"); Assert.Equal("2", store.Get(id)["history_total"]!.ToString());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }
}
