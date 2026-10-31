using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Automation;
using Microsoft.Win32;
using static WindowManager.Native;

namespace WindowManager;

// Win32 side of hint mode (decisions in HintEngine, Logic.cs). Ported from repo-task-manager's app.js `_hints`:
// UI Automation finds every clickable thing on screen, one click-through layered overlay draws a letter badge on
// each, and a real mouse click picks one. The badge is the web app's .hint-badge, a bit smaller.
static class Hints
{
    // Bounds: the element. Badge: where its badge is centred (bottom-right corner, inset, like the web version).
    // Click: where to click it (UIA's clickable point when it has one).
    public sealed record Target(Rect Bounds, int BadgeX, int BadgeY, int ClickX, int ClickY);

    // The web version's selector ("button, a[href], input, select, …") in UIA terms: things a user clicks or types into.
    static readonly ControlType[] Clickable =
    [
        ControlType.Button, ControlType.Hyperlink, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox,
        ControlType.Edit, ControlType.ListItem, ControlType.TreeItem, ControlType.TabItem, ControlType.MenuItem,
        ControlType.SplitButton,
    ];

    static readonly Condition Cond = new AndCondition(
        new PropertyCondition(AutomationElement.IsEnabledProperty, true),
        new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
        new OrCondition(Clickable.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray()));

    // Every property we read comes back WITH the FindAll results in one cross-process round trip. Without this, each
    // el.Current.X (and TryGetClickablePoint) was its own round trip per element: a browser tab + a ribbon took ~2s.
    static readonly CacheRequest Cache = MakeCache();
    static CacheRequest MakeCache()
    {
        var c = new CacheRequest { AutomationElementMode = AutomationElementMode.None, TreeScope = TreeScope.Element };
        c.Add(AutomationElement.BoundingRectangleProperty);
        return c;
    }

    // Every visible clickable element of the given top-level windows, in the order given (caller puts the foreground
    // window first so its letters stay stable), each window top-left to bottom-right. "Visible" = the window under
    // the badge spot is this element's own window, the same idea as the web version's elementFromPoint check.
    // Runs on the lie-worker, never the loop. Capped: a browser tab can expose thousands of elements.
    public static List<Target> Scan(IEnumerable<IntPtr> windowsTopDown, int cap = 400, bool log = true)
    {
        var sw = Stopwatch.StartNew();
        var targets = new List<Target>();
        var scanned = 0;
        var perWindow = new List<string>(); // "0xHWND:targets/ms", so a slow scan names the window that cost it
        foreach (var hwnd in windowsTopDown)
        {
            if (!AnyVisible(hwnd)) continue; // fully covered (everything behind a fullscreen slot): UIA would find badges nobody can see
            var took = Stopwatch.StartNew();
            AutomationElementCollection found;
            try
            {
                // The root is fetched OUTSIDE the cache scope: with AutomationElementMode.None an element made inside
                // it has no native reference, so FindAll on it throws (measured: "0 targets in 0 windows").
                var root = AutomationElement.FromHandle(hwnd);
                using (Cache.Activate()) found = root.FindAll(TreeScope.Descendants, Cond);
            }
            catch (Exception e) { Log.Info($"hints: scan 0x{hwnd:X} failed: {e.Message}"); continue; }
            scanned++;
            var mine = new List<Target>();
            foreach (AutomationElement el in found)
            {
                if (Spot(el, hwnd) is { } t) mine.Add(t);
                if (targets.Count + mine.Count >= cap) break;
            }
            targets.AddRange(mine.OrderBy(t => t.Bounds.Top).ThenBy(t => t.Bounds.Left));
            perWindow.Add($"0x{hwnd:X}:{mine.Count}/{took.ElapsedMilliseconds}ms");
            if (targets.Count >= cap) break;
        }
        var line = $"hints: {targets.Count} targets in {scanned} windows, scanned in {sw.ElapsedMilliseconds}ms [{string.Join(" ", perWindow)}]";
        if (log) Log.Info(line); else Log.Debug("prefetch " + line);
        return targets;
    }

    // Does any of the window show? A 3x3 sample of its rect against WindowFromPoint: a window behind a fullscreen
    // slot fails every point and is skipped (measured: each covered window scanned anyway cost ~200ms for 0 badges).
    static bool AnyVisible(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0) return false;
        for (var i = 1; i <= 5; i += 2) for (var j = 1; j <= 5; j += 2)
            if (GetAncestor(WindowFromPoint(new POINT { X = r.Left + r.Width * i / 6, Y = r.Top + r.Height * j / 6 }), GA_ROOT) == hwnd) return true;
        return false;
    }

    static Target? Spot(AutomationElement el, IntPtr root)
    {
        System.Windows.Rect b;
        try { b = el.Cached.BoundingRectangle; } catch { return null; } // from the cache: no round trip
        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0 || double.IsInfinity(b.Width)) return null;
        var r = new Rect((int)b.Left, (int)b.Top, (int)b.Right, (int)b.Bottom);
        var (bx, by) = r.Width > 16 && r.Height > 16 ? (r.Right - 6, r.Bottom - 6) : r.Center; // web: right-6, bottom-6
        if (GetAncestor(WindowFromPoint(new POINT { X = bx, Y = by }), GA_ROOT) != root) return null;  // covered / off screen
        var (cx, cy) = r.Center; // click the middle (TryGetClickablePoint was one more round trip per element)
        return new Target(r, bx, by, cx, cy);
    }

    // ---- the overlay ----

    // .hint-badge: padding 1px 5px; border-radius 4px; font 700 11px/1.3 var(--f-body); background var(--sun);
    // color var(--on-fill); border 1.5px solid var(--line); box-shadow 0 1px 3px rgba(0,0,0,.18); .dim { opacity .25 }.
    // "A bit smaller": 10px font, 1px 4px padding, 3.5px radius. CSS px scale with the DPI like the browser's do.
    const float FontPx = 10, PadX = 4, PadY = 1, Radius = 3.5f, Border = 1.5f, LineHeight = 1.3f, DimAlpha = .25f;
    static readonly string[] FontStack = ["Plus Jakarta Sans", "Segoe UI"]; // var(--f-body), then the system font
    static Color C(int rgb) => Color.FromArgb(unchecked((int)0xFF000000) | rgb);
    static readonly Color SunLight = C(0xf3da6c), OnFillLight = C(0x24211f), LineLight = C(0x2b2725);  // :root
    static readonly Color SunDark = C(0xd4bd5f), OnFillDark = C(0x1c1917), LineDark = C(0x5e5751);     // dark theme

    static IntPtr overlay;
    static WndProc? overlayProc; // kept in a field so the GC can't collect the delegate native code holds
    static Rect area;
    static IReadOnlyList<Target> shown = [];
    static IReadOnlyList<string> labels = [];
    static uint dpi = 96;

    public static void Show(IReadOnlyList<Target> targets, IReadOnlyList<string> lbls, uint dpiNow)
    {
        shown = targets; labels = lbls; dpi = dpiNow;
        // Only the part of the screen the badges are on. A bitmap of the whole virtual screen is ~46MB on three monitors, and it was
        // cleared, drawn and copied to the window again for every letter typed.
        var vs = VirtualScreen();
        var pad = (int)(120 * dpiNow / 96.0);
        area = new Rect(Math.Max(vs.Left, targets.Min(t => t.BadgeX) - pad), Math.Max(vs.Top, targets.Min(t => t.BadgeY) - pad),
                        Math.Min(vs.Right, targets.Max(t => t.BadgeX) + pad), Math.Min(vs.Bottom, targets.Max(t => t.BadgeY) + pad));
        if (area.Width <= 0 || area.Height <= 0) area = vs;
        if (overlay == IntPtr.Zero && !Create()) return;
        Render(_ => false);
        ShowWindow(overlay, SW_SHOWNOACTIVATE);
        SetWindowPos(overlay, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // Pay the one-time costs (overlay window, fonts, GDI+ text measurement) at start so the first Alt+F is as quick as the rest. Loop thread.
    public static void Warm()
    {
        try
        {
            if (overlay == IntPtr.Zero) Create();
            using var f = MakeFont(FontPx);
            using var bmp = new Bitmap(16, 16, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = Rounded(1, 1, 12, 9, 2); using var br = new SolidBrush(Color.Black);
            g.FillPath(br, path); g.MeasureString("ab", f, PointF.Empty, StringFormat.GenericTypographic); g.DrawString("ab", f, br, 0, 0);
            SystemIsDark();
        }
        catch (Exception e) { Log.Info($"hints warm-up failed: {e.Message}"); }
    }

    public static void Redraw(Func<string, bool> dim) { if (overlay != IntPtr.Zero && shown.Count > 0) Render(dim); }

    public static void Hide()
    {
        if (overlay != IntPtr.Zero) ShowWindow(overlay, SW_HIDE);
        shown = []; labels = [];
    }

    static Rect VirtualScreen()
    {
        var ms = Frames.AllMonitors();
        return new Rect(ms.Min(m => m.Bounds.Left), ms.Min(m => m.Bounds.Top), ms.Max(m => m.Bounds.Right), ms.Max(m => m.Bounds.Bottom));
    }

    static bool Create()
    {
        const string cls = "WindowManagerHints";
        overlayProc = (h, m, w, l) => DefWindowProc(h, m, w, l);
        var wc = new WNDCLASSEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = overlayProc, hInstance = GetModuleHandle(null), lpszClassName = cls };
        if (RegisterClassEx(ref wc) == 0) { Log.Win32("RegisterClassEx(hints)"); return false; }
        // Click-through, never activated, above everything: the badges must never take the click they label.
        overlay = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW_EX | WS_EX_NOACTIVATE,
            cls, null, WS_POPUP_STYLE, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (overlay == IntPtr.Zero) { Log.Win32("CreateWindowEx(hints)"); return false; }
        return true;
    }

    // Windows' app theme, so the badge matches what the web app would show on this machine.
    static bool SystemIsDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0; }
        catch { return false; }
    }

    static Font MakeFont(float px)
    {
        foreach (var name in FontStack)
            try { var f = new Font(name, px, FontStyle.Bold, GraphicsUnit.Pixel); if (f.Name == name) return f; f.Dispose(); } catch { }
        return new Font(FontFamily.GenericSansSerif, px, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    static void Render(Func<string, bool> dim)
    {
        var dark = SystemIsDark();
        var (sun, onFill, line) = dark ? (SunDark, OnFillDark, LineDark) : (SunLight, OnFillLight, LineLight);
        float s = dpi / 96f; // CSS px -> device px
        using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var font = MakeFont(FontPx * s))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var h = FontPx * LineHeight * s + 2 * PadY * s;
            for (var i = 0; i < shown.Count && i < labels.Count; i++)
            {
                var label = labels[i];
                var a = dim(label) ? DimAlpha : 1f;
                var w = g.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic).Width + 2 * PadX * s;
                var x = shown[i].BadgeX - area.Left - w / 2;  // transform: translate(-50%, -50%)
                var y = shown[i].BadgeY - area.Top - h / 2;
                // Keep the whole badge on screen: an element at the screen's edge otherwise shows half a badge.
                x = Math.Clamp(x, 0, Math.Max(0, area.Width - w)); y = Math.Clamp(y, 0, Math.Max(0, area.Height - h));
                using var path = Rounded(x, y, w, h, Radius * s);
                using var shadow = Rounded(x, y + 1 * s, w, h, Radius * s);
                using (var sb = new SolidBrush(Color.FromArgb((int)(0.18 * 255 * a), Color.Black))) g.FillPath(sb, shadow); // 0 1px 3px rgba(0,0,0,.18)
                using (var fb = new SolidBrush(Color.FromArgb((int)(255 * a), sun))) g.FillPath(fb, path);
                using (var pen = new Pen(Color.FromArgb((int)(255 * a), line), Border * s)) g.DrawPath(pen, path);
                using var tb = new SolidBrush(Color.FromArgb((int)(255 * a), onFill));
                var tr = new RectangleF(x + PadX * s, y, w - 2 * PadX * s, h);
                g.DrawString(label, font, tb, tr, new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
        }
        Push(bmp);
    }

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath(); var d = r * 2;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // Hand the premultiplied bitmap to the layered window: it IS the window's pixels, alpha included.
    static void Push(Bitmap bmp)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBmp);
        var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
        var src = new POINT();
        var dst = new POINT { X = area.Left, Y = area.Top };
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        if (!UpdateLayeredWindow(overlay, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA)) Log.Win32("UpdateLayeredWindow(hints)");
        SelectObject(memDc, old); DeleteObject(hBmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
    }
}
