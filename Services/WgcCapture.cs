using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;   // MarshalInterface<T>.FromAbi, CastExtensions.As<T>

namespace PWRUHelper.Services;

/// <summary>
/// Experimental screen-capture backend built on <c>Windows.Graphics.Capture</c> (WGC),
/// reached through the WinRT projections our TFM (<c>net8.0-windows10.0.19041.0</c>) provides.
/// Unlike GDI <c>BitBlt</c>, WGC uses the compositor / DXGI path, so it can capture true
/// full-screen-exclusive games that come back black under GDI.
///
/// One-shot &amp; synchronous by design: for a single <see cref="Capture"/> call it spins up a
/// D3D11 device, a free-threaded frame pool for the whole monitor, grabs exactly one frame,
/// copies it to a CPU-readable staging texture, crops to the requested rectangle, then tears
/// everything down. It throws on <em>any</em> failure (unsupported OS, no frame, interop error);
/// the caller (<see cref="ScreenCapture"/>) catches and falls back to GDI, so throwing — never
/// returning a black/partial bitmap — is the correct behaviour.
///
/// No SharpDX/Vortice is available, so the D3D11 / DXGI COM surface is declared here by hand.
/// The device/context methods we call are invoked through their raw vtable slots (see the slot
/// constants below) rather than by declaring the full — enormous — COM interfaces.
/// </summary>
public sealed class WgcCapture : ICaptureBackend
{
    public Bitmap Capture(int x, int y, int width, int height)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this OS.");

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        // The monitor that owns the capture point, and its top-left in physical screen pixels
        // (the app is Per-Monitor-V2 DPI aware, so these are real device pixels — the same space
        // the caller passes x/y/width/height in).
        IntPtr hmon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        if (hmon == IntPtr.Zero)
            throw new InvalidOperationException("No monitor contains the capture point.");
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref mi))
            throw new InvalidOperationException("GetMonitorInfo failed for the capture monitor.");

        IntPtr pDevice = IntPtr.Zero, pContext = IntPtr.Zero;
        IDirect3DDevice? device = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;

        try
        {
            CreateD3DDevice(out pDevice, out pContext);
            device = CreateWinRtDevice(pDevice);
            var item = CreateItemForMonitor(hmon);

            // Free-threaded so we can poll TryGetNextFrame from this (UI) thread without needing a
            // DispatcherQueue / message pump — frames arrive on a WGC-owned worker thread.
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = framePool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = false; } catch { /* cosmetic; ignore on older builds */ }
            session.StartCapture();

            Direct3D11CaptureFrame? frame = null;
            long deadline = Environment.TickCount64 + 500;   // up to ~500 ms for the first frame
            while (Environment.TickCount64 < deadline)
            {
                frame = framePool.TryGetNextFrame();
                if (frame != null) break;
                Thread.Sleep(5);
            }
            if (frame == null)
                throw new TimeoutException("WGC produced no frame within the timeout.");

            try
            {
                return CopyFrameToBitmap(frame, pDevice, pContext,
                    mi.rcMonitor.Left, mi.rcMonitor.Top, x, y, width, height);
            }
            finally
            {
                (frame as IDisposable)?.Dispose();
            }
        }
        finally
        {
            try { (session as IDisposable)?.Dispose(); } catch { /* best effort */ }
            try { (framePool as IDisposable)?.Dispose(); } catch { /* best effort */ }
            try { (device as IDisposable)?.Dispose(); } catch { /* best effort */ }
            if (pContext != IntPtr.Zero) Marshal.Release(pContext);
            if (pDevice != IntPtr.Zero) Marshal.Release(pDevice);
        }
    }

    /// <summary>
    /// Copy the captured monitor frame into a staging (CPU-readable) texture, then crop the
    /// requested rectangle out of it into a <see cref="PixelFormat.Format32bppArgb"/> bitmap.
    /// WGC hands us <c>B8G8R8A8UIntNormalized</c> and <c>Format32bppArgb</c> is BGRA in memory
    /// too, so this is a straight per-row byte copy (respecting both strides).
    /// </summary>
    private static Bitmap CopyFrameToBitmap(Direct3D11CaptureFrame frame, IntPtr pDevice, IntPtr pContext,
        int monLeft, int monTop, int x, int y, int width, int height)
    {
        var surface = frame.Surface;
        var sdesc = surface.Description;          // actual GPU texture dimensions (whole monitor)
        int texW = sdesc.Width;
        int texH = sdesc.Height;

        // Native ID3D11Texture2D behind the WinRT surface.
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid texIid = IID_ID3D11Texture2D;
        IntPtr srcTex = access.GetInterface(ref texIid);

        IntPtr pStaging = IntPtr.Zero;
        try
        {
            var td = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)texW,
                Height = (uint)texH,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1,
                SampleQuality = 0,
                Usage = D3D11_USAGE_STAGING,
                BindFlags = 0,
                CPUAccessFlags = D3D11_CPU_ACCESS_READ,
                MiscFlags = 0,
            };

            var createTex2D = GetVtblDelegate<CreateTexture2DDelegate>(pDevice, SLOT_CreateTexture2D);
            Marshal.ThrowExceptionForHR(createTex2D(pDevice, ref td, IntPtr.Zero, out pStaging));

            var copyResource = GetVtblDelegate<CopyResourceDelegate>(pContext, SLOT_CopyResource);
            copyResource(pContext, pStaging, srcTex);

            var map = GetVtblDelegate<MapDelegate>(pContext, SLOT_Map);
            var unmap = GetVtblDelegate<UnmapDelegate>(pContext, SLOT_Unmap);

            Marshal.ThrowExceptionForHR(map(pContext, pStaging, 0, D3D11_MAP_READ, 0, out D3D11_MAPPED_SUBRESOURCE msr));
            try
            {
                return Crop(msr, texW, texH, monLeft, monTop, x, y, width, height);
            }
            finally
            {
                unmap(pContext, pStaging, 0);
            }
        }
        finally
        {
            if (pStaging != IntPtr.Zero) Marshal.Release(pStaging);
            if (srcTex != IntPtr.Zero) Marshal.Release(srcTex);
        }
    }

    /// <summary>
    /// Copy the requested absolute-screen rectangle out of the mapped monitor texture. The
    /// rectangle is translated to monitor-relative coordinates and clipped to the texture; any
    /// part outside the monitor stays transparent/black rather than reading out of bounds.
    /// </summary>
    private static Bitmap Crop(D3D11_MAPPED_SUBRESOURCE msr, int texW, int texH,
        int monLeft, int monTop, int x, int y, int width, int height)
    {
        int relX = x - monLeft;
        int relY = y - monTop;

        // Overlap of the requested rect with the monitor texture, in texture (monitor-relative) space.
        int srcX0 = Math.Clamp(relX, 0, texW);
        int srcY0 = Math.Clamp(relY, 0, texH);
        int srcX1 = Math.Clamp(relX + width, 0, texW);
        int srcY1 = Math.Clamp(relY + height, 0, texH);
        int copyW = srcX1 - srcX0;
        int copyH = srcY1 - srcY0;

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (copyW > 0 && copyH > 0)
            {
                int destX0 = srcX0 - relX;          // >= 0: where the overlap lands in the output
                int destY0 = srcY0 - relY;
                int rowBytes = copyW * 4;
                int srcStride = (int)msr.RowPitch;
                var row = new byte[rowBytes];
                for (int r = 0; r < copyH; r++)
                {
                    IntPtr srcRow = msr.pData + (nint)((long)(srcY0 + r) * srcStride + (long)srcX0 * 4);
                    Marshal.Copy(srcRow, row, 0, rowBytes);
                    IntPtr dstRow = data.Scan0 + (nint)((long)(destY0 + r) * data.Stride + (long)destX0 * 4);
                    Marshal.Copy(row, 0, dstRow, rowBytes);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    // ----- D3D11 device creation & WinRT bridging -----------------------------------------------

    private static void CreateD3DDevice(out IntPtr pDevice, out IntPtr pContext)
    {
        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out pDevice, out _, out pContext);
        if (hr < 0)
        {
            // No usable hardware device (headless / some RDP sessions) — fall back to WARP.
            hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                out pDevice, out _, out pContext);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private static IDirect3DDevice CreateWinRtDevice(IntPtr pD3DDevice)
    {
        Guid dxgiIid = IID_IDXGIDevice;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(pD3DDevice, ref dxgiIid, out IntPtr pDxgi));
        try
        {
            CreateDirect3D11DeviceFromDXGIDevice(pDxgi, out IntPtr pInspectable);
            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(pInspectable);
            }
            finally
            {
                Marshal.Release(pInspectable);
            }
        }
        finally
        {
            Marshal.Release(pDxgi);
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        // The interop factory lives on GraphicsCaptureItem's activation factory, not on an instance.
        Guid interopIid = IID_IGraphicsCaptureItemInterop;

        // .NET 5+ dropped built-in HSTRING marshalling (UnmanagedType.HString), so we build the
        // activatableClassId HSTRING by hand and release it in finally — see the
        // RoGetActivationFactory / WindowsCreateString declarations below.
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hr = WindowsCreateString(className, className.Length, out IntPtr hClass);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);

        object factoryObj;
        try
        {
            RoGetActivationFactory(hClass, ref interopIid, out factoryObj);
        }
        finally
        {
            WindowsDeleteString(hClass);
        }
        var interop = (IGraphicsCaptureItemInterop)factoryObj;

        Guid itemIid = IID_GraphicsCaptureItem;
        IntPtr pItem = interop.CreateForMonitor(hmon, ref itemIid);
        try
        {
            return GraphicsCaptureItem.FromAbi(pItem);
        }
        finally
        {
            Marshal.Release(pItem);
        }
    }

    // ----- Manual COM vtable dispatch (no SharpDX) ----------------------------------------------

    // Vtable slot indices (0-based, IUnknown occupies 0,1,2).
    //  ID3D11Device:        IUnknown(3) + CreateBuffer, CreateTexture1D, CreateTexture2D.
    private const int SLOT_CreateTexture2D = 5;
    //  ID3D11DeviceContext: IUnknown(3) + ID3D11DeviceChild(4) then context methods.
    //  Context-relative:    Map=7, Unmap=8, CopyResource=40  →  +7 offset.
    private const int SLOT_Map = 14;
    private const int SLOT_Unmap = 15;
    private const int SLOT_CopyResource = 47;

    private static T GetVtblDelegate<T>(IntPtr comObject, int slot) where T : Delegate
    {
        IntPtr vtbl = Marshal.ReadIntPtr(comObject);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(IntPtr self, IntPtr dst, IntPtr src);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(IntPtr self, IntPtr resource, uint subresource, int mapType, uint mapFlags, out D3D11_MAPPED_SUBRESOURCE mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(IntPtr self, IntPtr resource, uint subresource);

    // ----- Interop types & P/Invoke -------------------------------------------------------------

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;         // DXGI_FORMAT
        public uint SampleCount;    // DXGI_SAMPLE_DESC.Count
        public uint SampleQuality;  // DXGI_SAMPLE_DESC.Quality
        public uint Usage;          // D3D11_USAGE
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    // IIDs
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // D3D11 constants
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const int D3D11_MAP_READ = 1;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true, PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // Built-in HSTRING marshalling (UnmanagedType.HString) was removed from the runtime in
    // .NET 5+, so activatableClassId is passed as a raw HSTRING we create/destroy by hand
    // (see CreateItemForMonitor) via WindowsCreateString / WindowsDeleteString.
    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
