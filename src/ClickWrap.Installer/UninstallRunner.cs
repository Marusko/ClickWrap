using System.IO;
using Microsoft.Win32;

namespace ClickWrap.Installer;

/// <summary>
/// The uninstall flow: ClickOnce's own uninstall, then everything ClickOnce leaves behind — the
/// registration, the install folder with update.exe in it, and optionally the app's data.
/// </summary>
/// <remarks>
/// This is the same cleanup <see cref="UpdaterRegistration.PruneOrphans"/> does after the fact,
/// done straight away, plus the app's own data, which pruning never touches because only this
/// app's config knows where it is.
/// </remarks>
public sealed class UninstallRunner(InstallConfig config, IInstallProgress progress)
{
    /// <summary>
    /// Files and folders this process could not delete while running, typically because it is
    /// update.exe in the very folder being removed. Start it as the process exits.
    /// </summary>
    public DeferredDeletion DeferredDeletion { get; } = new();

    /// <exception cref="UninstallStoppedException">The app is still installed, or never was.</exception>
    public async Task<UninstallResult> RunAsync(bool deleteData, CancellationToken cancellationToken = default)
    {
        var name = config.EffectiveDisplayName;
        UpdaterRegistration.PruneOrphans(config.AppId);

        var registration = UpdaterRegistration.Read(config.AppId);
        var clickOnce = registration?.DeploymentName is { Length: > 0 } deploymentName
            ? ClickOnceRegistry.Find(deploymentName)
            : null;

        if (registration is null && !(deleteData && config.Uninstall.HasData))
        {
            throw new UninstallStoppedException(
                $"{name} is not installed",
                $"This installer has no record of installing {name} for this user, so there is nothing " +
                "for it to remove. If it is installed some other way, remove it from Settings > Apps.");
        }

        if (clickOnce is not null)
        {
            progress.Status(
                $"Confirm in the ClickOnce dialog: choose \"Remove the application from this computer\", then OK.");
            progress.Percent(null);

            await ClickOnceRegistry.RunUninstallDialogAsync(clickOnce, cancellationToken).ConfigureAwait(false);

            if (!await WaitUntilUninstalledAsync(registration!.DeploymentName!, cancellationToken).ConfigureAwait(false))
            {
                throw new UninstallStoppedException(
                    $"{name} is still installed",
                    "Nothing was removed. If you cancelled the ClickOnce dialog, or restored the previous " +
                    "version instead, run the uninstaller again to remove it.");
            }
        }

        var leftovers = new List<string>();

        if (registration is not null)
        {
            progress.Status("Removing installer files…");
            RemoveInstallFolder(registration);
            TryRun(() => UpdaterRegistration.Remove(config.AppId), $@"HKCU\{InstalledApp.KeyPathFor(config.AppId)}", leftovers);
        }

        if (deleteData)
        {
            progress.Status("Deleting app data…");
            DeleteData(leftovers);
        }

        progress.Percent(100);
        return new UninstallResult(AppRemoved: registration is not null, leftovers);
    }

    /// <summary>
    /// The dialog closing is not proof of anything: the user may have cancelled or rolled back.
    /// Only the Add/Remove Programs entry disappearing is. Give ClickOnce a moment to remove it.
    /// </summary>
    private static async Task<bool> WaitUntilUninstalledAsync(string deploymentName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (ClickOnceRegistry.Find(deploymentName) is null)
            {
                return true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return ClickOnceRegistry.Find(deploymentName) is null;
    }

    /// <summary>
    /// Same rules as orphan pruning: a folder this installer created goes entirely, provided it
    /// still looks like one of ours. An adopted folder (someone's Downloads) keeps its contents
    /// and loses only the update.exe this installer put there.
    /// </summary>
    private void RemoveInstallFolder(Registration registration)
    {
        if (registration.InstallFolder is not { Length: > 0 } folder)
        {
            return;
        }

        if (registration.Managed && UpdaterRegistration.LooksLikeInstallFolder(folder))
        {
            TryDeleteNowOrLater(folder, isFolder: true);

            // %LOCALAPPDATA%\ClickWrap, once the last app in it has gone. Only ever when empty.
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
            if (parent is not null && string.Equals(Path.GetFileName(parent), "ClickWrap", StringComparison.OrdinalIgnoreCase))
            {
                DeferredDeletion.AddFolderIfEmpty(parent);
            }

            return;
        }

        TryDeleteNowOrLater(Path.Combine(folder, InstallRunner.UpdaterFileName), isFolder: false);
    }

    /// <summary>
    /// Deletes straight away when it can, so the folder is already gone when the window says so.
    /// When this process is update.exe inside it, Windows keeps the exe locked until exit.
    /// </summary>
    private void TryDeleteNowOrLater(string path, bool isFolder)
    {
        try
        {
            if (isFolder && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (!isFolder && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (isFolder)
            {
                DeferredDeletion.AddFolder(path);
            }
            else
            {
                DeferredDeletion.AddFile(path);
            }
        }
    }

    private void DeleteData(List<string> leftovers)
    {
        foreach (var path in config.Uninstall.ExpandedData)
        {
            TryRun(() =>
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }, path, leftovers);
        }

        foreach (var key in config.Uninstall.RegistryKeys)
        {
            // Validated at startup, so this always parses.
            if (UninstallConfig.TryParseRegistryKey(key, out var subKey))
            {
                TryRun(() => Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false), key, leftovers);
            }
        }
    }

    /// <summary>
    /// One item failing — a log file the app still has open, say — must not stop the rest. The
    /// reason goes with it: "in use" and "access denied" call for different fixes.
    /// </summary>
    private static void TryRun(Action delete, string label, List<string> leftovers)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            leftovers.Add($"{label}\n    {ex.Message}");
        }
    }
}

/// <param name="AppRemoved">False when only the app's data was deleted, the app itself having already gone.</param>
/// <param name="Leftovers">Anything that could not be deleted, for the window to list.</param>
public sealed record UninstallResult(bool AppRemoved, IReadOnlyList<string> Leftovers);

/// <summary>Thrown when the uninstall cannot go ahead, with nothing having been removed.</summary>
public sealed class UninstallStoppedException(string heading, string message) : Exception(message)
{
    public string Heading { get; } = heading;
}
