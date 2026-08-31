using System.Reflection;

namespace ClickWrap.Server.Storage;

/// <summary>
/// All configuration comes from environment variables so the app drops into a container
/// without an appsettings.json. Defaults are container-friendly.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>Environment variable holding the root folder of the app/version tree.</summary>
    public const string DataVariable = "CLICKWRAP_DATA";

    /// <summary>Environment variable holding the public base address of this deployment.</summary>
    public const string PublicBaseUrlVariable = "CLICKWRAP_PUBLIC_BASE_URL";

    /// <summary>Environment variable holding the upload size cap, in megabytes.</summary>
    public const string MaxUploadMbVariable = "CLICKWRAP_MAX_UPLOAD_MB";

    /// <summary>Standard ASP.NET Core variable holding the addresses to bind.</summary>
    public const string UrlsVariable = "ASPNETCORE_URLS";

    /// <summary>Addresses bound when <see cref="UrlsVariable"/> is not set.</summary>
    public const string DefaultUrls = "http://0.0.0.0:8080";

    private const int DefaultMaxUploadMb = 512;

    /// <summary>e.g. "1.0.0" — the informational version with any build metadata suffix removed.</summary>
    public static string Version { get; } = ReadVersion();

    public required string DataPath { get; init; }

    /// <summary>
    /// Absolute public base URL (e.g. https://updates.example.com). Behind a Cloudflare
    /// Tunnel the inbound request host is not the public host, so download URLs are built
    /// from this when set; otherwise from forwarded headers.
    /// </summary>
    public string? PublicBaseUrl { get; init; }

    public long MaxUploadBytes { get; init; }

    public int MaxUploadMb => (int)(MaxUploadBytes / 1024 / 1024);

    public static ServerOptions FromConfiguration(IConfiguration config)
    {
        var dataPath = config[DataVariable];
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            dataPath = OperatingSystem.IsWindows()
                ? Path.Combine(AppContext.BaseDirectory, "data")
                : "/data";
        }

        var maxMb = DefaultMaxUploadMb;
        if (int.TryParse(config[MaxUploadMbVariable], out var parsed) && parsed > 0)
        {
            maxMb = parsed;
        }

        var publicBaseUrl = config[PublicBaseUrlVariable]?.TrimEnd('/');

        return new ServerOptions
        {
            DataPath = Path.GetFullPath(dataPath),
            PublicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl,
            MaxUploadBytes = (long)maxMb * 1024 * 1024,
        };
    }

    private static string ReadVersion()
    {
        var assembly = typeof(ServerOptions).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0";

        // The informational version carries a "+<commit hash>" suffix when built from a repository.
        return version.Split('+')[0];
    }
}
