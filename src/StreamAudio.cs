using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
#if !MAELA_STUB_CODEC
using Concentus.Enums;
using Concentus.Structs;
#endif

namespace Maela
{
    /// <summary>Stereo Opus for the sound of a shared screen or window: 20 ms frames, music quality.</summary>
    internal sealed class StreamAudioEncoder
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;
        public const int FrameSamples = 960; // Per channel: 20 ms.
        public const int Bitrate = 128000;
        public const int MaxPacketBytes = 900;

#if !MAELA_STUB_CODEC
        private readonly OpusEncoder _opus;
#endif
        private readonly byte[] _packet = new byte[MaxPacketBytes];

        public StreamAudioEncoder()
        {
#if !MAELA_STUB_CODEC
            _opus = OpusEncoder.Create(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            _opus.Bitrate = Bitrate;
            _opus.UseVBR = true;
            _opus.Complexity = 5;
            _opus.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
#endif
        }

        /// <summary>Encodes interleaved samples of one frame. Null if nothing came out.</summary>
        public byte[] Encode(short[] interleaved)
        {
#if !MAELA_STUB_CODEC
            int length = _opus.Encode(interleaved, 0, FrameSamples, _packet, 0, _packet.Length);
            if (length <= 0)
                return null;
            byte[] data = new byte[length];
            Buffer.BlockCopy(_packet, 0, data, 0, length);
            return data;
#else
            return null;
#endif
        }
    }

    /// <summary>
    /// Decodes stream audio, hiding short losses with Opus's concealment. Longer gaps are the
    /// sharer's PC being quiet (nothing is sent then) and are left silent.
    /// </summary>
    internal sealed class StreamAudioDecoder
    {
        private const int MaxConcealedFrames = 3;
#if !MAELA_STUB_CODEC
        private readonly OpusDecoder _opus = OpusDecoder.Create(StreamAudioEncoder.SampleRate, StreamAudioEncoder.Channels);
#endif
        private readonly short[] _pcm = new short[StreamAudioEncoder.FrameSamples * StreamAudioEncoder.Channels];
        private bool _have;
        private uint _last;

        public long Concealed { get; private set; }

        /// <summary>Appends frames of interleaved 16-bit little-endian stereo.</summary>
        public void Decode(uint sequence, byte[] data, int offset, int length, List<byte[]> output)
        {
            if (_have)
            {
                int delta = unchecked((int)(sequence - _last));
                if (delta <= 0)
                    return; // Late or repeated.
                if (delta - 1 >= 1 && delta - 1 <= MaxConcealedFrames)
                {
                    for (int i = 0; i < delta - 1; i++)
                    {
                        if (Run(null, 0, 0, output))
                            Concealed++;
                    }
                }
            }
            _have = true;
            _last = sequence;
            Run(data, offset, length, output);
        }

        private bool Run(byte[] data, int offset, int length, List<byte[]> output)
        {
#if !MAELA_STUB_CODEC
            int samples;
            try { samples = _opus.Decode(data, offset, length, _pcm, 0, StreamAudioEncoder.FrameSamples, false); }
            catch { return false; } // A corrupt packet: skip it, don't stop the stream.
            if (samples <= 0)
                return false;
            byte[] frame = new byte[samples * StreamAudioEncoder.Channels * 2];
            Buffer.BlockCopy(_pcm, 0, frame, 0, frame.Length);
            output.Add(frame);
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Captures what programs are playing, using Windows' per-process loopback (Windows 10 2004
    /// and later). Sharing a screen takes everything except mæla and its viewers, so nobody hears
    /// the voice chat back. Sharing a window takes only that program's sound. Older Windows has
    /// no way to leave the voice chat out, so sharing there has no sound.
    /// </summary>
    internal sealed class DesktopAudioCapture : IDisposable
    {
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000, AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000), AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        private const int AUDCLNT_BUFFERFLAGS_SILENT = 2;
        private const long BufferDuration = 2000000; // 200 ms, in 100 ns units.
        private const int ActivationTimeoutMs = 5000;
        private static Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(out int result, out IntPtr activated);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        // Windows refuses a handler that doesn't say it can be called from any thread.
        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject
        {
        }

        [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
        private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public int Result = unchecked((int)0x80004005);
            public IntPtr Client;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                try { operation.GetActivateResult(out Result, out Client); }
                catch (Exception ex) { Result = Marshal.GetHRForException(ex); }
                Done.Set();
            }
        }

        [DllImport("mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
        private static extern int ActivateAudioInterfaceAsync(string device, ref Guid iid, IntPtr parameters, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateEvent(IntPtr attributes, bool manual, bool initial, string name);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        private delegate int InitializeFn(IntPtr self, int mode, int flags, long buffer, long period, ref WAVEFORMATEX format, IntPtr session);
        private delegate int SetEventFn(IntPtr self, IntPtr handle);
        private delegate int GetServiceFn(IntPtr self, ref Guid iid, out IntPtr service);
        private delegate int NoArgsFn(IntPtr self);
        private delegate int GetBufferFn(IntPtr self, out IntPtr data, out int frames, out int flags, out long position, out long time);
        private delegate int ReleaseBufferFn(IntPtr self, int frames);

        private readonly Thread _thread;
        private readonly Action<short[]> _onFrame;
        private readonly int _processId;
        private readonly bool _includeProcess;
        private volatile bool _stop;

        /// <summary>Why there's no sound, if there isn't.</summary>
        public volatile string Problem;
        public volatile bool Running;
        public long FramesCaptured;

        /// <param name="processId">Whose sound.</param>
        /// <param name="includeProcess">True: only that process and its children. False: everything except them.</param>
        /// <param name="onFrame">Called (capture thread) with each 20 ms of interleaved stereo. The array is reused.</param>
        public DesktopAudioCapture(int processId, bool includeProcess, Action<short[]> onFrame)
        {
            _processId = processId;
            _includeProcess = includeProcess;
            _onFrame = onFrame;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "Maela desktop audio";
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        private IntPtr Activate()
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS, inside a VT_BLOB PROPVARIANT.
            IntPtr parameters = Marshal.AllocHGlobal(12);
            IntPtr variant = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(parameters, 0, 1);                        // Process loopback.
                Marshal.WriteInt32(parameters, 4, _processId);
                Marshal.WriteInt32(parameters, 8, _includeProcess ? 0 : 1);  // Include or exclude the process tree.
                Marshal.WriteInt64(variant, 0, 0);
                Marshal.WriteInt16(variant, 0, (short)65);                   // VT_BLOB
                Marshal.WriteInt64(variant, 8, 12);
                Marshal.WriteIntPtr(variant, 16, parameters);

                CompletionHandler handler = new CompletionHandler();
                IActivateAudioInterfaceAsyncOperation operation;
                int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref IID_IAudioClient, variant, handler, out operation);
                if (hr < 0)
                    throw new InvalidOperationException("Windows wouldn't start sound capture (0x" + hr.ToString("X8") + ")");
                if (!handler.Done.WaitOne(ActivationTimeoutMs))
                    throw new InvalidOperationException("Windows didn't start audio capture");
                GC.KeepAlive(operation);
                if (handler.Result < 0 || handler.Client == IntPtr.Zero)
                    throw new InvalidOperationException("Windows wouldn't start sound capture; it needs Windows 10 2004 or later (0x" + handler.Result.ToString("X8") + ")");
                return handler.Client;
            }
            finally
            {
                Marshal.FreeHGlobal(variant);
                Marshal.FreeHGlobal(parameters);
            }
        }

        private void Run()
        {
            IntPtr client = IntPtr.Zero, capture = IntPtr.Zero, wake = IntPtr.Zero;
            bool started = false;
            try
            {
                if (IntPtr.Size != 8)
                    throw new InvalidOperationException("sound sharing needs 64-bit Windows");
                client = Activate();
                WAVEFORMATEX format = new WAVEFORMATEX();
                format.wFormatTag = 1;
                format.nChannels = StreamAudioEncoder.Channels;
                format.nSamplesPerSec = StreamAudioEncoder.SampleRate;
                format.wBitsPerSample = 16;
                format.nBlockAlign = (ushort)(StreamAudioEncoder.Channels * 2);
                format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;
                int flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                GpuCom.Check(GpuCom.Slot<InitializeFn>(client, 3)(client, 0, flags, BufferDuration, 0, ref format, IntPtr.Zero), "audio Initialize");
                wake = CreateEvent(IntPtr.Zero, false, false, null);
                GpuCom.Check(GpuCom.Slot<SetEventFn>(client, 13)(client, wake), "audio SetEventHandle");
                GpuCom.Check(GpuCom.Slot<GetServiceFn>(client, 14)(client, ref IID_IAudioCaptureClient, out capture), "audio GetService");
                GpuCom.Check(GpuCom.Slot<NoArgsFn>(client, 10)(client), "audio Start");
                started = true;
                Running = true;

                GetBufferFn getBuffer = GpuCom.Slot<GetBufferFn>(capture, 3);
                ReleaseBufferFn releaseBuffer = GpuCom.Slot<ReleaseBufferFn>(capture, 4);
                short[] frame = new short[StreamAudioEncoder.FrameSamples * StreamAudioEncoder.Channels];
                int fill = 0;
                using (AutoResetEvent signal = new AutoResetEvent(false))
                {
                    signal.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(wake, false);
                    while (!_stop)
                    {
                        signal.WaitOne(50);
                        while (!_stop)
                        {
                            IntPtr data;
                            int frames, bufferFlags;
                            long position, time;
                            int hr = getBuffer(capture, out data, out frames, out bufferFlags, out position, out time);
                            if (hr != 0)
                            {
                                if (hr < 0)
                                    GpuCom.Check(hr, "audio GetBuffer");
                                break; // Nothing waiting.
                            }
                            int samples = frames * StreamAudioEncoder.Channels;
                            int at = 0;
                            while (at < samples)
                            {
                                int take = Math.Min(samples - at, frame.Length - fill);
                                if ((bufferFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                                    Array.Clear(frame, fill, take);
                                else
                                    Marshal.Copy(new IntPtr(data.ToInt64() + at * 2), frame, fill, take);
                                fill += take;
                                at += take;
                                if (fill == frame.Length)
                                {
                                    fill = 0;
                                    Interlocked.Increment(ref FramesCaptured);
                                    _onFrame(frame);
                                }
                            }
                            releaseBuffer(capture, frames);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Problem = ex.Message; // The picture carries on without sound.
            }
            finally
            {
                Running = false;
                try
                {
                    if (started)
                        GpuCom.Slot<NoArgsFn>(client, 11)(client);
                    GpuCom.Release(capture);
                    GpuCom.Release(client);
                    if (wake != IntPtr.Zero)
                        CloseHandle(wake);
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            if (_thread.IsAlive && _thread != Thread.CurrentThread)
                _thread.Join(1500);
        }
    }

    /// <summary>Captures, encodes and sends a share's sound. Sends nothing while it's silent or nobody's watching.</summary>
    internal sealed class StreamAudioSender : IDisposable
    {
        private const int HangoverFrames = 25; // Half a second, so quiet endings aren't cut off.

        private readonly VoipEngine _engine;
        private readonly StreamAudioEncoder _encoder = new StreamAudioEncoder();
        private readonly DesktopAudioCapture _capture;
        private uint _sequence;
        private int _hangover;

        public string Problem { get { return _capture.Problem; } }
        public bool Running { get { return _capture.Running; } }

        public StreamAudioSender(VoipEngine engine, CaptureSource source)
        {
            _engine = engine;
            int processId = Process.GetCurrentProcess().Id;
            bool include = false;
            if (source != null && source.IsWindow)
            {
                uint owner;
                CaptureNative.GetWindowThreadProcessId(source.Window, out owner);
                processId = (int)owner;
                include = true;
            }
            _capture = new DesktopAudioCapture(processId, include, OnFrame);
        }

        private void OnFrame(short[] frame)
        {
            uint sequence = ++_sequence; // Keeps counting through silence, so receivers can tell loss from it.
            if (_engine.StreamViewerCount == 0)
                return;
            bool silent = true;
            for (int i = 0; i < frame.Length; i++)
            {
                if (frame[i] > 8 || frame[i] < -8)
                {
                    silent = false;
                    break;
                }
            }
            _hangover = silent ? _hangover - 1 : HangoverFrames;
            if (_hangover <= 0)
            {
                _hangover = 0;
                return;
            }
            byte[] packet = _encoder.Encode(frame);
            if (packet != null)
                _engine.SendStreamAudio(sequence, packet);
        }

        public void Dispose()
        {
            _capture.Dispose();
        }
    }
}
