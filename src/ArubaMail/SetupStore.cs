using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArubaMail;

public sealed record AccountInput(string Id, string Email, string Username, string? Password, string ImapHost, int ImapPort = 993);
public sealed record SaveAccountInput(string Revision, AccountInput Account);
public sealed record DeleteAccountInput(string Revision, string Id);
public sealed record ConnectionProfile(string Client = "codex", string Transport = "stdio", int Port = 8000, string WebuiLocation = "host");
public sealed record SaveProfileInput(string Revision, ConnectionProfile Profile);
public sealed class SetupConflict() : Exception("Configurazione cambiata: ricaricare il pannello prima di salvare.");

public static class PrivateFiles
{
    public static string EnsureToken(string path)
    {
        Settings.CreatePrivateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!File.Exists(path))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using var file = new FileStream(path, options);
                var bytes = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
                file.Write(bytes); file.Flush(true);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        Settings.PrivateFile(path);
        var token = File.ReadAllText(path).Trim();
        if (token.Length is < 32 or > 512 || token.Any(c => c > 127 || char.IsWhiteSpace(c))) throw new MailError("Token locale non valido.");
        return token;
    }
    public static void Write(string path, string content)
    {
        path = Path.GetFullPath(path);
        Settings.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new MailError("File simbolico non consentito.");
            Settings.PrivateFile(path);
        }
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".setup-" + Guid.NewGuid() + ".secret");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options))
            {
                file.Write(Encoding.UTF8.GetBytes(content)); file.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

/// <summary>Local administrative configuration. Never registered as MCP tools.</summary>
public sealed class SetupStore(string? configPath = null, string? profilePath = null, string? tokenPath = null,
    Func<Account, CancellationToken, Task>? testConnection = null)
{
    public string ConfigPath { get; } = Path.GetFullPath(configPath ?? Settings.ConfigPath);
    public string ProfilePath { get; } = Path.GetFullPath(profilePath ?? Settings.ProfilePath);
    public string TokenPath { get; } = Path.GetFullPath(tokenPath ?? Settings.HttpTokenPath);
    private static readonly JsonSerializerOptions Json = new(Settings.Json) { WriteIndented = true };
    public static string Revision(string path) => File.Exists(path)
        ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "new";
    private Dictionary<string, Account> Accounts() => File.Exists(ConfigPath) ? Settings.ReadAccounts(ConfigPath, allowEmpty: true) : new(StringComparer.Ordinal);
    private FileStream Lock()
    {
        Settings.CreatePrivateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(Path.GetDirectoryName(ConfigPath)!, ".setup.lock"), options);
    }
    public ConnectionProfile Profile() => LoadProfile(ProfilePath);
    public static ConnectionProfile LoadProfile(string path)
    {
        if (!File.Exists(path)) return new();
        Settings.PrivateFile(path);
        var value = JsonSerializer.Deserialize<ConnectionProfile>(File.ReadAllText(path), Settings.Json) ?? throw new MailError("Profilo non valido.");
        ValidateProfile(value); return value;
    }
    public object State() => new
    {
        revision = Revision(ConfigPath),
        profile_revision = Revision(ProfilePath),
        accounts = Accounts().Values.Select(a => new { a.Id, a.Email, a.Username, a.ImapHost, a.ImapPort, password_configured = a.Password.Length > 0 }).ToArray(),
        profile = Profile(),
        http_token_configured = File.Exists(TokenPath),
        instructions = Instructions(Profile()),
        notifications_enabled = false
    };
    private static void ValidateProfile(ConnectionProfile p)
    {
        if (!new[] { "codex", "open-webui", "other" }.Contains(p.Client) || !new[] { "stdio", "streamable-http" }.Contains(p.Transport)
            || p.Port is < 1024 or > 65535 || !new[] { "host", "docker" }.Contains(p.WebuiLocation)) throw new MailError("Profilo non valido: scegliere client, trasporto e porta tra 1024 e 65535.");
        if (p.Client == "open-webui" && p.Transport != "streamable-http") throw new MailError("Open WebUI richiede Streamable HTTP.");
    }
    private Account Resolve(AccountInput input)
    {
        var id = Validate.Text(input.Id, 100, true);
        if (id.Any(char.IsControl)) throw new MailError("Nome casella non valido.");
        var email = Validate.Text(input.Email, 254, true);
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email) throw new MailError("Indirizzo email non valido.");
        var user = Validate.Text(input.Username, 512, true);
        var host = Validate.Text(input.ImapHost, 253, true);
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || input.ImapPort is < 1 or > 65535) throw new MailError("Server o porta IMAP non validi.");
        var password = input.Password;
        if (string.IsNullOrEmpty(password)) password = Accounts().GetValueOrDefault(id)?.Password;
        if (string.IsNullOrWhiteSpace(password) || password.Length > 4096 || password.Any(c => c is '\0' or '\r' or '\n')) throw new MailError("Inserire una password valida. Il campo vuoto conserva quella già salvata.");
        // Do not trim password: leading/trailing spaces may be meaningful.
        return new(id, email, user, password, host, input.ImapPort);
    }
    public object SaveAccount(SaveAccountInput input)
    {
        using var guard = Lock();
        if (input.Revision != Revision(ConfigPath)) throw new SetupConflict();
        var account = Resolve(input.Account); var accounts = Accounts();
        if (!accounts.ContainsKey(account.Id) && accounts.Count >= 20) throw new MailError("Massimo 20 caselle.");
        accounts[account.Id] = account; SaveAccounts(accounts.Values);
        return State();
    }
    public object DeleteAccount(DeleteAccountInput input)
    {
        using var guard = Lock();
        if (input.Revision != Revision(ConfigPath)) throw new SetupConflict();
        var accounts = Accounts();
        if (!accounts.Remove(input.Id)) throw new MailError("Casella non trovata.");
        SaveAccounts(accounts.Values); return State();
    }
    private void SaveAccounts(IEnumerable<Account> accounts)
    {
        if (File.Exists(ConfigPath)) PrivateFiles.Write(Path.Combine(Path.GetDirectoryName(ConfigPath)!, "accounts.previous.secret"), File.ReadAllText(ConfigPath));
        PrivateFiles.Write(ConfigPath, JsonSerializer.Serialize(new AccountFile(accounts.ToArray()), Json));
    }
    public async Task<object> TestAccount(AccountInput input, CancellationToken ct)
    {
        var account = Resolve(input);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        if (testConnection != null) await testConnection(account, deadline.Token);
        else { await using var session = await MailConnection.Connect(account, deadline.Token); }
        return new { success = true, message = "Connessione TLS e accesso IMAP riusciti. Nessuna email letta o modificata." };
    }
    public object SaveProfile(SaveProfileInput input)
    {
        ValidateProfile(input.Profile);
        using var guard = Lock();
        if (input.Revision != Revision(ProfilePath)) throw new SetupConflict();
        // Create a separate MCP token without returning it from ordinary state responses.
        if (input.Profile.Transport == "streamable-http") PrivateFiles.EnsureToken(TokenPath);
        PrivateFiles.Write(ProfilePath, JsonSerializer.Serialize(input.Profile, Json)); return State();
    }
    public object RevealToken()
    {
        if (!File.Exists(TokenPath)) throw new MailError("Prima salvare un profilo HTTP.");
        return new { token = PrivateFiles.EnsureToken(TokenPath) };
    }
    public object Instructions(ConnectionProfile profile)
    {
        ValidateProfile(profile);
        // Paths are quoted as shell data. No secret is ever part of a generated command.
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        var root = Environment.GetEnvironmentVariable("ARUBA_MAIL_SETUP_PROJECT_DIR") ?? Settings.Root;
        var source = Environment.GetEnvironmentVariable("ARUBA_MAIL_SETUP_CONFIG_DIR") ?? Path.GetDirectoryName(ConfigPath)!;
        var native = Quote(Path.Combine(root, "scripts", "run-native.sh"));
        var inContainer = Environment.GetEnvironmentVariable("ARUBA_MAIL_SETUP_CONTAINER") == "1";
        var image = Quote(Environment.GetEnvironmentVariable("ARUBA_MAIL_SETUP_IMAGE") ?? "aruba-mail-mcp:local");
        var hostProcess = Environment.ProcessPath;
        var dotnet = !inContainer && hostProcess != null && Path.GetFileNameWithoutExtension(hostProcess).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? " --env ARUBA_MAIL_DOTNET=" + Quote(hostProcess) : "";
        var config = Quote(Path.Combine(source, "accounts.local.json"));
        var token = Quote(Path.Combine(source, "mcp.token"));
        var stdioNative = $"codex mcp add aruba-mail --env ARUBA_MAIL_CONFIG={config}{dotnet} -- {native} --transport stdio";
        var stdioDocker = $"codex mcp add aruba-mail --env ARUBA_MAIL_CONFIG={config} --env ARUBA_MAIL_IMAGE={image} -- {Quote(Path.Combine(root, "scripts", "run-container.sh"))}";
        var compose = $"ARUBA_MAIL_IMAGE={image} ARUBA_MAIL_CONFIG={config} ARUBA_MAIL_MCP_TOKEN_FILE={token} LOCAL_UID=\"$(id -u)\" LOCAL_GID=\"$(id -g)\" docker compose -f {Quote(Path.Combine(root, "compose.openwebui.yml"))} up -d --no-build --force-recreate";
        var startHttp = $"ARUBA_MAIL_IMAGE={image} ARUBA_MAIL_CONFIG={config} ARUBA_MAIL_MCP_TOKEN_FILE={token} ARUBA_MAIL_HTTP_PORT={profile.Port} {Quote(Path.Combine(root, "scripts", "run-http-container.sh"))}";
        var endpoint = profile.WebuiLocation == "docker" && profile.Client == "open-webui" ? "http://aruba-mail:8000/mcp" : $"http://127.0.0.1:{profile.Port}/mcp";
        var codexHttp = $"codex mcp add aruba-mail --url {Quote(endpoint)} --bearer-token-env-var ARUBA_MAIL_MCP_TOKEN";
        return new
        {
            stdio_native = stdioNative,
            stdio_docker = stdioDocker,
            start_http = profile.Client == "open-webui" && profile.WebuiLocation == "docker" ? compose : startHttp,
            endpoint,
            connect = profile.Client == "codex" ? (profile.Transport == "stdio" ? (inContainer ? stdioDocker : stdioNative) : codexHttp) : profile.Transport == "stdio" ? native + " --transport stdio" : "Open WebUI: Settings → Admin → Integrations → External Tool Servers → Add Connection. Tipo MCP (Streamable HTTP), indirizzo indicato, autenticazione Bearer e token nel campo Key. Per un altro client scegli gli equivalenti campi MCP HTTP e Bearer.",
            note = profile.WebuiLocation == "docker" && profile.Client == "open-webui"
                ? "Open WebUI e bridge devono condividere la rete Docker privata ai-local. Usa compose.openwebui.yml con la directory configurata. Non usare localhost nel container Open WebUI."
                : profile.Client == "codex" && profile.Transport == "streamable-http"
                    ? "Imposta ARUBA_MAIL_MCP_TOKEN nell’ambiente del client Codex con il token copiato dal pannello, poi ricarica MCP. Non inserire il valore in un comando condiviso."
                    : "Se aruba-mail è già registrato, verifica la configurazione prima di sostituirla. Ricarica MCP dopo il collegamento.",
            restart_required = true
        };
    }
}
