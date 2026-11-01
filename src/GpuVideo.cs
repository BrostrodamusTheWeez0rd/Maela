using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Maela
{
    /// <summary>
    /// Checks H.264 before it reaches the graphics driver, which sits outside the viewer's
    /// sandbox. Allows only what mæla's own encoders make: ordinary 8-bit 4:2:0 progressive video
    /// within the sizes screen sharing supports. SEI and other optional data are removed.
    /// </summary>
    internal static class H264StreamCheck
    {
        public const int MaxWidth = 5120, MaxHeight = 4096, MaxPixels = 5120 * 2160;
        private const int MaxParameterSetBytes = 256;
        private const int MaxLevel = 52;

        /// <summary>
        /// False if the data isn't allowed. Otherwise <paramref name="clean"/> is what to decode, and
        /// width and height are updated from any sequence parameter set found.
        /// </summary>
        public static bool Check(byte[] data, ref int width, ref int height, out byte[] clean)
        {
            clean = null;
            List<KeyValuePair<int, int>> nals = Split(data);
            if (nals.Count == 0)
                return false;
            bool strip = false;
            foreach (KeyValuePair<int, int> nal in nals)
            {
                int headerAt = nal.Key;
                if (nal.Value < 1 || (data[headerAt] & 0x80) != 0)
                    return false;
                int type = data[headerAt] & 0x1F;
                if (type == 7)
                {
                    int w, h;
                    if (nal.Value > MaxParameterSetBytes || !ParseSps(data, headerAt + 1, nal.Value - 1, out w, out h))
                        return false;
                    width = w;
                    height = h;
                }
                else if (type == 8)
                {
                    if (nal.Value > MaxParameterSetBytes)
                        return false;
                }
                else if (type == 6 || type == 9 || type == 12)
                    strip = true;
                else if (type != 1 && type != 5)
                    return false;
            }
            if (width == 0 || height == 0)
                return false; // Pictures before any sequence parameter set.
            if (!strip)
            {
                clean = data;
                return true;
            }
            int total = 0;
            foreach (KeyValuePair<int, int> nal in nals)
            {
                int type = data[nal.Key] & 0x1F;
                if (type != 6 && type != 9 && type != 12)
                    total += 4 + nal.Value;
            }
            if (total == 0)
                return false;
            clean = new byte[total];
            int at = 0;
            foreach (KeyValuePair<int, int> nal in nals)
            {
                int type = data[nal.Key] & 0x1F;
                if (type == 6 || type == 9 || type == 12)
                    continue;
                clean[at + 3] = 1;
                Buffer.BlockCopy(data, nal.Key, clean, at + 4, nal.Value);
                at += 4 + nal.Value;
            }
            return true;
        }

        /// <summary>Offset and length of each NAL unit in Annex B data. Empty if it doesn't start with a start code.</summary>
        private static List<KeyValuePair<int, int>> Split(byte[] d)
        {
            List<KeyValuePair<int, int>> list = new List<KeyValuePair<int, int>>();
            int start = -1;
            for (int i = 0; i + 2 < d.Length; i++)
            {
                if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1)
                    continue;
                if (start >= 0)
                {
                    int end = i;
                    while (end > start && d[end - 1] == 0)
                        end--;
                    list.Add(new KeyValuePair<int, int>(start, end - start));
                }
                else if (i > 1)
                    return new List<KeyValuePair<int, int>>(); // Junk first.
                start = i + 3;
                i += 2;
            }
            if (start >= 0 && start < d.Length)
                list.Add(new KeyValuePair<int, int>(start, d.Length - start));
            return list;
        }

        private sealed class Bits
        {
            private readonly byte[] _d;
            private int _bit;
            public bool Bad;
            public Bits(byte[] d) { _d = d; }

            public int U(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++)
                {
                    if (_bit >= _d.Length * 8)
                    {
                        Bad = true;
                        return 0;
                    }
                    v = (v << 1) | ((_d[_bit / 8] >> (7 - _bit % 8)) & 1);
                    _bit++;
                }
                return v;
            }

            public int Ue()
            {
                int zeros = 0;
                while (U(1) == 0)
                {
                    if (Bad || ++zeros > 16)
                    {
                        Bad = true;
                        return 0;
                    }
                }
                return (1 << zeros) - 1 + U(zeros);
            }

            public int Se()
            {
                int k = Ue();
                return (k & 1) != 0 ? (k + 1) / 2 : -(k / 2);
            }
        }

        private static bool ParseSps(byte[] data, int offset, int length, out int width, out int height)
        {
            width = height = 0;
            // Take out emulation-prevention bytes.
            byte[] rbsp = new byte[length];
            int n = 0, zeros = 0;
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                if (zeros >= 2 && b == 3)
                {
                    zeros = 0;
                    continue;
                }
                rbsp[n++] = b;
                zeros = b == 0 ? zeros + 1 : 0;
            }
            byte[] trimmed = new byte[n];
            Buffer.BlockCopy(rbsp, 0, trimmed, 0, n);

            Bits r = new Bits(trimmed);
            int profile = r.U(8);
            r.U(8); // Constraint flags.
            int level = r.U(8);
            if ((profile != 66 && profile != 77 && profile != 100) || level > MaxLevel)
                return false;
            if (r.Ue() > 31)
                return false; // seq_parameter_set_id
            if (profile == 100)
            {
                if (r.Ue() != 1 || r.Ue() != 0 || r.Ue() != 0)
                    return false; // 4:2:0, 8 bits luma and chroma.
                r.U(1);
                if (r.U(1) != 0)
                    return false; // Custom scaling matrices.
            }
            if (r.Ue() > 12)
                return false; // log2_max_frame_num
            int pocType = r.Ue();
            if (pocType == 0)
            {
                if (r.Ue() > 12)
                    return false;
            }
            else if (pocType == 1)
            {
                r.U(1);
                r.Se();
                r.Se();
                int cycle = r.Ue();
                if (cycle > 255)
                    return false;
                for (int i = 0; i < cycle; i++)
                    r.Se();
            }
            else if (pocType != 2)
                return false;
            if (r.Ue() > 16)
                return false; // Reference frames.
            r.U(1);
            int mbWide = r.Ue() + 1, mbHigh = r.Ue() + 1;
            if (r.U(1) != 1)
                return false; // Progressive only.
            r.U(1);
            int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (r.U(1) == 1)
            {
                cropLeft = r.Ue();
                cropRight = r.Ue();
                cropTop = r.Ue();
                cropBottom = r.Ue();
            }
            if (r.Bad || mbWide > MaxWidth / 16 + 1 || mbHigh > MaxHeight / 16 + 1)
                return false;
            width = mbWide * 16 - 2 * (cropLeft + cropRight);
            height = mbHigh * 16 - 2 * (cropTop + cropBottom);
            return width >= 16 && height >= 16 && width <= MaxWidth && height <= MaxHeight && (long)width * height <= MaxPixels;
        }
    }

    /// <summary>One decoded picture on its way to the screen: in memory (OpenH264) or on the graphics card.</summary>
    internal sealed class ViewerFrame : IDisposable
    {
        public I420Frame Software;
        public IntPtr Sample, Texture;
        public int Subresource, Width, Height;

        public void Dispose()
        {
            GpuCom.Release(Texture);
            GpuCom.Release(Sample);
            Texture = Sample = IntPtr.Zero;
        }
    }

    internal static class GpuCom
    {
        public delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);
        private delegate int ReleaseFn(IntPtr self);

        public static T Slot<T>(IntPtr com, int index) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(com);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, index * IntPtr.Size), typeof(T));
        }

        public static void Release(IntPtr com)
        {
            if (com != IntPtr.Zero)
                Slot<ReleaseFn>(com, 2)(com);
        }

        public static IntPtr Query(IntPtr com, Guid iid)
        {
            IntPtr result;
            return Slot<QueryInterfaceFn>(com, 0)(com, ref iid, out result) < 0 ? IntPtr.Zero : result;
        }

        public static void Check(int hr, string what)
        {
            if (hr < 0)
                throw new InvalidOperationException(what + " failed (0x" + hr.ToString("X8") + ")");
        }
    }

    /// <summary>
    /// Draws video with Direct3D 11: the graphics card converts colours, scales and presents, for
    /// frames from either decoder. The video is BT.709 limited range, as mæla's capture makes it.
    /// </summary>
    internal sealed class GpuVideoRenderer : IDisposable
    {
        private const int DXGI_FORMAT_B8G8R8A8 = 87, DXGI_FORMAT_NV12 = 103, DXGI_FORMAT_UNKNOWN = 0;
        private const int InputColorSpace = (1 << 2) | (1 << 4); // BT.709, 16-235.
        private const int OutputColorSpace = 0;                  // Full-range RGB.

        private static readonly Guid IID_VideoDevice = new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
        private static readonly Guid IID_VideoContext = new Guid("61F21C45-3C0E-4a74-9CEA-67100D9AD5E4");
        private static readonly Guid IID_Multithread = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
        private static Guid IID_Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [StructLayout(LayoutKind.Sequential)]
        private struct SwapChainDesc
        {
            public int Width, Height, RefreshN, RefreshD, Format, ScanlineOrder, Scaling, SampleCount, SampleQuality, Usage, BufferCount;
            public IntPtr Window;
            public int Windowed, SwapEffect, Flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Texture2DDesc
        {
            public int Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ContentDesc
        {
            public int FrameFormat, InRateN, InRateD, InWidth, InHeight, OutRateN, OutRateD, OutWidth, OutHeight, Usage;
        }
        [StructLayout(LayoutKind.Sequential)] private struct InputViewDesc { public int FourCC, Dimension, MipSlice, ArraySlice; }
        [StructLayout(LayoutKind.Sequential)] private struct OutputViewDesc { public int Dimension, MipSlice, Reserved0, Reserved1; }
        [StructLayout(LayoutKind.Sequential)]
        private struct StreamDesc
        {
            public int Enable, OutputIndex, InputFrameOrField, PastFrames, FutureFrames;
            public IntPtr Past, Input, Future, PastRight, InputRight, FutureRight;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Mapped { public IntPtr Data; public int RowPitch, DepthPitch; }

        private delegate int CreateTexture2DFn(IntPtr self, ref Texture2DDesc desc, IntPtr initial, out IntPtr texture);
        private delegate int MapFn(IntPtr self, IntPtr resource, int sub, int type, int flags, out Mapped mapped);
        private delegate void UnmapFn(IntPtr self, IntPtr resource, int sub);
        private delegate void CopyResourceFn(IntPtr self, IntPtr target, IntPtr source);
        private delegate int CreateEnumeratorFn(IntPtr self, ref ContentDesc desc, out IntPtr enumerator);
        private delegate int CreateProcessorFn(IntPtr self, IntPtr enumerator, int rateIndex, out IntPtr processor);
        private delegate int CreateInputViewFn(IntPtr self, IntPtr resource, IntPtr enumerator, ref InputViewDesc desc, out IntPtr view);
        private delegate int CreateOutputViewFn(IntPtr self, IntPtr resource, IntPtr enumerator, ref OutputViewDesc desc, out IntPtr view);
        private delegate void SetOutputTargetRectFn(IntPtr self, IntPtr processor, int enable, ref Rect rect);
        private delegate void SetOutputColorFn(IntPtr self, IntPtr processor, ref int colorSpace);
        private delegate void SetStreamFormatFn(IntPtr self, IntPtr processor, int stream, int format);
        private delegate void SetStreamColorFn(IntPtr self, IntPtr processor, int stream, ref int colorSpace);
        private delegate void SetStreamRectFn(IntPtr self, IntPtr processor, int stream, int enable, ref Rect rect);
        private delegate void SetStreamAutoFn(IntPtr self, IntPtr processor, int stream, int enable);
        private delegate int BltFn(IntPtr self, IntPtr processor, IntPtr view, int frame, int count, ref StreamDesc stream);
        private delegate int PresentFn(IntPtr self, int sync, int flags);
        private delegate int GetBufferFn(IntPtr self, int index, ref Guid iid, out IntPtr buffer);
        private delegate int ResizeBuffersFn(IntPtr self, int count, int width, int height, int format, int flags);
        private delegate int SetProtectedFn(IntPtr self, int on);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDeviceAndSwapChain(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdk, ref SwapChainDesc desc, out IntPtr chain, out IntPtr device, out int level, out IntPtr context);

        private IntPtr _device, _context, _chain, _videoDevice, _videoContext, _enumerator, _processor, _backBuffer, _outputView, _uploadStaging, _uploadTexture;
        private int _surfaceWidth, _surfaceHeight, _wantWidth, _wantHeight, _videoWidth, _videoHeight, _uploadWidth, _uploadHeight;
        private readonly object _sizeLock = new object();
        private bool _disposed;

        public IntPtr Device { get { return _device; } }

        /// <summary>Null if this PC can't draw this way (no graphics driver, some remote sessions).</summary>
        public static GpuVideoRenderer TryCreate(IntPtr window, int width, int height)
        {
            if (IntPtr.Size != 8)
                return null;
            GpuVideoRenderer renderer = new GpuVideoRenderer();
            try
            {
                renderer.Open(window, Math.Max(1, width), Math.Max(1, height));
                return renderer;
            }
            catch
            {
                renderer.Dispose();
                return null;
            }
        }

        private void Open(IntPtr window, int width, int height)
        {
            SwapChainDesc desc = new SwapChainDesc();
            desc.Width = width;
            desc.Height = height;
            desc.Format = DXGI_FORMAT_B8G8R8A8;
            desc.SampleCount = 1;
            desc.Usage = 0x20; // Render target.
            desc.BufferCount = 2;
            desc.Window = window;
            desc.Windowed = 1;
            desc.SwapEffect = 4; // Flip discard.
            int level;
            GpuCom.Check(D3D11CreateDeviceAndSwapChain(IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, 0x20 | 0x800 /* BGRA, video */, IntPtr.Zero, 0, 7, ref desc, out _chain, out _device, out level, out _context), "D3D11CreateDeviceAndSwapChain");
            _videoDevice = GpuCom.Query(_device, IID_VideoDevice);
            _videoContext = GpuCom.Query(_context, IID_VideoContext);
            if (_videoDevice == IntPtr.Zero || _videoContext == IntPtr.Zero)
                throw new InvalidOperationException("No video support.");
            // The decoder (reader thread) and drawing (player thread) share this device.
            IntPtr multithread = GpuCom.Query(_device, IID_Multithread);
            if (multithread != IntPtr.Zero)
            {
                GpuCom.Slot<SetProtectedFn>(multithread, 5)(multithread, 1);
                GpuCom.Release(multithread);
            }
            _surfaceWidth = _wantWidth = width;
            _surfaceHeight = _wantHeight = height;
        }

        /// <summary>The window changed size. Takes effect at the next frame.</summary>
        public void Resize(int width, int height)
        {
            lock (_sizeLock)
            {
                _wantWidth = width;
                _wantHeight = height;
            }
        }

        /// <summary>Draws one frame, keeping its shape, and shows it. Call from one thread.</summary>
        public void Render(ViewerFrame frame)
        {
            int wantWidth, wantHeight;
            lock (_sizeLock)
            {
                wantWidth = _wantWidth;
                wantHeight = _wantHeight;
            }
            if (wantWidth <= 0 || wantHeight <= 0)
                return; // Minimised.
            if (wantWidth != _surfaceWidth || wantHeight != _surfaceHeight)
            {
                ReleaseOutput();
                GpuCom.Check(GpuCom.Slot<ResizeBuffersFn>(_chain, 13)(_chain, 0, wantWidth, wantHeight, DXGI_FORMAT_UNKNOWN, 0), "ResizeBuffers");
                _surfaceWidth = wantWidth;
                _surfaceHeight = wantHeight;
            }
            if (frame.Width != _videoWidth || frame.Height != _videoHeight || _processor == IntPtr.Zero)
                OpenProcessor(frame.Width, frame.Height);
            if (_backBuffer == IntPtr.Zero)
            {
                GpuCom.Check(GpuCom.Slot<GetBufferFn>(_chain, 9)(_chain, 0, ref IID_Texture2D, out _backBuffer), "GetBuffer");
                OutputViewDesc outDesc = new OutputViewDesc();
                outDesc.Dimension = 1;
                GpuCom.Check(GpuCom.Slot<CreateOutputViewFn>(_videoDevice, 9)(_videoDevice, _backBuffer, _enumerator, ref outDesc, out _outputView), "CreateVideoProcessorOutputView");
            }

            IntPtr texture = frame.Texture;
            int slice = frame.Subresource;
            if (frame.Software != null)
            {
                texture = Upload(frame.Software);
                slice = 0;
            }

            InputViewDesc inDesc = new InputViewDesc();
            inDesc.Dimension = 1;
            inDesc.ArraySlice = slice;
            IntPtr view;
            GpuCom.Check(GpuCom.Slot<CreateInputViewFn>(_videoDevice, 8)(_videoDevice, texture, _enumerator, ref inDesc, out view), "CreateVideoProcessorInputView");
            try
            {
                // Letterbox: keep the picture's shape.
                double scale = Math.Min((double)_surfaceWidth / frame.Width, (double)_surfaceHeight / frame.Height);
                int dw = Math.Max(1, (int)(frame.Width * scale)), dh = Math.Max(1, (int)(frame.Height * scale));
                Rect source = new Rect();
                source.Right = frame.Width;
                source.Bottom = frame.Height;
                Rect dest = new Rect();
                dest.Left = (_surfaceWidth - dw) / 2;
                dest.Top = (_surfaceHeight - dh) / 2;
                dest.Right = dest.Left + dw;
                dest.Bottom = dest.Top + dh;
                Rect target = new Rect();
                target.Right = _surfaceWidth;
                target.Bottom = _surfaceHeight;
                GpuCom.Slot<SetOutputTargetRectFn>(_videoContext, 13)(_videoContext, _processor, 1, ref target);
                GpuCom.Slot<SetStreamRectFn>(_videoContext, 30)(_videoContext, _processor, 0, 1, ref source);
                GpuCom.Slot<SetStreamRectFn>(_videoContext, 31)(_videoContext, _processor, 0, 1, ref dest);

                StreamDesc stream = new StreamDesc();
                stream.Enable = 1;
                stream.Input = view;
                GpuCom.Check(GpuCom.Slot<BltFn>(_videoContext, 53)(_videoContext, _processor, _outputView, 0, 1, ref stream), "VideoProcessorBlt");
            }
            finally
            {
                GpuCom.Release(view);
            }
            if (HoldForTests)
                return;
            // No vsync wait: if frames come faster than the display, it shows the newest, without tearing.
            GpuCom.Check(GpuCom.Slot<PresentFn>(_chain, 8)(_chain, 0, 0), "Present");
        }

        private void OpenProcessor(int width, int height)
        {
            ReleaseOutput();
            GpuCom.Release(_processor);
            GpuCom.Release(_enumerator);
            _processor = _enumerator = IntPtr.Zero;
            ContentDesc content = new ContentDesc();
            content.InRateN = content.OutRateN = 60;
            content.InRateD = content.OutRateD = 1;
            content.InWidth = width;
            content.InHeight = height;
            content.OutWidth = Math.Max(_surfaceWidth, width);
            content.OutHeight = Math.Max(_surfaceHeight, height);
            GpuCom.Check(GpuCom.Slot<CreateEnumeratorFn>(_videoDevice, 10)(_videoDevice, ref content, out _enumerator), "CreateVideoProcessorEnumerator");
            GpuCom.Check(GpuCom.Slot<CreateProcessorFn>(_videoDevice, 4)(_videoDevice, _enumerator, 0, out _processor), "CreateVideoProcessor");
            int input = InputColorSpace, output = OutputColorSpace;
            GpuCom.Slot<SetOutputColorFn>(_videoContext, 15)(_videoContext, _processor, ref output);
            GpuCom.Slot<SetStreamFormatFn>(_videoContext, 27)(_videoContext, _processor, 0, 0);
            GpuCom.Slot<SetStreamColorFn>(_videoContext, 28)(_videoContext, _processor, 0, ref input);
            GpuCom.Slot<SetStreamAutoFn>(_videoContext, 37)(_videoContext, _processor, 0, 0); // No driver "enhancements" on screen content.
            _videoWidth = width;
            _videoHeight = height;
        }

        private unsafe IntPtr Upload(I420Frame frame)
        {
            int width = frame.Width, height = frame.Height;
            if (_uploadTexture == IntPtr.Zero || width != _uploadWidth || height != _uploadHeight)
            {
                GpuCom.Release(_uploadTexture);
                GpuCom.Release(_uploadStaging);
                _uploadTexture = _uploadStaging = IntPtr.Zero;
                Texture2DDesc desc = new Texture2DDesc();
                desc.Width = width;
                desc.Height = height;
                desc.MipLevels = 1;
                desc.ArraySize = 1;
                desc.Format = DXGI_FORMAT_NV12;
                desc.SampleCount = 1;
                desc.BindFlags = 0x20; // Render target: makes it a valid video processor input everywhere.
                CreateTexture2DFn create = GpuCom.Slot<CreateTexture2DFn>(_device, 5);
                GpuCom.Check(create(_device, ref desc, IntPtr.Zero, out _uploadTexture), "CreateTexture2D");
                desc.BindFlags = 0;
                desc.Usage = 3; // Staging.
                desc.CpuAccess = 0x10000; // Write.
                GpuCom.Check(create(_device, ref desc, IntPtr.Zero, out _uploadStaging), "CreateTexture2D");
                _uploadWidth = width;
                _uploadHeight = height;
            }
            Mapped mapped;
            GpuCom.Check(GpuCom.Slot<MapFn>(_context, 14)(_context, _uploadStaging, 0, 2 /* write */, 0, out mapped), "Map");
            try
            {
                byte* output = (byte*)mapped.Data;
                int pitch = mapped.RowPitch;
                fixed (byte* y = frame.Y, u = frame.U, v = frame.V)
                {
                    for (int row = 0; row < height; row++)
                        Buffer.MemoryCopy(y + row * width, output + row * pitch, pitch, width);
                    byte* chroma = output + height * pitch;
                    int chromaWidth = width / 2;
                    for (int row = 0; row < height / 2; row++)
                    {
                        byte* line = chroma + row * pitch;
                        byte* ur = u + row * chromaWidth;
                        byte* vr = v + row * chromaWidth;
                        for (int x = 0; x < chromaWidth; x++)
                        {
                            line[x * 2] = ur[x];
                            line[x * 2 + 1] = vr[x];
                        }
                    }
                }
            }
            finally
            {
                GpuCom.Slot<UnmapFn>(_context, 15)(_context, _uploadStaging, 0);
            }
            GpuCom.Slot<CopyResourceFn>(_context, 47)(_context, _uploadTexture, _uploadStaging);
            return _uploadTexture;
        }

        /// <summary>Tests only: don't present, because presenting discards the picture before it can be read.</summary>
        internal bool HoldForTests;

        /// <summary>Tests only: BGRA pixels of what was last drawn.</summary>
        internal byte[] ReadBackForTests(out int width, out int height)
        {
            width = _surfaceWidth;
            height = _surfaceHeight;
            Texture2DDesc desc = new Texture2DDesc();
            desc.Width = width;
            desc.Height = height;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8;
            desc.SampleCount = 1;
            desc.Usage = 3;
            desc.CpuAccess = 0x20000; // Read.
            IntPtr staging;
            GpuCom.Check(GpuCom.Slot<CreateTexture2DFn>(_device, 5)(_device, ref desc, IntPtr.Zero, out staging), "CreateTexture2D");
            try
            {
                GpuCom.Slot<CopyResourceFn>(_context, 47)(_context, staging, _backBuffer);
                Mapped mapped;
                GpuCom.Check(GpuCom.Slot<MapFn>(_context, 14)(_context, staging, 0, 1 /* read */, 0, out mapped), "Map");
                byte[] pixels = new byte[width * height * 4];
                for (int row = 0; row < height; row++)
                    Marshal.Copy(new IntPtr(mapped.Data.ToInt64() + row * mapped.RowPitch), pixels, row * width * 4, width * 4);
                GpuCom.Slot<UnmapFn>(_context, 15)(_context, staging, 0);
                return pixels;
            }
            finally
            {
                GpuCom.Release(staging);
            }
        }

        private void ReleaseOutput()
        {
            GpuCom.Release(_outputView);
            GpuCom.Release(_backBuffer);
            _outputView = _backBuffer = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                ReleaseOutput();
                GpuCom.Release(_uploadTexture);
                GpuCom.Release(_uploadStaging);
                GpuCom.Release(_processor);
                GpuCom.Release(_enumerator);
                GpuCom.Release(_videoContext);
                GpuCom.Release(_videoDevice);
                GpuCom.Release(_chain);
                GpuCom.Release(_context);
                GpuCom.Release(_device);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// H.264 decoding on the graphics card, through Windows' own decoder. Pictures stay on the
    /// card and go straight to the renderer. Runs in the locked-down viewer, and everything goes
    /// through <see cref="H264StreamCheck"/> first. Throws if the card can't do it; the viewer then
    /// falls back to OpenH264.
    /// </summary>
    internal sealed class GpuH264Decoder : IDisposable
    {
        private const int MFT_ENUM_FLAG_SYNCMFT = 1, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
        private const int MFT_MESSAGE_COMMAND_FLUSH = 0, MFT_MESSAGE_SET_D3D_MANAGER = 2, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000, MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001, MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
        private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72), MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        private const int OutputProvidesSamples = 0x100 | 0x200;

        private static Guid MFT_CATEGORY_VIDEO_DECODER = new Guid("d6c02d4b-6833-45b4-971a-05a4b04bab91");
        private static Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        private static Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        private static Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static Guid MF_SA_D3D11_AWARE = new Guid("206B4FC8-FCF9-4C51-AFE3-9764369E33A0");
        private static Guid MF_LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        private static Guid MF_SA_MINIMUM_OUTPUT_SAMPLE_COUNT = new Guid("851745d5-c3d6-476d-9527-498ef2d10d18");
        private static Guid IID_IMFTransform = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");
        private static Guid IID_Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private static readonly Guid IID_IMFDXGIBuffer = new Guid("e7174cfa-1c9e-48b1-8866-626226bfc258");

        [StructLayout(LayoutKind.Sequential)] private struct RegisterTypeInfo { public Guid Major, Sub; }
        [StructLayout(LayoutKind.Sequential)] private struct StreamInfo { public int Flags, Size, Alignment; }
        [StructLayout(LayoutKind.Sequential)] private struct OutputDataBuffer { public int StreamId; public IntPtr Sample; public int Status; public IntPtr Events; }

        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll")] private static extern int MFCreateDXGIDeviceManager(out uint token, out IntPtr manager);
        [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, int flags, ref RegisterTypeInfo input, IntPtr output, out IntPtr activates, out int count);
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(int length, out IntPtr buffer);
        [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr memory);

        private delegate int ResetDeviceFn(IntPtr self, IntPtr device, uint token);
        private delegate int ActivateObjectFn(IntPtr self, ref Guid iid, out IntPtr result);
        private delegate int GetAttributesFn(IntPtr self, out IntPtr attributes);
        private delegate int GetUInt32Fn(IntPtr self, ref Guid key, out int value);
        private delegate int SetUInt32Fn(IntPtr self, ref Guid key, int value);
        private delegate int SetUInt64Fn(IntPtr self, ref Guid key, long value);
        private delegate int SetGuidFn(IntPtr self, ref Guid key, ref Guid value);
        private delegate int GetGuidFn(IntPtr self, ref Guid key, out Guid value);
        private delegate int ProcessMessageFn(IntPtr self, int message, IntPtr parameter);
        private delegate int SetTypeFn(IntPtr self, int stream, IntPtr type, int flags);
        private delegate int GetAvailableTypeFn(IntPtr self, int stream, int index, out IntPtr type);
        private delegate int GetOutputInfoFn(IntPtr self, int stream, out StreamInfo info);
        private delegate int ProcessInputFn(IntPtr self, int stream, IntPtr sample, int flags);
        private delegate int ProcessOutputFn(IntPtr self, int flags, int count, ref OutputDataBuffer buffer, out int status);
        private delegate int LockFn(IntPtr self, out IntPtr data, out int max, out int current);
        private delegate int NoArgsFn(IntPtr self);
        private delegate int SetLengthFn(IntPtr self, int length);
        private delegate int AddBufferFn(IntPtr self, IntPtr buffer);
        private delegate int GetBufferByIndexFn(IntPtr self, int index, out IntPtr buffer);
        private delegate int SetSampleLongFn(IntPtr self, long value);
        private delegate int GetResourceFn(IntPtr self, ref Guid iid, out IntPtr resource);
        private delegate int GetSubresourceFn(IntPtr self, out int index);

        private IntPtr _manager, _transform, _activate;
        private ProcessInputFn _processInput;
        private ProcessOutputFn _processOutput;
        private StreamInfo _outputInfo;
        private bool _mfStarted, _disposed;
        private int _width, _height;
        private long _time;

        public string Name { get; private set; }

        public GpuH264Decoder(IntPtr device)
        {
            try
            {
                Open(device);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Open(IntPtr device)
        {
            GpuCom.Check(MFStartup(0x00020070, 1), "MFStartup");
            _mfStarted = true;
            uint token;
            GpuCom.Check(MFCreateDXGIDeviceManager(out token, out _manager), "MFCreateDXGIDeviceManager");
            GpuCom.Check(GpuCom.Slot<ResetDeviceFn>(_manager, 7)(_manager, device, token), "ResetDevice");

            RegisterTypeInfo input = new RegisterTypeInfo();
            input.Major = MFMediaType_Video;
            input.Sub = MFVideoFormat_H264;
            IntPtr activates;
            int count;
            GpuCom.Check(MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, ref input, IntPtr.Zero, out activates, out count), "MFTEnumEx");
            if (count == 0)
                throw new InvalidOperationException("No H.264 decoder.");
            for (int i = 0; i < count; i++)
            {
                IntPtr one = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                if (i == 0)
                    _activate = one;
                else
                    GpuCom.Release(one);
            }
            CoTaskMemFree(activates);

            Guid iid = IID_IMFTransform;
            GpuCom.Check(GpuCom.Slot<ActivateObjectFn>(_activate, 33)(_activate, ref iid, out _transform), "ActivateObject");
            IntPtr attributes;
            GpuCom.Check(GpuCom.Slot<GetAttributesFn>(_transform, 8)(_transform, out attributes), "GetAttributes");
            try
            {
                int aware;
                if (GpuCom.Slot<GetUInt32Fn>(attributes, 7)(attributes, ref MF_SA_D3D11_AWARE, out aware) < 0 || aware == 0)
                    throw new InvalidOperationException("The decoder can't use the graphics card.");
                GpuCom.Slot<SetUInt32Fn>(attributes, 21)(attributes, ref MF_LOW_LATENCY, 1);
                // The playout buffer holds on to pictures, so the decoder needs spares.
                GpuCom.Slot<SetUInt32Fn>(attributes, 21)(attributes, ref MF_SA_MINIMUM_OUTPUT_SAMPLE_COUNT, 24);
            }
            finally { GpuCom.Release(attributes); }
            GpuCom.Check(GpuCom.Slot<ProcessMessageFn>(_transform, 23)(_transform, MFT_MESSAGE_SET_D3D_MANAGER, _manager), "Attach graphics card");
            _processInput = GpuCom.Slot<ProcessInputFn>(_transform, 24);
            _processOutput = GpuCom.Slot<ProcessOutputFn>(_transform, 25);
            Name = "Graphics card";
        }

        private void Configure(int width, int height)
        {
            ProcessMessageFn message = GpuCom.Slot<ProcessMessageFn>(_transform, 23);
            if (_width != 0)
                message(_transform, MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
            IntPtr type;
            GpuCom.Check(MFCreateMediaType(out type), "MFCreateMediaType");
            try
            {
                GpuCom.Slot<SetGuidFn>(type, 24)(type, ref MF_MT_MAJOR_TYPE, ref MFMediaType_Video);
                GpuCom.Slot<SetGuidFn>(type, 24)(type, ref MF_MT_SUBTYPE, ref MFVideoFormat_H264);
                GpuCom.Slot<SetUInt64Fn>(type, 22)(type, ref MF_MT_FRAME_SIZE, ((long)width << 32) | (uint)height);
                GpuCom.Check(GpuCom.Slot<SetTypeFn>(_transform, 15)(_transform, 0, type, 0), "SetInputType");
            }
            finally { GpuCom.Release(type); }
            SetOutputType();
            GpuCom.Check(message(_transform, MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero), "Flush");
            GpuCom.Check(message(_transform, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero), "Begin streaming");
            GpuCom.Check(message(_transform, MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero), "Start of stream");
            _width = width;
            _height = height;
        }

        private void SetOutputType()
        {
            for (int i = 0; i < 64; i++)
            {
                IntPtr type;
                GpuCom.Check(GpuCom.Slot<GetAvailableTypeFn>(_transform, 14)(_transform, 0, i, out type), "GetOutputAvailableType");
                try
                {
                    Guid subtype;
                    if (GpuCom.Slot<GetGuidFn>(type, 10)(type, ref MF_MT_SUBTYPE, out subtype) >= 0 && subtype == MFVideoFormat_NV12)
                    {
                        GpuCom.Check(GpuCom.Slot<SetTypeFn>(_transform, 16)(_transform, 0, type, 0), "SetOutputType");
                        GpuCom.Check(GpuCom.Slot<GetOutputInfoFn>(_transform, 7)(_transform, 0, out _outputInfo), "GetOutputStreamInfo");
                        return;
                    }
                }
                finally { GpuCom.Release(type); }
            }
            throw new InvalidOperationException("The decoder doesn't offer NV12.");
        }

        /// <summary>
        /// Decodes one checked access unit at the given size. Returns the pictures that came out
        /// (usually one). Throws if the graphics card can't decode it.
        /// </summary>
        public void Decode(byte[] data, int width, int height, List<ViewerFrame> output)
        {
            if (width != _width || height != _height)
                Configure(width, height);

            IntPtr sample, buffer;
            GpuCom.Check(MFCreateSample(out sample), "MFCreateSample");
            try
            {
                GpuCom.Check(MFCreateMemoryBuffer(data.Length, out buffer), "MFCreateMemoryBuffer");
                try
                {
                    IntPtr memory;
                    int max, current;
                    GpuCom.Check(GpuCom.Slot<LockFn>(buffer, 3)(buffer, out memory, out max, out current), "Lock");
                    Marshal.Copy(data, 0, memory, data.Length);
                    GpuCom.Slot<NoArgsFn>(buffer, 4)(buffer);
                    GpuCom.Check(GpuCom.Slot<SetLengthFn>(buffer, 6)(buffer, data.Length), "SetCurrentLength");
                    GpuCom.Check(GpuCom.Slot<AddBufferFn>(sample, 42)(sample, buffer), "AddBuffer");
                }
                finally { GpuCom.Release(buffer); }
                GpuCom.Slot<SetSampleLongFn>(sample, 36)(sample, _time);
                _time += 166666;
                GpuCom.Check(_processInput(_transform, 0, sample, 0), "ProcessInput");
            }
            finally { GpuCom.Release(sample); }

            while (true)
            {
                OutputDataBuffer out1 = new OutputDataBuffer();
                if ((_outputInfo.Flags & OutputProvidesSamples) == 0)
                    throw new InvalidOperationException("The decoder isn't using the graphics card."); // It wants memory buffers: software.
                int status;
                int hr = _processOutput(_transform, 0, 1, ref out1, out status);
                GpuCom.Release(out1.Events);
                if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
                    return;
                if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    GpuCom.Release(out1.Sample);
                    SetOutputType();
                    continue;
                }
                GpuCom.Check(hr, "ProcessOutput");
                if (out1.Sample != IntPtr.Zero)
                    output.Add(Wrap(out1.Sample));
            }
        }

        private ViewerFrame Wrap(IntPtr sample)
        {
            ViewerFrame frame = new ViewerFrame();
            frame.Sample = sample; // It now owns the reference, which keeps the picture from being reused.
            frame.Width = _width;
            frame.Height = _height;
            IntPtr buffer;
            bool ok = false;
            try
            {
                GpuCom.Check(GpuCom.Slot<GetBufferByIndexFn>(sample, 40)(sample, 0, out buffer), "GetBufferByIndex");
                try
                {
                    IntPtr dxgi = GpuCom.Query(buffer, IID_IMFDXGIBuffer);
                    if (dxgi == IntPtr.Zero)
                        throw new InvalidOperationException("The decoder isn't using the graphics card.");
                    try
                    {
                        GpuCom.Check(GpuCom.Slot<GetResourceFn>(dxgi, 3)(dxgi, ref IID_Texture2D, out frame.Texture), "GetResource");
                        GpuCom.Check(GpuCom.Slot<GetSubresourceFn>(dxgi, 4)(dxgi, out frame.Subresource), "GetSubresourceIndex");
                    }
                    finally { GpuCom.Release(dxgi); }
                }
                finally { GpuCom.Release(buffer); }
                ok = true;
                return frame;
            }
            finally
            {
                if (!ok)
                    frame.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_transform != IntPtr.Zero && _width != 0)
                    GpuCom.Slot<ProcessMessageFn>(_transform, 23)(_transform, MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
                GpuCom.Release(_transform);
                if (_activate != IntPtr.Zero)
                {
                    GpuCom.Slot<NoArgsFn>(_activate, 34)(_activate);
                    GpuCom.Release(_activate);
                }
                GpuCom.Release(_manager);
                if (_mfStarted)
                    MFShutdown();
            }
            catch
            {
            }
        }
    }
}
