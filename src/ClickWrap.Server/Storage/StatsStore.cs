using System.Collections.Concurrent;
using System.Text.Json;

namespace ClickWrap.Server.Storage;

/// <summary>
/// Download counters, kept as one <c>stats.json</c> per app beside its version folders.
/// Best-effort by design: counting is not what this server is for, so a stats failure is
/// logged and swallowed rather than allowed to break a download.
/// </summary>
public sealed class StatsStore
{
    public const string StatsFileName = "stats.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // One lock per app: two downloads of the same app must not lose a count to a read-modify-write
    // race, but two different apps write two different files and need not wait for each other.
    private readonly ConcurrentDictionary<string, Lock> _locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly ServerOptions _options;
    private readonly ILogger<StatsStore> _logger;

    public StatsStore(ServerOptions options, ILogger<StatsStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Counters for one app. An app that has never been downloaded reads back as zeroes.</summary>
    public AppStats GetStats(string appId)
    {
        if (!AppStore.IsValidSegment(appId))
        {
            return new AppStats { AppId = appId };
        }

        lock (LockFor(appId))
        {
            return Read(appId);
        }
    }

    /// <summary>
    /// Adds one to the app total and to the counter for this version. Silently does nothing for
    /// an app that is not on disk, so a 404'd download never creates a stats file.
    /// </summary>
    public void RecordDownload(string appId, string version)
    {
        if (!AppStore.IsValidSegment(appId) || !AppStore.IsValidSegment(version))
        {
            return;
        }

        if (!Directory.Exists(Path.Combine(_options.DataPath, appId)))
        {
            return;
        }

        lock (LockFor(appId))
        {
            try
            {
                var stats = Read(appId);
                var now = DateTimeOffset.UtcNow;
                var current = stats.Versions.GetValueOrDefault(version) ?? new VersionStats();

                stats.Versions[version] = current with
                {
                    Downloads = current.Downloads + 1,
                    LastDownloadUtc = now,
                };

                Write(stats with { TotalDownloads = stats.TotalDownloads + 1, LastDownloadUtc = now });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record a download of {AppId} {Version}.", appId, version);
            }
        }
    }

    private Lock LockFor(string appId) => _locks.GetOrAdd(appId, _ => new Lock());

    private AppStats Read(string appId)
    {
        var path = Path.Combine(_options.DataPath, appId, StatsFileName);
        if (File.Exists(path))
        {
            try
            {
                var stats = JsonSerializer.Deserialize<AppStats>(File.ReadAllText(path), JsonOptions);
                if (stats is not null)
                {
                    // Deserialisation builds a case-sensitive dictionary; rebuild it so a version
                    // looked up in a different casing than it was written still finds its counter.
                    return stats with
                    {
                        AppId = appId,
                        Versions = new Dictionary<string, VersionStats>(stats.Versions, StringComparer.OrdinalIgnoreCase),
                    };
                }
            }
            catch (Exception ex)
            {
                // A corrupt counter file is not worth failing over: start counting again.
                _logger.LogWarning(ex, "Unreadable {File} for {AppId}; starting from zero.", StatsFileName, appId);
            }
        }

        return new AppStats { AppId = appId };
    }

    /// <summary>Writes through a temporary file, so a crash mid-write cannot leave a truncated one.</summary>
    private void Write(AppStats stats)
    {
        var path = Path.Combine(_options.DataPath, stats.AppId, StatsFileName);
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(stats, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
