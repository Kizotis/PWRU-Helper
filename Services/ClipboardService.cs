using System.Runtime.InteropServices;

namespace PWRUHelper.Services;

/// <summary>
/// Puts text on the Windows clipboard using the raw Win32 API, on a background thread.
///
/// Why not WPF's Clipboard.SetDataObject? Two real-world problems it caused here:
///  1. It hides an internal retry loop (10 × 100 ms, blocking) inside EVERY call, so when
///     the clipboard is contended each of our attempts froze the UI for ~1 s — users saw
///     multi-second hangs when pressing Enter in the compact reply box.
///  2. Its OLE flush is notorious for failing with CLIPBRD_E_CANT_OPEN when Windows
///     clipboard history (Win+V) or the RDP clipboard is enabled — machines with history
///     ON failed on every copy ("clipboard busy"), machines with it OFF worked.
///
/// The raw API has none of that: CF_UNICODETEXT data is stored by the OS immediately
/// (no delayed rendering, survives app exit), and our retry policy is explicit and runs
/// off the UI thread, so the app never freezes no matter how busy the clipboard is.
/// </summary>
public static class ClipboardService
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>
    /// Try to put <paramref name="text"/> on the clipboard, retrying for up to ~2 s while
    /// another app (clipboard history, etc.) holds it. Runs on a worker thread; never
    /// throws; never blocks the UI.
    /// </summary>
    public static Task<bool> SetTextAsync(string text) => Task.Run(() =>
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (TrySetOnce(text)) return true;
            Thread.Sleep(50);   // worker thread — the UI keeps running
        }
        return false;
    });

    private static bool TrySetOnce(string text)
    {
        // NULL owner window is fine for plain CF_UNICODETEXT (no delayed rendering).
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            int bytes = (text.Length + 1) * 2;   // UTF-16 + null terminator
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (h == IntPtr.Zero) return false;

            IntPtr ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero) { GlobalFree(h); return false; }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0);   // null terminator
            }
            finally { GlobalUnlock(h); }

            if (!EmptyClipboard() || SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero)
            {
                GlobalFree(h);
                return false;
            }
            return true;   // success: the OS now owns the memory
        }
        finally { CloseClipboard(); }
    }
}
