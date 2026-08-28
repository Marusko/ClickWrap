namespace ClickWrap;

/// <summary>
/// A version published on the server. <see cref="UpdateClient.CheckForUpdateAsync"/> returns one
/// only when it is newer than what is running; <see cref="UpdateClient.GetLatestAsync"/> returns
/// the newest one regardless.
/// </summary>
public sealed record UpdateInfo
{
    /// <summary>The newest version published on the server.</summary>
    public required string LatestVersion { get; init; }

    /// <summary>Absolute URL of the zipped ClickOnce publish folder for that version.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>Release notes entered when the version was published, if any.</summary>
    public string? ReleaseNotes { get; init; }
}
