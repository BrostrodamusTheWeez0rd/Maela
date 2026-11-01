using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>Something that can be shared: a whole monitor or one window.</summary>
    internal sealed class CaptureSource
    {
        public string Name;
        public bool IsWindow;
        public string MonitorDevice;   // e.g. \\.\DISPLAY1
        public Rectangle Bounds;
        public IntPtr Window;

        public override string ToString() { return Name; }

        public static List<CaptureSource> Monitors()
        {
            List<CaptureSource> list = new List<CaptureSource>();
            int n = 1;
            foreach (Screen screen in Screen.AllScreens)
            {
                CaptureSource s = new CaptureSource();
                s.Name = "Screen " + n++ + (screen.Primary ? " (main)" : String.Empty) + " · " + screen.Bounds.Width + "×" + screen.Bounds.Height;
                s.MonitorDevice = screen.DeviceName;
                s.Bounds = screen.Bounds;
                list.Add(s);
            }
            return list;
        }

        /// <summary>Visible, titled top-level windows (not mæla's own).</summary>
        public static List<CaptureSource> Windows()
        {
            List<CaptureSource> list = new List<CaptureSource>();
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            CaptureNative.EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                if (!CaptureNative.IsWindowVisible(hwnd) || CaptureNative.IsIconic(hwnd))
                    return true;
                int length = CaptureNative.GetWindowTextLength(hwnd);
                if (length == 0)
                    return true;
                uint pid;
                CaptureNative.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == self)
                    return true;
                int exStyle = CaptureNative.GetWindowLong(hwnd, -20);
                if ((exStyle & 0x80) != 0) // WS_EX_TOOLWINDOW
                    return true;
                int cloaked;
                if (CaptureNative.DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0)
                    return true; // Hidden by Windows (other virtual desktop, suspended app).
                CaptureNative.RECT r;
                if (!CaptureNative.GetWindowRect(hwnd, out r) || r.Right - r.Left < 120 || r.Bottom - r.Top < 80)
                    return true;
                StringBuilder title = new StringBuilder(length + 1);
                CaptureNative.GetWindowText(hwnd, title, title.Capacity);
                CaptureSource s = new CaptureSource();
                s.Name = title.ToString();
                s.IsWindow = true;
                s.Window = hwnd;
                s.Bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                list.Add(s);
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }

    /// <summary>Delivers BGRA frames of a monitor or window. Not thread-safe: use from one thread.</summary>
    internal interface IScreenCapture : IDisposable
    {
        int Width { get; }
        int Height { get; }
        string Method { get; }

        /// <summary>
        /// Waits up to <paramref name="timeoutMs"/> for a new picture and hands it to
        /// <paramref name="onFrame"/> (BGRA pointer, stride). Returns false if nothing changed.
        /// </summary>
        bool Capture(int timeoutMs, bool drawCursor, Action<IntPtr, int> onFrame);
    }

    internal static class ScreenCaptureFactory
    {
        public static IScreenCapture Create(CaptureSource source)
        {
            if (source.IsWindow)
                return new WindowCapture(source.Window);
            try
            {
                return new DesktopDuplicationCapture(source.MonitorDevice);
            }
            catch
            {
                return new GdiScreenCapture(source.Bounds); // Older Windows, remote desktop, odd drivers.
            }
        }
    }

    /// <summary>
    /// Whole-monitor capture with DXGI Desktop Duplication: frames come straight from the graphics
    /// card at the monitor's refresh rate (60/120/144 Hz), only when something changed.
    /// </summary>
    internal sealed class DesktopDuplicationCapture : IScreenCapture
    {
        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        private static Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        private static Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumFn(IntPtr self, uint index, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescFn(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DuplicateOutputFn(IntPtr self, IntPtr device, out IntPtr duplication);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireNextFrameFn(IntPtr self, uint timeoutMs, IntPtr frameInfo, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseFrameFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2DFn(IntPtr self, ref TextureDesc desc, IntPtr initial, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetImmediateContextFn(IntPtr self, out IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResourceFn(IntPtr self, IntPtr destination, IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapFn(IntPtr self, IntPtr resource, uint subresource, int mapType, uint flags, out MappedSubresource mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapFn(IntPtr self, IntPtr resource, uint subresource);

        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
        [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint levels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        private struct TextureDesc
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MappedSubresource
        {
            public IntPtr Data;
            public uint RowPitch;
            public uint DepthPitch;
        }

        private readonly string _device;
        private IntPtr _d3d, _context, _duplication, _staging;
        private int _width, _height;
        private readonly IntPtr _frameInfo = Marshal.AllocHGlobal(64);
        private Rectangle _bounds;
        private bool _disposed;

        public int Width { get { return _width; } }
        public int Height { get { return _height; } }
        public string Method { get { return "Desktop Duplication"; } }

        public DesktopDuplicationCapture(string monitorDevice)
        {
            _device = monitorDevice;
            Open();
        }

        private static T Slot<T>(IntPtr instance, int index) where T : class
        {
            return OpenH264Pack.Slot<T>(instance, index);
        }

        private static void Release(ref IntPtr p)
        {
            if (p != IntPtr.Zero)
            {
                Slot<ReleaseFn>(p, 2)(p);
                p = IntPtr.Zero;
            }
        }

        private void Open()
        {
            IntPtr factory;
            Check(CreateDXGIFactory1(ref IID_IDXGIFactory1, out factory), "CreateDXGIFactory1");
            try
            {
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    if (Slot<EnumFn>(factory, 12)(factory, a, out adapter) != 0)
                        break; // EnumAdapters1: no more.
                    try
                    {
                        for (uint o = 0; ; o++)
                        {
                            IntPtr output;
                            if (Slot<EnumFn>(adapter, 7)(adapter, o, out output) != 0)
                                break; // EnumOutputs: no more.
                            try
                            {
                                IntPtr desc = Marshal.AllocHGlobal(128);
                                try
                                {
                                    Slot<GetDescFn>(output, 7)(output, desc);
                                    string name = Marshal.PtrToStringUni(desc).TrimEnd('\0');
                                    if (!String.Equals(name, _device, StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    _bounds = Rectangle.FromLTRB(Marshal.ReadInt32(desc, 64), Marshal.ReadInt32(desc, 68), Marshal.ReadInt32(desc, 72), Marshal.ReadInt32(desc, 76));
                                }
                                finally
                                {
                                    Marshal.FreeHGlobal(desc);
                                }
                                int level;
                                Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out _d3d, out level, out _context), "D3D11CreateDevice");
                                IntPtr output1;
                                Check(Slot<QueryInterfaceFn>(output, 0)(output, ref IID_IDXGIOutput1, out output1), "IDXGIOutput1");
                                try { Check(Slot<DuplicateOutputFn>(output1, 22)(output1, _d3d, out _duplication), "DuplicateOutput"); }
                                finally { Release(ref output1); }
                                _width = _bounds.Width;
                                _height = _bounds.Height;
                                TextureDesc t = new TextureDesc();
                                t.Width = (uint)_width; t.Height = (uint)_height; t.MipLevels = 1; t.ArraySize = 1;
                                t.Format = 87; t.SampleCount = 1; t.Usage = 3; t.CpuAccessFlags = 0x20000;
                                Check(Slot<CreateTexture2DFn>(_d3d, 5)(_d3d, ref t, IntPtr.Zero, out _staging), "CreateTexture2D");
                                return;
                            }
                            finally
                            {
                                Release(ref output);
                            }
                        }
                    }
                    finally
                    {
                        Release(ref adapter);
                    }
                }
            }
            finally
            {
                Release(ref factory);
            }
            Close();
            throw new InvalidOperationException("That screen can't be captured with Desktop Duplication.");
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0)
                throw new InvalidOperationException(what + " failed (0x" + hr.ToString("X8") + ").");
        }

        public bool Capture(int timeoutMs, bool drawCursor, Action<IntPtr, int> onFrame)
        {
            if (_disposed)
                return false;
            if (_duplication == IntPtr.Zero)
            {
                try { Open(); } catch { return false; } // Lost earlier (resolution change, secure desktop): retry.
            }
            IntPtr resource;
            int hr = Slot<AcquireNextFrameFn>(_duplication, 8)(_duplication, (uint)Math.Max(0, timeoutMs), _frameInfo, out resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                return false;
            if (hr == DXGI_ERROR_ACCESS_LOST || hr < 0)
            {
                Close();
                return false;
            }
            try
            {
                IntPtr texture;
                if (Slot<QueryInterfaceFn>(resource, 0)(resource, ref IID_ID3D11Texture2D, out texture) < 0)
                    return false;
                try { Slot<CopyResourceFn>(_context, 47)(_context, _staging, texture); }
                finally { Release(ref texture); }
            }
            finally
            {
                Release(ref resource);
                Slot<ReleaseFrameFn>(_duplication, 14)(_duplication);
            }
            MappedSubresource mapped;
            if (Slot<MapFn>(_context, 14)(_context, _staging, 0, 1, 0, out mapped) < 0)
                return false;
            try
            {
                if (drawCursor)
                    CursorOverlay.Draw(mapped.Data, (int)mapped.RowPitch, _width, _height, _bounds.Location);
                onFrame(mapped.Data, (int)mapped.RowPitch);
            }
            finally
            {
                Slot<UnmapFn>(_context, 15)(_context, _staging, 0);
            }
            return true;
        }

        private void Close()
        {
            Release(ref _staging);
            Release(ref _duplication);
            Release(ref _context);
            Release(ref _d3d);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Close();
            Marshal.FreeHGlobal(_frameInfo);
        }
    }

    /// <summary>Fallback monitor capture with a classic screen copy (slower, but works everywhere).</summary>
    internal sealed class GdiScreenCapture : IScreenCapture
    {
        private readonly Rectangle _bounds;
        private readonly DibSurface _surface;
        public int Width { get { return _bounds.Width; } }
        public int Height { get { return _bounds.Height; } }
        public string Method { get { return "screen copy"; } }

        public GdiScreenCapture(Rectangle bounds)
        {
            _bounds = bounds;
            _surface = new DibSurface(bounds.Width, bounds.Height);
        }

        public bool Capture(int timeoutMs, bool drawCursor, Action<IntPtr, int> onFrame)
        {
            IntPtr screen = CaptureNative.GetDC(IntPtr.Zero);
            try
            {
                CaptureNative.BitBlt(_surface.Hdc, 0, 0, _bounds.Width, _bounds.Height, screen, _bounds.X, _bounds.Y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
            }
            finally
            {
                CaptureNative.ReleaseDC(IntPtr.Zero, screen);
            }
            if (drawCursor)
                CursorOverlay.Draw(_surface.Bits, _surface.Stride, _bounds.Width, _bounds.Height, _bounds.Location);
            onFrame(_surface.Bits, _surface.Stride);
            return true;
        }

        public void Dispose() { _surface.Dispose(); }
    }

    /// <summary>
    /// One window, even when it's partly covered: other windows, pop-ups and notifications never
    /// appear in the stream. Uses PrintWindow with full-content rendering (Windows 8.1+).
    /// </summary>
    internal sealed class WindowCapture : IScreenCapture
    {
        private readonly IntPtr _window;
        private DibSurface _surface;
        private int _width, _height;
        public int Width { get { return _width; } }
        public int Height { get { return _height; } }
        public string Method { get { return "window"; } }

        public WindowCapture(IntPtr window)
        {
            _window = window;
            Resize();
        }

        private void Resize()
        {
            CaptureNative.RECT r;
            CaptureNative.GetWindowRect(_window, out r);
            int w = Math.Max(16, r.Right - r.Left) & ~1, h = Math.Max(16, r.Bottom - r.Top) & ~1;
            if (_surface != null && w == _width && h == _height)
                return;
            if (_surface != null)
                _surface.Dispose();
            _width = w;
            _height = h;
            _surface = new DibSurface(w, h);
        }

        public bool Capture(int timeoutMs, bool drawCursor, Action<IntPtr, int> onFrame)
        {
            if (!CaptureNative.IsWindow(_window))
                return false;
            if (CaptureNative.IsIconic(_window))
                return false; // Minimised: keep showing the last picture.
            Resize();
            if (!CaptureNative.PrintWindow(_window, _surface.Hdc, 2)) // PW_RENDERFULLCONTENT
                return false;
            if (drawCursor)
            {
                CaptureNative.RECT r;
                CaptureNative.GetWindowRect(_window, out r);
                CursorOverlay.Draw(_surface.Bits, _surface.Stride, _width, _height, new Point(r.Left, r.Top));
            }
            onFrame(_surface.Bits, _surface.Stride);
            return true;
        }

        public void Dispose() { if (_surface != null) _surface.Dispose(); }
    }

    /// <summary>A 32-bit top-down DIB we can draw into with GDI and read as BGRA.</summary>
    internal sealed class DibSurface : IDisposable
    {
        public readonly IntPtr Hdc;
        public readonly IntPtr Bits;
        public readonly int Stride;
        private readonly IntPtr _bitmap, _old;

        public DibSurface(int width, int height)
        {
            CaptureNative.BITMAPINFOHEADER info = new CaptureNative.BITMAPINFOHEADER();
            info.biSize = Marshal.SizeOf(typeof(CaptureNative.BITMAPINFOHEADER));
            info.biWidth = width;
            info.biHeight = -height; // Top-down.
            info.biPlanes = 1;
            info.biBitCount = 32;
            IntPtr bits;
            Hdc = CaptureNative.CreateCompatibleDC(IntPtr.Zero);
            _bitmap = CaptureNative.CreateDIBSection(Hdc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Not enough memory for the capture surface.");
            Bits = bits;
            Stride = width * 4;
            _old = CaptureNative.SelectObject(Hdc, _bitmap);
        }

        public void Dispose()
        {
            CaptureNative.SelectObject(Hdc, _old);
            CaptureNative.DeleteObject(_bitmap);
            CaptureNative.DeleteDC(Hdc);
        }
    }

    /// <summary>Draws the mouse pointer into a captured BGRA picture.</summary>
    internal static class CursorOverlay
    {
        public static void Draw(IntPtr bits, int stride, int width, int height, Point origin)
        {
            CaptureNative.CURSORINFO ci = new CaptureNative.CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CaptureNative.CURSORINFO));
            if (!CaptureNative.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero)
                return;
            int x = ci.ptScreenPos.X - origin.X, y = ci.ptScreenPos.Y - origin.Y;
            if (x < -64 || y < -64 || x > width || y > height)
                return;
            CaptureNative.ICONINFO icon;
            int hotX = 0, hotY = 0;
            if (CaptureNative.GetIconInfo(ci.hCursor, out icon))
            {
                hotX = icon.xHotspot; hotY = icon.yHotspot;
                if (icon.hbmMask != IntPtr.Zero) CaptureNative.DeleteObject(icon.hbmMask);
                if (icon.hbmColor != IntPtr.Zero) CaptureNative.DeleteObject(icon.hbmColor);
            }
            using (Bitmap target = new Bitmap(width, height, stride, System.Drawing.Imaging.PixelFormat.Format32bppRgb, bits))
            using (Graphics g = Graphics.FromImage(target))
            {
                IntPtr hdc = g.GetHdc();
                try { CaptureNative.DrawIconEx(hdc, x - hotX, y - hotY, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
                finally { g.ReleaseHdc(hdc); }
            }
        }
    }

    /// <summary>Fast downscale of a BGRA picture to the share size (area averaging), into I420.</summary>
    internal static unsafe class FrameScaler
    {
        /// <summary>Scales BGRA (srcW x srcH) into a BGRA buffer of the frame's size, then converts to I420.</summary>
        public static void ToI420(IntPtr src, int stride, int srcW, int srcH, I420Frame frame, ref byte[] scratch)
        {
            if (srcW == frame.Width && srcH == frame.Height)
            {
                ColorConvert.BgraToI420(src, stride, frame);
                return;
            }
            int w = frame.Width, h = frame.Height;
            if (scratch == null || scratch.Length < w * h * 4)
                scratch = new byte[w * h * 4];
            byte* s = (byte*)src.ToPointer();
            fixed (byte* d = scratch)
            {
                // Each target pixel averages up to 2x2 source samples spread over its footprint.
                for (int y = 0; y < h; y++)
                {
                    int sy0 = (int)((long)y * srcH / h), sy1 = Math.Min(srcH - 1, (int)(((long)y * 2 + 1) * srcH / (2 * h)));
                    byte* row0 = s + (long)sy0 * stride;
                    byte* row1 = s + (long)sy1 * stride;
                    byte* o = d + y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int sx0 = (int)((long)x * srcW / w) * 4, sx1 = Math.Min(srcW - 1, (int)(((long)x * 2 + 1) * srcW / (2 * w))) * 4;
                        o[0] = (byte)((row0[sx0] + row0[sx1] + row1[sx0] + row1[sx1] + 2) >> 2);
                        o[1] = (byte)((row0[sx0 + 1] + row0[sx1 + 1] + row1[sx0 + 1] + row1[sx1 + 1] + 2) >> 2);
                        o[2] = (byte)((row0[sx0 + 2] + row0[sx1 + 2] + row1[sx0 + 2] + row1[sx1 + 2] + 2) >> 2);
                        o[3] = 255;
                        o += 4;
                    }
                }
                ColorConvert.BgraToI420(new IntPtr(d), w * 4, frame);
            }
        }

        /// <summary>The share size for a source and a chosen height (keeps aspect, never upscales, even numbers).</summary>
        public static Size Fit(int srcW, int srcH, int maxHeight)
        {
            if (srcH <= maxHeight)
                return new Size(srcW & ~1, srcH & ~1);
            int w = (int)((long)srcW * maxHeight / srcH);
            return new Size(w & ~1, maxHeight & ~1);
        }
    }

    internal static class CaptureNative
    {
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
        [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize; public int biWidth; public int biHeight; public short biPlanes; public short biBitCount;
            public int biCompression; public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter; public int biClrUsed; public int biClrImportant;
        }

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    }
}
