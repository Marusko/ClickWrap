namespace ClickWrap.Server.Storage;

/// <summary>
/// All configuration comes from environment variables so the app drops into a container
/// without an appsettings.json. Defaults are container-friendly.
/// </summary>
public sealed class ServerOptions
{
    public required string DataPath { get; init; }

    /// <summary>
    /// Absolute public base URL (e.g. https://updates.example.com). Behind a Cloudflare
    /// Tunnel the inbound request host is not the public host, so download URLs are built
    /// from this when set; otherwise from forwarded headers.
    /// </summary>
    public string? PublicBaseUrl { get; init; }

    public long MaxUploadBytes { get; init; }

    public static ServerOptions FromConfiguration(IConfiguration config)
    {
        var dataPath = config["CLICKWRAP_DATA"];
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            dataPath = OperatingSystem.IsWindows()
                ? Path.Combine(AppContext.BaseDirectory, "data")
                : "/data";
        }

        var maxMb = 512;
        if (int.TryParse(config["CLICKWRAP_MAX_UPLOAD_MB"], out var parsed) && parsed > 0)
        {
            maxMb = parsed;
        }

        var publicBaseUrl = config["CLICKWRAP_PUBLIC_BASE_URL"]?.TrimEnd('/');

        return new ServerOptions
        {
            DataPath = Path.GetFullPath(dataPath),
            PublicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl,
            MaxUploadBytes = (long)maxMb * 1024 * 1024,
        };
    }
}
