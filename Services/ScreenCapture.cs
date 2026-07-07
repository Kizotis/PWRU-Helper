using System.Drawing;

namespace PWRUHelper.Services;

public enum CaptureMode { Gdi, Wgc }

/// <summary>
/// Grabs a rectangle of the screen as a bitmap (physical pixels), routing to the selected
/// backend. GDI is the default; the experimental Windows.Graphics.Capture backend can be
/// chosen in Settings for full-screen games where GDI returns black. Any WGC failure falls
/// back to GDI automatically, so switching it on can never leave the app unable to capture.
///
/// WGC failure is additionally <em>latched</em>: because a failing WGC attempt is expensive
/// (D3D device setup + up to ~500 ms synchronous wait) and noisy (a Warn every tick), after
/// three consecutive failures we stop attempting WGC for the rest of the session and serve
/// GDI directly. Re-selecting a mode via <see cref="SetMode"/> clears the latch and retries.
/// </summary>
public static class ScreenCapture
{
    private static readonly ICaptureBackend Gdi = new GdiCapture();
    private static WgcCapture? _wgc;   // created lazily on first WGC use

    // WGC failure latch. Each failing Capture in Wgc mode re-runs the full (slow) WGC attempt
    // and would log once per tick; that rhythmically stalls the UI and can roll the 1 MB log
    // within a session. So we count consecutive failures and, once the limit is hit, park on
    // GDI (_wgcBroken) until the user re-picks a mode. A success clears the streak.
    private const int WgcFailureLimit = 3;
    private static int _wgcFailures;
    private static bool _wgcBroken;

    /// <summary>Active backend. Set from settings via <see cref="SetMode"/>.</summary>
    public static CaptureMode Mode { get; private set; } = CaptureMode.Gdi;

    /// <summary>Set the backend from a settings string ("wgc" → experimental, anything else → GDI).</summary>
    public static void SetMode(string? mode)
    {
        Mode = string.Equals(mode, "wgc", StringComparison.OrdinalIgnoreCase) ? CaptureMode.Wgc : CaptureMode.Gdi;
        // Re-picking a mode is a deliberate user action — give WGC a fresh chance, clearing the latch.
        _wgcBroken = false;
        _wgcFailures = 0;
    }

    /// <param name="x">Left, in physical screen pixels.</param>
    /// <param name="y">Top, in physical screen pixels.</param>
    /// <param name="width">Width in physical pixels.</param>
    /// <param name="height">Height in physical pixels.</param>
    public static Bitmap Capture(int x, int y, int width, int height)
    {
        if (Mode == CaptureMode.Wgc && !_wgcBroken)
        {
            try
            {
                Bitmap bmp = (_wgc ??= new WgcCapture()).Capture(x, y, width, height);
                _wgcFailures = 0;   // a good frame clears the failure streak
                return bmp;
            }
            catch (Exception ex)
            {
                // Experimental backend unavailable or failed — never break capture over it.
                _wgcFailures++;
                if (_wgcFailures == 1)
                    Logging.Warn("WGC capture failed, falling back to GDI: " + ex.Message);
                else if (_wgcFailures >= WgcFailureLimit)
                {
                    _wgcBroken = true;
                    Logging.Warn("WGC failed 3 times in a row — staying on GDI for this session.");
                }
                // Otherwise stay quiet: no per-tick log spam while counting toward the limit.
            }
        }
        return Gdi.Capture(x, y, width, height);
    }
}
