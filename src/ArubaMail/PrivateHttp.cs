using System.Security.Cryptography;
using System.Text;

namespace ArubaMail;

public static class PrivateHttp
{
    public static byte[] LoadToken()
    {
        var path = Environment.GetEnvironmentVariable("ARUBA_MAIL_MCP_TOKEN_FILE") ?? throw new MailError("Token non configurato.");
        Settings.PrivateFile(path); var value = File.ReadAllText(path).Trim();
        if (value.Length is < 32 or > 512 || value.Any(c => c > 127 || char.IsWhiteSpace(c))) throw new MailError("Token non valido.");
        return Encoding.ASCII.GetBytes("Bearer " + value);
    }
    public static bool AllowedHost(string host)
    {
        var allowed = (Environment.GetEnvironmentVariable("ARUBA_MAIL_ALLOWED_HOSTS") ?? "127.0.0.1:*,localhost:*,[::1]:*,aruba-mail:*").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return allowed.Any(a => a.EndsWith(":*", StringComparison.Ordinal) ? host.Equals(a[..^2], StringComparison.OrdinalIgnoreCase) || (host.StartsWith(a[..^1], StringComparison.OrdinalIgnoreCase) && int.TryParse(host[(a.Length - 1)..], out var p) && p is > 0 and <= 65535) : host.Equals(a, StringComparison.OrdinalIgnoreCase));
    }
    public static async Task Handle(HttpContext context, Func<Task> next, byte[] expected)
    {
        var auth = context.Request.Headers.Authorization;
        if (auth.Count != 1 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth[0] ?? ""), expected))
        { context.Response.StatusCode = 401; context.Response.Headers.WWWAuthenticate = "Bearer"; return; }
        if (!AllowedHost((context.Request.Host.Value ?? ""))) { context.Response.StatusCode = 421; return; }
        var origins = context.Request.Headers.Origin;
        if (origins.Count > 0 && (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) || origin.Scheme != "http" || !(origin.Host is "127.0.0.1" or "localhost" or "[::1]")))
        { context.Response.StatusCode = 403; return; }
        if (context.Request.ContentLength > 65536) { context.Response.StatusCode = 413; return; }
        if (context.Request.Method is "POST" or "PUT" or "PATCH")
        {
            // Bound chunked requests too. Request bodies contain tool inputs; never log them.
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
            try
            {
                while ((count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted)) > 0)
                { if (buffer.Length + count > 65536) { context.Response.StatusCode = 413; return; } buffer.Write(bytes, 0, count); }
            }
            catch (BadHttpRequestException) { context.Response.StatusCode = 413; return; }
            buffer.Position = 0; var original = context.Request.Body; context.Request.Body = buffer;
            try { await next(); } finally { context.Request.Body = original; }
            return;
        }
        await next();
    }
}
