using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace Maela
{
    /// <summary>What you chose in the Share screen dialog.</summary>
    internal sealed class ShareSettings
    {
        public CaptureSource Source;
        public int MaxHeight = 1080;   // 720, 1080, 1440 or 2160
        public int Fps = 60;           // 30, 60 or 120
        public bool ShowCursor = true;
    }

    /// <summary>
    /// Runs a screen share: capture, scale, encode and send, on its own thread, paced to the
    /// chosen frame rate. Quality follows what viewers report; a keyframe is made when someone
    /// starts watching or loses data, and every few seconds.
    /// </summary>
    internal sealed class ScreenShareSender : IDisposable
    {
        private readonly VoipEngine _engine;
        private readonly ShareSettings _settings;
        private readonly Thread _thread;
        private volatile bool _stop;

        public volatile string Error;
        public volatile int ActualFps;
        public volatile int BitrateKbps;
        public volatile string Method = String.Empty;
        public Size OutputSize;
        private I420Frame _preview;
        private long _previewAt;
        private readonly object _previewLock = new object();

        /// <summary>A copy of what you're sending, about 10 times a second (for your own preview).</summary>
        public I420Frame LatestPreview { get { lock (_previewLock) return _preview; } }

        public ScreenShareSender(VoipEngine engine, ShareSettings settings)
        {
            _engine = engine;
            _settings = settings;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "Maela screen share";
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Start();
        }

        public static int DefaultBitrateKbps(int height, int fps)
        {
            int kbps = height <= 720 ? 2500 : height <= 1080 ? 4500 : height <= 1440 ? 9000 : 16000;
            if (fps >= 60) kbps = kbps * 3 / 2;
            if (fps >= 120) kbps = kbps * 3 / 2;
            return kbps;
        }

        private void Run()
        {
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { } // Real pixels on scaled displays (Windows 10 1607+).
            IScreenCapture capture = null;
            H264Encoder encoder = null;
            try
            {
                capture = ScreenCaptureFactory.Create(_settings.Source);
                Method = capture.Method;
                int maxKbps = DefaultBitrateKbps(_settings.MaxHeight, _settings.Fps);
                int kbps = maxKbps;
                I420Frame frame = null;
                byte[] scratch = null;
                Stopwatch clock = Stopwatch.StartNew();
                double interval = 1000.0 / _settings.Fps;
                double next = 0;
                long lastKeyframeMs = -100000, lastRateChangeMs = 0;
                int framesThisSecond = 0;
                long secondStart = 0;
                bool haveFrame = false;
                int captureWidth = 0, captureHeight = 0;

                while (!_stop)
                {
                    double now = clock.Elapsed.TotalMilliseconds;
                    if (now < next)
                    {
                        int wait = (int)(next - now);
                        if (wait > 1) Thread.Sleep(wait - 1);
                        continue;
                    }
                    next = Math.Max(next + interval, now - interval); // Don't try to catch up a backlog.

                    // Capture (keep the last picture if nothing changed, so viewers keep a steady rate).
                    bool fresh = capture.Capture(0, _settings.ShowCursor, delegate(IntPtr bits, int stride)
                    {
                        int w = capture.Width, h = capture.Height;
                        if (frame == null || w != captureWidth || h != captureHeight)
                        {
                            captureWidth = w;
                            captureHeight = h;
                            Size size = FrameScaler.Fit(w, h, _settings.MaxHeight);
                            frame = new I420Frame(size.Width, size.Height);
                            OutputSize = new Size(frame.Width, frame.Height);
                        }
                        FrameScaler.ToI420(bits, stride, w, h, frame, ref scratch);
                    });
                    if (fresh) haveFrame = true;
                    if (fresh && frame != null && (long)now - _previewAt >= 100)
                    {
                        _previewAt = (long)now;
                        I420Frame copy = new I420Frame(frame.Width, frame.Height);
                        Buffer.BlockCopy(frame.Y, 0, copy.Y, 0, frame.Y.Length);
                        Buffer.BlockCopy(frame.U, 0, copy.U, 0, frame.U.Length);
                        Buffer.BlockCopy(frame.V, 0, copy.V, 0, frame.V.Length);
                        lock (_previewLock) _preview = copy;
                    }
                    if (!haveFrame || frame == null)
                        continue;
                    if (_engine.StreamViewerCount == 0)
                    {
                        lastKeyframeMs = -100000; // Next viewer gets a keyframe straight away.
                        continue; // Nobody watching: capture only, encode and send nothing.
                    }

                    // (Re)create the encoder for the size and a bitrate the viewers can take.
                    long nowMs = (long)now;
                    int suggested = Math.Min(maxKbps, _engine.SuggestedBitrateKbps(maxKbps));
                    bool rateChanged = Math.Abs(suggested - kbps) > kbps * 0.3 && nowMs - lastRateChangeMs > 3000;
                    if (encoder == null || encoder.Width != frame.Width || encoder.Height != frame.Height || rateChanged)
                    {
                        if (rateChanged) { kbps = suggested; lastRateChangeMs = nowMs; }
                        if (encoder != null) encoder.Dispose();
                        encoder = new H264Encoder(frame.Width, frame.Height, _settings.Fps, kbps);
                        lastKeyframeMs = -100000;
                    }
                    BitrateKbps = kbps;

                    if (_engine.ConsumeKeyframeRequest() || nowMs - lastKeyframeMs > 4000)
                    {
                        encoder.ForceKeyframe();
                        lastKeyframeMs = nowMs;
                    }
                    bool keyframe;
                    byte[] data = encoder.Encode(frame, nowMs, out keyframe);
                    if (data != null)
                    {
                        if (keyframe) lastKeyframeMs = nowMs;
                        _engine.SendVideoFrame(data, keyframe);
                        framesThisSecond++;
                    }
                    if (nowMs - secondStart >= 1000)
                    {
                        ActualFps = framesThisSecond;
                        framesThisSecond = 0;
                        secondStart = nowMs;
                    }
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                if (encoder != null) encoder.Dispose();
                if (capture != null) capture.Dispose();
            }
        }

        public void Dispose()
        {
            _stop = true;
            if (_thread.IsAlive && _thread != Thread.CurrentThread)
                _thread.Join(2000);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    }
}
