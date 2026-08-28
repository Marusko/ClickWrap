using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace ClickWrap.Installer;

/// <summary>Progress callbacks so the window can show what is happening.</summary>
public interface IInstallProgress
{
    void Status(string message);

    /// <param name="percent">0-100, or null for indeterminate.</param>
    void Percent(double? percent);
}

/// <summary>
/// The whole install/update flow: pre-install steps, fetch latest, download, extract into the
/// fixed folder, run setup.exe. Running it again just repeats it against the newest version.
/// </summary>
public sealed class InstallRunner(InstallConfig config, IInstallProgress progress)
{
    /// <summary>Name this installer takes when it copies itself into the install folder.</summary>
    public const string UpdaterFileName = "update.exe";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // ClickOnce uninstall cannot be hooked, so every run clears up after any of these apps
        // that has since been uninstalled.
        UpdaterRegistration.PruneOrphans(config.AppId);

        await RunPreInstallStepsAsync(cancellationToken).ConfigureAwait(false);

        progress.Status("Checking for the latest version…");
        progress.Percent(null);

        using var updateClient = new UpdateClient(Http, config.ServerUrl);
        var latest = await updateClient.GetLatestAsync(config.AppId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"The server has no versions published for '{config.AppId}'.");

        var zipPath = Path.Combine(Path.GetTempPath(), $"clickwrap-{config.AppId}-{Guid.NewGuid():N}.zip");

        try
        {
            await DownloadAsync(latest.DownloadUrl, zipPath, latest.LatestVersion, cancellationToken)
                .ConfigureAwait(false);

            var deploymentName = ReadDeploymentName(zipPath);
            var targetFolder = ResolveTargetFolder(deploymentName);

            progress.Status($"Installing into {targetFolder}…");
            progress.Percent(null);
            ExtractFresh(zipPath, targetFolder);
            CopySelfAsUpdater(targetFolder);

            await RunSetupAsync(targetFolder, cancellationToken).ConfigureAwait(false);

            // Written last: only a completed install is worth pointing the app at.
            UpdaterRegistration.Record(
                config.AppId,
                targetFolder,
                latest.LatestVersion,
                deploymentName,
                managed: ClickOnceRegistry.SameFolder(targetFolder, config.ExpandedInstallFolder));

            progress.Status($"{config.EffectiveDisplayName} {latest.LatestVersion} is installed.");
            progress.Percent(100);
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    private async Task RunPreInstallStepsAsync(CancellationToken cancellationToken)
    {
        if (config.PreInstall.Count == 0)
        {
            return;
        }

        for (var i = 0; i < config.PreInstall.Count; i++)
        {
            var step = config.PreInstall[i];
            var path = Environment.ExpandEnvironmentVariables(step.Path ?? "");

            switch (step.Type.Trim().ToLowerInvariant())
            {
                case "createfolder":
                    progress.Status($"Preparing {path}…");
                    Directory.CreateDirectory(path);
                    break;

                case "downloadfile":
                    if (File.Exists(path) && !step.Overwrite)
                    {
                        break;
                    }

                    progress.Status($"Downloading {Path.GetFileName(path)}…");
                    var parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    await DownloadAsync(step.Url!, path, Path.GetFileName(path), cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }

            progress.Percent((i + 1) * 100.0 / config.PreInstall.Count);
        }
    }

    private async Task DownloadAsync(string url, string destination, string label, CancellationToken cancellationToken)
    {
        progress.Status($"Downloading {label}…");
        progress.Percent(0);

        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long copied = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;

            // No Content-Length (chunked) means no meaningful percentage.
            progress.Percent(total is > 0 ? copied * 100.0 / total.Value : null);
        }
    }

    /// <summary>
    /// Reads the deployment manifest name straight out of the zip, before extracting, so the
    /// existing-install lookup can decide where to extract to.
    /// </summary>
    private static string ReadDeploymentName(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var manifest = archive.Entries.FirstOrDefault(e =>
            !e.FullName.Contains('/') && !e.FullName.Contains('\\') &&
            e.Name.EndsWith(".application", StringComparison.OrdinalIgnoreCase));

        return manifest?.Name
            ?? throw new InvalidOperationException(
                "The downloaded zip has no .application manifest at its root, so it is not a ClickOnce publish folder.");
    }

    /// <summary>
    /// ClickOnce refuses to update an app from a folder other than the one it was installed from
    /// ("already installed from a different location"). If a previous install exists elsewhere,
    /// update it in place rather than breaking it.
    /// </summary>
    private string ResolveTargetFolder(string deploymentName)
    {
        var configured = config.ExpandedInstallFolder;
        var existing = ClickOnceRegistry.Find(deploymentName);

        if (existing?.InstallFolder is null || ClickOnceRegistry.SameFolder(existing.InstallFolder, configured))
        {
            return configured;
        }

        if (config.OnExistingInstall == ExistingInstallPolicy.Reinstall)
        {
            progress.Status(
                $"{existing.DisplayName} is installed from {existing.InstallFolder}. " +
                "Choose \"Remove the application\" in the dialog, then run this installer again.");
            ClickOnceRegistry.LaunchUninstallDialog(existing);

            throw new InstallPausedException(
                $"{existing.DisplayName} must be uninstalled before it can be moved to {configured}. " +
                "A ClickOnce dialog has opened: choose \"Remove the application from this computer\", " +
                "then run this installer again.");
        }

        progress.Status($"Updating the existing installation in {existing.InstallFolder}…");
        return existing.InstallFolder;
    }

    /// <summary>
    /// Clears the folder before extracting. ClickOnce does not need the previous version's files
    /// to be present, and a clean folder avoids stale files from an older publish lingering.
    /// </summary>
    private static void ExtractFresh(string zipPath, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);

        foreach (var directory in Directory.EnumerateDirectories(targetFolder))
        {
            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(targetFolder))
        {
            // update.exe in this folder is this very process when an app relaunches its own
            // updater. Windows locks a running exe, so deleting it would fail the install --
            // and it is the updater the app depends on next time.
            if (IsCurrentProcess(file))
            {
                continue;
            }

            File.Delete(file);
        }

        ZipFile.ExtractToDirectory(zipPath, targetFolder, overwriteFiles: true);
    }

    /// <summary>
    /// Leaves a copy of this installer beside setup.exe as update.exe, so the app can relaunch
    /// its own updater without shipping any install machinery of its own.
    /// </summary>
    private void CopySelfAsUpdater(string targetFolder)
    {
        var selfPath = Environment.ProcessPath;
        if (selfPath is null)
        {
            return;
        }

        var updaterPath = Path.Combine(targetFolder, UpdaterFileName);
        if (IsCurrentProcess(updaterPath))
        {
            // Already running from there; it is by definition up to date.
            return;
        }

        try
        {
            File.Copy(selfPath, updaterPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another copy of the updater is running from this folder. The install itself is
            // fine and the existing update.exe still works, so this is not worth failing over.
            progress.Status("Installed, but update.exe could not be refreshed (it is in use).");
        }
    }

    private static bool IsCurrentProcess(string path)
    {
        var selfPath = Environment.ProcessPath;
        if (selfPath is null)
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(selfPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async Task RunSetupAsync(string targetFolder, CancellationToken cancellationToken)
    {
        var setupPath = Path.Combine(targetFolder, "setup.exe");
        if (!File.Exists(setupPath))
        {
            throw new InvalidOperationException($"setup.exe is missing from {targetFolder}.");
        }

        progress.Status("Running the ClickOnce installer…");

        using var process = Process.Start(new ProcessStartInfo(setupPath)
        {
            WorkingDirectory = targetFolder,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Could not start setup.exe.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"setup.exe exited with code {process.ExitCode}.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing the install over.
        }
    }
}

/// <summary>Thrown when the user has to do something before the install can continue.</summary>
public sealed class InstallPausedException(string message) : Exception(message);
