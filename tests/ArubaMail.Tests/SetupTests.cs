using System.Text.Json;
using System.Text.Json.Nodes;
using ArubaMail;

namespace ArubaMail.Tests;

public sealed class SetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mail-setup-" + Guid.NewGuid());
    private string Config => Path.Combine(root, "accounts.local.json");
    private string Profile => Path.Combine(root, "connection.local.json");
    private string Token => Path.Combine(root, "mcp.token");
    private SetupStore Store => new(Config, Profile, Token);
    private static AccountInput Input(string id = "Ufficio", string? password = " SYNTHETIC_PASSWORD ") => new(id, "test@example.invalid", "test@example.invalid", password, "imap.example.invalid", 993);
    private static JsonNode View(object value) => JsonSerializer.SerializeToNode(value, Settings.Json)!;
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact]
    public void EmptyStateDoesNotCreateCredentialsOrDatabase()
    {
        var state = View(Store.State()); Assert.Empty(state["accounts"]!.AsArray()); Assert.Equal("new", state["revision"]!.ToString()); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public void SavingAccountHidesPasswordAndKeepsPrivatePermissions()
    {
        var state = View(Store.SaveAccount(new("new", Input()))); Assert.DoesNotContain("SYNTHETIC_PASSWORD", state.ToJsonString());
        Assert.True(state["accounts"]![0]!["password_configured"]!.GetValue<bool>());
        Assert.Equal(" SYNTHETIC_PASSWORD ", Settings.ReadAccounts(Config)["Ufficio"].Password);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Config));
    }
    [Fact]
    public void EmptyPasswordRetainsExistingAndBackupIsPrivate()
    {
        Store.SaveAccount(new("new", Input())); var revision = SetupStore.Revision(Config);
        Store.SaveAccount(new(revision, Input(password: null) with { ImapHost = "new.example.invalid" }));
        Assert.Equal(" SYNTHETIC_PASSWORD ", Settings.ReadAccounts(Config)["Ufficio"].Password);
        var backup = Path.Combine(root, "accounts.previous.secret"); Assert.True(File.Exists(backup));
        Assert.Contains("imap.example.invalid", File.ReadAllText(backup));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backup));
    }
    [Fact]
    public void StaleRevisionNeverOverwrites()
    {
        Store.SaveAccount(new("new", Input())); Assert.Throws<SetupConflict>(() => Store.SaveAccount(new("new", Input(password: "CHANGED"))));
        Assert.Equal(" SYNTHETIC_PASSWORD ", Settings.ReadAccounts(Config)["Ufficio"].Password);
    }
    [Fact]
    public void AddingAndDeletingPreservesOtherAccountsAndAllowsEmptyRegister()
    {
        Store.SaveAccount(new("new", Input())); Store.SaveAccount(new(SetupStore.Revision(Config), Input("Seconda")));
        Store.DeleteAccount(new(SetupStore.Revision(Config), "Ufficio")); Assert.Single(Settings.ReadAccounts(Config));
        Store.DeleteAccount(new(SetupStore.Revision(Config), "Seconda")); Assert.Empty(View(Store.State())["accounts"]!.AsArray()); Assert.Throws<MailError>(() => Settings.ReadAccounts(Config));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidPortRejected(int port) { Assert.Throws<MailError>(() => Store.SaveAccount(new("new", Input() with { ImapPort = port }))); Assert.False(File.Exists(Config)); }
    [Fact]
    public void NewAccountNeedsPasswordAndValidHostAndEmail()
    {
        Assert.Throws<MailError>(() => Store.SaveAccount(new("new", Input(password: null))));
        Assert.Throws<MailError>(() => Store.SaveAccount(new("new", Input() with { ImapHost = "http://server/path" })));
        Assert.Throws<MailError>(() => Store.SaveAccount(new("new", Input() with { Email = "not-email" })));
    }
    [Fact]
    public async Task ConnectionTestDoesNotSaveAndCanUseStoredPassword()
    {
        Account? tested = null;
        var store = new SetupStore(Config, Profile, Token, (a, _) => { tested = a; return Task.CompletedTask; });
        await store.TestAccount(Input(), default); Assert.Equal(" SYNTHETIC_PASSWORD ", tested!.Password); Assert.False(File.Exists(Config));
        store.SaveAccount(new("new", Input())); var old = File.ReadAllText(Config);
        await store.TestAccount(Input(password: null), default); Assert.Equal(old, File.ReadAllText(Config));
    }
    [Fact]
    public void ProfileCreatesSeparateTokenButDoesNotReturnIt()
    {
        var state = View(Store.SaveProfile(new("new", new("open-webui", "streamable-http", 8123)))); var token = File.ReadAllText(Token);
        Assert.True(token.Length >= 32); Assert.DoesNotContain(token, state.ToJsonString()); Assert.False(File.Exists(Config));
        Assert.Equal("http://127.0.0.1:8123/mcp", state["instructions"]!["endpoint"]!.ToString());
        Store.SaveProfile(new(SetupStore.Revision(Profile), new("codex", "streamable-http", 8124))); Assert.Equal(token, File.ReadAllText(Token));
        Assert.Equal(token, View(Store.RevealToken())["token"]!.ToString());
    }
    [Fact]
    public void StdioDoesNotGenerateHttpToken()
    {
        var state = View(Store.SaveProfile(new("new", new()))); Assert.False(File.Exists(Token)); Assert.Contains("--transport stdio", state["instructions"]!["connect"]!.ToString());
        Assert.Equal("stdio", SetupStore.LoadProfile(Profile).Transport);
    }
    [Fact]
    public void RejectInvalidClientTransportAndStaleProfile()
    {
        Assert.Throws<MailError>(() => Store.SaveProfile(new("new", new("open-webui", "stdio"))));
        Assert.Throws<MailError>(() => Store.SaveProfile(new("new", new("unknown", "stdio"))));
        Store.SaveProfile(new("new", new())); Assert.Throws<SetupConflict>(() => Store.SaveProfile(new("new", new("codex", "streamable-http")))); Assert.False(File.Exists(Token));
    }
    [Fact]
    public void WebuiDockerHasPrivateNetworkInstructions()
    {
        var state = View(Store.SaveProfile(new("new", new("open-webui", "streamable-http", 8000, "docker"))));
        Assert.Equal("http://aruba-mail:8000/mcp", state["instructions"]!["endpoint"]!.ToString()); Assert.Contains("compose", state["instructions"]!["start_http"]!.ToString());
    }
    [Fact]
    public void OtherClientStdioReceivesServerCommandRatherThanHttpInstructions()
    {
        var state = View(Store.SaveProfile(new("new", new("other", "stdio"))));
        Assert.Contains("--transport stdio", state["instructions"]!["connect"]!.ToString());
        Assert.DoesNotContain("Streamable HTTP", state["instructions"]!["connect"]!.ToString());
    }
    [Fact]
    public void InsecureCredentialsCannotBeOverwrittenByWizard()
    {
        if (OperatingSystem.IsWindows()) return;
        Store.SaveAccount(new("new", Input())); File.SetUnixFileMode(Config, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Assert.Throws<MailError>(() => Store.SaveAccount(new(SetupStore.Revision(Config), Input(password: "NEW"))));
    }
}
