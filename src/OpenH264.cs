using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Maela
{
    /// <summary>
    /// Cisco's OpenH264 (BSD 2-clause), used to encode and decode screen shares.
    ///
    /// The ready-made DLL is downloaded the first time it's needed from Cisco's own server
    /// (ciscobinary.openh264.org), never bundled: Cisco pays the H.264 patent royalties for
    /// copies obtained that way. The compressed download and the DLL inside must both match
    /// SHA-256 hashes built into mæla, or nothing is installed or loaded.
    /// </summary>
    internal static class OpenH264Pack
    {
        public const string Version = "2.4.1";
        public const string License = "BSD 2-clause; H.264 patent licence paid by Cisco";
        private const string BaseUrl = "http://ciscobinary.openh264.org/";

        // Pinned hashes (lower-case hex) of Cisco's files for this version.
        internal static string Win64Bz2Sha256 = "c0df66e90d46c688558d5697c845886839c918b8253b86425bcde6be0d871f13";
        internal static string Win64DllSha256 = "081b0c081480d177cbfddfbc90b1613640e702f875897b30d8de195cde73dd34"; // 974,944 bytes
        // Screen sharing needs 64-bit Windows; 32-bit hashes aren't pinned.
        internal static string Win32Bz2Sha256 = "";
        internal static string Win32DllSha256 = "";

        private static bool Is64 { get { return IntPtr.Size == 8; } }
        public static string FileName { get { return "openh264-" + Version + (Is64 ? "-win64" : "-win32") + ".dll"; } }
        public static string DownloadUrl { get { return BaseUrl + FileName + ".bz2"; } }
        private static string Bz2Hash { get { return Is64 ? Win64Bz2Sha256 : Win32Bz2Sha256; } }
        private static string DllHash { get { return Is64 ? Win64DllSha256 : Win32DllSha256; } }

        public static string InstalledPath
        {
            get { return Path.Combine(Path.Combine(AssetStore.Root, "tools"), FileName); }
        }

        /// <summary>True if the DLL is installed and still matches its pinned hash.</summary>
        public static bool Verify()
        {
            try
            {
                string path = InstalledPath;
                return File.Exists(path) && DllHash.Length == 64 && Sha256Hex(File.ReadAllBytes(path)) == DllHash;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Downloads, checks and installs the DLL. Throws with a readable message on failure.</summary>
        public static void Install()
        {
            if (!Is64)
                throw new InvalidOperationException("Screen sharing needs 64-bit Windows.");
            if (Bz2Hash.Length != 64 || DllHash.Length != 64)
                throw new InvalidOperationException("This version of mæla doesn't know the expected OpenH264 checksums.");
            byte[] compressed;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(DownloadUrl);
            request.UserAgent = "Maela/" + Updater.CurrentVersionText;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream memory = new MemoryStream())
            {
                byte[] buffer = new byte[65536];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    memory.Write(buffer, 0, read);
                    if (memory.Length > 8 * 1024 * 1024)
                        throw new InvalidOperationException("The OpenH264 download is larger than expected.");
                }
                compressed = memory.ToArray();
            }
            if (Sha256Hex(compressed) != Bz2Hash)
                throw new InvalidOperationException("The OpenH264 download didn't match its checksum, so it wasn't used.");
            byte[] dll = Bzip2.Decompress(compressed, 16 * 1024 * 1024);
            if (Sha256Hex(dll) != DllHash)
                throw new InvalidOperationException("The unpacked OpenH264 library didn't match its checksum, so it wasn't used.");
            string path = InstalledPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, dll);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>Tests and setup: the download's hashes, to pin a new version.</summary>
        internal static string[] DescribeDownload()
        {
            using (WebClient client = new WebClient())
            {
                byte[] compressed = client.DownloadData(DownloadUrl);
                byte[] dll = Bzip2.Decompress(compressed, 16 * 1024 * 1024);
                return new string[] { Sha256Hex(compressed), Sha256Hex(dll), dll.Length.ToString() };
            }
        }

        internal static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", String.Empty).ToLowerInvariant();
        }

        private static IntPtr _module = IntPtr.Zero;
        private static readonly object LoadLock = new object();

        /// <summary>Loads the verified DLL (once). Returns false if it isn't installed or doesn't verify.</summary>
        public static bool Load()
        {
            lock (LoadLock)
            {
                if (_module != IntPtr.Zero)
                    return true;
                if (!Verify())
                    return false;
                _module = NativeLoader.LoadLibrary(InstalledPath);
                return _module != IntPtr.Zero;
            }
        }

        internal static T Function<T>(string name) where T : class
        {
            IntPtr p = NativeLoader.GetProcAddress(_module, name);
            if (p == IntPtr.Zero)
                throw new InvalidOperationException("OpenH264 is missing " + name + ".");
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        /// <summary>Calls slot <paramref name="index"/> of a C++ interface's function table.</summary>
        internal static T Slot<T>(IntPtr instance, int index) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(instance);
            IntPtr fn = Marshal.ReadIntPtr(table, index * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }
    }

    internal static class NativeLoader
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern IntPtr GetProcAddress(IntPtr module, string name);
    }

    /// <summary>One frame in I420 (Y plane, then quarter-size U and V planes).</summary>
    internal sealed class I420Frame
    {
        public readonly int Width, Height;
        public readonly byte[] Y, U, V;

        public I420Frame(int width, int height)
        {
            Width = width & ~1;
            Height = height & ~1;
            Y = new byte[Width * Height];
            U = new byte[(Width / 2) * (Height / 2)];
            V = new byte[(Width / 2) * (Height / 2)];
        }
    }

    /// <summary>Encodes I420 frames to H.264 (Annex B) with OpenH264, tuned for screen content and low delay.</summary>
    internal sealed class H264Encoder : IVideoEncoder
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateEncoderFn(out IntPtr encoder);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DestroyEncoderFn(IntPtr encoder);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeFn(IntPtr self, ref EncParamBase param);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UninitializeFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EncodeFrameFn(IntPtr self, ref SourcePicture picture, IntPtr bitstream);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ForceIntraFn(IntPtr self, int idr, int layer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetBitrateOptionFn(IntPtr self, int option, ref BitrateInfo info);

        [StructLayout(LayoutKind.Sequential)]
        private struct BitrateInfo
        {
            public int Layer;          // 4 = all layers
            public int Bitrate;        // bits per second
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EncParamBase
        {
            public int UsageType;      // 1 = screen content, real time
            public int Width;
            public int Height;
            public int TargetBitrate;  // bits per second
            public int RcMode;         // 1 = bitrate mode
            public float MaxFrameRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SourcePicture
        {
            public int ColorFormat;    // 23 = I420
            public int Stride0, Stride1, Stride2, Stride3;
            public IntPtr Data0, Data1, Data2, Data3;
            public int Width;
            public int Height;
            public long TimeStamp;
        }

        private const int BitstreamInfoBytes = 8192;
        private readonly IntPtr _encoder;
        private readonly IntPtr _bitstream;
        private readonly EncodeFrameFn _encode;
        private readonly ForceIntraFn _forceIntra;
        private readonly SetBitrateOptionFn _setOption;
        private readonly UninitializeFn _uninitialize;
        private readonly DestroyEncoderFn _destroy;
        private readonly int _width, _height;
        private bool _disposed;

        public int Width { get { return _width; } }
        public int Height { get { return _height; } }
        public string Name { get { return "OpenH264 (processor)"; } }

        public H264Encoder(int width, int height, int fps, int bitrateKbps)
        {
            if (!OpenH264Pack.Load())
                throw new InvalidOperationException("OpenH264 isn't installed.");
            _width = width & ~1;
            _height = height & ~1;
            CreateEncoderFn create = OpenH264Pack.Function<CreateEncoderFn>("WelsCreateSVCEncoder");
            _destroy = OpenH264Pack.Function<DestroyEncoderFn>("WelsDestroySVCEncoder");
            IntPtr encoder;
            if (create(out encoder) != 0 || encoder == IntPtr.Zero)
                throw new InvalidOperationException("OpenH264 couldn't create an encoder.");
            _encoder = encoder;
            InitializeFn initialize = OpenH264Pack.Slot<InitializeFn>(encoder, 0);
            _uninitialize = OpenH264Pack.Slot<UninitializeFn>(encoder, 3);
            _encode = OpenH264Pack.Slot<EncodeFrameFn>(encoder, 4);
            _forceIntra = OpenH264Pack.Slot<ForceIntraFn>(encoder, 6);
            _setOption = OpenH264Pack.Slot<SetBitrateOptionFn>(encoder, 7);

            EncParamBase param = new EncParamBase();
            param.UsageType = 1;
            param.Width = _width;
            param.Height = _height;
            param.TargetBitrate = Math.Max(300, bitrateKbps) * 1000;
            param.RcMode = 1;
            param.MaxFrameRate = fps;
            int rc = initialize(encoder, ref param);
            if (rc != 0)
            {
                _destroy(encoder);
                throw new InvalidOperationException("OpenH264 rejected the settings (" + rc + ").");
            }
            _bitstream = Marshal.AllocHGlobal(BitstreamInfoBytes);
        }

        public void ForceKeyframe()
        {
            if (!_disposed)
                _forceIntra(_encoder, 1, -1);
        }

        public bool SetBitrate(int kbps)
        {
            if (_disposed)
                return false;
            BitrateInfo info = new BitrateInfo();
            info.Layer = 4;
            info.Bitrate = Math.Max(300, kbps) * 1000;
            return _setOption(_encoder, 5 /* ENCODER_OPTION_BITRATE */, ref info) == 0;
        }

        /// <summary>Encodes one frame. Returns Annex B bytes (or null if the encoder skipped it).</summary>
        public byte[] Encode(I420Frame frame, long timestampMs, out bool keyframe)
        {
            keyframe = false;
            if (_disposed || frame.Width != _width || frame.Height != _height)
                return null;
            GCHandle y = GCHandle.Alloc(frame.Y, GCHandleType.Pinned);
            GCHandle u = GCHandle.Alloc(frame.U, GCHandleType.Pinned);
            GCHandle v = GCHandle.Alloc(frame.V, GCHandleType.Pinned);
            try
            {
                SourcePicture picture = new SourcePicture();
                picture.ColorFormat = 23;
                picture.Stride0 = _width;
                picture.Stride1 = _width / 2;
                picture.Stride2 = _width / 2;
                picture.Data0 = y.AddrOfPinnedObject();
                picture.Data1 = u.AddrOfPinnedObject();
                picture.Data2 = v.AddrOfPinnedObject();
                picture.Width = _width;
                picture.Height = _height;
                picture.TimeStamp = timestampMs;
                for (int i = 0; i < BitstreamInfoBytes; i += 8)
                    Marshal.WriteInt64(_bitstream, i, 0);
                int rc = _encode(_encoder, ref picture, _bitstream);
                if (rc != 0)
                    return null;
                return ReadBitstream(out keyframe);
            }
            finally
            {
                y.Free(); u.Free(); v.Free();
            }
        }

        /// <summary>Reads SFrameBSInfo: layers, each a run of NAL units (with start codes) in one buffer.</summary>
        private byte[] ReadBitstream(out bool keyframe)
        {
            bool x64 = IntPtr.Size == 8;
            int layerSize = x64 ? 40 : 28;
            int layersAt = x64 ? 8 : 4;
            int frameTypeAt = layersAt + 128 * layerSize;
            int layers = Marshal.ReadInt32(_bitstream, 0);
            int frameType = Marshal.ReadInt32(_bitstream, frameTypeAt);
            keyframe = frameType == 1 || frameType == 2; // IDR or I.
            if (layers <= 0 || layers > 128 || frameType == 4 || frameType == 0)
                return null; // Skipped or invalid.
            MemoryStream output = new MemoryStream();
            for (int l = 0; l < layers; l++)
            {
                int at = layersAt + l * layerSize;
                int nalCount = Marshal.ReadInt32(_bitstream, at + 16);
                IntPtr lengths = Marshal.ReadIntPtr(_bitstream, at + (x64 ? 24 : 20));
                IntPtr data = Marshal.ReadIntPtr(_bitstream, at + (x64 ? 32 : 24));
                if (nalCount <= 0 || nalCount > 4096 || lengths == IntPtr.Zero || data == IntPtr.Zero)
                    continue;
                int total = 0;
                for (int n = 0; n < nalCount; n++)
                    total += Marshal.ReadInt32(lengths, n * 4);
                if (total <= 0 || total > 32 * 1024 * 1024)
                    continue;
                byte[] bytes = new byte[total];
                Marshal.Copy(data, bytes, 0, total);
                output.Write(bytes, 0, total);
            }
            return output.Length == 0 ? null : output.ToArray();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _uninitialize(_encoder); } catch { }
            try { _destroy(_encoder); } catch { }
            Marshal.FreeHGlobal(_bitstream);
        }
    }

    /// <summary>Decodes H.264 (Annex B) to I420 with OpenH264. Runs in the locked-down viewer process.</summary>
    internal sealed class H264Decoder : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateDecoderFn(out IntPtr decoder);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DestroyDecoderFn(IntPtr decoder);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeFn(IntPtr self, IntPtr param);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UninitializeFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DecodeNoDelayFn(IntPtr self, IntPtr source, int length, IntPtr planes, IntPtr info);

        private readonly IntPtr _decoder;
        private readonly IntPtr _info;
        private readonly IntPtr _planes;
        private readonly DecodeNoDelayFn _decode;
        private readonly UninitializeFn _uninitialize;
        private readonly DestroyDecoderFn _destroy;
        private bool _disposed;

        public H264Decoder()
        {
            if (!OpenH264Pack.Load())
                throw new InvalidOperationException("OpenH264 isn't installed.");
            CreateDecoderFn create = OpenH264Pack.Function<CreateDecoderFn>("WelsCreateDecoder");
            _destroy = OpenH264Pack.Function<DestroyDecoderFn>("WelsDestroyDecoder");
            IntPtr decoder;
            if (create(out decoder) != 0 || decoder == IntPtr.Zero)
                throw new InvalidOperationException("OpenH264 couldn't create a decoder.");
            _decoder = decoder;
            InitializeFn initialize = OpenH264Pack.Slot<InitializeFn>(decoder, 0);
            _uninitialize = OpenH264Pack.Slot<UninitializeFn>(decoder, 1);
            _decode = OpenH264Pack.Slot<DecodeNoDelayFn>(decoder, 3);

            // SDecodingParam: AVC bitstream, error concealment off (mæla waits for keyframes instead).
            bool x64 = IntPtr.Size == 8;
            IntPtr param = Marshal.AllocHGlobal(64);
            for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(param, i, 0);
            Marshal.WriteByte(param, x64 ? 12 : 8, 0xFF);           // uiTargetDqLayer
            Marshal.WriteInt32(param, x64 ? 24 : 20, 8);            // sVideoProperty.size
            Marshal.WriteInt32(param, x64 ? 28 : 24, 0);            // VIDEO_BITSTREAM_AVC
            int rc = initialize(decoder, param);
            Marshal.FreeHGlobal(param);
            if (rc != 0)
            {
                _destroy(decoder);
                throw new InvalidOperationException("OpenH264 couldn't start the decoder (" + rc + ").");
            }
            _info = Marshal.AllocHGlobal(256);
            _planes = Marshal.AllocHGlobal(IntPtr.Size * 3);
        }

        /// <summary>Decodes one frame. Returns the picture, or null if none is ready (or the data was bad).</summary>
        public I420Frame Decode(byte[] annexB)
        {
            if (_disposed || annexB == null || annexB.Length == 0)
                return null;
            for (int i = 0; i < 256; i += 4) Marshal.WriteInt32(_info, i, 0);
            for (int i = 0; i < 3; i++) Marshal.WriteIntPtr(_planes, i * IntPtr.Size, IntPtr.Zero);
            GCHandle source = GCHandle.Alloc(annexB, GCHandleType.Pinned);
            int state;
            try { state = _decode(_decoder, source.AddrOfPinnedObject(), annexB.Length, _planes, _info); }
            finally { source.Free(); }
            if (state != 0 || Marshal.ReadInt32(_info, 0) != 1)
                return null;

            bool x64 = IntPtr.Size == 8;
            int width = Marshal.ReadInt32(_info, 24), height = Marshal.ReadInt32(_info, 28);
            int strideY = Marshal.ReadInt32(_info, 36), strideUV = Marshal.ReadInt32(_info, 40);
            int planesAt = x64 ? 48 : 44;
            IntPtr y = Marshal.ReadIntPtr(_info, planesAt), u = Marshal.ReadIntPtr(_info, planesAt + IntPtr.Size), v = Marshal.ReadIntPtr(_info, planesAt + 2 * IntPtr.Size);
            if (width < 2 || height < 2 || width > 8192 || height > 8192 || strideY < width || strideUV < width / 2 || y == IntPtr.Zero || u == IntPtr.Zero || v == IntPtr.Zero)
                return null;
            I420Frame frame = new I420Frame(width, height);
            for (int row = 0; row < frame.Height; row++)
                Marshal.Copy(new IntPtr(y.ToInt64() + (long)row * strideY), frame.Y, row * frame.Width, frame.Width);
            int cw = frame.Width / 2;
            for (int row = 0; row < frame.Height / 2; row++)
            {
                Marshal.Copy(new IntPtr(u.ToInt64() + (long)row * strideUV), frame.U, row * cw, cw);
                Marshal.Copy(new IntPtr(v.ToInt64() + (long)row * strideUV), frame.V, row * cw, cw);
            }
            return frame;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _uninitialize(_decoder); } catch { }
            try { _destroy(_decoder); } catch { }
            Marshal.FreeHGlobal(_info);
            Marshal.FreeHGlobal(_planes);
        }
    }

    /// <summary>Colour conversion between screen pixels (BGRA) and video (I420, BT.709 limited range).</summary>
    internal static unsafe class ColorConvert
    {
        /// <summary>BGRA (any stride) to I420, averaging each 2x2 block for colour.</summary>
        public static void BgraToI420(IntPtr bgra, int stride, I420Frame frame)
        {
            int w = frame.Width, h = frame.Height, cw = w / 2;
            byte* src = (byte*)bgra.ToPointer();
            fixed (byte* yp = frame.Y, up = frame.U, vp = frame.V)
            {
                for (int row = 0; row < h; row += 2)
                {
                    byte* r0 = src + (long)row * stride;
                    byte* r1 = r0 + stride;
                    byte* y0 = yp + row * w;
                    byte* y1 = y0 + w;
                    byte* uo = up + (row / 2) * cw;
                    byte* vo = vp + (row / 2) * cw;
                    for (int x = 0; x < w; x += 2)
                    {
                        int b00 = r0[0], g00 = r0[1], r00 = r0[2];
                        int b01 = r0[4], g01 = r0[5], r01 = r0[6];
                        int b10 = r1[0], g10 = r1[1], r10 = r1[2];
                        int b11 = r1[4], g11 = r1[5], r11 = r1[6];
                        y0[0] = (byte)((47 * r00 + 157 * g00 + 16 * b00 + 4096 + 128) >> 8);
                        y0[1] = (byte)((47 * r01 + 157 * g01 + 16 * b01 + 4096 + 128) >> 8);
                        y1[0] = (byte)((47 * r10 + 157 * g10 + 16 * b10 + 4096 + 128) >> 8);
                        y1[1] = (byte)((47 * r11 + 157 * g11 + 16 * b11 + 4096 + 128) >> 8);
                        int r = r00 + r01 + r10 + r11, g = g00 + g01 + g10 + g11, b = b00 + b01 + b10 + b11;
                        uo[0] = (byte)((-26 * r - 87 * g + 112 * b + 4 * 32768 + 512) >> 10);
                        vo[0] = (byte)((112 * r - 102 * g - 10 * b + 4 * 32768 + 512) >> 10);
                        r0 += 8; r1 += 8; y0 += 2; y1 += 2; uo++; vo++;
                    }
                }
            }
        }

        /// <summary>I420 to BGRA (stride = width * 4).</summary>
        public static void I420ToBgra(I420Frame frame, IntPtr bgra, int stride)
        {
            int w = frame.Width, h = frame.Height, cw = w / 2;
            byte* dst = (byte*)bgra.ToPointer();
            fixed (byte* yp = frame.Y, up = frame.U, vp = frame.V)
            {
                for (int row = 0; row < h; row++)
                {
                    byte* o = dst + (long)row * stride;
                    byte* yr = yp + row * w;
                    byte* ur = up + (row / 2) * cw;
                    byte* vr = vp + (row / 2) * cw;
                    for (int x = 0; x < w; x++)
                    {
                        int c = 298 * (yr[x] - 16);
                        int d = ur[x >> 1] - 128, e = vr[x >> 1] - 128;
                        int r = (c + 459 * e + 128) >> 8;
                        int g = (c - 55 * d - 136 * e + 128) >> 8;
                        int b = (c + 541 * d + 128) >> 8;
                        o[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        o[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        o[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        o[3] = 255;
                        o += 4;
                    }
                }
            }
        }
    }
}
