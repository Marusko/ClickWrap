using ClickWrap.Server.Api;
using ClickWrap.Server.Components;
using ClickWrap.Server.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

var options = ServerOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<AppStore>();

// Bind to all interfaces by default: this is reached through a Cloudflare Tunnel, not loopback.
// An explicit ASPNETCORE_URLS (or the dev launch profile) still wins.
if (string.IsNullOrWhiteSpace(builder.Configuration[ServerOptions.UrlsVariable]) &&
    string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls(ServerOptions.DefaultUrls);
}

// cloudflared connects from inside the container network, so the proxy is not in a known range.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

// Data Protection keys otherwise land in the container filesystem and are lost on every redeploy.
// Keeping them on the data volume also means a second replica would agree with the first.
// The leading dot keeps the folder out of the app listing: AppStore ids must start alphanumeric.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(options.DataPath, ".dataprotection-keys")))
    .SetApplicationName("ClickWrap");

// The data folder is the only thing this server needs to work, and an unmounted volume is the
// failure it cannot recover from on its own -- so that is what /health actually checks.
builder.Services.AddHealthChecks()
    .AddCheck(
        "data-path",
        () => Directory.Exists(options.DataPath)
            ? HealthCheckResult.Healthy($"Data path available at {options.DataPath}.")
            : HealthCheckResult.Unhealthy($"Data path {options.DataPath} is missing."),
        tags: ["ready"]);

builder.Services.AddMudServices();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // Headroom for InputFile chunks when uploading a publish zip.
    .AddHubOptions(hub => hub.MaximumReceiveMessageSize = 10 * 1024 * 1024);

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// No HTTPS redirection or HSTS: TLS is terminated by Cloudflare, this listens on plain HTTP.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapUpdateApi();

// Plain text "Healthy" / "Unhealthy" with 200 / 503, which is all a container healthcheck needs.
app.MapHealthChecks("/health");

// Printed once the addresses are actually bound, so "Listening on" is the real thing.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var store = app.Services.GetRequiredService<AppStore>();
    var appIds = store.GetAppIds();
    var versionCount = appIds.Sum(id => store.GetVersions(id).Count);

    app.Logger.LogInformation(
        "--------------------- ClickWrap server starting v{Version} ---------------------",
        ServerOptions.Version);
    app.Logger.LogInformation("Current user: {User}", Environment.UserName);
    app.Logger.LogInformation("Working directory: {WorkingDirectory}", Directory.GetCurrentDirectory());
    app.Logger.LogInformation("Listening on: {Urls}", string.Join(", ", app.Urls));
    app.Logger.LogInformation(
        "Public base URL: {PublicBaseUrl}",
        options.PublicBaseUrl ?? $"(unset - built from forwarded headers, set {ServerOptions.PublicBaseUrlVariable} in production)");
    app.Logger.LogInformation("Data path: {DataPath}", options.DataPath);
    app.Logger.LogInformation("Max upload: {MaxUploadMb} MB", options.MaxUploadMb);
    app.Logger.LogInformation("Apps published: {AppCount} ({VersionCount} versions)", appIds.Count, versionCount);
});

app.Run();
