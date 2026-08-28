using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ClickWrap.Installer.Themes;

/// <summary>
/// Produces a rounded <see cref="RectangleGeometry" /> sized to the element it clips.
/// </summary>
/// <remarks>
/// <para>
/// WPF has no way to clip a <c>Border</c>'s children to its rounded corners:
/// <c>ClipToBounds</c> clips to the rectangular layout bounds, so anything sliding past
/// the end of a rounded track — the indeterminate progress thumb — shows a square corner
/// exactly where the track is round.
/// </para>
/// <para>
/// Binding <c>Clip</c> through this converter gives the real rounded geometry. The radius
/// comes from <c>ConverterParameter</c> and is clamped to half the height, so it can never
/// exceed what the shape can express.
/// </para>
/// </remarks>
public sealed class RoundedClipConverter : IMultiValueConverter
{
    /// <summary>Builds the clip geometry from an element's actual width and height.</summary>
    /// <param name="values">Actual width and actual height, in that order.</param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">Corner radius, defaulting to half the height.</param>
    /// <param name="culture">Culture used to parse <paramref name="parameter" />.</param>
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [double width, double height]) return null;

        // Zero-sized elements happen during the first layout pass; a geometry there would
        // clip everything away until the next measure.
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0) return null;

        var radius = parameter is string text
                     && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : height / 2;

        radius = Math.Min(radius, Math.Min(width, height) / 2);

        return new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }

    /// <summary>Not supported; a clip geometry is never converted back.</summary>
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
