using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ClickWrap.Installer;

/// <summary>An existing per-user ClickOnce install, as recorded in Add/Remove Programs.</summary>
public sealed record ClickOnceInstallation(
    string DisplayName,
    string? Version,
    string? InstallFolder,
    string UninstallString,
    string RegistryKeyName);

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
        // The recorded string is already "rundll32.exe dfshim.dll,ShArpMaintain <identity>".
        const string prefix = "rundll32.exe ";
        var arguments = installation.UninstallString.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? installation.UninstallString[prefix.Length..]
            : installation.UninstallString;

        Process.Start(new ProcessStartInfo("rundll32.exe", arguments) { UseShellExecute = true });
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
