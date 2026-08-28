using ClickWrap.Server.Storage;

namespace ClickWrap.Server.Api;

/// <summary>The two endpoints the installer and the update-check library talk to.</summary>
public static class UpdateApi
{
    public static void MapUpdateApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/apps/{appId}/latest", (string appId, AppStore store, ServerOptions options, HttpContext http) =>
        {
            if (!AppStore.IsValidSegment(appId))
            {
                return Results.BadRequest(new { error = "Invalid app id." });
            }

            var latest = store.GetLatest(appId);
            if (latest is null)
            {
                return Results.NotFound(new { error = $"No versions published for '{appId}'." });
            }

            var downloadUrl =
                $"{PublicBaseUrl(options, http)}/api/apps/{Uri.EscapeDataString(appId)}" +
                $"/versions/{Uri.EscapeDataString(latest.Version)}/download";

            return Results.Ok(new LatestResponse(
                appId,
                latest.Version,
                downloadUrl,
                latest.Metadata.ReleaseNotes,
                latest.Metadata.UploadedUtc,
                latest.Metadata.SizeBytes,
                latest.Metadata.Sha256));
        });

        routes.MapGet("/api/apps/{appId}/versions/{version}/download", (string appId, string version, AppStore store) =>
        {
            if (!AppStore.IsValidSegment(appId) || !AppStore.IsValidSegment(version))
            {
                return Results.BadRequest(new { error = "Invalid app id or version." });
            }

            var entry = store.GetVersion(appId, version);
            if (entry is null)
            {
                return Results.NotFound(new { error = $"Version {version} of '{appId}' was not found." });
            }

            // Range processing lets a large download resume instead of restarting.
            return Results.File(
                entry.ZipPath,
                "application/zip",
                fileDownloadName: $"{appId}-{entry.Version}.zip",
                enableRangeProcessing: true);
        });
    }

    /// <summary>
    /// Behind a Cloudflare Tunnel the inbound host is the tunnel's, not the public one, so an
    /// explicitly configured public base URL wins. Forwarded headers are the fallback.
    /// </summary>
    private static string PublicBaseUrl(ServerOptions options, HttpContext http) =>
        options.PublicBaseUrl ?? $"{http.Request.Scheme}://{http.Request.Host}";
}
