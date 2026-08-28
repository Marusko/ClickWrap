using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClickWrap.Installer.Themes;

/// <summary>
/// Sweeps a progress bar's indeterminate thumb across the full width of its track.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Storyboard" /> declared inside a <see cref="Trigger" /> is frozen, so its
/// <c>To</c> cannot bind to the track width — it has to be a constant. Any constant is
/// wrong: too small and the thumb turns back before reaching the end (the bar in this app
/// widens when an operation starts and its buttons hide, which made the thumb stop around
/// 80%); too large and it spends most of the cycle clipped out of sight.
/// </para>
/// <para>
/// Running the animation from code instead lets it use the measured width, and re-run when
/// that width changes.
/// </para>
/// </remarks>
public static class IndeterminateSweep
{
    /// <summary>How long one pass across the track takes.</summary>
    private static readonly Duration SweepDuration = new(TimeSpan.FromMilliseconds(1100));

    /// <summary>Thumb width as a fraction of the track.</summary>
    private const double ThumbFraction = 0.28;

    private const double MinThumbWidth = 36;
    private const double MaxThumbWidth = 180;

    /// <summary>Enables the sweep on the element it is set on.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(IndeterminateSweep),
        new PropertyMetadata(false, OnIsActiveChanged));

    /// <summary>Tracks the handler so it can be detached again.</summary>
    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(SizeChangedEventHandler), typeof(IndeterminateSweep));

    /// <summary>Sets <see cref="IsActiveProperty" />.</summary>
    public static void SetIsActive(DependencyObject element, bool value)
        => element.SetValue(IsActiveProperty, value);

    /// <summary>Gets <see cref="IsActiveProperty" />.</summary>
    public static bool GetIsActive(DependencyObject element)
        => (bool)element.GetValue(IsActiveProperty);

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement thumb) return;

        Detach(thumb);

        if (e.NewValue is not true) return;

        // The host is the track's content area, so its width is what the thumb has to
        // cross. It is not measured yet when the template first loads, hence both the
        // Loaded hook and the SizeChanged one.
        var handler = new SizeChangedEventHandler((_, _) => Start(thumb));
        thumb.SetValue(HandlerProperty, handler);

        if (Host(thumb) is { } host)
        {
            host.SizeChanged += handler;
        }

        thumb.Loaded += OnThumbLoaded;
        Start(thumb);
    }

    private static void OnThumbLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement thumb) Start(thumb);
    }

    private static void Detach(FrameworkElement thumb)
    {
        thumb.Loaded -= OnThumbLoaded;

        if (thumb.GetValue(HandlerProperty) is SizeChangedEventHandler handler
            && Host(thumb) is { } host)
        {
            host.SizeChanged -= handler;
        }

        thumb.ClearValue(HandlerProperty);

        if (thumb.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }

    private static FrameworkElement? Host(FrameworkElement thumb)
        => VisualTreeHelper.GetParent(thumb) as FrameworkElement;

    private static void Start(FrameworkElement thumb)
    {
        if (!GetIsActive(thumb)) return;

        var width = Host(thumb)?.ActualWidth ?? 0;
        if (width <= 0) return;

        var thumbWidth = Math.Clamp(width * ThumbFraction, MinThumbWidth, MaxThumbWidth);
        thumb.Width = thumbWidth;

        // A fresh transform each time: re-animating a transform that is mid-flight can
        // leave the old animation's hold value in place.
        var transform = new TranslateTransform(-thumbWidth, 0);
        thumb.RenderTransform = transform;

        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = -thumbWidth,
            To = width,
            Duration = SweepDuration,
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }
}
