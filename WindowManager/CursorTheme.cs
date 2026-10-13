using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WindowManager;

// Replaces Windows' standard arrow and hand with the portfolio's hand-drawn cursors while the program runs.
// SetSystemCursor only swaps the *standard* images: apps that set their own cursor (CSS cursors in a browser, games,
// drawing tools) keep theirs. Nothing is written to the user's settings; Restore reloads their cursor scheme.
public static class CursorTheme
{
    const uint OCR_NORMAL = 32512, OCR_HAND = 32649, SPI_SETCURSORS = 0x57;

    public static void Apply()
    {
        var dpi = GetDpiForSystem();
        Set(CursorArt.Arrow, OCR_NORMAL, dpi);
        Set(CursorArt.Hand, OCR_HAND, dpi);
        Log.Info($"cursors: hand-drawn arrow and hand at {CursorArt.PixelSize(dpi)}px");
    }

    public static void Restore()
    {
        if (!SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0)) Log.Win32("SystemParametersInfo(SPI_SETCURSORS)");
        else Log.Info("cursors: user's scheme restored");
    }

    static void Set(CursorArt.Shape shape, uint id, uint dpi)
    {
        var cursor = Create(shape, dpi);
        if (cursor == IntPtr.Zero) return;
        // SetSystemCursor takes ownership of the handle (and destroys it), so it is not freed here.
        if (!SetSystemCursor(cursor, id)) { Log.Win32($"SetSystemCursor({id})"); DestroyIcon(cursor); }
    }

    public static Bitmap Render(CursorArt.Shape shape, uint dpi)
    {
        var px = CursorArt.PixelSize(dpi);
        var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(px / (float)CursorArt.Size, px / (float)CursorArt.Size);

        using var outline = ToPath(shape.Outline);
        using (var fill = new SolidBrush(ToColor(CursorArt.Fill))) g.FillPath(fill, outline);
        using (var pen = new Pen(ToColor(CursorArt.Line), 2) { LineJoin = LineJoin.Round }) g.DrawPath(pen, outline);
        using var details = ToPath(shape.Details);
        using (var pen = new Pen(ToColor(CursorArt.Line, CursorArt.DetailOpacity), 1) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawPath(pen, details);
        return bmp;
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
        var path = new GraphicsPath();
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

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
}
