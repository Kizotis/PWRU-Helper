using System.Drawing;

namespace PWRUHelper.Services;

/// <summary>A way to grab a rectangle of the screen as a bitmap (physical pixels). Lets the
/// app swap capture strategies — plain GDI (default) or the experimental Windows.Graphics
/// capture — behind one call site.</summary>
public interface ICaptureBackend
{
    /// <param name="x">Left, in physical screen pixels.</param>
    /// <param name="y">Top, in physical screen pixels.</param>
    /// <param name="width">Width in physical pixels.</param>
    /// <param name="height">Height in physical pixels.</param>
    Bitmap Capture(int x, int y, int width, int height);
}
