using ClickWrap.Server.Api;
using ClickWrap.Server.Components;
using ClickWrap.Server.Storage;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Lets the app run from the build output (not just `dotnet publish` output) outside Development.
builder.WebHost.UseStaticWebAssets();

var options = ServerOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<AppStore>();

// Bind to all interfaces by default: this is reached through a Cloudflare Tunnel, not loopback.
// An explicit ASPNETCORE_URLS (or the dev launch profile) still wins.
if (string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]) &&
    string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

// cloudflared connects from inside the container network, so the proxy is not in a known range.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

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

app.Logger.LogInformation(
    "ClickWrap server starting. Data path: {DataPath}. Public base URL: {PublicBaseUrl}.",
    options.DataPath,
    options.PublicBaseUrl ?? "(from forwarded headers)");

app.Run();
