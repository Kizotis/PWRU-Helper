using System.Drawing;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class OcrImageFilterTests
{
    [Fact]
    public void WithinTolerance_matches_close_colours_and_rejects_far_ones()
    {
        long tol = 70L * 70;
        Assert.True(OcrImageFilter.WithinTolerance(255, 255, 255, Color.White, tol));   // exact
        Assert.True(OcrImageFilter.WithinTolerance(250, 250, 250, Color.White, tol));   // near
        Assert.False(OcrImageFilter.WithinTolerance(0, 0, 0, Color.White, tol));        // far
    }

    [Fact]
    public void KeepColor_whitens_the_target_and_blackens_the_rest()
    {
        using var src = new Bitmap(2, 1);
        src.SetPixel(0, 0, Color.White);
        src.SetPixel(1, 0, Color.Black);

        using var outp = OcrImageFilter.KeepColor(src, Color.White, tolerance: 40);

        Assert.Equal(255, outp.GetPixel(0, 0).R);   // kept → white
        Assert.Equal(0, outp.GetPixel(1, 0).R);     // dropped → black
    }

    [Fact]
    public void KeepColor_isolates_by_hue_not_just_brightness()
    {
        using var src = new Bitmap(2, 1);
        src.SetPixel(0, 0, Color.FromArgb(255, 255, 0, 0));   // red — the target channel
        src.SetPixel(1, 0, Color.White);                      // bright but wrong colour

        using var outp = OcrImageFilter.KeepColor(src, Color.FromArgb(255, 255, 0, 0), tolerance: 40);

        Assert.Equal(255, outp.GetPixel(0, 0).R);   // red kept
        Assert.Equal(0, outp.GetPixel(1, 0).R);     // white dropped despite being bright
    }

    [Fact]
    public void BoostContrast_keeps_bright_text_of_any_colour_and_suppresses_dark_background()
    {
        using var src = new Bitmap(3, 1);
        src.SetPixel(0, 0, Color.White);                      // bright white glyph
        src.SetPixel(1, 0, Color.FromArgb(255, 255, 0, 0));   // bright RED glyph (max channel = 255)
        src.SetPixel(2, 0, Color.FromArgb(255, 90, 90, 90));  // dark busy background

        using var outp = OcrImageFilter.BoostContrast(src, gamma: 2.5);

        Assert.True(outp.GetPixel(0, 0).R >= 250, "white glyph should stay bright");
        Assert.True(outp.GetPixel(1, 0).R >= 250, "coloured glyph should stay bright");
        Assert.True(outp.GetPixel(2, 0).R < 90, "dark background should be pushed darker");
    }

    [Fact]
    public void Filter_returns_a_new_bitmap_and_does_not_mutate_the_source()
    {
        using var src = new Bitmap(1, 1);
        src.SetPixel(0, 0, Color.FromArgb(255, 90, 90, 90));

        using var outp = OcrImageFilter.BoostContrast(src);

        Assert.NotSame(src, outp);
        Assert.Equal(90, src.GetPixel(0, 0).R);   // source untouched
    }
}
