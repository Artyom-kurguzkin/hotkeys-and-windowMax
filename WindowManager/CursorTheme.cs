using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WindowManager;

// Replaces Windows' standard pointers with the portfolio's hand-drawn cursors while the program runs.
// SetSystemCursor only swaps the *standard* images: apps that set their own cursor (CSS cursors in a browser, games,
// drawing tools) keep theirs. Nothing is written to the user's settings; Restore reloads their cursor scheme.
public static class CursorTheme
{
    const uint SPI_SETCURSORS = 0x57, IMAGE_CURSOR = 2, LR_LOADFROMFILE = 0x10;
    const int IDC_ARROW = 32512;

    public static void Apply()
    {
        var dpi = GetDpiForSystem();
        var done = 0;
        foreach (var shape in CursorArt.All)
        {
            var cursor = shape.Animated ? CreateAnimated(shape, dpi) : Create(shape, dpi);
            if (cursor == IntPtr.Zero) continue;
            // SetSystemCursor takes ownership of the handle (and destroys it), so it is not freed here.
            if (SetSystemCursor(cursor, shape.Id)) done++;
            else { Log.Win32($"SetSystemCursor({shape.Name})"); DestroyIcon(cursor); }
        }
        Log.Info($"cursors: {done}/{CursorArt.All.Length} hand-drawn pointers at {CursorArt.PixelSize(dpi)}px");
    }

    // Windows reloads the cursors from the user's scheme on its own (after waking from sleep, on display/DPI or
    // theme changes), silently undoing SetSystemCursor (measured: applied once, gone after a morning of wakes).
    // Cheap check: is the standard arrow still ours (size and hotspot)? If not, apply again.
    public static void EnsureApplied(string why)
    {
        var dpi = GetDpiForSystem();
        if (CurrentArrow() is { } c && CursorArt.IsOurs(c.HotX, c.HotY, c.Width, dpi)) return;
        Log.Info($"cursors: Windows reset them ({why}), applying again");
        Apply();
    }

    // Hotspot and width of the system's current standard arrow.
    static (int HotX, int HotY, int Width)? CurrentArrow()
    {
        if (!GetIconInfo(LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW), out var info)) return null;
        try
        {
            var bmp = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
            return GetObject(bmp, Marshal.SizeOf<BITMAP>(), out var b) == 0 ? null : (info.xHotspot, info.yHotspot, b.bmWidth);
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
        }
    }

    public static void Restore()
    {
        if (!SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0)) Log.Win32("SystemParametersInfo(SPI_SETCURSORS)");
        else Log.Info("cursors: user's scheme restored");
    }

    // spin: rotation in degrees of the spinning layers (busy cursors' animation frames).
    public static Bitmap Render(CursorArt.Shape shape, uint dpi, float spin = 0)
    {
        var px = CursorArt.PixelSize(dpi);
        var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(px / (float)CursorArt.Size, px / (float)CursorArt.Size);
        if (shape.Rotate != 0) RotateAbout(g, (float)shape.Rotate, 20, 20);

        using var fill = new SolidBrush(ToColor(CursorArt.Fill));
        using var outlinePen = new Pen(ToColor(CursorArt.Line), 2) { LineJoin = LineJoin.Round };
        using var detailPen = new Pen(ToColor(CursorArt.Line, CursorArt.DetailOpacity), 1) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var layer in shape.Layers)
        {
            var saved = g.Save();
            if (layer.Spins && spin != 0) RotateAbout(g, spin, (float)layer.CX, (float)layer.CY);
            using (var outline = ToPath(layer.Outline)) { g.FillPath(fill, outline); g.DrawPath(outlinePen, outline); }
            using (var details = ToPath(layer.Details)) g.DrawPath(detailPen, details);
            using (var markPen = new Pen(ToColor(CursorArt.Line), (float)layer.MarkWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var marks = ToPath(layer.Marks)) g.DrawPath(markPen, marks);
            g.Restore(saved);
        }
        return bmp;
    }

    static void RotateAbout(Graphics g, float degrees, float cx, float cy)
    {
        g.TranslateTransform(cx, cy);
        g.RotateTransform(degrees);
        g.TranslateTransform(-cx, -cy);
    }

    // Busy cursors spin: Windows only animates .ani cursors, so the frames go into an .ani file built in memory,
    // written to a temp file and loaded from there.
    static IntPtr CreateAnimated(CursorArt.Shape shape, uint dpi)
    {
        var frames = Enumerable.Range(0, CursorArt.SpinFrames)
            .Select(i => Render(shape, dpi, i * 360f / CursorArt.SpinFrames)).ToList();
        var (hx, hy) = CursorArt.Hotspot(shape, dpi);
        var path = Path.Combine(Path.GetTempPath(), $"wm-cursor-{shape.Name}-{Environment.ProcessId}.ani");
        try
        {
            File.WriteAllBytes(path, CursorFile.Ani(frames, hx, hy, CursorArt.SpinJiffies));
            var px = CursorArt.PixelSize(dpi);
            var cursor = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, px, px, LR_LOADFROMFILE);
            if (cursor == IntPtr.Zero) Log.Win32($"LoadImage({shape.Name}.ani)");
            return cursor;
        }
        finally
        {
            foreach (var f in frames) f.Dispose();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    static IntPtr Create(CursorArt.Shape shape, uint dpi)
    {
        using var bmp = Render(shape, dpi);
        var (hx, hy) = CursorArt.Hotspot(shape, dpi);
        var color = bmp.GetHbitmap(Color.FromArgb(0)); // 32bpp with alpha
        using var maskBmp = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format1bppIndexed);
        var mask = maskBmp.GetHbitmap(); // unused with an alpha colour bitmap, but required
        var info = new ICONINFO { fIcon = false, xHotspot = hx, yHotspot = hy, hbmMask = mask, hbmColor = color };
        var cursor = CreateIconIndirect(ref info);
        if (cursor == IntPtr.Zero) Log.Win32("CreateIconIndirect");
        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        return cursor;
    }

    static GraphicsPath ToPath(string d)
    {
        var path = new GraphicsPath(FillMode.Alternate); // a figure inside another is a hole (rings)
        if (d.Length == 0) return path;
        foreach (var f in CursorArt.Parse(d))
        {
            path.StartFigure();
            var at = f.Start;
            foreach (var (c1, c2, end) in f.Segments)
            {
                path.AddBezier(P(at), P(c1), P(c2), P(end));
                at = end;
            }
            if (f.Closed) path.CloseFigure();
        }
        return path;
    }

    static PointF P(CursorArt.Pt p) => new((float)p.X, (float)p.Y);
    static Color ToColor(uint rgb, double alpha = 1) =>
        Color.FromArgb((int)Math.Round(alpha * 255), (int)(rgb >> 16) & 0xFF, (int)(rgb >> 8) & 0xFF, (int)rgb & 0xFF);

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { [MarshalAs(UnmanagedType.Bool)] public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr obj, int size, out BITMAP bmp);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
}
