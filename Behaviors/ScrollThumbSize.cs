using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Bigfile.Behaviors;

/// <summary>
/// Keeps a scrollbar thumb big enough to grab on a document of millions of lines.
///
/// A Track sizes its thumb as viewport over extent. On 8 million lines that is a
/// few ten-thousandths of the track — a couple of pixels — and Track does not
/// honour the thumb's MinHeight, so the floor has to be applied to the number the
/// bar is given rather than to the thumb it draws. Reporting a larger viewport
/// size only widens the thumb: the offset the thumb sits at is computed from
/// Value against Maximum, which this does not touch.
/// </summary>
public sealed class MinimumThumbConverter : IMultiValueConverter
{
    /// <summary>Smallest share of the track the thumb may shrink to.</summary>
    private const double MinFraction = 0.05;

    /// <param name="values">The viewport length, then the scrollable length.</param>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 ||
            values[0] is not double viewport ||
            values[1] is not double scrollable)
        {
            return DependencyProperty.UnsetValue;
        }

        if (viewport <= 0 || scrollable <= 0)
        {
            return viewport;
        }

        // Solving viewport / (scrollable + viewport) >= MinFraction for viewport.
        var floor = MinFraction * scrollable / (1 - MinFraction);

        return Math.Max(viewport, floor);
    }

    public object[] ConvertBack(
        object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
