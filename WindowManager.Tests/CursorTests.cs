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
    public void Rendered_arrow_is_yellow_inside_and_transparent_outside()
    {
        using var bmp = CursorTheme.Render(CursorArt.Arrow, 96);
        Assert.Equal(40, bmp.Width);
        var inside = bmp.GetPixel(10, 20); // well inside the arrow body
        Assert.Equal((0xFF, 0xC9, 0x4D, 255), (inside.R, inside.G, inside.B, inside.A));
        Assert.Equal(0, bmp.GetPixel(37, 3).A); // top-right corner: nothing drawn
    }
}
