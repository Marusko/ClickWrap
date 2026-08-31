using System.IO;
using Microsoft.Win32;

namespace ClickWrap.Installer;

/// <summary>
/// Records where an app ended up, so the app itself can find its own update.exe, and clears
/// those records again once ClickOnce has uninstalled the app.
/// </summary>
/// <remarks>
/// The app runs from the ClickOnce store, not from the install folder, and with
/// <see cref="ExistingInstallPolicy.Adopt"/> the install folder is not necessarily the one in
/// install.yaml. A pointer under HKCU is the small stable thing an app can read without
/// containing any install logic of its own.
/// </remarks>
public static class UpdaterRegistration
{
    /// <summary>Root key holding one subkey per installed app.</summary>
    private const string RootKeyPath = InstalledApp.RootKeyPath;

    /// <param name="managed">
    /// True when this installer created the folder, false when it adopted a pre-existing install
    /// somewhere else. Only managed folders may ever be deleted.
    /// </param>
    public static void Record(
        string appId,
        string installFolder,
        string version,
        string deploymentName,
        bool managed)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{RootKeyPath}\{appId}");
            if (key is null)
            {
                return;
            }

            key.SetValue(InstalledApp.InstallFolderValueName, installFolder);
            key.SetValue(InstalledApp.UpdaterValueName, Path.Combine(installFolder, InstallRunner.UpdaterFileName));
            key.SetValue(InstalledApp.VersionValueName, version);
            key.SetValue(InstalledApp.DeploymentNameValueName, deploymentName);
            key.SetValue(InstalledApp.ManagedValueName, managed ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The app is installed and working; only the self-update shortcut is unavailable.
        }
    }

    /// <summary>
    /// Drops records for apps ClickOnce has since uninstalled, and deletes the install folders
    /// left behind with them.
    /// </summary>
    /// <remarks>
    /// ClickOnce uninstall removes only what ClickOnce owns — its Add/Remove Programs entry and
    /// its store — so the install folder (with a ~62 MB update.exe in it) and this registration
    /// would otherwise survive forever. There is no hook into that uninstall, so every installer
    /// run cleans up after any of these apps, not just its own.
    /// </remarks>
    /// <param name="currentAppId">The app being installed right now, which is never pruned.</param>
    public static void PruneOrphans(string currentAppId)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(RootKeyPath, writable: true);
            if (root is null)
            {
                return;
            }

            foreach (var appId in root.GetSubKeyNames())
            {
                if (string.Equals(appId, currentAppId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsOrphan(root, appId, out var folder, out var managed))
                {
                    if (managed)
                    {
                        TryDeleteFolder(folder!);
                    }

                    root.DeleteSubKeyTree(appId, throwOnMissingSubKey: false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Cleanup is opportunistic; never fail an install over it.
        }
    }

    private static bool IsOrphan(RegistryKey root, string appId, out string? installFolder, out bool managed)
    {
        installFolder = null;
        managed = false;

        using var key = root.OpenSubKey(appId);
        if (key?.GetValue(InstalledApp.DeploymentNameValueName) is not string deploymentName || deploymentName.Length == 0)
        {
            // Written by an older installer that did not record this; leave it alone rather than
            // guess, since the folder cannot be verified as ours.
            return false;
        }

        installFolder = key.GetValue(InstalledApp.InstallFolderValueName) as string;
        managed = key.GetValue(InstalledApp.ManagedValueName) is int flag && flag == 1;

        // Still in Add/Remove Programs means still installed.
        return ClickOnceRegistry.Find(deploymentName) is null;
    }

    /// <summary>
    /// Deletes a folder only when it still looks like one of ours, so a mistaken or hand-edited
    /// registry entry can never take out an unrelated directory.
    /// </summary>
    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder) ||
                !File.Exists(Path.Combine(folder, InstallRunner.UpdaterFileName)) ||
                !Directory.EnumerateFiles(folder, "*.application").Any())
            {
                return;
            }

            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something in there is in use; it will be retried on the next installer run.
        }
    }
}
