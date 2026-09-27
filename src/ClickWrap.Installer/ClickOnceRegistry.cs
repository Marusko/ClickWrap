using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ClickWrap.Installer;

/// <summary>An existing per-user ClickOnce install, as recorded in Add/Remove Programs.</summary>
/// <param name="UninstallString">As recorded, so possibly hooked to point at update.exe.</param>
public sealed record ClickOnceInstallation(
    string DisplayName,
    string? Version,
    string? InstallFolder,
    string UninstallString,
    string RegistryKeyName)
{
    /// <summary>ClickOnce's own "rundll32.exe dfshim.dll,ShArpMaintain …", hooked or not.</summary>
    public string ClickOnceUninstallString { get; } = ClickOnceRegistry.Unhooked(UninstallString);

    /// <summary>True when Add/Remove Programs runs update.exe rather than ClickOnce directly.</summary>
    public bool IsHooked => !string.Equals(UninstallString, ClickOnceUninstallString, StringComparison.Ordinal);
}

/// <summary>
/// Finds the Add/Remove Programs entry ClickOnce writes for an installed deployment.
/// This is how the installer learns where an app was previously installed from, which
/// matters because ClickOnce refuses to update an app from a different folder.
/// </summary>
public static class ClickOnceRegistry
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <param name="deploymentName">The deployment manifest file name, e.g. "RaceTimer.application".</param>
    public static ClickOnceInstallation? Find(string deploymentName)
    {
        using var uninstallKey = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        if (uninstallKey is null)
        {
            return null;
        }

        foreach (var subKeyName in uninstallKey.GetSubKeyNames())
        {
            using var subKey = uninstallKey.OpenSubKey(subKeyName);
            if (subKey?.GetValue("UninstallString") is not string uninstallString)
            {
                continue;
            }

            // ClickOnce entries look like:
            //   rundll32.exe dfshim.dll,ShArpMaintain App.application, Culture=…, PublicKeyToken=…, …
            // A hooked entry keeps that whole command at its end, so this still matches it.
            if (!uninstallString.Contains("ShArpMaintain", StringComparison.OrdinalIgnoreCase) ||
                !uninstallString.Contains(deploymentName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new ClickOnceInstallation(
                subKey.GetValue("DisplayName") as string ?? deploymentName,
                subKey.GetValue("DisplayVersion") as string,
                ResolveInstallFolder(subKey.GetValue("UrlUpdateInfo") as string),
                uninstallString,
                subKeyName);
        }

        return null;
    }

    /// <summary>
    /// UrlUpdateInfo is the location the app was installed from, e.g.
    /// file:///C:/Users/x/Downloads/Race%20Timer/Race%20timer.application. Only local paths are
    /// usable as an install folder; a web-deployed app has an http URL and is left alone.
    /// </summary>
    private static string? ResolveInstallFolder(string? urlUpdateInfo)
    {
        if (string.IsNullOrWhiteSpace(urlUpdateInfo) ||
            !Uri.TryCreate(urlUpdateInfo, UriKind.Absolute, out var uri) ||
            !uri.IsFile)
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(uri.LocalPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the ClickOnce maintenance dialog. There is no silent uninstall for ClickOnce, so the
    /// user has to pick "Remove the application" themselves.
    /// </summary>
    public static void LaunchUninstallDialog(ClickOnceInstallation installation)
    {
        using var _ = StartMaintenance(installation);
    }

    /// <summary>
    /// Opens the ClickOnce maintenance dialog and waits for it to close. Closing it does not mean
    /// the app is gone: the user can cancel, or restore the previous version instead, so the
    /// caller must check with <see cref="Find"/> afterwards.
    /// </summary>
    public static async Task RunUninstallDialogAsync(ClickOnceInstallation installation, CancellationToken cancellationToken)
    {
        using var process = StartMaintenance(installation)
            ?? throw new InvalidOperationException("Could not open the ClickOnce uninstall dialog.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What every ClickOnce uninstall command starts with; nothing else is ever run.</summary>
    private const string MaintenanceCommandPrefix = "rundll32.exe dfshim.dll,ShArpMaintain ";

    private static Process? StartMaintenance(ClickOnceInstallation installation)
    {
        // "rundll32.exe dfshim.dll,ShArpMaintain <identity>", with any hook already taken off.
        // The string comes from the registry, which anything running as the user can edit, so
        // only the identity part is taken from it: a tampered entry cannot name another DLL.
        var command = installation.ClickOnceUninstallString;
        if (!command.StartsWith(MaintenanceCommandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The Add/Remove Programs entry for {installation.DisplayName} does not hold a ClickOnce " +
                $"uninstall command, so it was not run:\n{command}");
        }

        // Full path: a bare "rundll32.exe" is looked for in the current directory first.
        var rundll32 = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
        var arguments = "dfshim.dll,ShArpMaintain " + command[MaintenanceCommandPrefix.Length..];

        return Process.Start(new ProcessStartInfo(rundll32, arguments) { UseShellExecute = true });
    }

    /// <summary>
    /// Sits between the uninstaller and ClickOnce's own command in a hooked UninstallString:
    /// "C:\…\update.exe" --uninstall --clickonce rundll32.exe dfshim.dll,ShArpMaintain …
    /// </summary>
    private const string HookMarker = " --clickonce ";

    /// <summary>
    /// Points the app's Add/Remove Programs Uninstall at update.exe, so removing it from
    /// Settings > Apps also cleans up after it. Idempotent: re-hooking replaces the old hook.
    /// </summary>
    /// <remarks>
    /// ClickOnce's command stays on the end of the string: the uninstaller runs it from there, and
    /// anything that recognises a ClickOnce entry by "ShArpMaintain" plus the deployment name —
    /// <see cref="Find"/>, and the same check in installers built before this existed — still
    /// does. Without that, orphan pruning would take a hooked app for uninstalled and delete it.
    /// </remarks>
    /// <returns>False when the entry could not be written; the app is installed either way.</returns>
    public static bool Hook(ClickOnceInstallation installation, string uninstallerPath) =>
        // Never carry forward anything but a genuine ClickOnce command.
        installation.ClickOnceUninstallString.StartsWith(MaintenanceCommandPrefix, StringComparison.OrdinalIgnoreCase) &&
        SetUninstallString(
            installation,
            $"\"{uninstallerPath}\" {InstalledApp.UninstallArgument}{HookMarker}{installation.ClickOnceUninstallString}");

    /// <summary>Puts ClickOnce's own command back, when an app opts out of the hook.</summary>
    public static bool Unhook(ClickOnceInstallation installation) =>
        !installation.IsHooked || SetUninstallString(installation, installation.ClickOnceUninstallString);

    /// <summary>The ClickOnce command inside a hooked UninstallString, or the string itself.</summary>
    internal static string Unhooked(string uninstallString)
    {
        var marker = uninstallString.IndexOf(HookMarker, StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && uninstallString.Contains(InstalledApp.UninstallArgument, StringComparison.OrdinalIgnoreCase)
            ? uninstallString[(marker + HookMarker.Length)..]
            : uninstallString;
    }

    private static bool SetUninstallString(ClickOnceInstallation installation, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{UninstallKeyPath}\{installation.RegistryKeyName}", writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue("UninstallString", value);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>True when two folders refer to the same place, ignoring case and trailing slashes.</summary>
    public static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        static string Normalise(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        try
        {
            return string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
