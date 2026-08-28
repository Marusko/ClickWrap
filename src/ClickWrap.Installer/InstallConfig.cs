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
    }
}
