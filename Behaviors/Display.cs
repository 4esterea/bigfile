using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Bigfile.Behaviors;

/// <summary>
/// What the screen can actually show, and getting WPF to use it.
///
/// WPF's timing engine ticks animations at 60 Hz unless told otherwise, and its
/// composition target is capped the same way, so on a 120 or 165 Hz display a
/// glide is quantised to 60 steps a second and reads as stepped. The refresh
/// rate is read from the display once and used as the desired frame rate of the
/// animations this application runs.
/// </summary>
public static class Display
{
    /// <summary>Index of the vertical refresh rate in GetDeviceCaps.</summary>
    private const int VRefresh = 116;

    /// <summary>Assumed when the display will not say, and the floor for what it does.</summary>
    private const int MinRefreshRate = 60;

    /// <summary>Nothing above this is believed, in case a driver reports nonsense.</summary>
    private const int MaxRefreshRate = 360;

    private static readonly Lazy<int> Rate = new(Query);

    /// <summary>Refresh rate of the primary display, in hertz.</summary>
    public static int RefreshRate => Rate.Value;

    /// <summary>
    /// Raises the ceiling on how often WPF composes a frame. The tier check
    /// keeps it to hardware rendering: forcing frames out of a software
    /// renderer buys nothing and costs the CPU.
    /// </summary>
    public static void UseFullRefreshRate()
    {
        if (RenderCapability.Tier >> 16 == 0)
        {
            return;
        }

        try
        {
            // Changing the default of DesiredFrameRate lifts every animation in
            // the process, including those inside the control library, without
            // having to reach into each one. The property is int?, so the default
            // has to be boxed as one.
            Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(Timeline),
                new PropertyMetadata((int?)RefreshRate));
        }
        catch (ArgumentException)
        {
            // Metadata can only be overridden before the property is first used.
            // If something animated during startup, the per-animation rate that
            // SmoothScroll sets still covers scrolling, which is what is felt.
        }
    }

    private static int Query()
    {
        var dc = GetDC(IntPtr.Zero);

        if (dc == IntPtr.Zero)
        {
            return MinRefreshRate;
        }

        try
        {
            var rate = GetDeviceCaps(dc, VRefresh);

            // 0 and 1 both mean "the hardware default", which tells us nothing.
            return rate <= 1
                ? MinRefreshRate
                : Math.Clamp(rate, MinRefreshRate, MaxRefreshRate);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr dc, int index);
}
