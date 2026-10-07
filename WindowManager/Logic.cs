using System.Runtime.InteropServices;

namespace WindowManager;

public static class Vk
{
    public const int Shift = 0x10, Ctrl = 0x11, Alt = 0x12;
    public const int Enter = 0x0D, Esc = 0x1B, PgUp = 0x21, PgDn = 0x22, End = 0x23, Home = 0x24, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28;
    public const int Ins = 0x2D, Del = 0x2E, LWin = 0x5B, RWin = 0x5C, Apps = 0x5D;
    public const int LShift = 0xA0, RShift = 0xA1, LCtrl = 0xA2, RCtrl = 0xA3, LAlt = 0xA4, RAlt = 0xA5;
    public const int OemOpen = 0xDB, OemClose = 0xDD; // [ and ]
    public const int Mask = 0xE8; // unassigned; the "menu mask" key

    public static bool IsModifier(int vk) => vk is LShift or RShift or LCtrl or RCtrl or LAlt or RAlt or LWin or RWin or Shift or Ctrl or Alt;
    public static bool IsAltOrWin(int vk) => vk is LAlt or RAlt or LWin or RWin or Alt;
    public static bool IsExtended(int vk) => vk is PgUp or PgDn or End or Home or Left or Up or Right or Down or Ins or Del or LWin or RWin or Apps or RCtrl or RAlt;
}

[Flags]
public enum Mod { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

public enum Act
{
    None, AppsKey, Click, Esc, End, Home, ShiftEnd, ShiftHome, NewLineBelow, DesktopLeft, DesktopRight,
    WheelDown, WheelUp, WheelLeft, WheelRight, Quit,
    Close, ToggleMaximize, Minimize, ToggleChrome,
    ToggleReveal, SnapLayouts,
}

public readonly record struct KeyStroke(int Vk, bool Up);

public readonly record struct Decision(bool Suppress, Act Act = Act.None, bool MaskFirst = false)
{
    public static readonly Decision Pass = new(false);
}

public readonly record struct Binding(Mod Mods, int Vk, Act Act, bool LeftAltOnly = false);

// Pure keyboard state machine: fed every low-level key event, decides whether to swallow it and what to fire.
// Tracks physically held modifiers itself (injected events are ignored), so it is fully deterministic under test.
public sealed class KeyEngine
{
    public static readonly Binding[] Bindings =
    [
        new(Mod.Alt, 'Z', Act.AppsKey),
        new(Mod.Alt, 'C', Act.Click),
        new(Mod.Alt, 'X', Act.Esc, LeftAltOnly: true),
        new(Mod.Alt, Vk.OemClose, Act.End),
        new(Mod.Alt, Vk.OemOpen, Act.Home),
        new(Mod.Alt | Mod.Shift, Vk.OemClose, Act.ShiftEnd),
        new(Mod.Alt | Mod.Shift, Vk.OemOpen, Act.ShiftHome),
        new(Mod.Alt, 'O', Act.NewLineBelow),
        new(Mod.Alt | Mod.Shift, 'O', Act.NewLineBelow),
        new(Mod.None, Vk.PgUp, Act.DesktopLeft),
        new(Mod.None, Vk.PgDn, Act.DesktopRight),
        new(Mod.Alt, 'J', Act.WheelDown),
        new(Mod.Alt, 'K', Act.WheelUp),
        new(Mod.Alt, 'H', Act.WheelLeft),
        new(Mod.Alt, 'L', Act.WheelRight),
        new(Mod.Ctrl | Mod.Alt | Mod.Shift, 'Q', Act.Quit),
        new(Mod.Alt, 'Q', Act.Close),
        new(Mod.Alt, 'M', Act.ToggleMaximize),
        new(Mod.Alt, 'N', Act.Minimize),
        new(Mod.Alt, 'T', Act.ToggleChrome),    ];

    // Modifier-only taps: hold exactly these modifiers, release one, no other key in between.
    public static Act TapAct(Mod mods) => mods switch
    {
        Mod.Win | Mod.Alt => Act.ToggleReveal,
        Mod.Ctrl | Mod.Win | Mod.Alt => Act.SnapLayouts,
        _ => Act.None,
    };

    Act tapArmed; // the tap held right now (None = not a tap); releasing one of its keys fires it
    readonly HashSet<int> held = [];
    readonly HashSet<int> dirty = [];      // Alt/Win keys held while a shortcut fired: their release must be masked
    readonly HashSet<int> suppressed = []; // non-modifier keys whose key-down we swallowed: swallow the key-up too
    readonly object gate = new();

    public int[] Held { get { lock (gate) return [.. held]; } }

    // Something other than a binding (e.g. TrackPoint scrolling) emitted output under a held Alt/Win:
    // mask their release the same way.
    public void MarkChordUsed()
    {
        lock (gate) foreach (var h in held) if (Vk.IsAltOrWin(h)) dirty.Add(h);
    }

    public Mod Mods { get { lock (gate) return ModsOf(held); } }

    static Mod ModsOf(IEnumerable<int> vks)
    {
        var m = Mod.None;
        foreach (var vk in vks)
            m |= vk switch
            {
                Vk.LAlt or Vk.RAlt or Vk.Alt => Mod.Alt,
                Vk.LCtrl or Vk.RCtrl or Vk.Ctrl => Mod.Ctrl,
                Vk.LShift or Vk.RShift or Vk.Shift => Mod.Shift,
                _ => Mod.Win,
            };
        return m;
    }

    public Decision Feed(int vk, bool down, bool injected)
    {
        if (injected) return Decision.Pass;
        lock (gate)
        {
            if (Vk.IsModifier(vk))
            {
                if (down)
                {
                    held.Add(vk);
                    // Pressing more modifiers moves to a bigger tap (Win+Alt -> Ctrl+Win+Alt); anything else disarms.
                    tapArmed = TapAct(ModsOf(held));
                    return Decision.Pass;
                }
                if (tapArmed != Act.None)
                {
                    // Tap: mask this release (no Start/menu bar) and the other held Alt/Win keys' later releases.
                    var act = tapArmed;
                    tapArmed = Act.None;
                    held.Remove(vk);
                    dirty.Remove(vk);
                    foreach (var h in held) if (Vk.IsAltOrWin(h)) dirty.Add(h);
                    return new Decision(false, act, MaskFirst: true);
                }
                held.Remove(vk);
                return dirty.Remove(vk) ? new Decision(false, MaskFirst: true) : Decision.Pass;
            }
            if (!down) return suppressed.Remove(vk) ? new Decision(true) : Decision.Pass;
            tapArmed = Act.None; // any real key in between means it wasn't a tap
        }

        var mods = Mods;
        foreach (var b in Bindings)
        {
            if (b.Vk != vk || b.Mods != mods) continue;
            lock (gate)
            {
                if (b.LeftAltOnly && !held.Contains(Vk.LAlt)) continue;
                suppressed.Add(vk);
                foreach (var h in held) if (Vk.IsAltOrWin(h)) dirty.Add(h);
            }
            return new Decision(true, b.Act);
        }
        return Decision.Pass;
    }

    // A masked modifier release: the real key-up is swallowed and re-injected after the mask key, so the mask is
    // guaranteed to land first even though injection happens later on the sender thread.
    public static KeyStroke[] MaskedRelease(int vk) => [.. Tap(Vk.Mask), new KeyStroke(vk, true)];

    // Keystrokes emitted for keyboard-output actions; empty for actions handled elsewhere (click, wheel, quit).
    public static KeyStroke[] Output(Act act) => act switch
    {
        Act.AppsKey => Tap(Vk.Apps),
        Act.SnapLayouts => Tap(Vk.LWin, 'Z'), // Windows 11 Snap Layouts for the active window
        Act.Esc => Tap(Vk.Esc),
        Act.End => Tap(Vk.End),
        Act.Home => Tap(Vk.Home),
        Act.ShiftEnd => Tap(Vk.LShift, Vk.End),
        Act.ShiftHome => Tap(Vk.LShift, Vk.Home),
        Act.NewLineBelow => [.. Tap(Vk.End), .. Tap(Vk.LShift, Vk.Enter)],
        Act.DesktopLeft => Tap(Vk.LCtrl, Vk.LWin, Vk.Left),
        Act.DesktopRight => Tap(Vk.LCtrl, Vk.LWin, Vk.Right),
        _ => [],
    };

    // Press keys in order, release in reverse: Tap(Ctrl, Left) = Ctrl down, Left down, Left up, Ctrl up.
    public static KeyStroke[] Tap(params int[] vks) =>
        [.. vks.Select(v => new KeyStroke(v, false)), .. vks.Reverse().Select(v => new KeyStroke(v, true))];

    // Before emitting output while modifiers are physically held: mask (if Alt/Win held, so the injected
    // modifier-up doesn't open a menu) then release them.
    public static KeyStroke[] Lift(int[] held)
    {
        var mods = held.Where(Vk.IsModifier).ToArray();
        if (mods.Length == 0) return [];
        var pre = mods.Any(Vk.IsAltOrWin) ? Tap(Vk.Mask) : [];
        return [.. pre, .. mods.Select(v => new KeyStroke(v, true))];
    }

    // After the output: re-press only modifiers that were lifted AND are still physically held,
    // so a modifier released mid-output never ends up stuck down.
    public static KeyStroke[] Restore(int[] lifted, int[] nowHeld) =>
        [.. lifted.Where(Vk.IsModifier).Intersect(nowHeld).Select(v => new KeyStroke(v, false))];
}

[StructLayout(LayoutKind.Sequential)]
public record struct Rect(int Left, int Top, int Right, int Bottom)
{
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
    public readonly (int X, int Y) Center => (Left + Width / 2, Top + Height / 2);
    public override readonly string ToString() => $"({Left},{Top} {Width}x{Height})";
}

public static class Focus
{
    public const uint WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_CHILD = 0x40000000, WS_POPUP = 0x80000000;

    // Same filter as binds.ahk IsRealWindow: a visible, framed, top-level application window.
    public static bool IsRealWindow(uint style) =>
        (style & WS_VISIBLE) != 0 && (style & WS_OVERLAPPEDWINDOW) != 0 && (style & WS_CHILD) == 0 && (style & WS_POPUP) == 0;

    public static bool ShouldHoverFocus(IntPtr hwnd, IntPtr lastHover, bool altHeld, bool isReal) =>
        !altHeld && hwnd != IntPtr.Zero && hwnd != lastHover && isReal;

    public static bool ShouldCenter(IntPtr prev, IntPtr now, bool mouseInside) =>
        now != IntPtr.Zero && now != prev && !mouseInside;
}

public readonly record struct RawMouse(IntPtr Device, ushort Flags, int Dx, int Dy)
{
    public const int HeaderSize = 24; // sizeof(RAWINPUTHEADER) on x64
    public const ushort MouseMoveAbsolute = 1;

    // RAWINPUT buffer from GetRawInputData(RID_INPUT): header, then RAWMOUSE (usFlags @0, lLastX @12, lLastY @16).
    public static RawMouse? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderSize + 20 || BitConverter.ToUInt32(b) != 0) return null; // 0 = RIM_TYPEMOUSE
        return new RawMouse(
            (IntPtr)BitConverter.ToInt64(b[8..]),
            BitConverter.ToUInt16(b[HeaderSize..]),
            BitConverter.ToInt32(b[(HeaderSize + 12)..]),
            BitConverter.ToInt32(b[(HeaderSize + 16)..]));
    }
}

// Alt+TrackPoint drag-scroll gesture: raw motion -> curved -> smooth wheel deltas.
// Emits fractional wheel deltas (like a touchpad / precision wheel) instead of whole 120-unit notches,
// so scrolling is continuous rather than steppy; remainders carry over between samples.
public sealed class TrackPointScroller
{
    public const int WheelDelta = 120;       // one wheel notch
    public const double UnitsPerNotch = 8;   // curved scroll-units per notch (lower = faster/twitchier)
    public const double CurveScale = 4;      // raw deflection that maps 1:1 to scroll-units
    public const double CurveExponent = 1.6; // >1 = slow start, ramps up with deflection
    // ponytail: caps the burst one sample can queue; a held hard push re-reports a big delta ~100+/s and apps
    // would keep smooth-scrolling through the backlog after Alt is released. Raise only if knob taps feel throttled.
    public const int MaxNotchesPerEvent = 3;

    public double AccumX { get; private set; } // unsent fraction of a wheel unit
    public double AccumY { get; private set; }
    public bool InGesture { get; private set; }

    public static double CurveDelta(int d)
    {
        if (d == 0) return 0;
        var mag = Math.Pow(Math.Abs(d) / CurveScale, CurveExponent) * CurveScale;
        mag = Math.Min(mag, UnitsPerNotch * MaxNotchesPerEvent);
        return d > 0 ? mag : -mag;
    }

    // Curved motion in wheel units added to the carried remainder; a direction reversal drops the stale remainder.
    public static double AccumulateAxis(double accum, int rawDelta)
    {
        var wheel = CurveDelta(rawDelta) * WheelDelta / UnitsPerNotch;
        if (wheel == 0) return accum;
        if ((accum > 0 && wheel < 0) || (accum < 0 && wheel > 0)) return wheel;
        return accum + wheel;
    }

    // Returns signed wheel deltas to send (+Y = down, +X = right; 120 = one notch) and whether this started a gesture.
    public (int X, int Y, bool Started) Motion(int dx, int dy)
    {
        var started = !InGesture;
        InGesture = true;
        AccumX = AccumulateAxis(AccumX, dx);
        AccumY = AccumulateAxis(AccumY, dy);
        var x = (int)Math.Truncate(AccumX);
        var y = (int)Math.Truncate(AccumY);
        AccumX -= x;
        AccumY -= y;
        return (x, y, started);
    }

    public void Stop() { AccumX = 0; AccumY = 0; InGesture = false; }
}

// Decisions for stripping window chrome. Layout itself stays with Windows; we only remove the frame.
public static class Chrome
{
    public const uint WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_EX_TOOLWINDOW = 0x80;
    public const uint SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_CLOSE = 0xF060, SC_RESTORE = 0xF120;

    public const string UwpFrameClass = "ApplicationFrameWindow";

    // UWP app frames carry WS_POPUP but are ordinary app windows, so the popup bit is ignored for them.
    public static bool IsManageable(uint style, uint exStyle, string? className = null) =>
        Focus.IsRealWindow(className == UwpFrameClass ? style & ~Focus.WS_POPUP : style) && (exStyle & WS_EX_TOOLWINDOW) == 0;

    public static bool HasCaption(uint style) => (style & WS_CAPTION) != 0;

    // Only the caption goes: WS_THICKFRAME and the min/max boxes stay so Snap / Win+Arrow / maximize keep working.
    public static uint StrippedStyle(uint style) => style & ~WS_CAPTION;

    public static bool NeedsRestrip(uint style, bool userShown) => !userShown && HasCaption(style);

    // Maximized frame-only windows get their client area on the work area. To cover the taskbar, grow the
    // window rect so the client area lands exactly on the monitor. Null = nothing to do.
    public static Rect? CoverRect(bool zoomed, Rect monitor, Rect window, Rect client)
    {
        if (!zoomed || client == monitor) return null;
        return new Rect(
            monitor.Left - (client.Left - window.Left),
            monitor.Top - (client.Top - window.Top),
            monitor.Right + (window.Right - client.Right),
            monitor.Bottom + (window.Bottom - client.Bottom));
    }

    // Some apps (Electron) refuse the resize and snap back, which fires another location change. Retrying the
    // same move from the same rect would fight forever; only retry once the app or Windows moved it somewhere new.
    public static bool AlreadyTried((Rect Window, Rect Target)? last, Rect window, Rect target) => last == (window, target);

    // WM_SYSCOMMAND to post for a caption-button hotkey; 0 = not a window command.
    public static uint WindowCommand(Act act, bool zoomed) => act switch
    {
        Act.Close => SC_CLOSE,
        Act.ToggleMaximize => zoomed ? SC_RESTORE : SC_MAXIMIZE,
        Act.Minimize => SC_MINIMIZE,
        _ => 0,
    };
}

// Cropping the title bar / tab strip that apps draw inside their own client area.
public static class Crop
{
    public const int DefaultDip = 32, SelfDrawnMaxGapDip = 16;

    // ponytail: hand-measured/estimated strip heights in DIP; Win+Alt+PgUp/PgDn tunes and persists per app.
    public static readonly IReadOnlyDictionary<string, int> Defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = 40, ["chrome"] = 40,
        ["vivaldi"] = 30, // measured: title row only (tabs on the side, no address bar) ["explorer"] = 41, ["Slack"] = 37, ["olk"] = 50,
        ["Code"] = 35, ["Obsidian"] = 30, ["Discord"] = 22, ["WindowsTerminal"] = 40, ["Notepad"] = 48,
    };

    // No system caption above the content (only the frame overhang a maximized window has) = the app draws its own bar.
    // Must be judged before WS_CAPTION is stripped, or every classic window would look self-drawn.
    public static bool IsSelfDrawn(Rect window, Rect client, uint dpi) => client.Top - window.Top <= SelfDrawnMaxGapDip * (int)dpi / 96;

    // App-level fullscreen (F11, VS Code fullscreen): exactly the monitor rect, no frame overhang. A real maximized
    // window always overhangs by its frame, and a cropped one extends above. The app already hid its bar: don't crop.
    // Chromium fullscreen stops 1px short of the bottom so an auto-hide taskbar can still be summoned (measured: 1920x1199).
    public static bool IsAppFullscreen(Rect window, Rect monitor) =>
        window.Left == monitor.Left && window.Top == monitor.Top && window.Right == monitor.Right &&
        monitor.Bottom - window.Bottom is >= 0 and <= 2;

    public static int Pixels(int dip, uint dpi) => (int)Math.Round(dip * dpi / 96.0);

    // Maximized Chromium windows start their content above the monitor edge (measured: Vivaldi 8px). That part of
    // the bar is already off-screen; cropping the full height on top of it cut 8px into the page/side tabs.
    // Snapped/floating windows: Windows lays out the window's visible frame (DWM extended frame bounds), but Chromium
    // draws its content inside invisible 7px borders. Once a cropped window's DWM frame is off, the frame bounds equal
    // the window rect, so a snap left 7px gaps left/right/bottom (measured on Vivaldi). Grow the window by its own
    // insets so the content fills the intended rect. The top stays: the bar starts at the client top.
    public static (Rect Window, Rect Client) FillVisible(Rect window, Rect client, Rect visible) =>
        (new Rect(visible.Left - (client.Left - window.Left), window.Top,
                  visible.Right + (window.Right - client.Right), visible.Bottom + (window.Bottom - client.Bottom)),
         new Rect(visible.Left, client.Top, visible.Right, visible.Bottom));

    public static int VisibleBar(int n, Rect slotClient, int monitorTop) =>
        Math.Max(0, n - Math.Max(0, monitorTop - slotClient.Top));

    public static int DipFor(string process, IReadOnlyDictionary<string, int> overrides) =>
        overrides.TryGetValue(process, out var o) ? o : Defaults.TryGetValue(process, out var d) ? d : DefaultDip;

    // Stretch the window up by n so its bar sits above the slot, and clip to the content below the crop line.
    // Region is in target-window coordinates and also drops the invisible borders, so nothing leaks to another monitor.
    public static (Rect Target, Rect Region) Plan(Rect window, Rect client, int n)
    {
        var target = window with { Top = window.Top - n };
        var region = new Rect(client.Left - window.Left, client.Top - window.Top + n, client.Right - window.Left, client.Bottom - window.Top + n);
        return (target, region);
    }
}

// Refused: the window is back on the slot right after our crop move (an app veto, or Windows re-applying a Snap
// Layout position after its animation). Our clip no longer matches the window and must be removed.
public enum CropKind { None, RegionOnly, Apply, Refused }

public readonly record struct CropStep(CropKind Kind, Rect Target = default, Rect Region = default)
{
    public static readonly CropStep None = new(CropKind.None);
}

// Per-window crop state. Fed every location change; decides whether to crop, just refresh the clip, or leave it.
public sealed class CropTracker
{
    const int HistorySize = 8;
    readonly List<(Rect Target, Rect Slot, Rect SlotClient, Rect Region)> history = [];
    (Rect Window, Rect Target)? lastAttempt;

    public const int MaxRetries = 3;
    public int Refusals { get; private set; } // consecutive refused crops of the same slot
    Rect? refusedSlot;
    bool awaitingRetry; // refused: ignore location events for this slot until Retry()

    public Rect? Applied { get; private set; } // the rect we last put the window at (or recognised as ours)
    public Rect? Slot => Current()?.Slot;      // where Windows had put it before we cropped (null = not cropped)

    // n = the bar height measured from the content top. monitorTop: the part of the bar already above the monitor
    // edge is hidden anyway and must not be cut again.
    // visible: for non-maximized windows, the rect Windows meant the window to occupy (DWM frame bounds); null to
    // keep the window's own rect.
    public CropStep OnLocation(Rect window, Rect client, int n, int? monitorTop = null, Rect? visible = null)
    {
        if (Applied == window) { Refusals = 0; return CropStep.None; } // our own move echoing back: it stuck

        // Windows put it back on a rect we produced earlier (e.g. restore-from-maximize remembers our cropped
        // normal rect): it's already cropped, only the clip needs refreshing for that size.
        var i = history.FindLastIndex(h => h.Target == window);
        if (i >= 0)
        {
            Applied = window;
            return new CropStep(CropKind.RegionOnly, window, history[i].Region);
        }

        // Still at our cropped top: the window was resized sideways (e.g. dragging its edge or a snap divider) while
        // our crop stayed in effect, and Windows' size clamp may have cut the stretched height. It is not a new slot:
        // keep the slot's vertical extent and take only the new horizontal one. Treating it as a slot crops twice.
        if (Applied is { } a && window.Top == a.Top && Current() is { } c)
        {
            if (visible is { } v) (window, client) = Crop.FillVisible(window, client, v with { Bottom = client.Bottom });
            window = window with { Top = c.Slot.Top, Bottom = c.Slot.Bottom };
            client = client with { Top = c.SlotClient.Top, Bottom = c.SlotClient.Bottom };
        }
        else if (history.FindLastIndex(h => h.Slot == window) is var j and >= 0)
        {
            // Back on a slot we computed ourselves (uncrop for reveal / Alt+T moved it there): it already fills its
            // area. Widening it again from the frame bounds overshot by the border width (measured: 7px spill).
            client = history[j].SlotClient;
        }
        else if (visible is { } v)
        {
            (window, client) = Crop.FillVisible(window, client, v);
        }

        var (target, region) = Crop.Plan(window, client, monitorTop is { } mt ? Crop.VisibleBar(n, client, mt) : n);
        if (lastAttempt == (window, target))
        {
            // Removing the clip makes Windows report the location again: stay quiet until Retry(), or the caller's
            // reaction to Refused feeds itself (measured: a storm of hundreds of events per ms on Slack).
            if (awaitingRetry) return CropStep.None;
            awaitingRetry = true;
            // Back on the slot after this exact crop: refused. Not retried from location events (that would fight an
            // app forever); the caller drops the clip and may Retry() later, up to MaxRetries.
            if (refusedSlot != window) Refusals = 0; // counted per slot
            refusedSlot = window;
            Refusals++;
            Applied = null;
            return new CropStep(CropKind.Refused);
        }
        lastAttempt = (window, target);
        awaitingRetry = false; // a new attempt (new slot or a retry) may be refused again
        return Remember(target, window, client, region);
    }

    // Allow the refused crop to be attempted again (after Windows' snap animation has settled).
    public void Retry() { lastAttempt = null; awaitingRetry = false; }

    // Stop cropping; returns the slot to move the window back to (null if it wasn't cropped).
    public Rect? Uncrop()
    {
        var slot = Current()?.Slot;
        Applied = null;
        lastAttempt = null;
        Refusals = 0;
        awaitingRetry = false;
        return slot;
    }

    (Rect Target, Rect Slot, Rect SlotClient, Rect Region)? Current()
    {
        var i = Applied is { } a ? history.FindLastIndex(h => h.Target == a) : -1;
        return i >= 0 ? history[i] : null;
    }

    CropStep Remember(Rect target, Rect slot, Rect slotClient, Rect region)
    {
        Applied = target;
        history.Add((target, slot, slotClient, region));
        if (history.Count > HistorySize) history.RemoveAt(0);
        return new CropStep(CropKind.Apply, target, region);
    }
}
