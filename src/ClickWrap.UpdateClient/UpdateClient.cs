using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ClickWrap;

/// <summary>
/// Asks a ClickWrap server whether a newer version of an app has been published.
/// HTTP call plus a version comparison; no UI, no download, no install.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <param name="serverBaseUrl">Base URL of the ClickWrap server, e.g. https://updates.example.com.</param>
    public UpdateClient(string serverBaseUrl)
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, serverBaseUrl, ownsHttpClient: true)
    {
    }

    /// <summary>Use this overload to supply a pooled or pre-configured <see cref="HttpClient"/>.</summary>
    public UpdateClient(HttpClient httpClient, string serverBaseUrl)
        : this(httpClient, serverBaseUrl, ownsHttpClient: false)
    {
    }

    private UpdateClient(HttpClient httpClient, string serverBaseUrl, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverBaseUrl);

        _http = httpClient;
        _ownsHttpClient = ownsHttpClient;
        ServerBaseUrl = serverBaseUrl.TrimEnd('/');
    }

    /// <summary>Base URL of the ClickWrap server, without a trailing slash.</summary>
    public string ServerBaseUrl { get; }

    /// <summary>
    /// Checks for an update using the version this app was installed at, so callers do not have to
    /// work out their own version. See <see cref="InstalledApp.GetCurrentVersion" />.
    /// </summary>
    /// <param name="appId">App id as published on the server, e.g. "race-timer".</param>
    /// <param name="cancellationToken">Cancels the HTTP call.</param>
    /// <exception cref="HttpRequestException">The server could not be reached or returned an error.</exception>
    public Task<UpdateInfo?> CheckForUpdateAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return CheckForUpdateAsync(appId, InstalledApp.GetCurrentVersion(appId), cancellationToken);
    }

    /// <summary>
    /// Returns <c>null</c> when the app is already up to date, or when the server has no
    /// versions for this app id. Returns an <see cref="UpdateInfo"/> when a newer version exists.
    /// </summary>
    /// <param name="appId">App id as published on the server, e.g. "race-timer".</param>
    /// <param name="currentVersion">
    /// The running version. Accepts two to four components; missing components count as zero,
    /// so "1.2" and "1.2.0.0" compare equal.
    /// </param>
    /// <param name="cancellationToken">Cancels the HTTP call.</param>
    /// <exception cref="ArgumentException"><paramref name="currentVersion"/> is not a version number.</exception>
    /// <exception cref="HttpRequestException">The server could not be reached or returned an error.</exception>
    public async Task<UpdateInfo?> CheckForUpdateAsync(
        string appId,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        if (!TryParseVersion(currentVersion, out var current))
        {
            throw new ArgumentException(
                $"'{currentVersion}' is not a version number.", nameof(currentVersion));
        }

        var latest = await GetLatestAsync(appId, cancellationToken).ConfigureAwait(false);

        if (latest is null || !TryParseVersion(latest.LatestVersion, out var available))
        {
            return null;
        }

        return available > current ? latest : null;
    }

    /// <summary>
    /// Returns the newest published version whatever is running locally, or <c>null</c> when the
    /// server has nothing for this app id. The installer uses this: it always wants the latest.
    /// </summary>
    /// <param name="appId">App id as published on the server.</param>
    /// <param name="cancellationToken">Cancels the HTTP call.</param>
    /// <exception cref="HttpRequestException">The server could not be reached or returned an error.</exception>
    public async Task<UpdateInfo?> GetLatestAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        var url = $"{ServerBaseUrl}/api/apps/{Uri.EscapeDataString(appId)}/latest";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

        // Nothing published under this id yet: not an update, and not worth crashing a startup check over.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var latest = await response.Content
            .ReadFromJsonAsync<LatestResponse>(cancellationToken)
            .ConfigureAwait(false);

        if (latest is null ||
            string.IsNullOrWhiteSpace(latest.Version) ||
            string.IsNullOrWhiteSpace(latest.DownloadUrl))
        {
            return null;
        }

        return new UpdateInfo
        {
            LatestVersion = latest.Version,
            DownloadUrl = latest.DownloadUrl,
            ReleaseNotes = latest.ReleaseNotes,
        };
    }

    /// <summary>
    /// Version.Parse treats "1.2.3" and "1.2.3.0" as different (Revision -1 vs 0), which would
    /// make an app reporting three components look permanently out of date. Normalise to four.
    /// </summary>
    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0, 0);

        if (!Version.TryParse(value.Trim(), out var parsed))
        {
            return false;
        }

        version = new Version(
            parsed.Major,
            parsed.Minor,
            parsed.Build < 0 ? 0 : parsed.Build,
            parsed.Revision < 0 ? 0 : parsed.Revision);

        return true;
    }

    /// <summary>Disposes the <see cref="HttpClient"/> only if this instance created it.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private sealed record LatestResponse(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
        [property: JsonPropertyName("releaseNotes")] string? ReleaseNotes);
}
