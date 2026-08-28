using System.Text.Json.Serialization;

namespace ClickWrap.Server.Storage;

/// <summary>Contents of metadata.json, stored next to app.zip.</summary>
public sealed record VersionMetadata
{
    public required string Version { get; init; }
    public string? ReleaseNotes { get; init; }
    public DateTimeOffset UploadedUtc { get; init; }
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
}

/// <summary>A version folder on disk, with its parsed version for ordering.</summary>
public sealed record VersionEntry(string Version, Version Parsed, VersionMetadata Metadata, string ZipPath);

/// <summary>Response body of GET /api/apps/{appId}/latest.</summary>
public sealed record LatestResponse(
    [property: JsonPropertyName("appId")] string AppId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
    [property: JsonPropertyName("releaseNotes")] string? ReleaseNotes,
    [property: JsonPropertyName("uploadedUtc")] DateTimeOffset UploadedUtc,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);
