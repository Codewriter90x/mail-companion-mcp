using System.Text.Json.Nodes;
using ArubaMail;
using Microsoft.Data.Sqlite;

namespace ArubaMail.Tests;

public sealed class FollowupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "aruba-test-" + Guid.NewGuid());
    private string Db => Path.Combine(directory, "register.sqlite3");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-18T10:00:00Z");
    private FollowupStore Store => new(Db, () => now);
    private JsonObject Create(string request = "create", string date = "2026-10-20T10:00:00+02:00", string title = "Porta A07") => Store.Create(title, "Comunicazione verificata", date, request, ["A", "B", "C", "D"]);
    private static string Id(JsonObject response) => response["followup"]!["id"]!.GetValue<string>();
    private static long Number(JsonNode? value) => long.Parse(value!.ToString());
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    [Fact] public void EmptyReadDoesNotCreateDatabase() { Assert.Equal(0, Number(Store.List()["total"])); Assert.False(File.Exists(Db)); }
    [Fact]
    public void MultiplePartiesUnknownAssigneeAndPersistence()
    {
        var row = Create()["followup"]!; Assert.Equal("open", row["status"]!.ToString()); Assert.Equal("", row["waiting_for"]!.ToString()); Assert.Equal(4, row["participants"]!.AsArray().Count);
        Assert.Equal("2026-10-20T08:00:00+00:00", row["next_review_at"]!.ToString()); Assert.Equal(1, Number(new FollowupStore(Db).List()["total"]));
        if (!OperatingSystem.IsWindows()) { Assert.Equal((UnixFileMode)0x180, File.GetUnixFileMode(Db)); Assert.Equal((UnixFileMode)0x1c0, File.GetUnixFileMode(directory)); }
    }
    [Fact] public void RetryReturnsOriginalAndDoesNotDuplicate() { var a = Create(); var b = Create(); Assert.Equal(Id(a), Id(b)); Assert.True(b["replayed"]!.GetValue<bool>()); Assert.Equal(1, Number(Store.Get(Id(a))["history_total"])); }
    [Fact] public void RetryWithDifferentInputIsRejected() { Create(); Assert.Throws<MailError>(() => Create(title: "Diverso")); Assert.Equal(1, Number(Store.List()["total"])); }
    [Fact]
    public async Task ConcurrentRetryCreatesOneRecord()
    {
        var rows = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Create()))); Assert.Single(rows.Select(Id).Distinct()); Assert.Equal(1, Number(Store.List()["total"]));
    }
    [Theory]
    [InlineData("domani")]
    [InlineData("2026-10-20T10:00:00")]
    [InlineData("2026-99-99T10:00:00Z")]
    public void InvalidOrAmbiguousDateRejected(string date) { Assert.Throws<MailError>(() => Create(date: date)); Assert.False(File.Exists(Db)); }
    [Fact]
    public void DstOffsetsRemainDistinct()
    {
        var a = Create("one", "2026-10-25T02:30:00+02:00")["followup"]!; var b = Create("two", "2026-10-25T02:30:00+01:00")["followup"]!;
        Assert.NotEqual(a["next_review_at"]!.ToString(), b["next_review_at"]!.ToString()); Assert.Equal("2026-10-25T02:30:00+01:00", b["next_review_local"]!.ToString());
    }
    [Fact]
    public void NoteDoesNotCloseOrResetDate()
    {
        var created = Create(); var updated = Store.Change(Id(created), 1, "note", note: "Risposta arrivata: stanno verificando");
        Assert.Equal("open", updated["followup"]!["status"]!.ToString()); Assert.Equal(created["followup"]!["next_review_at"]!.ToString(), updated["followup"]!["next_review_at"]!.ToString()); Assert.Equal(2, Number(Store.Get(Id(created))["history_total"]));
    }
    [Fact]
    public void SnoozeHistoryAndReplay()
    {
        var id = Id(Create()); Store.Change(id, 1, "later", "snooze", next_review_at: "2026-10-23T12:00:00Z");
        Assert.True(Store.Change(id, 1, "later", "snooze", next_review_at: "2026-10-23T12:00:00Z")["replayed"]!.GetValue<bool>());
        var changes = Store.Get(id)["history"]![0]!["changes"]!["next_review_at"]!; Assert.Equal("2026-10-20T08:00:00+00:00", changes["before"]!.ToString()); Assert.Equal("2026-10-23T12:00:00+00:00", changes["after"]!.ToString());
    }
    [Fact]
    public void StaleRevisionDoesNotOverwriteOrAddHistory()
    {
        var id = Id(Create()); Store.Change(id, 1, "change", note: "A"); Assert.Throws<MailError>(() => Store.Change(id, 1, "stale", title: "Sovrascrittura")); Assert.Equal(2, Number(Store.Get(id)["history_total"]));
    }
    [Fact]
    public void CloseRequiresReasonAndRemovesFromDueLists()
    {
        var id = Id(Create(date: "2026-10-18T10:00:00Z")); Assert.Equal(1, Number(Store.List(due: "overdue")["total"]));
        Assert.Throws<MailError>(() => Store.Change(id, 1, "bad", "close", closure_reason: " "));
        var closed = Store.Change(id, 1, "close", "close", closure_reason: "Ripristino verificato"); Assert.False(closed["followup"]!["overdue"]!.GetValue<bool>());
        Assert.Equal(0, Number(Store.List()["total"])); Assert.Equal(0, Number(Store.List(status: "all", due: "overdue")["total"])); Assert.Equal(1, Number(Store.List(status: "closed")["total"]));
        Assert.Throws<MailError>(() => Store.Change(id, 2, "closed", note: "X"));
    }
    [Fact] public void GenericUpdateCannotClose() { var id = Id(Create()); Assert.Throws<MailError>(() => Store.Change(id, 1, "bad", status: "closed")); }
    [Fact]
    public void DuePaginationAndHistoryPagination()
    {
        var id = Id(Create("a", "2026-10-18T09:00:00Z")); Create("b", "2026-10-18T10:00:00Z"); Create("c", "2026-10-19T10:00:00Z"); Create("d", "2026-10-22T10:00:00Z");
        Assert.Equal(2, Number(Store.List(due: "overdue")["total"])); Assert.Equal(1, Number(Store.List(due: "upcoming", days: 2)["total"])); Assert.True(Store.List(limit: 1)["has_more"]!.GetValue<bool>());
        Store.Change(id, 1, "note", note: "Verifica"); Assert.True(Store.Get(id, 1)["history_has_more"]!.GetValue<bool>()); Assert.Equal("create", Store.Get(id, 1, 1)["history"]![0]!["action"]!.ToString());
    }
    [Fact] public void RejectPastSnoozeAndMissingRecord() { var id = Id(Create()); Assert.Throws<MailError>(() => Store.Change(id, 1, "past", "snooze", next_review_at: "2026-10-17T10:00:00Z")); Assert.Throws<MailError>(() => Store.Get("missing")); }
    [Fact] public void FailedFirstMutationLeavesValidRegister() { Assert.Throws<MailError>(() => Store.Change("missing", 1, "x", note: "A")); Assert.Equal(0, Number(Store.List()["total"])); Assert.Equal(1, Number(Create()["followup"]!["revision"])); }
    [Fact]
    public void RejectOversizeAndBodyReferences()
    {
        Assert.Throws<MailError>(() => Create(title: new string('a', 201))); Assert.Throws<MailError>(() => Store.Create("A", "B", "2026-10-20T10:00:00Z", "bad", email_references: [new() { ["body"] = "mail" }]));
    }
    [Fact]
    public void ReferencesAndUnknownResponsibleCanBeUpdated()
    {
        var a = Store.Create("A", "B", "2026-10-20T10:00:00Z", "a", waiting_for: "Fornitore", email_references: [new() { ["account_id"] = "test", ["folder"] = "INBOX", ["message_uid"] = "42", ["uidvalidity"] = "77" }]);
        Assert.Equal("waiting", a["followup"]!["status"]!.ToString()); var b = Store.Change(Id(a), 1, "b", waiting_for: "", status: "needs_review", participants: ["A", "A", "B"]);
        Assert.Equal("", b["followup"]!["waiting_for"]!.ToString()); Assert.Equal(2, b["followup"]!["participants"]!.AsArray().Count); Assert.Single(b["followup"]!["email_references"]!.AsArray());
    }
    [Fact] public void SqlTextStaysData() { var title = "'); DROP TABLE followups; --"; var a = Create(title: title); Assert.Equal(title, Store.Get(Id(a))["followup"]!["title"]!.ToString()); }
    [Fact]
    public void FutureDatabaseRejected()
    {
        Create(); using (var db = new SqliteConnection("Data Source=" + Db)) { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version=99"; cmd.ExecuteNonQuery(); }
        Assert.Throws<MailError>(() => Store.List()); Assert.Throws<MailError>(() => Create("new"));
    }
    [Fact]
    public void InsecureFileRejected()
    {
        if (OperatingSystem.IsWindows()) return; Create(); File.SetUnixFileMode(Db, (UnixFileMode)0x1a4); Assert.Throws<MailError>(() => Store.List());
    }
}
