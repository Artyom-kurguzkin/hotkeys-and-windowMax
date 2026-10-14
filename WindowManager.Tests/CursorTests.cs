using WindowManager;

namespace WindowManager.Tests;

public class CursorTests
{
    [Fact]
    public void Arrow_outline_parses_into_one_closed_figure()
    {
        var f = Assert.Single(CursorArt.Parse(CursorArt.Arrow.Outline));
        Assert.True(f.Closed);
        Assert.Equal(new CursorArt.Pt(4.5, 3), f.Start);
        Assert.Equal(7, f.Segments.Count);                 // seven Q commands
        Assert.Equal(new CursorArt.Pt(4.5, 3), f.Segments[^1].End); // ends back at the tip
    }

    [Fact]
    public void Hand_outline_mixes_curves_and_lines()
    {
        var f = Assert.Single(CursorArt.Parse(CursorArt.Hand.Outline));
        Assert.True(f.Closed);
        Assert.Equal(new CursorArt.Pt(13.2, 25.2), f.Segments[^1].End); // last L before Z
    }

    [Fact]
    public void Detail_strokes_are_separate_open_figures()
    {
        Assert.Equal(3, CursorArt.Parse(CursorArt.Arrow.Details).Count);
        Assert.Equal(4, CursorArt.Parse(CursorArt.Hand.Details).Count);
        Assert.All(CursorArt.Parse(CursorArt.Hand.Details), f => Assert.False(f.Closed));
    }

    [Fact]
    public void Quadratic_to_cubic_is_exact()
    {
        // a quadratic and its cubic agree at t=0.5: Q(0.5) = (p0 + 2q + p)/4
        var (c1, c2, end) = CursorArt.QuadToCubic(new(0, 0), new(10, 20), new(20, 0));
        double Cubic(double a, double b, double c, double d, double t) =>
            (1 - t) * (1 - t) * (1 - t) * a + 3 * (1 - t) * (1 - t) * t * b + 3 * (1 - t) * t * t * c + t * t * t * d;
        Assert.Equal(10, Cubic(0, c1.X, c2.X, end.X, 0.5), 9);
        Assert.Equal(10, Cubic(0, c1.Y, c2.Y, end.Y, 0.5), 9); // (0 + 40 + 0)/4
    }

    [Fact]
    public void Unsupported_path_command_is_rejected() =>
        Assert.Throws<FormatException>(() => CursorArt.Parse("M0 0 C1 1 2 2 3 3"));

    [Theory]
    [InlineData(96u, 40, 16, 3)]
    [InlineData(144u, 60, 24, 5)] // 150%: what the browser draws at devicePixelRatio 1.5
    public void Size_and_hotspot_scale_with_DPI(uint dpi, int size, int handX, int handY)
    {
        Assert.Equal(size, CursorArt.PixelSize(dpi));
        Assert.Equal((handX, handY), CursorArt.Hotspot(CursorArt.Hand, dpi));
    }

    [Fact]
    public void Every_standard_pointer_is_covered_once()
    {
        uint[] standard = [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651];
        Assert.Equal(standard.Order(), CursorArt.All.Select(s => s.Id).Order());
    }

    [Fact]
    public void Every_pointer_parses_and_renders()
    {
        foreach (var s in CursorArt.All)
        {
            foreach (var l in s.Layers) { CursorArt.Parse(l.Outline); if (l.Details != "") CursorArt.Parse(l.Details); if (l.Marks != "") CursorArt.Parse(l.Marks); }
            using var b = CursorTheme.Render(s, 144);
            Assert.Equal(60, b.Width);
        }
    }

    [Fact]
    public void Rotated_pointers_click_at_the_centre() // rotation is about (20,20), so only a centred hotspot stays right
        => Assert.All(CursorArt.All.Where(s => s.Rotate != 0), s => Assert.Equal((20.0, 20.0), (s.HotX, s.HotY)));

    [Fact]
    public void Only_the_busy_pointers_animate() =>
        Assert.Equal(["busy", "working"], CursorArt.All.Where(s => s.Animated).Select(s => s.Name).Order());

    [Fact]
    public void Ring_has_a_hole() // two concentric figures, filled even-odd
    {
        using var b = CursorTheme.Render(CursorArt.Wait, 96);
        Assert.Equal(0, b.GetPixel(20, 20).A);                   // centre: transparent
        Assert.Equal(255, b.GetPixel(20, 31).A);                 // on the band: drawn
    }

    [Fact]
    public void Cur_file_has_cursor_header_and_hotspot()
    {
        using var b = CursorTheme.Render(CursorArt.Arrow, 96);
        var cur = CursorFile.Cur(b, 4, 3);
        Assert.Equal(2, BitConverter.ToUInt16(cur, 2));          // type: cursor
        Assert.Equal(40, cur[6]);                                 // width
        Assert.Equal((4, 3), (BitConverter.ToUInt16(cur, 10), BitConverter.ToUInt16(cur, 12)));
        Assert.Equal(80, BitConverter.ToInt32(cur, 22 + 8));     // DIB height = image + mask
        Assert.Equal(cur.Length - 22, BitConverter.ToInt32(cur, 14)); // declared image size matches
    }

    [Fact]
    public void Ani_file_is_a_riff_acon_with_every_frame()
    {
        var frames = Enumerable.Range(0, 3).Select(i => CursorTheme.Render(CursorArt.Wait, 96, i * 30)).ToList();
        var ani = CursorFile.Ani(frames, 20, 20, 4);
        string Tag(int at) => System.Text.Encoding.ASCII.GetString(ani, at, 4);
        Assert.Equal(("RIFF", "ACON", "anih"), (Tag(0), Tag(8), Tag(12)));
        Assert.Equal(ani.Length - 8, BitConverter.ToInt32(ani, 4)); // RIFF size
        Assert.Equal(3, BitConverter.ToInt32(ani, 20 + 4));          // anih: frames
        Assert.Equal(4, BitConverter.ToInt32(ani, 20 + 28));         // anih: rate in jiffies
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.ASCII.GetString(ani), "icon").Count);
        frames.ForEach(f => f.Dispose());
    }

    [Fact]
    public void Our_arrow_is_recognised() => Assert.True(CursorArt.IsOurs(6, 5, 60, 144));

    [Theory]
    [InlineData(0, 0, 48)]  // Windows' own arrow at 150%: hotspot at the tip
    [InlineData(0, 0, 60)]  // a scheme cursor that happens to be the same size
    [InlineData(6, 5, 40)]  // ours, but rendered for another DPI
    public void Reset_arrow_is_detected(int hotX, int hotY, int width) => Assert.False(CursorArt.IsOurs(hotX, hotY, width, 144));

    [Fact]
    public void Rendered_arrow_is_yellow_inside_and_transparent_outside()
    {
        using var bmp = CursorTheme.Render(CursorArt.Arrow, 96);
        Assert.Equal(40, bmp.Width);
        var inside = bmp.GetPixel(10, 20); // well inside the arrow body
        Assert.Equal((0xFF, 0xC9, 0x4D, 255), (inside.R, inside.G, inside.B, inside.A));
        Assert.Equal(0, bmp.GetPixel(37, 3).A); // top-right corner: nothing drawn
    }
}
