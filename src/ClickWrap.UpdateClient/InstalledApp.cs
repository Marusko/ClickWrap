using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace ClickWrap;

/// <summary>
/// Reads what the ClickWrap installer recorded about an installed app, and can hand off to that
/// app's updater.
/// </summary>
/// <remarks>
/// The installer writes these values under <c>HKCU\Software\ClickWrap\{appId}</c> after a
/// successful install. An app runs from the ClickOnce store rather than its install folder, so
/// this registration is how it finds its own <c>update.exe</c>.
/// </remarks>
public static class InstalledApp
{
    /// <summary>Root key under HKCU holding one subkey per installed app.</summary>
    public const string RootKeyPath = @"Software\ClickWrap";

    /// <summary>Version the installer last installed successfully.</summary>
    public const string VersionValueName = "Version";

    /// <summary>Full path to the app's update.exe.</summary>
    public const string UpdaterValueName = "Updater";

    /// <summary>Folder the ClickOnce publish output was extracted into.</summary>
    public const string InstallFolderValueName = "InstallFolder";

    /// <summary>The app's ClickOnce deployment manifest file name.</summary>
    public const string DeploymentNameValueName = "DeploymentName";

    /// <summary>1 when the installer created the install folder, 0 when it adopted an existing one.</summary>
    public const string ManagedValueName = "Managed";

    /// <summary>Registry key path for one app.</summary>
    public static string KeyPathFor(string appId) => $@"{RootKeyPath}\{appId}";

    /// <summary>The version the installer last installed, or <c>null</c> if this app was not installed by it.</summary>
    public static string? GetInstalledVersion(string appId) => ReadValue(appId, VersionValueName);

    /// <summary>The folder the publish output was extracted into, or <c>null</c> if not recorded.</summary>
    public static string? GetInstallFolder(string appId) => ReadValue(appId, InstallFolderValueName);

    /// <summary>Path to this app's update.exe, or <c>null</c> when it is not recorded or no longer on disk.</summary>
    public static string? GetUpdaterPath(string appId)
    {
        var path = ReadValue(appId, UpdaterValueName);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The version to compare against the server: what the installer recorded, falling back to the
    /// entry assembly's version.
    /// </summary>
    /// <remarks>
    /// The registry value is preferred because it is the version actually pulled from the server,
    /// whereas a ClickOnce <c>ApplicationVersion</c> and an <c>AssemblyVersion</c> drift apart the
    /// moment one is bumped without the other — which would leave an app permanently convinced an
    /// update is available. The assembly version is the fallback for a build that was never
    /// installed through ClickWrap, such as one running from a developer machine.
    /// </remarks>
    public static string GetCurrentVersion(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        if (GetInstalledVersion(appId) is { Length: > 0 } recorded)
        {
            return recorded;
        }

        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
    }

    /// <summary>
    /// Starts this app's updater and leaves the app running, for when it needs to save state or
    /// close windows itself before exiting.
    /// </summary>
    /// <returns><c>false</c> when there is no updater to start; the app is unaffected.</returns>
    public static bool StartUpdater(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        var updater = GetUpdaterPath(appId);
        if (updater is null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(updater) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts this app's updater and immediately exits the app — the whole self-update in one call.
    /// </summary>
    /// <returns>
    /// <c>false</c> when there is no updater to start, leaving the app running. On success this
    /// does not return: the process ends.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The app must exit. <c>setup.exe</c> launches the app once it has updated it, so an app that
    /// stays open ends up running beside a second, newer copy of itself.
    /// </para>
    /// <para>
    /// This exits the process directly, so unsaved state is not flushed and WPF's shutdown does not
    /// run. Use <see cref="StartUpdater" /> and close the app yourself when that matters.
    /// </para>
    /// </remarks>
    public static bool UpdateAndExit(string appId, int exitCode = 0)
    {
        if (!StartUpdater(appId))
        {
            return false;
        }

        Environment.Exit(exitCode);
        return true; // Not reached.
    }

    private static string? ReadValue(string appId, string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPathFor(appId));
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
