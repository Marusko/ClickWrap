using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClickWrap.Server.Storage;

/// <summary>
/// The whole persistence layer: plain files under {DataPath}/{appId}/{version}/.
/// "Latest" is simply the highest parseable version folder name.
/// </summary>
public sealed partial class AppStore
{
    public const string ZipFileName = "app.zip";
    public const string MetadataFileName = "metadata.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ServerOptions _options;
    private readonly ILogger<AppStore> _logger;

    public AppStore(ServerOptions options, ILogger<AppStore> logger)
    {
        _options = options;
        _logger = logger;
        Directory.CreateDirectory(_options.DataPath);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SegmentRegex();

    /// <summary>
    /// appId and version become path segments, so they are whitelisted rather than sanitised.
    /// Rejects traversal, separators, and anything exotic.
    /// </summary>
    public static bool IsValidSegment(string? segment) =>
        !string.IsNullOrEmpty(segment) && segment != "." && segment != ".." && SegmentRegex().IsMatch(segment);

    public IReadOnlyList<string> GetAppIds()
    {
        if (!Directory.Exists(_options.DataPath))
        {
            return [];
        }

        return Directory.EnumerateDirectories(_options.DataPath)
            .Select(Path.GetFileName)
            .Where(IsValidSegment)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>All versions of an app, newest first. Folders that are not a valid version are ignored.</summary>
    public IReadOnlyList<VersionEntry> GetVersions(string appId)
    {
        if (!IsValidSegment(appId))
        {
            return [];
        }

        var appDir = Path.Combine(_options.DataPath, appId);
        if (!Directory.Exists(appDir))
        {
            return [];
        }

        var entries = new List<VersionEntry>();
        foreach (var dir in Directory.EnumerateDirectories(appDir))
        {
            var name = Path.GetFileName(dir);

            // Version.TryParse, not string ordering: 3.10.0.0 must beat 3.9.0.0.
            if (!IsValidSegment(name) || !Version.TryParse(name, out var parsed))
            {
                continue;
            }

            var zipPath = Path.Combine(dir, ZipFileName);
            if (!File.Exists(zipPath))
            {
                continue;
            }

            entries.Add(new VersionEntry(name, parsed, ReadMetadata(dir, name, zipPath), zipPath));
        }

        return entries.OrderByDescending(e => e.Parsed).ToList();
    }

    public VersionEntry? GetLatest(string appId) => GetVersions(appId).FirstOrDefault();

    public VersionEntry? GetVersion(string appId, string version) =>
        !IsValidSegment(version)
            ? null
            : GetVersions(appId).FirstOrDefault(e => string.Equals(e.Version, version, StringComparison.OrdinalIgnoreCase));

    private VersionMetadata ReadMetadata(string versionDir, string version, string zipPath)
    {
        var metadataPath = Path.Combine(versionDir, MetadataFileName);
        if (File.Exists(metadataPath))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<VersionMetadata>(File.ReadAllText(metadataPath), JsonOptions);
                if (metadata is not null)
                {
                    return metadata;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unreadable {File} for {Version}; falling back to file info.", MetadataFileName, version);
            }
        }

        // A version folder dropped in by hand still works, just without release notes.
        var info = new FileInfo(zipPath);
        return new VersionMetadata
        {
            Version = version,
            UploadedUtc = info.LastWriteTimeUtc,
            SizeBytes = info.Length,
        };
    }

    /// <summary>
    /// Streams an uploaded zip to disk, validates it looks like a ClickOnce publish folder,
    /// and only then moves it into place, so a failed or partial upload is never served.
    /// </summary>
    public async Task<VersionMetadata> SaveVersionAsync(
        string appId,
        string version,
        Stream content,
        string? releaseNotes,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidSegment(appId))
        {
            throw new ArgumentException(
                $"Invalid app id '{appId}'. Use letters, digits, dot, dash or underscore.", nameof(appId));
        }

        if (!Version.TryParse(version, out _))
        {
            throw new ArgumentException(
                $"Version '{version}' is not a valid version number (for example 1.2.3.0).", nameof(version));
        }

        var appDir = Path.Combine(_options.DataPath, appId);
        var targetDir = Path.Combine(appDir, version);
        if (File.Exists(Path.Combine(targetDir, ZipFileName)) && !overwrite)
        {
            throw new InvalidOperationException(
                $"Version {version} of '{appId}' already exists. Tick overwrite to replace it.");
        }

        Directory.CreateDirectory(appDir);
        var stagingDir = Path.Combine(appDir, $".upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            var stagedZip = Path.Combine(stagingDir, ZipFileName);
            long sizeBytes;
            string sha256;

            await using (var file = File.Create(stagedZip))
            using (var hasher = SHA256.Create())
            {
                // Hash while writing so the upload is read once and never fully buffered in memory.
                await using var hashing = new CryptoStream(file, hasher, CryptoStreamMode.Write, leaveOpen: true);
                await content.CopyToAsync(hashing, cancellationToken);
                await hashing.FlushFinalBlockAsync(cancellationToken);
                sizeBytes = file.Length;
                sha256 = Convert.ToHexStringLower(hasher.Hash!);
            }

            ValidateClickOnceZip(stagedZip);

            var metadata = new VersionMetadata
            {
                Version = version,
                ReleaseNotes = string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes.Trim(),
                UploadedUtc = DateTimeOffset.UtcNow,
                SizeBytes = sizeBytes,
                Sha256 = sha256,
            };

            await File.WriteAllTextAsync(
                Path.Combine(stagingDir, MetadataFileName),
                JsonSerializer.Serialize(metadata, JsonOptions),
                cancellationToken);

            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }

            Directory.Move(stagingDir, targetDir);
            _logger.LogInformation("Stored {AppId} {Version} ({Size} bytes).", appId, version, sizeBytes);
            return metadata;
        }
        catch
        {
            TryDeleteDirectory(stagingDir);
            throw;
        }
    }

    /// <summary>
    /// The installer extracts this zip and runs setup.exe from the folder root, so a zip of the
    /// parent folder would silently produce a broken install. Catch that at upload time instead.
    /// </summary>
    private static void ValidateClickOnceZip(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var rootEntries = archive.Entries
            .Where(e => !e.FullName.Contains('/') && !e.FullName.Contains('\\'))
            .Select(e => e.Name)
            .ToList();

        var hasSetup = rootEntries.Any(n => string.Equals(n, "setup.exe", StringComparison.OrdinalIgnoreCase));
        var hasDeploymentManifest = rootEntries.Any(n => n.EndsWith(".application", StringComparison.OrdinalIgnoreCase));

        if (hasSetup && hasDeploymentManifest)
        {
            return;
        }

        var missing = new List<string>();
        if (!hasSetup)
        {
            missing.Add("setup.exe");
        }

        if (!hasDeploymentManifest)
        {
            missing.Add("*.application");
        }

        throw new InvalidOperationException(
            $"This does not look like a ClickOnce publish folder: {string.Join(" and ", missing)} missing from " +
            "the root of the zip. Zip the contents of the publish folder, not the folder itself.");
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up staging folder {Path}.", path);
        }
    }
}
