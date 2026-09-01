using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;

namespace ClickWrap.Installer;

public partial class MainWindow : Window, IInstallProgress
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InstallConfig config;

        try
        {
            config = InstallConfig.LoadEmbedded();
        }
        catch (Exception ex)
        {
            // A broken or missing install.yaml is a packaging mistake, not a user error.
            Finish("This installer is misconfigured", ex.Message, Outcome.Error);
            return;
        }

        Title = $"Install {config.EffectiveDisplayName}";
        HeadingText.Text = $"Installing {config.EffectiveDisplayName}";

        try
        {
            await new InstallRunner(config, this).RunAsync();
            Finish($"{config.EffectiveDisplayName} is ready", StatusText.Text, Outcome.Success);
        }
        catch (InstallPausedException ex)
        {
            Finish("One more step", ex.Message, Outcome.Warning);
        }
        catch (HttpRequestException ex)
        {
            Finish("Could not reach the update server",
                $"{ex.Message}\n\nCheck your connection and try again.", Outcome.Error);
        }
        catch (Exception ex)
        {
            Finish("Installation failed", ex.Message, Outcome.Error);
        }
    }

    private enum Outcome
    {
        Success,
        Warning,
        Error,
    }

    private void Finish(string heading, string detail, Outcome outcome)
    {
        HeadingText.Text = heading;
        StatusText.Text = detail;

        var (badge, glyph, foreground) = outcome switch
        {
            Outcome.Success => ("SuccessSoftBrush", "✓", "SuccessBrush"),
            Outcome.Warning => ("WarningSoftBrush", "!", "WarningBrush"),
            _ => ("ErrorSoftBrush", "!", "ErrorBrush"),
        };

        IconBadge.Background = (System.Windows.Media.Brush)FindResource(badge);
        IconGlyph.Foreground = (System.Windows.Media.Brush)FindResource(foreground);
        IconGlyph.Text = glyph;

        Progress.IsIndeterminate = false;
        Progress.Value = outcome == Outcome.Success ? 100 : 0;

        CloseButton.Content = outcome == Outcome.Success ? "Done" : "Close";
        ButtonRow.Visibility = Visibility.Visible;
    }

    public void Status(string message) =>
        Dispatcher.Invoke(() => StatusText.Text = message);

    public void Percent(double? percent) =>
        Dispatcher.Invoke(() =>
        {
            if (percent is null)
            {
                Progress.IsIndeterminate = true;
                return;
            }

            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(percent.Value, 0, 100);
        });

    // The window is chromeless, so it needs to be draggable by its surface.
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    // WPF hyperlinks do not navigate on their own.
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser available. Not worth interrupting an install over.
        }

        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
