using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ArubaMail;

namespace ArubaMail.Tests;

public class SetupHttpTests
{
    [Fact]
    public async Task AuthCsrfPrivateAssetsPasswordPreservationAndProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "mail-setup-http-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var config = Path.Combine(root, "accounts.local.json"); var auth = Path.Combine(root, "setup.token");
        var secret = PrivateFiles.EnsureToken(auth);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var process = new Process { StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in new[] { typeof(SetupWeb).Assembly.Location, "--setup", "--port", port.ToString() }) process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["ARUBA_MAIL_CONFIG"] = config; process.StartInfo.Environment["ARUBA_MAIL_SETUP_TOKEN_FILE"] = auth;
        process.StartInfo.Environment["ARUBA_MAIL_PROFILE_PATH"] = Path.Combine(root, "connection.local.json");
        process.StartInfo.Environment["ARUBA_MAIL_MCP_TOKEN_FILE"] = Path.Combine(root, "mcp.token");
        process.StartInfo.Environment["ARUBA_MAIL_DB_PATH"] = Path.Combine(root, "data", "followups.sqlite3");
        process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(25) };
            var ready = false;
            for (var i = 0; i < 100; i++)
            {
                if (process.HasExited) throw new Exception("Setup failed: " + await stderr);
                try { using var response = await http.GetAsync("/"); if (response.IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { }
                await Task.Delay(50);
            }
            Assert.True(ready);
            using (var page = await http.GetAsync("/")) { Assert.Contains("Mail Companion", await page.Content.ReadAsStringAsync()); Assert.Contains("no-store", page.Headers.CacheControl!.ToString()); Assert.True(page.Headers.Contains("Content-Security-Policy")); }
            using (var denied = await http.GetAsync("/setup/api/state")) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            async Task<JsonNode> GetState() => JsonNode.Parse(await http.GetStringAsync("/setup/api/state"))!;
            using (var deniedReminders = await http.GetAsync("/setup/api/reminders")) Assert.True(deniedReminders.IsSuccessStatusCode);
            var state = await GetState(); Assert.Empty(state["accounts"]!.AsArray());
            using (var evil = new HttpRequestMessage(HttpMethod.Post, "/setup/api/profile") { Content = new StringContent("{}", Encoding.UTF8, "application/json") })
            { evil.Headers.Add("Origin", "https://evil.invalid"); using var response = await http.SendAsync(evil); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); }
            using (var evil = new HttpRequestMessage(HttpMethod.Get, "/setup/api/state"))
            { evil.Headers.Host = "evil.invalid"; using var response = await http.SendAsync(evil); Assert.Equal(421, (int)response.StatusCode); }
            using (var form = await http.PostAsync("/setup/api/accounts/save", new FormUrlEncodedContent(new Dictionary<string, string>()))) Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);
            using (var large = await http.PostAsync("/setup/api/accounts/save", new StringContent(new string('x', 65537), Encoding.UTF8, "application/json"))) Assert.True((int)large.StatusCode is 400 or 413);
            var input = new AccountInput("Demo", "demo@example.invalid", "demo", "SYNTHETIC_PASSWORD_FOR_TEST", "imap.example.invalid");
            using (var saved = await http.PostAsJsonAsync("/setup/api/accounts/save", new SaveAccountInput("new", input), Settings.Json)) Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            state = await GetState(); Assert.DoesNotContain(input.Password!, state.ToJsonString()); var revision = state["revision"]!.ToString();
            using (var stale = await http.PostAsJsonAsync("/setup/api/accounts/save", new SaveAccountInput("new", input), Settings.Json)) Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using (var saved = await http.PostAsJsonAsync("/setup/api/accounts/save", new SaveAccountInput(revision, input with { Password = null, Username = "updated" }), Settings.Json)) Assert.True(saved.IsSuccessStatusCode);
            Assert.Equal(input.Password, Settings.ReadAccounts(config)["Demo"].Password);
            using (var tested = await http.PostAsJsonAsync("/setup/api/accounts/test", input, Settings.Json)) { Assert.False(tested.IsSuccessStatusCode); Assert.DoesNotContain(input.Password!, await tested.Content.ReadAsStringAsync()); }
            using (var profile = await http.PostAsJsonAsync("/setup/api/profile", new SaveProfileInput("new", new("open-webui", "streamable-http", 8123)), Settings.Json)) Assert.True(profile.IsSuccessStatusCode, await profile.Content.ReadAsStringAsync());
            state = await GetState(); Assert.Equal("http://127.0.0.1:8123/mcp", state["instructions"]!["endpoint"]!.ToString()); Assert.True(state["http_token_configured"]!.GetValue<bool>());
            using (var reveal = await http.PostAsJsonAsync("/setup/api/token", new { })) { Assert.True(reveal.IsSuccessStatusCode); Assert.Contains(File.ReadAllText(Path.Combine(root, "mcp.token")), await reveal.Content.ReadAsStringAsync()); }
            foreach (var path in new[] { "/accounts.local.json", "/setup.token", "/mcp.token", "/mcp" }) { using var response = await http.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); }
            using (var deleted = await http.PostAsJsonAsync("/setup/api/accounts/delete", new DeleteAccountInput((await GetState())["revision"]!.ToString(), "Demo"), Settings.Json)) Assert.True(deleted.IsSuccessStatusCode);
            Assert.Empty((await GetState())["accounts"]!.AsArray());
        }
        finally
        {
            if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync();
            var logs = await stdout + await stderr; Assert.DoesNotContain(secret, logs); Assert.DoesNotContain("SYNTHETIC_PASSWORD_FOR_TEST", logs);
            Directory.Delete(root, true);
        }
    }
}
