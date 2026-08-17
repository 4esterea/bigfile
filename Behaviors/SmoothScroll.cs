using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Bigfile.Behaviors;

/// <summary>
/// ScrollViewer.VerticalOffset is read-only, so the way to glide the viewport
/// instead of jumping is to animate a proxy property and forward its value.
/// </summary>
public static class SmoothScroll
{
    private static readonly Duration Glide = TimeSpan.FromMilliseconds(150);

    private static readonly DependencyProperty OffsetProperty =
        DependencyProperty.RegisterAttached(
            "Offset",
            typeof(double),
            typeof(SmoothScroll),
            new PropertyMetadata(0d, OnOffsetChanged));

    /// <summary>Where the glide in flight is heading, for <see cref="By"/>.</summary>
    private static readonly DependencyProperty TargetProperty =
        DependencyProperty.RegisterAttached(
            "Target", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0d));

    /// <summary>When that glide arrives, as a tick count.</summary>
    private static readonly DependencyProperty GlideEndProperty =
        DependencyProperty.RegisterAttached(
            "GlideEnd", typeof(long), typeof(SmoothScroll), new PropertyMetadata(0L));

    /// <summary>
    /// The last offset this class pushed into the viewer. When the viewer has
    /// moved away from it, something else — a drag of the thumb, a click in the
    /// track — scrolled in the meantime, and any glide still on the clock is
    /// stale.
    /// </summary>
    private static readonly DependencyProperty AppliedProperty =
        DependencyProperty.RegisterAttached(
            "Applied", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN));

    /// <summary>How far the viewer may drift before a glide counts as overtaken.</summary>
    private const double Drift = 0.5;

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var offset = (double)e.NewValue;

        d.SetValue(AppliedProperty, offset);
        ((ScrollViewer)d).ScrollToVerticalOffset(offset);
    }

    /// <summary>Eases the viewer from where it is now to <paramref name="offset"/>.</summary>
    public static void To(ScrollViewer viewer, double offset)
    {
        var target = Math.Clamp(offset, 0, viewer.ScrollableHeight);

        viewer.SetValue(TargetProperty, target);
        viewer.SetValue(
            GlideEndProperty,
            Environment.TickCount64 + (long)Glide.TimeSpan.TotalMilliseconds);

        // From is given explicitly rather than left to the property's current
        // value. A finished animation holds its final value and outranks the
        // local one, so after the thumb has been dragged the property still reads
        // where the last glide ended — and the next glide would start by snapping
        // the viewport back there. The real offset is the only honest start.
        var animation = new DoubleAnimation(viewer.VerticalOffset, target, Glide)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        // Without this the timing engine ticks at its default 60 Hz and a glide
        // looks stepped on a faster display.
        Timeline.SetDesiredFrameRate(animation, Display.RefreshRate);

        viewer.BeginAnimation(OffsetProperty, animation);
    }

    /// <summary>
    /// Glides <paramref name="delta"/> further down the document.
    ///
    /// A step that arrives while an earlier one is still gliding is measured
    /// from that step's destination rather than from the offset the animation
    /// happens to be passing through. Without it, spinning the wheel would
    /// restart the same short hop over and over and barely move.
    /// </summary>
    public static void By(ScrollViewer viewer, double delta)
    {
        var applied = (double)viewer.GetValue(AppliedProperty);

        // A glide only counts as in flight while the viewer is still where this
        // class put it; if the user has dragged the thumb since, its destination
        // is meaningless and the step starts from where the viewer really is.
        var gliding = Environment.TickCount64 < (long)viewer.GetValue(GlideEndProperty)
                      && !double.IsNaN(applied)
                      && Math.Abs(viewer.VerticalOffset - applied) <= Drift;

        var from = gliding ? (double)viewer.GetValue(TargetProperty) : viewer.VerticalOffset;

        To(viewer, from + delta);
    }

    /// <summary>Digs the ScrollViewer out of a control's template.</summary>
    public static ScrollViewer? FindViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
