using WindowManager;

namespace WindowManager.Tests;

// Test names mirror the scenarios in openspec/specs/*/spec.md.
public class HotkeysTests
{
    readonly KeyEngine e = new();

    // Holds the modifiers down, then presses key; returns the key-down decision.
    Decision Chord(int key, params int[] mods)
    {
        foreach (var m in mods) Assert.Equal(Decision.Pass, e.Feed(m, true, false));
        return e.Feed(key, true, false);
    }

    [Fact]
    public void AltZ_sends_AppsKey()
    {
        var d = Chord('Z', Vk.LAlt);
        Assert.Equal(new Decision(true, Act.AppsKey), d);
        Assert.True(e.Feed('Z', false, false).Suppress); // key-up swallowed too
        Assert.Equal(KeyEngine.Tap(Vk.Apps), KeyEngine.Output(Act.AppsKey));
    }

    [Fact]
    public void AltC_clicks() => Assert.Equal(new Decision(true, Act.Click), Chord('C', Vk.RAlt));

    [Fact]
    public void LeftAltX_sends_Escape() => Assert.Equal(new Decision(true, Act.Esc), Chord('X', Vk.LAlt));

    [Fact]
    public void RightAltX_passes_through() => Assert.Equal(Decision.Pass, Chord('X', Vk.RAlt));

    [Theory]
    [InlineData(Vk.OemClose, Act.End)]
    [InlineData(Vk.OemOpen, Act.Home)]
    public void AltBracket_jumps_to_line_edges(int key, Act act) => Assert.Equal(new Decision(true, act), Chord(key, Vk.LAlt));

    [Theory]
    [InlineData(Vk.OemClose, Act.ShiftEnd)]
    [InlineData(Vk.OemOpen, Act.ShiftHome)]
    public void ShiftAltBracket_selects_to_line_edges(int key, Act act) =>
        Assert.Equal(new Decision(true, act), Chord(key, Vk.LShift, Vk.LAlt));

    [Fact]
    public void ShiftEnd_output_is_shift_wrapped() =>
        Assert.Equal([new(Vk.LShift, false), new(Vk.End, false), new(Vk.End, true), new(Vk.LShift, true)], KeyEngine.Output(Act.ShiftEnd));

    [Fact]
    public void AltO_opens_line_below()
    {
        Assert.Equal(new Decision(true, Act.NewLineBelow), Chord('O', Vk.LAlt));
        Assert.Equal([.. KeyEngine.Tap(Vk.End), .. KeyEngine.Tap(Vk.LShift, Vk.Enter)], KeyEngine.Output(Act.NewLineBelow));
    }

    [Fact]
    public void ShiftAltO_opens_line_below() => Assert.Equal(new Decision(true, Act.NewLineBelow), Chord('O', Vk.LShift, Vk.LAlt));

    [Fact]
    public void PageUp_switches_desktop_left()
    {
        Assert.Equal(new Decision(true, Act.DesktopLeft), Chord(Vk.PgUp));
        Assert.Equal(KeyEngine.Tap(Vk.LCtrl, Vk.LWin, Vk.Left), KeyEngine.Output(Act.DesktopLeft));
    }

    [Fact]
    public void PageDown_switches_desktop_right() => Assert.Equal(new Decision(true, Act.DesktopRight), Chord(Vk.PgDn));

    [Fact]
    public void Modified_PageUp_passes_through() => Assert.Equal(Decision.Pass, Chord(Vk.PgUp, Vk.LShift));

    [Theory]
    [InlineData('J', Act.WheelDown)]
    [InlineData('K', Act.WheelUp)]
    [InlineData('H', Act.WheelLeft)]
    [InlineData('L', Act.WheelRight)]
    public void AltHJKL_scrolls(int key, Act act) => Assert.Equal(new Decision(true, act), Chord(key, Vk.LAlt));

    [Fact]
    public void Extra_modifier_does_not_match() => Assert.Equal(Decision.Pass, Chord('J', Vk.LCtrl, Vk.LAlt));

    [Fact]
    public void Held_modifiers_are_lifted_around_output()
    {
        Assert.Equal([.. KeyEngine.Tap(Vk.Mask), new(Vk.LAlt, true)], KeyEngine.Lift([Vk.LAlt]));
        Assert.Equal([new(Vk.LAlt, false)], KeyEngine.Restore([Vk.LAlt], [Vk.LAlt]));
    }

    [Fact]
    public void Shift_only_lift_needs_no_mask() => Assert.Equal([new(Vk.LShift, true)], KeyEngine.Lift([Vk.LShift]));

    [Fact]
    public void Modifier_released_mid_output_is_not_restored() => Assert.Empty(KeyEngine.Restore([Vk.LAlt], []));

    [Fact]
    public void Alt_release_after_shortcut_is_masked()
    {
        Chord('J', Vk.LAlt);
        e.Feed('J', false, false);
        Chord('J'); // second shortcut while Alt still held
        Assert.Equal(new Decision(false, MaskFirst: true), e.Feed(Vk.LAlt, false, false));
        // a later plain tap is clean again
        e.Feed(Vk.LAlt, true, false);
        Assert.Equal(Decision.Pass, e.Feed(Vk.LAlt, false, false));
    }

    [Fact]
    public void Plain_Alt_tap_is_untouched()
    {
        Assert.Equal(Decision.Pass, e.Feed(Vk.LAlt, true, false));
        Assert.Equal(Decision.Pass, e.Feed(Vk.LAlt, false, false));
    }

    [Fact]
    public void Injected_key_is_passed_through()
    {
        e.Feed(Vk.LAlt, true, true);
        Assert.Equal(Decision.Pass, e.Feed('Z', true, true));
        Assert.Equal(Mod.None, e.Mods); // injected modifiers don't count as physically held
    }

    [Fact]
    public void CtrlAltShiftQ_quits() => Assert.Equal(new Decision(true, Act.Quit), Chord('Q', Vk.LCtrl, Vk.LAlt, Vk.LShift));
}

public class DiagnosticsTests
{
    [Fact]
    public void Large_log_is_rotated()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "wm.log");
        File.WriteAllBytes(path, new byte[Log.MaxBytes + 1]);
        File.WriteAllText(path + ".old", "previous");

        Log.Init(dir, verbose: false);
        Log.Info("hello");

        Assert.Equal(Log.MaxBytes + 1, new FileInfo(path + ".old").Length);
        Assert.Contains("hello", File.ReadAllText(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Raw_keys_hidden_by_default()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Log.Init(dir, verbose: false);
        Log.Debug("raw key");
        Log.Info("action");
        var text = File.ReadAllText(Path.Combine(dir, "wm.log"));
        Assert.Contains("action", text);
        Assert.DoesNotContain("raw key", text);
        Directory.Delete(dir, true);
    }
}

public class FocusTests
{
    const uint Real = Focus.WS_VISIBLE | 0x00CF0000;
    static readonly IntPtr A = 1, B = 2;

    [Fact]
    public void Hovering_a_new_window_focuses_it() => Assert.True(Focus.ShouldHoverFocus(B, A, false, Focus.IsRealWindow(Real)));

    [Fact]
    public void Same_window_is_not_reactivated() => Assert.False(Focus.ShouldHoverFocus(A, A, false, true));

    [Theory]
    [InlineData(Real | Focus.WS_POPUP)]
    [InlineData(Real | Focus.WS_CHILD)]
    [InlineData(Focus.WS_VISIBLE)] // no frame at all
    [InlineData(0x00CF0000u)]      // hidden
    public void Popup_is_not_focused(uint style) => Assert.False(Focus.IsRealWindow(style));

    [Fact]
    public void Captionless_window_is_still_real() => Assert.True(Focus.IsRealWindow(Focus.WS_VISIBLE | 0x00040000)); // WS_THICKFRAME only

    [Fact]
    public void Hover_ignored_while_Alt_held() => Assert.False(Focus.ShouldHoverFocus(B, A, true, true));

    [Fact]
    public void Switching_window_centers_cursor() => Assert.True(Focus.ShouldCenter(A, B, mouseInside: false));

    [Fact]
    public void Cursor_already_inside_stays_put() => Assert.False(Focus.ShouldCenter(A, B, mouseInside: true));

    [Fact]
    public void Same_window_does_not_move_cursor() => Assert.False(Focus.ShouldCenter(A, A, mouseInside: false));

    [Fact]
    public void Rect_center_and_contains()
    {
        var r = new Rect(100, 50, 300, 250);
        Assert.Equal((200, 150), r.Center);
        Assert.True(r.Contains(100, 250));
        Assert.False(r.Contains(301, 100));
    }
}

public class TrackPointScrollTests
{
    readonly TrackPointScroller s = new();

    [Fact]
    public void Raw_buffer_offsets_match_x64_layout() // same values as binds.ahk SelfTestParseRawMouse
    {
        var buf = new byte[RawMouse.HeaderSize + 24];
        BitConverter.TryWriteBytes(buf.AsSpan(8), 4321L);
        BitConverter.TryWriteBytes(buf.AsSpan(RawMouse.HeaderSize + 12), 5);
        BitConverter.TryWriteBytes(buf.AsSpan(RawMouse.HeaderSize + 16), -7);
        Assert.Equal(new RawMouse(4321, 0, 5, -7), RawMouse.Parse(buf));
    }

    [Fact]
    public void Non_mouse_raw_input_is_rejected()
    {
        var buf = new byte[RawMouse.HeaderSize + 24];
        buf[0] = 1; // RIM_TYPEKEYBOARD
        Assert.Null(RawMouse.Parse(buf));
    }

    static int Wheel(int raw) => (int)Math.Truncate(TrackPointScroller.CurveDelta(raw) * TrackPointScroller.WheelDelta / TrackPointScroller.UnitsPerNotch);

    [Fact]
    public void Push_down_scrolls_down()
    {
        var (x, y, started) = s.Motion(0, 10); // curve(10) ≈ 17.4 units ≈ 2.2 notches
        Assert.True(started);
        Assert.Equal(0, x);
        Assert.Equal(Wheel(10), y);
        Assert.InRange(y, 241, 299);
    }

    [Fact]
    public void Scrolling_is_smooth_not_notched() => Assert.NotEqual(0, s.Motion(0, 10).Y % TrackPointScroller.WheelDelta);

    [Fact]
    public void Push_left_scrolls_left() => Assert.True(s.Motion(-10, 0).X < 0);

    [Fact]
    public void Hard_push_is_capped() =>
        Assert.Equal(TrackPointScroller.MaxNotchesPerEvent * TrackPointScroller.WheelDelta, s.Motion(0, 1000).Y);

    [Fact]
    public void Small_motion_accumulates()
    {
        var sent = 0;
        for (var i = 0; i < 10; i++) sent += s.Motion(0, 1).Y; // each ≈ 6.5 wheel units: fractions carry over
        var exact = 10 * TrackPointScroller.CurveDelta(1) * TrackPointScroller.WheelDelta / TrackPointScroller.UnitsPerNotch;
        Assert.Equal((int)Math.Truncate(exact), sent); // nothing lost to rounding
    }

    [Fact]
    public void Reversal_starts_fresh()
    {
        s.Motion(0, 1); // leaves a downward remainder
        Assert.True(s.AccumY > 0);
        Assert.Equal(-Wheel(1), s.Motion(0, -1).Y); // the old remainder is not subtracted
        Assert.True(s.AccumY < 0);
    }

    [Fact]
    public void Alt_release_stops_scrolling()
    {
        s.Motion(0, 1);
        s.Stop();
        Assert.False(s.InGesture);
        Assert.Equal(0, s.AccumY);
        Assert.True(s.Motion(0, 1).Started);
    }

    [Fact]
    public void Scrolling_marks_Alt_release_for_masking()
    {
        var e = new KeyEngine();
        e.Feed(Vk.LAlt, true, false);
        e.MarkChordUsed();
        Assert.True(e.Feed(Vk.LAlt, false, false).MaskFirst);
    }
}

public class BorderlessWindowsTests
{
    const uint Overlapped = Focus.WS_VISIBLE | 0x00CF0000; // WS_OVERLAPPEDWINDOW incl. caption

    [Fact]
    public void Framed_window_is_stripped()
    {
        Assert.True(Chrome.IsManageable(Overlapped, 0));
        Assert.False(Chrome.HasCaption(Chrome.StrippedStyle(Overlapped)));
    }

    [Theory]
    [InlineData(Overlapped, Chrome.WS_EX_TOOLWINDOW)]
    [InlineData(Overlapped | Focus.WS_POPUP, 0u)]
    public void Tool_window_is_left_alone(uint style, uint ex) => Assert.False(Chrome.IsManageable(style, ex));

    [Theory]
    [InlineData("explorer")]
    [InlineData("Explorer")] // case-insensitive
    [InlineData("notepad")]
    [InlineData("Notepad")]
    public void Excluded_app_is_left_alone(string processName) => Assert.False(Chrome.IsManageable(Overlapped, 0, processName: processName));

    [Fact]
    public void Other_apps_are_unaffected_by_the_exclusion() => Assert.True(Chrome.IsManageable(Overlapped, 0, processName: "vivaldi"));

    [Fact]
    public void Window_stays_resizable()
    {
        var s = Chrome.StrippedStyle(Overlapped);
        Assert.Equal(Chrome.WS_THICKFRAME, s & Chrome.WS_THICKFRAME);
        Assert.Equal(0x00030000u, s & 0x00030000u); // WS_MINIMIZEBOX | WS_MAXIMIZEBOX
        Assert.True(Chrome.IsManageable(s, 0)); // still recognised after stripping
    }

    // Maximized frame-only window: 8px invisible frame around a client area that sits on the work area.
    static readonly Rect Monitor = new(0, 0, 1920, 1080);
    static readonly Rect Client = new(0, 0, 1920, 1032); // work area (taskbar 48px)
    static readonly Rect Window = new(-8, -8, 1928, 1040);

    [Fact]
    public void Maximize_covers_monitor()
    {
        var target = Chrome.CoverRect(true, Monitor, Window, Client);
        Assert.Equal(new Rect(-8, -8, 1928, 1088), target);
    }

    [Fact]
    public void Maximize_covers_monitor_on_second_screen()
    {
        var mon = new Rect(1920, -200, 4480, 1240);
        var target = Chrome.CoverRect(true, mon, new Rect(1912, -208, 4488, 1200), new Rect(1920, -200, 4480, 1192));
        Assert.Equal(new Rect(1912, -208, 4488, 1248), target);
    }

    [Fact]
    public void Already_covering_does_nothing() => Assert.Null(Chrome.CoverRect(true, Monitor, new(-8, -8, 1928, 1088), Monitor));

    [Fact]
    public void Refused_cover_is_not_retried()
    {
        var target = Chrome.CoverRect(true, Monitor, Window, Client)!.Value;
        Assert.False(Chrome.AlreadyTried(null, Window, target));
        Assert.True(Chrome.AlreadyTried((Window, target), Window, target)); // app snapped back to the same rect
        Assert.False(Chrome.AlreadyTried((Window, target), new Rect(0, 0, 10, 10), target)); // moved elsewhere: retry
    }

    [Fact]
    public void Normal_window_is_not_resized() => Assert.Null(Chrome.CoverRect(false, Monitor, Window, Client));

    [Fact]
    public void Reappearing_caption_is_stripped()
    {
        Assert.True(Chrome.NeedsRestrip(Overlapped, userShown: false));
        Assert.False(Chrome.NeedsRestrip(Overlapped, userShown: true));
        Assert.False(Chrome.NeedsRestrip(Chrome.StrippedStyle(Overlapped), userShown: false));
    }
}

public class WindowHotkeysTests
{
    readonly KeyEngine e = new();

    Decision AltPress(int key)
    {
        e.Feed(Vk.LAlt, true, false);
        return e.Feed(key, true, false);
    }

    [Fact]
    public void AltQ_closes_active_window()
    {
        Assert.Equal(new Decision(true, Act.Close), AltPress('Q'));
        Assert.Equal(Chrome.SC_CLOSE, Chrome.WindowCommand(Act.Close, false));
    }

    [Fact]
    public void AltM_maximizes()
    {
        Assert.Equal(new Decision(true, Act.ToggleMaximize), AltPress('M'));
        Assert.Equal(Chrome.SC_MAXIMIZE, Chrome.WindowCommand(Act.ToggleMaximize, zoomed: false));
    }

    [Fact]
    public void AltM_restores() => Assert.Equal(Chrome.SC_RESTORE, Chrome.WindowCommand(Act.ToggleMaximize, zoomed: true));

    [Fact]
    public void AltN_minimizes()
    {
        Assert.Equal(new Decision(true, Act.Minimize), AltPress('N'));
        Assert.Equal(Chrome.SC_MINIMIZE, Chrome.WindowCommand(Act.Minimize, false));
    }

    [Fact]
    public void AltT_toggles_title_bar() => Assert.Equal(new Decision(true, Act.ToggleChrome), AltPress('T'));
}
