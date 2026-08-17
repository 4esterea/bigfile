using Bigfile.Behaviors;

namespace Bigfile.Tests;

/// <summary>
/// The refresh rate drives how often animations are recomputed, so a nonsense
/// reading would either cap the reader at a stutter or burn frames for nothing.
/// </summary>
public class DisplayTests
{
    [Fact]
    public void Reports_a_believable_refresh_rate()
    {
        // 60 is the floor it falls back to, 360 the ceiling it trusts.
        Assert.InRange(Display.RefreshRate, 60, 360);
    }

    [Fact]
    public void Reports_the_same_rate_every_time()
    {
        // The value is queried once and cached; callers rely on it being stable.
        Assert.Equal(Display.RefreshRate, Display.RefreshRate);
    }
}
