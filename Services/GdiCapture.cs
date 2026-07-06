using System.Drawing;

namespace PWRUHelper.Services;

/// <summary>
/// The default capture backend: GDI <c>BitBlt</c> via <see cref="Graphics.CopyFromScreen"/>.
/// Fast and reliable for windowed / borderless games (which the app recommends). It can return
/// black for some true full-screen-exclusive games — that's the case the experimental
/// <see cref="WgcCapture"/> exists for.
/// </summary>
public sealed class GdiCapture : ICaptureBackend
{
    public Bitmap Capture(int x, int y, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }
        catch
        {
            // Locked workstation, UAC secure desktop, etc. Never leak the GDI handle.
            bmp.Dispose();
            throw;
        }
        return bmp;
    }
}
