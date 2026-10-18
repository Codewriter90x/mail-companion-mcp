using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace ArubaMail;

/// <summary>Schema v1 compatible with the original Python register. No IMAP dependencies.</summary>
public sealed class FollowupStore(string? path = null, Func<DateTimeOffset>? now = null)
{
    public string PathName { get; } = Path.GetFullPath(path ?? Settings.DbPath);
    private DateTimeOffset Now => (now ?? (() => DateTimeOffset.UtcNow))();
    private string Clock => Validate.Format(Now);
    private static readonly string[] Statuses = ["open", "waiting", "needs_review", "closed"];

    private SqliteConnection? Open(bool write = false)
    {
        if (!write && !File.Exists(PathName)) return null;
        if (write)
        {
            Settings.CreatePrivateDirectory(Path.GetDirectoryName(PathName)!);
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var file = new FileStream(PathName, options);
            }
            catch (IOException) when (File.Exists(PathName)) { }
        }
        Settings.PrivateFile(PathName);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = PathName,
            Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            db.Open();
            Exec(db, "PRAGMA foreign_keys=ON");
            var version = Convert.ToInt64(Scalar(db, "PRAGMA user_version"));
            if (version != 1 && !(write && version == 0)) throw new MailError("Versione database non supportata.");
            if (write)
            {
                using var schema = db.BeginTransaction(deferred: false);
                Exec(db, """
                    CREATE TABLE IF NOT EXISTS followups (
                    id TEXT PRIMARY KEY, title TEXT NOT NULL, expected_outcome TEXT NOT NULL,
                    participants TEXT NOT NULL, waiting_for TEXT NOT NULL, status TEXT NOT NULL
                    CHECK(status IN ('open','waiting','needs_review','closed')),
                    next_review_at TEXT NOT NULL, email_references TEXT NOT NULL,
                    revision INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                    closed_at TEXT, closure_reason TEXT);
                    CREATE TABLE IF NOT EXISTS events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, followup_id TEXT NOT NULL REFERENCES followups(id),
                    request_id TEXT NOT NULL UNIQUE, fingerprint TEXT NOT NULL, action TEXT NOT NULL,
                    created_at TEXT NOT NULL, changes TEXT NOT NULL, note TEXT NOT NULL, response TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS followups_due ON followups(status,next_review_at,id);
                    CREATE INDEX IF NOT EXISTS followups_history ON events(followup_id,sequence);
                    PRAGMA user_version=1;
                    """, transaction: schema);
                schema.Commit();
            }
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    internal static SqliteCommand Command(SqliteConnection db, string sql, IEnumerable<object?>? args = null, SqliteTransaction? transaction = null)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = transaction;
        var i = 0;
        foreach (var arg in args ?? []) cmd.Parameters.AddWithValue("$p" + i++, arg ?? DBNull.Value);
        return cmd;
    }
    private static void Exec(SqliteConnection db, string sql, IEnumerable<object?>? args = null, SqliteTransaction? transaction = null)
    { using var cmd = Command(db, sql, args, transaction); cmd.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql, IEnumerable<object?>? args = null, SqliteTransaction? tx = null)
    { using var cmd = Command(db, sql, args, tx); return cmd.ExecuteScalar(); }
    private static JsonObject Row(SqliteDataReader reader)
    {
        var row = new JsonObject();
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetFieldType(i) == typeof(long) ? JsonValue.Create(reader.GetInt64(i)) : JsonValue.Create(reader.GetString(i));
        return row;
    }
    private JsonObject View(JsonObject raw)
    {
        var row = (JsonObject)raw.DeepClone();
        foreach (var field in new[] { "participants", "email_references" }) row[field] = JsonNode.Parse(row[field]!.GetValue<string>());
        var due = DateTimeOffset.Parse(row["next_review_at"]!.GetValue<string>());
        row["overdue"] = row["status"]!.GetValue<string>() != "closed" && due <= Now;
        row["next_review_local"] = TimeZoneInfo.ConvertTime(due, TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome")).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        return row;
    }
    private static JsonObject Require(SqliteConnection? db, string id, SqliteTransaction? tx = null)
    {
        Validate.Text(id, 100, true);
        if (db != null)
        {
            using var cmd = Command(db, "SELECT * FROM followups WHERE id=$p0", [id], tx);
            using var reader = cmd.ExecuteReader(); if (reader.Read()) return Row(reader);
        }
        throw new MailError("Questione non trovata. Usare list_followups.");
    }
    // Python json.dumps(sort_keys=True, ensure_ascii=False) compatibility for existing retry fingerprints.
    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(", ", obj.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => Canonical(JsonValue.Create(v.Key)) + ": " + Canonical(v.Value))) + "}",
        JsonArray array => "[" + string.Join(", ", array.Select(Canonical)) + "]",
        _ => node?.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null"
    };
    private JsonObject Mutate(string action, string request, JsonObject payload, Func<SqliteConnection, SqliteTransaction, (string Id, JsonObject Changes, string Note)> operation)
    {
        request = Validate.Text(request, 100, true);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(new JsonObject { ["action"] = action, ["payload"] = payload.DeepClone() }))));
        using var db = Open(true)!;
        using var tx = db.BeginTransaction(deferred: false);
        using (var cmd = Command(db, "SELECT fingerprint,response FROM events WHERE request_id=$p0", [request], tx))
        using (var reader = cmd.ExecuteReader())
            if (reader.Read())
            {
                if (reader.GetString(0) != hash) throw new MailError("request_id già usato con parametri diversi.");
                var replay = JsonNode.Parse(reader.GetString(1))!.AsObject(); replay["replayed"] = true; return replay;
            }
        var change = operation(db, tx);
        var result = new JsonObject { ["followup"] = View(Require(db, change.Id, tx)), ["replayed"] = false };
        Exec(db, "INSERT INTO events(followup_id,request_id,fingerprint,action,created_at,changes,note,response) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)",
            [change.Id, request, hash, action, Clock, change.Changes.ToJsonString(), change.Note, result.ToJsonString()], tx);
        tx.Commit(); return result;
    }
    public JsonObject Create(string title, string expected_outcome, string next_review_at, string request_id, string[]? participants = null, string waiting_for = "", List<Dictionary<string, string>>? email_references = null, string note = "")
    {
        var payload = new JsonObject
        {
            ["title"] = Validate.Text(title, 200, true),
            ["expected_outcome"] = Validate.Text(expected_outcome, 2000, true),
            ["next_review_at"] = Validate.Timestamp(next_review_at),
            ["participants"] = Validate.People(participants),
            ["waiting_for"] = Validate.Text(waiting_for, 300),
            ["email_references"] = Validate.References(email_references),
            ["note"] = Validate.Text(note, 2000)
        };
        return Mutate("create", request_id, payload, (db, tx) =>
        {
            var id = Guid.NewGuid().ToString();
            Exec(db, "INSERT INTO followups(id,title,expected_outcome,participants,waiting_for,status,next_review_at,email_references,revision,created_at,updated_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,1,$p8,$p9)",
                [id,payload["title"]!.GetValue<string>(),payload["expected_outcome"]!.GetValue<string>(),payload["participants"]!.ToJsonString(),payload["waiting_for"]!.GetValue<string>(),
                payload["waiting_for"]!.GetValue<string>() == "" ? "open" : "waiting",payload["next_review_at"]!.GetValue<string>(),payload["email_references"]!.ToJsonString(),Clock,Clock], tx);
            var changes = (JsonObject)payload.DeepClone(); changes.Remove("note"); return (id, changes, payload["note"]!.GetValue<string>());
        });
    }
    public JsonObject Change(string id, int revision, string request, string action = "update", string note = "", string? title = null, string? expected_outcome = null, string[]? participants = null,
        string? waiting_for = null, string? status = null, List<Dictionary<string, string>>? email_references = null, string? next_review_at = null, string? closure_reason = null)
    {
        if (revision < 1) throw new MailError("expected_revision deve essere la revisione corrente.");
        note = Validate.Text(note, 2000);
        var fields = new JsonObject();
        foreach (var (key, value, max, required) in new[] { ("title", title, 200, true), ("expected_outcome", expected_outcome, 2000, true), ("waiting_for", waiting_for, 300, false), ("closure_reason", closure_reason, 2000, true) })
            if (value != null) fields[key] = Validate.Text(value, max, required);
        if (participants != null) fields["participants"] = Validate.People(participants);
        if (email_references != null) fields["email_references"] = Validate.References(email_references);
        if (next_review_at != null) fields["next_review_at"] = Validate.Timestamp(next_review_at);
        if (status != null)
        {
            if (!Statuses.Contains(status)) throw new MailError("Stato non valido.");
            fields["status"] = status;
        }
        if (!new[] { "update", "close", "snooze" }.Contains(action)) throw new MailError("Operazione non supportata.");
        if (action == "close" && closure_reason == null) throw new MailError("Indicare motivo di chiusura.");
        if (action != "close" && (closure_reason != null || status == "closed")) throw new MailError("Per chiudere usare close_followup con motivo esplicito.");
        if (fields.Count == 0 && note == "") throw new MailError("Nessun aggiornamento specificato.");
        var payload = new JsonObject { ["id"] = id, ["expected_revision"] = revision, ["fields"] = fields.DeepClone(), ["note"] = note };
        return Mutate(action, request, payload, (db, tx) =>
        {
            var current = Require(db, id, tx);
            if (current["revision"]!.GetValue<long>() != revision) throw new MailError("Revisione cambiata: rileggere prima di modificare.");
            if (current["status"]!.GetValue<string>() == "closed") throw new MailError("Questione già chiusa: nessuna modifica applicata.");
            var updates = (JsonObject)fields.DeepClone();
            if (action == "close") { updates["status"] = "closed"; updates["closed_at"] = Clock; }
            if (action == "snooze" && (next_review_at == null || DateTimeOffset.Parse(updates["next_review_at"]!.GetValue<string>()) <= Now)) throw new MailError("Il prossimo controllo deve essere nel futuro.");
            var changes = new JsonObject();
            foreach (var pair in updates)
            {
                var before = pair.Key is "participants" or "email_references" ? JsonNode.Parse(current[pair.Key]!.GetValue<string>()) : current[pair.Key]?.DeepClone();
                changes[pair.Key] = new JsonObject { ["before"] = before, ["after"] = pair.Value?.DeepClone() };
            }
            updates["updated_at"] = Clock; updates["revision"] = revision + 1;
            var args = updates.Select(v => v.Value is JsonArray ? (object?)v.Value.ToJsonString() : v.Value is JsonValue value && value.TryGetValue<int>(out var n) ? n : v.Value?.GetValue<string>()).ToList(); args.Add(id);
            Exec(db, "UPDATE followups SET " + string.Join(",", updates.Select((v, i) => v.Key + "=$p" + i)) + " WHERE id=$p" + updates.Count, args, tx);
            return (id, changes, note);
        });
    }
    public JsonObject List(string status = "active", string due = "all", int days = 7, int limit = 20, int offset = 0)
    {
        if (!Statuses.Concat(["active", "all"]).Contains(status) || !new[] { "all", "overdue", "upcoming" }.Contains(due) || days is < 1 or > 365 || limit is < 1 or > 50 || offset is < 0 or > 10000)
            throw new MailError("Filtri non validi: limit 1–50, offset 0–10000, days 1–365.");
        var where = new List<string>(); var args = new List<object?>();
        if (status == "active") where.Add("status!='closed'");
        else if (status != "all") { where.Add("status=$p0"); args.Add(status); }
        if (due != "all")
        {
            where.Add("status!='closed'"); where.Add("next_review_at" + (due == "overdue" ? "<=" : ">") + "$p" + args.Count); args.Add(Clock);
            if (due == "upcoming") { where.Add("next_review_at<=$p" + args.Count); args.Add(Validate.Format(Now.AddDays(days))); }
        }
        var clause = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        using var db = Open(); long total = 0; var rows = new JsonArray();
        if (db != null)
        {
            total = Convert.ToInt64(Scalar(db, "SELECT count(*) FROM followups" + clause, args));
            using var cmd = Command(db, "SELECT * FROM followups" + clause + " ORDER BY next_review_at,id LIMIT $p" + args.Count + " OFFSET $p" + (args.Count + 1), args.Concat([limit, offset]));
            using var reader = cmd.ExecuteReader(); while (reader.Read()) rows.Add(View(Row(reader)));
        }
        return new JsonObject { ["followups"] = rows, ["total"] = total, ["offset"] = offset, ["has_more"] = offset + limit < total, ["checked_at"] = Clock, ["notifications_enabled"] = false };
    }
    public JsonObject Get(string id, int history_limit = 20, int history_offset = 0)
    {
        if (history_limit is < 1 or > 50 || history_offset is < 0 or > 10000) throw new MailError("Paginazione storico non valida.");
        using var db = Open(); var row = Require(db, id); var history = new JsonArray();
        var total = Convert.ToInt64(Scalar(db!, "SELECT count(*) FROM events WHERE followup_id=$p0", [id]));
        using var cmd = Command(db!, "SELECT sequence,action,created_at,changes,note FROM events WHERE followup_id=$p0 ORDER BY sequence DESC LIMIT $p1 OFFSET $p2", [id, history_limit, history_offset]);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) { var item = Row(reader); item["changes"] = JsonNode.Parse(item["changes"]!.GetValue<string>()); history.Add(item); }
        return new JsonObject { ["followup"] = View(row), ["history"] = history, ["history_total"] = total, ["history_has_more"] = history_offset + history_limit < total };
    }
}
