using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;

namespace ClickWrap.Installer;

public partial class MainWindow : Window, IInstallProgress
{
    private InstallConfig? _config;
    private UninstallRunner? _uninstaller;

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

        _config = config;

        if (IsUninstallRequested())
        {
            ShowUninstallPrompt(config);
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

    /// <summary>update.exe --uninstall (or /uninstall) turns the installer into the uninstaller.</summary>
    private static bool IsUninstallRequested() =>
        Environment.GetCommandLineArgs().Skip(1).Any(arg =>
            string.Equals(arg, InstalledApp.UninstallArgument, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase));

    /// <summary>Unlike an install, an uninstall asks first: it may delete the user's data.</summary>
    private void ShowUninstallPrompt(InstallConfig config)
    {
        var name = config.EffectiveDisplayName;
        var options = config.Uninstall;

        Title = $"Uninstall {name}";
        HeadingText.Text = $"Uninstall {name}?";
        StatusText.Text = $"This removes {name} from this computer. Windows then asks you to confirm " +
            "in a ClickOnce dialog.";
        IconGlyph.Text = "×";
        Progress.Visibility = Visibility.Collapsed;

        if (options.HasData && options.AskAboutData)
        {
            DeleteDataCheckBox.IsChecked = options.DeleteData;
            DataItemsText.Text = string.Join("\n", options.DisplayItems);
            DataOptionPanel.Visibility = Visibility.Visible;
        }
        else if (options.HasData && options.DeleteData)
        {
            // Not asked, but still never silent about deleting someone's files.
            StatusText.Text += $"\n\nIts data is deleted too:\n{string.Join("\n", options.DisplayItems)}";
        }

        CloseButton.Content = "Cancel";
        CloseButton.Style = (Style)FindResource("SecondaryButtonStyle");
        UninstallButton.Visibility = Visibility.Visible;
        ButtonRow.Visibility = Visibility.Visible;
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var config = _config!;
        var name = config.EffectiveDisplayName;
        var options = config.Uninstall;
        var deleteData = options.HasData &&
            (options.AskAboutData ? DeleteDataCheckBox.IsChecked == true : options.DeleteData);

        ButtonRow.Visibility = Visibility.Collapsed;
        UninstallButton.Visibility = Visibility.Collapsed;
        CloseButton.ClearValue(StyleProperty);
        DeleteDataCheckBox.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        HeadingText.Text = $"Uninstalling {name}";

        _uninstaller = new UninstallRunner(config, this);

        try
        {
            var result = await _uninstaller.RunAsync(deleteData);

            var heading = result.AppRemoved ? $"{name} has been removed" : $"{name}'s data has been deleted";
            if (result.Leftovers.Count == 0)
            {
                Finish(heading, "Everything it left behind has been cleaned up.", Outcome.Success);
            }
            else
            {
                Finish(heading,
                    $"These could not be deleted. You can delete them by hand:\n\n{string.Join("\n", result.Leftovers)}",
                    Outcome.Warning);
            }
        }
        catch (UninstallStoppedException ex)
        {
            Finish(ex.Heading, ex.Message, Outcome.Warning);
        }
        catch (Exception ex)
        {
            Finish("Uninstall failed", ex.Message, Outcome.Error);
        }
    }

    // Only now can update.exe's own folder go: Windows keeps a running exe locked.
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _uninstaller?.DeferredDeletion.Start();
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
