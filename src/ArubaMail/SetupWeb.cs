using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace ArubaMail;

public static class SetupWeb
{
    public static async Task Run(string host, int port, bool openBrowser)
    {
        var container = Environment.GetEnvironmentVariable("ARUBA_MAIL_SETUP_CONTAINER") == "1";
        if (!(host is "127.0.0.1" or "localhost" or "::1") && !(container && host == "0.0.0.0"))
            throw new MailError("Il pannello deve restare locale. Nel container pubblicare soltanto su 127.0.0.1.");
        if (port is < 1024 or > 65535) throw new MailError("Porta pannello non valida.");
        var authPath = Environment.GetEnvironmentVariable("ARUBA_MAIL_SETUP_TOKEN_FILE") ?? Path.Combine(Path.GetDirectoryName(Settings.ConfigPath)!, "setup.token");
        var secret = PrivateFiles.EnsureToken(authPath);
        var expected = Encoding.ASCII.GetBytes("Bearer " + secret);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 65536);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddSingleton(new SetupStore());
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddConcurrencyLimiter("imap-test", o => { o.PermitLimit = 2; o.QueueLimit = 0; });
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter("local", _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            var request = context.Request;
            if (!(request.Host.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]")) { context.Response.StatusCode = 421; return; }
            if (request.Headers.Origin.Count > 0 && (request.Headers.Origin.Count != 1 || request.Headers.Origin[0] != $"{request.Scheme}://{request.Host}"))
            { context.Response.StatusCode = 403; return; }
            if (request.Headers["Sec-Fetch-Site"].ToString() is "cross-site") { context.Response.StatusCode = 403; return; }
            if (request.Path.StartsWithSegments("/setup/api"))
            {
                var auth = request.Headers.Authorization;
                if (auth.Count != 1 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth[0] ?? ""), expected))
                { context.Response.StatusCode = 401; return; }
                if (request.Method != "GET" && !request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
            }
            try { await next(); }
            catch (SetupConflict) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "Configurazione cambiata: ricaricare prima di salvare." }); }
            catch (BadHttpRequestException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Richiesta non valida o troppo grande." }); }
            catch (MailError error) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = error.Message }); }
            catch (Exception) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Operazione non riuscita. Verificare parametri, TLS, credenziali, rete e permessi. Nessun dettaglio sensibile registrato." }); }
        });
        app.UseRateLimiter();
        app.MapGet("/", () => Asset("index.html", "text/html; charset=utf-8"));
        app.MapGet("/setup.js", () => Asset("setup.js", "text/javascript; charset=utf-8"));
        app.MapGet("/setup.css", () => Asset("setup.css", "text/css; charset=utf-8"));
        app.MapGet("/setup/api/state", (SetupStore store) => Results.Json(store.State(), Settings.Json));
        app.MapPost("/setup/api/accounts/save", (SaveAccountInput input, SetupStore store) => Results.Json(store.SaveAccount(input), Settings.Json));
        app.MapPost("/setup/api/accounts/delete", (DeleteAccountInput input, SetupStore store) => Results.Json(store.DeleteAccount(input), Settings.Json));
        app.MapPost("/setup/api/accounts/test", async (AccountInput input, SetupStore store, CancellationToken ct) => Results.Json(await store.TestAccount(input, ct), Settings.Json)).RequireRateLimiting("imap-test");
        app.MapPost("/setup/api/profile", (SaveProfileInput input, SetupStore store) => Results.Json(store.SaveProfile(input), Settings.Json));
        app.MapPost("/setup/api/token", (SetupStore store) => Results.Json(store.RevealToken(), Settings.Json));
        var bindHost = host == "::1" ? "[::1]" : host;
        app.Urls.Add($"http://{bindHost}:{port}");
        if (openBrowser)
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var url = $"http://127.0.0.1:{port}/#{secret}";
                try
                {
                    if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open") { ArgumentList = { url }, UseShellExecute = false });
                    else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    else Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false });
                }
                catch { Console.Error.WriteLine("Aprire il pannello in locale usando il codice nel file setup.token. Non condividerlo."); }
            });
        Console.Error.WriteLine($"Pannello locale: http://127.0.0.1:{port}/ — nessun segreto stampato. Ctrl+C per chiudere.");
        await app.RunAsync();
    }
    private static IResult Asset(string name, string mime)
    {
        using var stream = typeof(SetupWeb).Assembly.GetManifestResourceStream("ArubaMail.SetupAssets." + name) ?? throw new InvalidOperationException();
        using var reader = new StreamReader(stream);
        return Results.Content(reader.ReadToEnd(), mime);
    }
}
