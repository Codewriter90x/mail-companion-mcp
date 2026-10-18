using ArubaMail;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
string Option(string key, string fallback) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
if (args.Contains("--setup"))
{
    try { await SetupWeb.Run(Option("--host", "127.0.0.1"), int.Parse(Option("--port", "8765")), args.Contains("--open-browser")); }
    catch { Console.Error.WriteLine("Pannello non avviato: verificare porta locale, configurazione e permessi."); Environment.ExitCode = 2; }
    return;
}
var selectedProfile = args.Contains("--configured") ? SetupStore.LoadProfile(Settings.ProfilePath) : null;
var transport = Option("--transport", selectedProfile?.Transport ?? "stdio");
if (transport == "stdio")
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
    builder.Logging.ClearProviders();
    builder.Services.AddMcpServer(o => { o.ServerInfo = new Implementation { Name = "Aruba Mail", Version = "2.1.0" }; o.ServerInstructions = Tools.Instructions; })
        .WithStdioServerTransport().WithTools<Tools>();
    await builder.Build().RunAsync();
}
else if (transport == "streamable-http")
{
    byte[] token;
    try { token = PrivateHttp.LoadToken(); }
    catch { Console.Error.WriteLine("HTTP non avviato: configurare file token privato 600, almeno 32 caratteri casuali."); Environment.ExitCode = 2; return; }
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 65536);
    builder.Services.AddMcpServer(o => { o.ServerInfo = new Implementation { Name = "Aruba Mail", Version = "2.1.0" }; o.ServerInstructions = Tools.Instructions; })
        .WithHttpTransport(o => { o.SessionMode = HttpServerSessionMode.Stateless; }).WithTools<Tools>();
    var app = builder.Build();
    app.Use((context, next) => PrivateHttp.Handle(context, next, token));
    app.MapMcp("/mcp");
    await app.RunAsync($"http://{Option("--host", "127.0.0.1")}:{Option("--port", (selectedProfile?.Port ?? 8000).ToString())}");
}
else { Console.Error.WriteLine("Trasporto non supportato: stdio oppure streamable-http."); Environment.ExitCode = 2; }
