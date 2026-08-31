using MudBlazor;

namespace ClickWrap.Server.Components;

/// <summary>
/// MudBlazor theme built from the palette the installer and the store launcher share
/// (<c>Themes/ModernTheme.xaml</c>), so the admin UI reads as part of the same family of tools.
/// </summary>
public static class ClickWrapTheme
{
    public static readonly MudTheme Instance = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#2563EB",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#4338CA",
            Success = "#15803D",
            Error = "#DC2626",
            Warning = "#B45309",
            Info = "#2563EB",

            Background = "#F3F4F6",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#111827",
            DrawerBackground = "#FFFFFF",

            TextPrimary = "#111827",
            TextSecondary = "#6B7280",
            ActionDefault = "#6B7280",
            ActionDisabled = "#9CA3AF",
            Divider = "#E5E7EB",
            LinesDefault = "#E5E7EB",
            TableLines = "#E5E7EB",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Segoe UI", "Helvetica", "Arial", "sans-serif"] },
        },
    };
}
