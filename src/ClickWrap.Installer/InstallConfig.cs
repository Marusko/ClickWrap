using System.IO;
using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ClickWrap.Installer;

/// <summary>What to do when the app is already installed from a folder other than InstallFolder.</summary>
public enum ExistingInstallPolicy
{
    /// <summary>Update it where it already lives. No prompts, no loss of the app's ClickOnce data.</summary>
    Adopt,

    /// <summary>Uninstall it, then install fresh into InstallFolder. Needs the user to confirm a ClickOnce dialog.</summary>
    Reinstall,
}

/// <summary>One step to run before setup.exe.</summary>
public sealed class PreInstallStep
{
    /// <summary>"createFolder" or "downloadFile".</summary>
    public string Type { get; set; } = "";

    /// <summary>Folder to create, or destination path of the download.</summary>
    public string? Path { get; set; }

    /// <summary>Source URL, for downloadFile.</summary>
    public string? Url { get; set; }

    /// <summary>Re-download even if the destination already exists. Default false.</summary>
    public bool Overwrite { get; set; }
}

/// <summary>What the uninstaller removes besides the app itself.</summary>
public sealed class UninstallConfig
{
    /// <summary>
    /// Folders or files the app leaves outside anything ClickOnce owns: settings, logs,
    /// databases. Environment variables expand.
    /// </summary>
    public List<string> Data { get; set; } = [];

    /// <summary>Keys the app writes under HKCU\Software, e.g. HKCU\Software\TimeMaker.</summary>
    public List<string> RegistryKeys { get; set; } = [];

    /// <summary>
    /// Whether the "also delete data" checkbox starts ticked. With <see cref="AskAboutData"/> off,
    /// this is the decision itself. Default false: deleting someone's data should be a choice.
    /// </summary>
    public bool DeleteData { get; set; }

    /// <summary>Show the checkbox. Default true.</summary>
    public bool AskAboutData { get; set; } = true;

    /// <summary>
    /// Point the app's Uninstall in Settings > Apps at this uninstaller instead of ClickOnce's
    /// dialog alone. Default true. Rewrites an entry ClickOnce owns; see the README warning.
    /// </summary>
    public bool HookAddRemovePrograms { get; set; } = true;

    public bool HasData => Data.Count > 0 || RegistryKeys.Count > 0;

    public IEnumerable<string> ExpandedData => Data.Select(Environment.ExpandEnvironmentVariables);

    /// <summary>What the window lists under the checkbox.</summary>
    public IEnumerable<string> DisplayItems => ExpandedData.Concat(RegistryKeys);

    /// <summary>
    /// Turns "HKCU\Software\X" (or "HKEY_CURRENT_USER\Software\X") into the subkey path under
    /// HKCU. Only HKCU: the uninstaller runs unelevated, like everything else here.
    /// </summary>
    public static bool TryParseRegistryKey(string key, out string subKey)
    {
        subKey = "";

        foreach (var hive in (string[])[@"HKCU\", @"HKEY_CURRENT_USER\"])
        {
            if (key.StartsWith(hive, StringComparison.OrdinalIgnoreCase))
            {
                subKey = key[hive.Length..].Trim('\\');
                return subKey.Length > 0;
            }
        }

        return false;
    }

    internal void Validate()
    {
        foreach (var entry in Data)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                throw new InvalidOperationException("'uninstall.data' has an empty entry.");
            }

            var path = Environment.ExpandEnvironmentVariables(entry);

            if (path.IndexOfAny(['*', '?']) >= 0 || !Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException(
                    $"'uninstall.data' entry '{entry}' must be a full path to a folder or file, without wildcards.");
            }

            if (IsProtectedFolder(path))
            {
                throw new InvalidOperationException(
                    $"'uninstall.data' entry '{entry}' is, or contains, a Windows or user folder. " +
                    "Point it at the app's own folder instead.");
            }
        }

        foreach (var key in RegistryKeys)
        {
            // Software\{Vendor} at the very least, and never a vendor Windows or ClickWrap itself
            // depends on: a typo here must not be able to wipe someone's registry.
            if (!TryParseRegistryKey(key ?? "", out var subKey) ||
                subKey.Split('\\') is not [var software, var vendor, ..] ||
                !string.Equals(software, "Software", StringComparison.OrdinalIgnoreCase) ||
                ProtectedVendors.Contains(vendor))
            {
                throw new InvalidOperationException(
                    $"'uninstall.registryKeys' entry '{key}' must be an app's own key under HKCU\\Software, " +
                    @"e.g. HKCU\Software\TimeMaker.");
            }
        }
    }

    private static readonly HashSet<string> ProtectedVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "Wow6432Node", "ClickWrap",
    };

    /// <summary>
    /// True for a drive root, any known Windows or user folder, and anything above one of them
    /// (C:\Users, say), so a bad entry cannot take out more than the app's own folder.
    /// </summary>
    private static bool IsProtectedFolder(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var knownFolders = Enum.GetValues<Environment.SpecialFolder>()
            .Select(Environment.GetFolderPath)
            .Append(Path.GetTempPath())
            .Where(folder => !string.IsNullOrEmpty(folder))
            .Select(folder => Path.TrimEndingDirectorySeparator(folder));

        return knownFolders.Any(folder =>
            string.Equals(folder, full, StringComparison.OrdinalIgnoreCase) ||
            folder.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The install.yaml baked into this exe at build time.</summary>
public sealed class InstallConfig
{
    public string AppId { get; set; } = "";

    /// <summary>Shown in the installer window. Falls back to AppId.</summary>
    public string? DisplayName { get; set; }

    public string ServerUrl { get; set; } = "";

    /// <summary>
    /// The fixed folder the publish output is always extracted into. Environment variables expand.
    /// ClickOnce refuses to update an app from a different folder than it was installed from, so
    /// this must never change once an app has shipped.
    /// </summary>
    public string InstallFolder { get; set; } = "";

    public ExistingInstallPolicy OnExistingInstall { get; set; } = ExistingInstallPolicy.Adopt;

    public List<PreInstallStep> PreInstall { get; set; } = [];

    public UninstallConfig Uninstall { get; set; } = new();

    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? AppId : DisplayName;

    public string ExpandedInstallFolder => Environment.ExpandEnvironmentVariables(InstallFolder);

    /// <summary>Reads the YAML embedded as "install.yaml" and validates it.</summary>
    public static InstallConfig LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("install.yaml")
            ?? throw new InvalidOperationException(
                "No install.yaml embedded in this exe. Build with -p:AppConfig=<name>.");

        using var reader = new StreamReader(stream);

        var config = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<InstallConfig>(reader)
            ?? throw new InvalidOperationException("install.yaml is empty.");

        config.Validate();
        return config;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(AppId))
        {
            throw new InvalidOperationException("install.yaml is missing 'appId'.");
        }

        if (string.IsNullOrWhiteSpace(ServerUrl) ||
            !Uri.TryCreate(ServerUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("install.yaml needs a 'serverUrl' like https://updates.example.com.");
        }

        if (string.IsNullOrWhiteSpace(InstallFolder))
        {
            throw new InvalidOperationException("install.yaml is missing 'installFolder'.");
        }

        foreach (var step in PreInstall)
        {
            switch (step.Type?.Trim().ToLowerInvariant())
            {
                case "createfolder":
                    if (string.IsNullOrWhiteSpace(step.Path))
                    {
                        throw new InvalidOperationException("A 'createFolder' step needs a 'path'.");
                    }

                    break;

                case "downloadfile":
                    if (string.IsNullOrWhiteSpace(step.Url) || string.IsNullOrWhiteSpace(step.Path))
                    {
                        throw new InvalidOperationException("A 'downloadFile' step needs both 'url' and 'path'.");
                    }

                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown pre-install step type '{step.Type}'. Use 'createFolder' or 'downloadFile'.");
            }
        }

        // YamlDotNet assigns null for a key that is present but empty ("uninstall:" or "data:").
        Uninstall ??= new UninstallConfig();
        Uninstall.Data ??= [];
        Uninstall.RegistryKeys ??= [];
        Uninstall.Validate();
    }
}
