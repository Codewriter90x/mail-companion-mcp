using System.Text.Json.Nodes;
using ArubaMail;
using Microsoft.Data.Sqlite;

namespace ArubaMail.Tests;

public sealed class AppleReminderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mail-apple-test-" + Guid.NewGuid());
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-18T10:00:00Z");
    private string Db => Path.Combine(directory, "followups.sqlite3");
    private string Profile => Path.Combine(directory, "apple-reminders.local.json");
    private FollowupStore Store => new(Db, () => now);
    private static readonly AppleReminderProfile Enabled = new(true, "list-one", "Follow-up email");
    private sealed class FakeNative : IAppleReminders
    {
        public List<JsonObject> Calls { get; } = [];
        public bool FailAfterWrite { get; set; }
        public bool Deny { get; set; }
        public string? FailKey { get; set; }
        public Dictionary<string, string> Items { get; } = [];
        public Task<JsonObject> Call(JsonObject request, CancellationToken ct)
        {
            Calls.Add((JsonObject)request.DeepClone());
            if (Deny) throw new MailError("Accesso negato.");
            if (request["action"]!.ToString() is "lists" or "authorize")
                return Task.FromResult(new JsonObject { ["success"] = true, ["calendars"] = new JsonArray(new JsonObject { ["id"] = "list-one", ["title"] = "Follow-up email", ["source"] = "iCloud" }) });
            var key = request["followup_id"]!.ToString();
            if (!Items.ContainsKey(key)) Items[key] = Guid.NewGuid().ToString();
            if (FailAfterWrite || key == FailKey) throw new IOException("simulated interruption after Apple save");
            return Task.FromResult(new JsonObject { ["success"] = true, ["reminder_id"] = Items[key], ["state"] = "scheduled" });
        }
    }
    private string Create() => Store.Create("Questione inventata", "Verificare il cavo", "2026-10-20T10:00:00+02:00", "create", waiting_for: "Parte da identificare")["followup"]!["id"]!.ToString();
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    [Fact]
    public async Task DisabledOrEmptyDoesNotCreateDbOrCallApple()
    {
        var fake = new FakeNative(); var sync = new AppleReminderSync(Store, fake);
        Assert.Equal(0, await sync.Sync(Enabled, default)); Assert.False(File.Exists(Db));
        Create(); Assert.Equal(0, await sync.Sync(new(), default)); Assert.Empty(fake.Calls);
    }
    [Fact]
    public async Task CreatesOncePersistsReceiptAndDoesNotCopyMailReferences()
    {
        var id = Create(); var fake = new FakeNative();
        Assert.Equal(1, await new AppleReminderSync(Store, fake).Sync(Enabled, default));
        Assert.Equal(0, await new AppleReminderSync(new(Db), fake).Sync(Enabled, default));
        var call = Assert.Single(fake.Calls); Assert.Equal(id, call["followup_id"]!.ToString());
        Assert.Equal("2026-10-20T08:00:00+00:00", call["due"]!.ToString()); Assert.True(call["rearm"]!.GetValue<bool>());
        Assert.DoesNotContain("email_references", call.ToJsonString()); Assert.Equal(1, Store.Get(id)["history_total"]!.GetValue<long>());
    }
    [Fact]
    public async Task NotesDoNotNotifyAgainButSnoozeRearmsAndCloseCompletes()
    {
        var id = Create(); var fake = new FakeNative(); var sync = new AppleReminderSync(Store, fake);
        await sync.Sync(Enabled, default);
        Store.Change(id, 1, "note", note: "Controllato, problema ancora aperto");
        Assert.Equal(0, await sync.Sync(Enabled, default));
        Store.Change(id, 2, "title", title: "Titolo corretto"); await sync.Sync(Enabled, default);
        Assert.False(fake.Calls.Last()["rearm"]!.GetValue<bool>());
        Store.Change(id, 3, "snooze", "snooze", next_review_at: "2026-10-21T10:00:00+02:00"); await sync.Sync(Enabled, default);
        Assert.True(fake.Calls.Last()["rearm"]!.GetValue<bool>());
        Store.Change(id, 4, "close", "close", closure_reason: "Esito verificato"); await sync.Sync(Enabled, default);
        Assert.True(fake.Calls.Last()["closed"]!.GetValue<bool>()); Assert.Equal(0, await sync.Sync(Enabled, default));
    }
    [Fact]
    public async Task ClosedBeforeFirstSyncNeverCreatesReminder()
    {
        var id = Create(); Store.Change(id, 1, "close", "close", closure_reason: "Risolto");
        var fake = new FakeNative(); Assert.Equal(0, await new AppleReminderSync(Store, fake).Sync(Enabled, default)); Assert.Empty(fake.Calls);
    }
    [Fact]
    public async Task FailureAfterExternalWriteRetriesSameKeyWithoutLosingIssue()
    {
        var id = Create(); var fake = new FakeNative { FailAfterWrite = true }; var sync = new AppleReminderSync(Store, fake);
        await Assert.ThrowsAsync<MailError>(() => sync.Sync(Enabled, default));
        fake.FailAfterWrite = false; Assert.Equal(1, await sync.Sync(Enabled, default)); Assert.Single(fake.Items);
        Assert.All(fake.Calls, c => Assert.Equal(id, c["followup_id"]!.ToString()));
        Assert.Equal("waiting", Store.Get(id)["followup"]!["status"]!.ToString()); Assert.Equal(1, Store.Get(id)["history_total"]!.GetValue<long>());
    }
    [Fact]
    public async Task OneFailedIssueDoesNotBlockOtherReminders()
    {
        var first = Create();
        var second = Store.Create("Altra questione", "Esito atteso", "2026-10-21T10:00:00Z", "second")["followup"]!["id"]!.ToString();
        var fake = new FakeNative { FailKey = first }; var sync = new AppleReminderSync(Store, fake);
        await Assert.ThrowsAsync<MailError>(() => sync.Sync(Enabled, default));
        Assert.Contains(second, fake.Items.Keys);
        fake.FailKey = null; Assert.Equal(1, await sync.Sync(Enabled, default)); Assert.Equal(2, fake.Items.Count);
    }
    [Fact]
    public async Task ListChangeKeepsExternalIdentityAndPreviousListForRecovery()
    {
        Create(); var fake = new FakeNative(); var sync = new AppleReminderSync(Store, fake); await sync.Sync(Enabled, default);
        var originalId = fake.Items.Values.Single();
        await sync.Sync(Enabled with { CalendarId = "list-two" }, default);
        Assert.Equal(originalId, fake.Calls.Last()["reminder_id"]!.ToString()); Assert.Equal("list-one", fake.Calls.Last()["previous_calendar_id"]!.ToString());
        Assert.False(fake.Calls.Last()["rearm"]!.GetValue<bool>());
    }
    [Fact]
    public async Task ConfigurationRequiresWritableListConsentAndCurrentRevision()
    {
        var fake = new FakeNative(); var settings = new AppleReminderSettings(Profile, fake);
        Assert.False(settings.Load().Enabled); Assert.False(File.Exists(Profile));
        await settings.Save(new("new", Enabled), default); Assert.Equal(Enabled, settings.Load());
        await Assert.ThrowsAsync<SetupConflict>(() => settings.Save(new("new", Enabled), default));
        var revision = SetupStore.Revision(Profile);
        await Assert.ThrowsAsync<MailError>(() => settings.Save(new(revision, Enabled with { CalendarId = "unknown" }), default));
        fake.Deny = true;
        await Assert.ThrowsAsync<MailError>(() => settings.Save(new(revision, Enabled), default));
        await settings.Save(new(revision, new()), default); Assert.False(settings.Load().Enabled);
        if (!OperatingSystem.IsWindows()) Assert.Equal((UnixFileMode)0x180, File.GetUnixFileMode(Profile));
    }
    [Fact]
    public void MissingStaleOrCorruptHeartbeatDoesNotBreakRegisterReads()
    {
        Create(); Assert.False(Store.List()["notifications_enabled"]!.GetValue<bool>());
        var path = Db + ".apple-status.json";
        PrivateFiles.Write(path, "not json"); Assert.Equal("unavailable", AppleReminderAgent.Status(Db)["state"]!.ToString());
        Assert.Equal(1, Store.List()["total"]!.GetValue<long>());
        PrivateFiles.Write(path, new JsonObject { ["state"] = "ok", ["checked_at"] = Validate.Format(DateTimeOffset.UtcNow.AddMinutes(-5)) }.ToJsonString());
        Assert.False(Store.List()["notifications_enabled"]!.GetValue<bool>());
        PrivateFiles.Write(path, new JsonObject { ["state"] = "ok", ["checked_at"] = Validate.Format(DateTimeOffset.UtcNow) }.ToJsonString());
        Assert.True(Store.List()["notifications_enabled"]!.GetValue<bool>());
    }
    [Fact]
    public async Task TestIsExplicitInventedAndIndependentOfRegister()
    {
        var fake = new FakeNative(); var settings = new AppleReminderSettings(Profile, fake);
        await Assert.ThrowsAsync<MailError>(() => settings.Test(default)); Assert.Empty(fake.Calls);
        await settings.Save(new("new", Enabled), default); await settings.Test(default);
        var call = fake.Calls.Last(); Assert.Equal("Prova Mail Companion", call["title"]!.ToString()); Assert.False(File.Exists(Db));
        Assert.InRange(DateTimeOffset.Parse(call["due"]!.ToString()) - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4));
    }
}
