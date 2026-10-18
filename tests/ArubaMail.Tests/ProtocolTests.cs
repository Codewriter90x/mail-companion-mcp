using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ArubaMail;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ArubaMail.Tests;

public sealed class ProtocolTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "aruba-protocol-" + Guid.NewGuid());
    private string Db => Path.Combine(directory, "register.sqlite3");
    private string Config => Path.Combine(directory, "accounts.local.json");
    private string Token => Path.Combine(directory, "token");
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public ProtocolTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Config, "{\"accounts\":[{\"id\":\"test\",\"email\":\"test@example.invalid\",\"username\":\"test\",\"password\":\"SYNTHETIC_PASSWORD_ONLY\",\"imap_host\":\"example.invalid\"}]}");
        File.WriteAllText(Token, secret); if (!OperatingSystem.IsWindows()) foreach (var path in new[] { Config, Token }) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private string Server => typeof(MailService).Assembly.Location;
    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    private Task<McpClient> ConnectStdio() => McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "Aruba C# test",
        Command = Dotnet,
        Arguments = [Server],
        EnvironmentVariables = new Dictionary<string, string?>
        { ["ARUBA_MAIL_CONFIG"] = Config, ["ARUBA_MAIL_DB_PATH"] = Db }
    }));
    private static async Task<JsonObject> Call(McpClient client, string name, Dictionary<string, object?>? args = null)
    {
        var result = await client.CallToolAsync(name, args ?? []); Assert.False(result.IsError == true);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(v => v.Text));
        var data = JsonNode.Parse(text)!.AsObject(); Assert.False(data.ContainsKey("error"), text); return data;
    }
    private static async Task CheckTools(McpClient client)
    {
        var tools = await client.ListToolsAsync(); string[] expected = ["list_accounts", "list_folders", "list_emails", "search_emails", "read_email", "mark_as_read", "create_followup", "list_followups", "get_followup", "update_followup", "snooze_followup", "close_followup"];
        Assert.Equal(expected.Order(), tools.Select(v => v.Name).Order());
        foreach (var tool in tools)
        {
            var schema = tool.ProtocolTool;
            Assert.Equal(tool.Name.StartsWith("list_") || tool.Name.StartsWith("get_") || tool.Name is "search_emails" or "read_email", schema.Annotations?.ReadOnlyHint);
            Assert.False(schema.Annotations?.DestructiveHint);
        }
    }
    [Fact]
    public async Task StdioDiscoveryReadsWritesRetryAndPersistenceAfterRestart()
    {
        string id;
        await using (var client = await ConnectStdio())
        {
            await CheckTools(client); var accounts = await Call(client, "list_accounts"); Assert.DoesNotContain("SYNTHETIC_PASSWORD_ONLY", accounts.ToJsonString()); Assert.Single(accounts["accounts"]!.AsArray());
            Assert.Equal("0", (await Call(client, "list_followups"))["total"]!.ToString());
            var args = new Dictionary<string, object?> { ["title"] = "Porta test", ["expected_outcome"] = "Ripristino verificato", ["next_review_at"] = "2099-10-23T10:00:00+02:00", ["request_id"] = "create", ["participants"] = new[] { "A", "B", "C", "D" } };
            var created = await Call(client, "create_followup", args); id = created["followup"]!["id"]!.ToString(); Assert.True((await Call(client, "create_followup", args))["replayed"]!.GetValue<bool>());
            await Call(client, "update_followup", new() { ["followup_id"] = id, ["expected_revision"] = 1, ["request_id"] = "note", ["note"] = "Risposta interlocutoria" });
            await Call(client, "snooze_followup", new() { ["followup_id"] = id, ["expected_revision"] = 2, ["request_id"] = "later", ["next_review_at"] = "2099-10-24T10:00:00+02:00" });
        }
        await using (var client = await ConnectStdio())
        {
            var row = await Call(client, "get_followup", new() { ["followup_id"] = id }); Assert.Equal("3", row["history_total"]!.ToString()); Assert.Equal("open", row["followup"]!["status"]!.ToString());
            await Call(client, "close_followup", new() { ["followup_id"] = id, ["expected_revision"] = 3, ["request_id"] = "close", ["closure_reason"] = "Prova conclusa" });
            Assert.Equal("0", (await Call(client, "list_followups"))["total"]!.ToString());
        }
    }
    [Fact]
    public async Task HttpAuthHostOriginLimitsAndRealMcpFlow()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var process = new Process { StartInfo = new ProcessStartInfo(Dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in new[] { Server, "--transport", "streamable-http", "--host", "127.0.0.1", "--port", port.ToString() }) process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["ARUBA_MAIL_CONFIG"] = Config; process.StartInfo.Environment["ARUBA_MAIL_DB_PATH"] = Db; process.StartInfo.Environment["ARUBA_MAIL_MCP_TOKEN_FILE"] = Token;
        var url = new Uri($"http://127.0.0.1:{port}/mcp"); process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; var ready = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (process.HasExited) throw new Exception("HTTP start failed: " + await stderr);
                try { using var response = await http.GetAsync(url); if (response.StatusCode == HttpStatusCode.Unauthorized) { ready = true; break; } } catch (HttpRequestException) { }
                await Task.Delay(50);
            }
            Assert.True(ready, "Server HTTP non pronto");
            using (var noauth = await http.PostAsync(url, new StringContent("{}"))) Assert.Equal(HttpStatusCode.Unauthorized, noauth.StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using (var badHost = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}") }) { badHost.Headers.Host = "attacker.invalid"; using var response = await http.SendAsync(badHost); Assert.Equal(421, (int)response.StatusCode); }
            using (var badOrigin = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}") }) { badOrigin.Headers.Add("Origin", "https://attacker.invalid"); using var response = await http.SendAsync(badOrigin); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); }
            using (var oversized = await http.PostAsync(url, new StringContent(new string('x', 65537)))) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            using (var chunked = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StreamContent(new NonSeekableStream(new byte[65537])) }) { chunked.Headers.TransferEncodingChunked = true; try { using var response = await http.SendAsync(chunked); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); } catch (HttpRequestException) { /* Kestrel may close a chunked connection once its 64 KiB body limit is reached. */ } }
            var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = url, TransportMode = HttpTransportMode.StreamableHttp, AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret } });
            await using var client = await McpClient.CreateAsync(transport); await CheckTools(client);
            var created = await Call(client, "create_followup", new() { ["title"] = "HTTP test", ["expected_outcome"] = "Verificato", ["next_review_at"] = "2099-10-23T10:00:00+02:00", ["request_id"] = "http-create" });
            var id = created["followup"]!["id"]!.ToString(); Assert.Equal("1", (await Call(client, "list_followups"))["total"]!.ToString());
            await Call(client, "close_followup", new() { ["followup_id"] = id, ["expected_revision"] = 1, ["request_id"] = "http-close", ["closure_reason"] = "Test concluso" });
        }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); Assert.DoesNotContain(secret, await stdout + await stderr); }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
}
