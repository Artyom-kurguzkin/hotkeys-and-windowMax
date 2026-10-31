namespace WindowManager.Tests;

// Where a fullscreen window's slot goes when the user moves the window to another monitor (Win+Shift+Arrow).
public class LieFollowTests
{
    static readonly Rect Laptop = new(0, 0, 1920, 1200);
    static readonly Rect Big = new(940, -2172, 4780, -12);      // 3840x2160 above the laptop panel
    static readonly Rect Other = new(-2900, -2160, 940, 0);      // 3840x2160 to the left

    [Fact] public void The_whole_monitor_becomes_the_whole_new_monitor() => Assert.Equal(Big, LieLogic.FollowMonitor(Laptop, Laptop, Big));

    [Fact] public void Back_again_is_exact() => Assert.Equal(Laptop, LieLogic.FollowMonitor(Big, Big, Laptop));

    [Fact]
    public void A_left_tile_stays_a_left_tile()
    {
        var got = LieLogic.FollowMonitor(LieLogic.LeftHalf(Laptop), Laptop, Big);
        Assert.Equal(LieLogic.LeftHalf(Big), got);
    }

    [Fact]
    public void A_right_tile_stays_a_right_tile_between_the_two_big_monitors()
    {
        var got = LieLogic.FollowMonitor(LieLogic.RightHalf(Big), Big, Other);
        Assert.Equal(LieLogic.RightHalf(Other), got);
    }

    [Fact]
    public void A_floating_slot_keeps_its_place_and_lands_inside_the_new_monitor()
    {
        var got = LieLogic.FollowMonitor(new Rect(104, 0, 1911, 1191), Laptop, Big);
        Assert.True(got.Left >= Big.Left && got.Top >= Big.Top && got.Right <= Big.Right && got.Bottom <= Big.Bottom);
        Assert.True(got.Width > 0 && got.Height > 0);
    }
}
