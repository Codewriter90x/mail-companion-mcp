using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArubaMail;

public sealed class MailError(string message) : Exception(message);

public static class Settings
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static string Root => Environment.GetEnvironmentVariable("ARUBA_MAIL_ROOT") ?? FindRoot();
    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "accounts.example.json"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    public static string ConfigPath => Environment.GetEnvironmentVariable("ARUBA_MAIL_CONFIG") ??
        (File.Exists(Path.Combine(Root, "config", "accounts.local.json")) ? Path.Combine(Root, "config", "accounts.local.json") : Path.Combine(Root, "accounts.local.json"));
    public static string ProfilePath => Environment.GetEnvironmentVariable("ARUBA_MAIL_PROFILE_PATH") ?? Path.Combine(Path.GetDirectoryName(ConfigPath)!, "connection.local.json");
    public static string HttpTokenPath => Environment.GetEnvironmentVariable("ARUBA_MAIL_MCP_TOKEN_FILE") ?? Path.Combine(Path.GetDirectoryName(ConfigPath)!, "mcp.token");
    public static string DbPath => Environment.GetEnvironmentVariable("ARUBA_MAIL_DB_PATH") ?? Path.Combine(Root, "data", "followups.sqlite3");
    public static void PrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode)0x3f) != 0)
            throw new MailError("File non privato: impostare permessi 600.");
    }
    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static Dictionary<string, Account> Accounts() => ReadAccounts(ConfigPath);
    public static Dictionary<string, Account> ReadAccounts(string path, bool allowEmpty = false)
    {
        try
        {
            PrivateFile(path);
            var accounts = JsonSerializer.Deserialize<AccountFile>(File.ReadAllText(path), Json)?.Accounts ?? [];
            var result = new Dictionary<string, Account>(StringComparer.Ordinal);
            foreach (var account in accounts)
            {
                if (string.IsNullOrWhiteSpace(account.Id) || !result.TryAdd(account.Id, account) ||
                    new[] { account.Email, account.Username, account.Password, account.ImapHost }.Any(string.IsNullOrWhiteSpace) || account.ImapPort is < 1 or > 65535)
                    throw new MailError("Configurazione incompleta o non valida.");
            }
            if (!allowEmpty && result.Count == 0) throw new MailError("Nessuna casella configurata.");
            return result;
        }
        catch (MailError) { throw; }
        catch { throw new MailError("Configurazione assente o non valida: controllare accounts.local.json localmente, senza incollare credenziali."); }
    }
    public static Account Account(string id) => Accounts().TryGetValue(id, out var value) ? value : throw new MailError("Casella non configurata. Usare list_accounts.");
}
public sealed record AccountFile(Account[] Accounts);
public sealed record Account(string Id, string Email, string Username, string Password, string ImapHost, int ImapPort = 993);

public static partial class Validate
{
    public static string Text(string? value, int max, bool required = false)
    {
        if (value is null || value.Length > max || value.Contains('\0') || (required && string.IsNullOrWhiteSpace(value)))
            throw new MailError($"Testo non valido o troppo lungo (max {max}).");
        return value.Trim();
    }
    public static string Imap(string value) => value.Length is > 0 and <= 512 && !value.Any(char.IsControl)
        ? value : throw new MailError("Parametro IMAP non valido.");
    public static uint Uid(string value) => Regex.IsMatch(value, "^[1-9][0-9]{0,9}$") && uint.TryParse(value, out var uid) ? uid : throw new MailError("UID/UIDVALIDITY non valido.");
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex OffsetDate();
    public static string Timestamp(string value)
    {
        if (!OffsetDate().IsMatch(value) || !DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            throw new MailError("Data non valida: usare ISO 8601 con offset esplicito e chiarire date ambigue.");
        return Format(date);
    }
    public static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz");
    public static JsonArray People(IEnumerable<string>? people)
    {
        var list = (people ?? []).ToArray();
        if (list.Length > 20) throw new MailError("Massimo 20 partecipanti.");
        return new JsonArray(list.Select(v => Text(v, 200, true)).Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    }
    public static JsonArray References(IEnumerable<Dictionary<string, string>>? references)
    {
        var list = (references ?? []).ToArray();
        if (list.Length > 30) throw new MailError("Massimo 30 riferimenti email.");
        var result = new JsonArray();
        string[] keys = ["account_id", "folder", "message_uid", "uidvalidity", "message_id"];
        foreach (var item in list)
        {
            if (item.Keys.Except(keys).Any()) throw new MailError("Riferimento email non valido. Non salvare corpi o allegati.");
            var row = new JsonObject();
            foreach (var key in keys) row[key] = Text(item.GetValueOrDefault(key, ""), key == "message_id" ? 998 : 512, key != "message_id");
            Uid(row["message_uid"]!.GetValue<string>()); Uid(row["uidvalidity"]!.GetValue<string>());
            if (!result.Any(v => JsonNode.DeepEquals(v, row))) result.Add(row);
        }
        return result;
    }
}
