using System.Drawing;

namespace PWRUHelper.Services;

public enum CaptureMode { Gdi, Wgc }

/// <summary>
/// Grabs a rectangle of the screen as a bitmap (physical pixels), routing to the selected
/// backend. GDI is the default; the experimental Windows.Graphics.Capture backend can be
/// chosen in Settings for full-screen games where GDI returns black. Any WGC failure falls
/// back to GDI automatically, so switching it on can never leave the app unable to capture.
/// </summary>
public static class ScreenCapture
{
    private static readonly ICaptureBackend Gdi = new GdiCapture();
    private static WgcCapture? _wgc;   // created lazily on first WGC use

    /// <summary>Active backend. Set from settings via <see cref="SetMode"/>.</summary>
    public static CaptureMode Mode { get; set; } = CaptureMode.Gdi;

    /// <summary>Set the backend from a settings string ("wgc" → experimental, anything else → GDI).</summary>
    public static void SetMode(string? mode) =>
        Mode = string.Equals(mode, "wgc", StringComparison.OrdinalIgnoreCase) ? CaptureMode.Wgc : CaptureMode.Gdi;

    /// <param name="x">Left, in physical screen pixels.</param>
    /// <param name="y">Top, in physical screen pixels.</param>
    /// <param name="width">Width in physical pixels.</param>
    /// <param name="height">Height in physical pixels.</param>
    public static Bitmap Capture(int x, int y, int width, int height)
    {
        if (Mode == CaptureMode.Wgc)
        {
            try
            {
                return (_wgc ??= new WgcCapture()).Capture(x, y, width, height);
            }
            catch (Exception ex)
            {
                // Experimental backend unavailable or failed — never break capture over it.
                Logging.Warn("WGC capture failed, falling back to GDI: " + ex.Message);
            }
        }
        return Gdi.Capture(x, y, width, height);
    }
}
