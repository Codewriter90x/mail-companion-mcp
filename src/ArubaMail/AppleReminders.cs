using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace ArubaMail;

public sealed record AppleReminderProfile(bool Enabled = false, string CalendarId = "", string CalendarTitle = "");
public sealed record SaveAppleReminderInput(string Revision, AppleReminderProfile Profile);
public interface IAppleReminders
{
    Task<JsonObject> Call(JsonObject request, CancellationToken ct);
}

/// <summary>Local OS integration, never exposed as an MCP tool or through a public endpoint.</summary>
public sealed class NativeAppleReminders : IAppleReminders
{
    public static string Bundle => Path.Combine(Settings.Root, "artifacts", "apple", "Mail Companion Reminders.app");
    public static bool Available => OperatingSystem.IsMacOSVersionAtLeast(14) && File.Exists(Path.Combine(Bundle, "Contents", "MacOS", "MailCompanionReminders"));
    public async Task<JsonObject> Call(JsonObject request, CancellationToken ct)
    {
        if (!Available) throw new MailError("Promemoria Apple richiede il pannello nativo su macOS 14+ e il componente compilato. Vedi la guida Promemoria.");
        var directory = Path.Combine(Path.GetTempPath(), "mailcompanion-reminders-" + Guid.NewGuid());
        Settings.CreatePrivateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "request.json"); var output = Path.Combine(directory, "response.json");
            PrivateFiles.Write(input, request.ToJsonString()); PrivateFiles.Write(output, "{}");
            await ProcessTools.Run("/usr/bin/open", ["-W", "-n", Bundle, "--args", "--request", input, "--response", output], ct);
            var file = new FileInfo(output); Settings.PrivateFile(output);
            if (file.Length > 65536) throw new MailError("Risposta Promemoria non valida.");
            var response = JsonNode.Parse(await File.ReadAllTextAsync(output, ct))?.AsObject() ?? throw new MailError("Risposta Promemoria assente.");
            if (response["error"] != null) throw new MailError(response["error"]!.GetValue<string>());
            if (response["success"]?.GetValue<bool>() != true) throw new MailError("Operazione Promemoria non completata. Riprova dal pannello.");
            return response;
        }
        finally { Directory.Delete(directory, true); }
    }
}

internal static class ProcessTools
{
    public static async Task Run(string executable, IEnumerable<string> arguments, CancellationToken ct, IDictionary<string, string>? environment = null)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        foreach (var pair in environment ?? new Dictionary<string, string>()) process.StartInfo.Environment[pair.Key] = pair.Value;
        process.Start(); var stderr = process.StandardError.ReadToEndAsync(ct); var stdout = process.StandardOutput.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); await Task.WhenAll(stderr, stdout); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new MailError("Componente locale non avviato. Verifica installazione e permessi del Mac.");
    }
}

public sealed class AppleReminderSettings(string? path = null, IAppleReminders? native = null)
{
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(Settings.ConfigPath)!, "apple-reminders.local.json");
    public string PathName { get; } = Path.GetFullPath(path ?? DefaultPath);
    private readonly IAppleReminders helper = native ?? new NativeAppleReminders();
    public AppleReminderProfile Load()
    {
        if (!File.Exists(PathName)) return new();
        Settings.PrivateFile(PathName);
        var profile = JsonSerializer.Deserialize<AppleReminderProfile>(File.ReadAllText(PathName), Settings.Json) ?? throw new MailError("Configurazione Promemoria non valida.");
        ValidateProfile(profile); return profile;
    }
    private static void ValidateProfile(AppleReminderProfile profile)
    {
        Validate.Text(profile.CalendarId, 1024, profile.Enabled); Validate.Text(profile.CalendarTitle, 300, profile.Enabled);
        if (profile.CalendarId.Any(char.IsControl) || profile.CalendarTitle.Any(char.IsControl)) throw new MailError("Lista Promemoria non valida.");
    }
    public object State() => new { available = NativeAppleReminders.Available, profile = Load(), revision = SetupStore.Revision(PathName), agent_installed = AppleReminderAgent.Installed };
    public async Task<object> Save(SaveAppleReminderInput input, CancellationToken ct)
    {
        ValidateProfile(input.Profile);
        if (input.Profile.Enabled)
        {
            var lists = await helper.Call(new() { ["action"] = "lists" }, ct);
            if (!lists["calendars"]!.AsArray().Any(x => x?["id"]?.GetValue<string>() == input.Profile.CalendarId)) throw new MailError("Lista non disponibile: ricarica le liste di Promemoria.");
        }
        Settings.CreatePrivateDirectory(Path.GetDirectoryName(PathName)!);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var guard = new FileStream(PathName + ".lock", options);
        if (input.Revision != SetupStore.Revision(PathName)) throw new SetupConflict();
        PrivateFiles.Write(PathName, JsonSerializer.Serialize(input.Profile, Settings.Json));
        return State();
    }
    public Task<JsonObject> Lists(bool authorize, CancellationToken ct) => helper.Call(new() { ["action"] = authorize ? "authorize" : "lists" }, ct);
    public async Task<JsonObject> Test(CancellationToken ct)
    {
        var profile = Load(); if (!profile.Enabled) throw new MailError("Prima abilita Promemoria e salva la lista.");
        var result = await helper.Call(new()
        {
            ["action"] = "upsert", ["calendar_id"] = profile.CalendarId, ["followup_id"] = Guid.NewGuid().ToString(),
            ["title"] = "Prova Mail Companion", ["notes"] = "Promemoria inventato per verificare la notifica. Puoi eliminarlo dopo la prova.",
            ["due"] = Validate.Format(DateTimeOffset.UtcNow.AddMinutes(3)), ["rearm"] = true, ["closed"] = false
        }, ct);
        result["message"] = "Promemoria di prova creato, con avviso tra circa 3 minuti. Verifica la sincronizzazione sull’iPhone e le notifiche di Promemoria.";
        return result;
    }
}

public sealed class AppleReminderSync(FollowupStore store, IAppleReminders native)
{
    public async Task<int> Sync(AppleReminderProfile profile, CancellationToken ct)
    {
        if (!profile.Enabled || !File.Exists(store.PathName)) return 0;
        // Same DB as the register; derived delivery state does not change issue revisions/events.
        using var db = store.Open(write: true)!;
        using (var schema = FollowupStore.Command(db, """
            CREATE TABLE IF NOT EXISTS apple_reminder_sync (
            followup_id TEXT PRIMARY KEY REFERENCES followups(id), calendar_id TEXT NOT NULL,
            reminder_id TEXT NOT NULL, fingerprint TEXT NOT NULL, review_at TEXT NOT NULL);
            """)) schema.ExecuteNonQuery();
        var rows = new List<JsonObject>();
        using (var command = FollowupStore.Command(db, "SELECT id,title,expected_outcome,waiting_for,status,next_review_at FROM followups ORDER BY id"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var row = new JsonObject(); for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.GetString(i); rows.Add(row);
            }
        var count = 0; var failures = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            string Value(string key) => row[key]!.GetValue<string>();
            string? previousCalendar = null, previousId = null, previousHash = null, previousReview = null;
            using (var command = FollowupStore.Command(db, "SELECT calendar_id,reminder_id,fingerprint,review_at FROM apple_reminder_sync WHERE followup_id=$p0", [Value("id")]))
            using (var reader = command.ExecuteReader())
                if (reader.Read()) { previousCalendar = reader.GetString(0); previousId = reader.GetString(1); previousHash = reader.GetString(2); previousReview = reader.GetString(3); }
            var closed = Value("status") == "closed";
            if (closed && previousHash == null) continue;
            // Only relevant reminder fields: an ordinary note or revision never re-arms an alert.
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profile.CalendarId + "\n" + row.ToJsonString())));
            if (fingerprint == previousHash) continue;
            var notes = Value("expected_outcome") + (Value("waiting_for") == "" ? "" : "\nIn attesa di: " + Value("waiting_for"));
            try
            {
            var result = await native.Call(new()
            {
                ["action"] = "upsert", ["calendar_id"] = profile.CalendarId, ["previous_calendar_id"] = previousCalendar,
                ["reminder_id"] = previousId, ["followup_id"] = Value("id"), ["title"] = Value("title"),
                ["notes"] = notes, ["due"] = Value("next_review_at"), ["closed"] = closed,
                ["rearm"] = previousReview == null || previousReview != Value("next_review_at")
            }, ct);
            if (result["success"]?.GetValue<bool>() != true || result["reminder_id"] == null) throw new MailError("Sincronizzazione Promemoria non confermata: verrà riprovata.");
            using var save = FollowupStore.Command(db, """
                INSERT INTO apple_reminder_sync(followup_id,calendar_id,reminder_id,fingerprint,review_at) VALUES($p0,$p1,$p2,$p3,$p4)
                ON CONFLICT(followup_id) DO UPDATE SET calendar_id=excluded.calendar_id,reminder_id=excluded.reminder_id,fingerprint=excluded.fingerprint,review_at=excluded.review_at
                """, [Value("id"), profile.CalendarId, result["reminder_id"]!.GetValue<string>(), fingerprint, Value("next_review_at")]);
            save.ExecuteNonQuery(); count++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { failures++; }
        }
        if (failures > 0) throw new MailError("Alcuni promemoria non sono sincronizzati. Gli altri sono stati elaborati; i fallimenti verranno riprovati.");
        return count;
    }
}

public static class AppleReminderAgent
{
    private const string Label = "it.mailcompanion.followups";
    private static string PlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");
    public static bool Installed => OperatingSystem.IsMacOS() && File.Exists(PlistPath);
    public static string StatusPath => Settings.DbPath + ".apple-status.json";
    public static JsonObject Status(string? databasePath = null)
    {
        var path = databasePath == null ? StatusPath : databasePath + ".apple-status.json";
        if (!File.Exists(path)) return new() { ["state"] = "not_started" };
        try
        {
            Settings.PrivateFile(path);
            if (new FileInfo(path).Length > 4096) throw new IOException();
            var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var checkedAt = DateTimeOffset.Parse(state["checked_at"]!.GetValue<string>());
            var age = DateTimeOffset.UtcNow - checkedAt;
            state["stale"] = age > TimeSpan.FromMinutes(2) || age < TimeSpan.FromMinutes(-1); return state;
        }
        catch { return new() { ["state"] = "unavailable", ["stale"] = true }; }
    }
    public static async Task Install(bool enable, CancellationToken ct)
    {
        if (!NativeAppleReminders.Available) throw new MailError("Il servizio richiede macOS e il componente Promemoria compilato.");
        if (enable && !new AppleReminderSettings().Load().Enabled) throw new MailError("Prima abilita e salva la lista Promemoria.");
        var uid = Environment.GetEnvironmentVariable("UID");
        if (string.IsNullOrEmpty(uid))
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/id", "-u") { RedirectStandardOutput = true, UseShellExecute = false })!;
            uid = (await process.StandardOutput.ReadToEndAsync(ct)).Trim(); await process.WaitForExitAsync(ct);
        }
        if (!uint.TryParse(uid, out _)) throw new MailError("Utente macOS non identificato.");
        if (File.Exists(PlistPath))
        {
            try { await ProcessTools.Run("/bin/launchctl", ["bootout", "gui/" + uid, PlistPath], ct); }
            catch (MailError) { /* Already stopped: replacing/removing our own agent is safe. */ }
        }
        if (!enable) { if (File.Exists(PlistPath)) File.Delete(PlistPath); return; }
        var dotnet = Environment.GetEnvironmentVariable("ARUBA_MAIL_DOTNET") ?? Environment.ProcessPath!;
        var assembly = typeof(AppleReminderAgent).Assembly.Location;
        var env = new Dictionary<string, string>
        {
            ["ARUBA_MAIL_ROOT"] = Settings.Root, ["ARUBA_MAIL_CONFIG"] = Settings.ConfigPath,
            ["ARUBA_MAIL_DB_PATH"] = Settings.DbPath, ["ARUBA_MAIL_DOTNET"] = dotnet
        };
        var plist = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
            new XElement("key", "Label"), new XElement("string", Label),
            new XElement("key", "ProgramArguments"), new XElement("array", new[] { dotnet, assembly, "--apple-reminders-worker" }.Select(x => new XElement("string", x))),
            new XElement("key", "EnvironmentVariables"), new XElement("dict", env.SelectMany(x => new[] { new XElement("key", x.Key), new XElement("string", x.Value) })),
            new XElement("key", "RunAtLoad"), new XElement("true"), new XElement("key", "KeepAlive"), new XElement("true"),
            new XElement("key", "ThrottleInterval"), new XElement("integer", 30),
            new XElement("key", "StandardOutPath"), new XElement("string", "/dev/null"), new XElement("key", "StandardErrorPath"), new XElement("string", "/dev/null"))));
        PrivateFiles.Write(PlistPath, plist.ToString());
        await ProcessTools.Run("/bin/launchctl", ["bootstrap", "gui/" + uid, PlistPath], ct);
    }
    public static async Task Run(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new MailError("Il servizio Promemoria gira sul Mac, fuori dal container.");
        Settings.CreatePrivateDirectory(Path.GetDirectoryName(Settings.DbPath)!);
        var lockOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        using var guard = new FileStream(Settings.DbPath + ".apple-sync.lock", lockOptions);
        var sync = new AppleReminderSync(new(), new NativeAppleReminders());
        while (!ct.IsCancellationRequested)
        {
            var status = "disabled";
            try
            {
                var profile = new AppleReminderSettings().Load();
                if (profile.Enabled)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    await sync.Sync(profile, timeout.Token); status = "ok";
                }
            }
            catch (Exception) { status = "retry_pending"; }
            PrivateFiles.Write(StatusPath, new JsonObject { ["state"] = status, ["checked_at"] = Validate.Format(DateTimeOffset.UtcNow) }.ToJsonString());
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
