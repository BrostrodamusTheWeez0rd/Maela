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
        public bool ShareAudio = true;
    }

    /// <summary>
    /// Runs a screen share on two threads. One captures and scales at the chosen frame rate; the
    /// other encodes and sends the newest picture. A slow encode costs a frame instead of delaying
    /// capture. Quality follows what viewers report; a keyframe is made when someone starts
    /// watching or needs one, and every few seconds.
    /// </summary>
    internal sealed class ScreenShareSender : IDisposable
    {
        private const int PeriodicKeyframeMs = 8000;
        private const int RateChangeGapMs = 1000;
        private const int EncoderFailFrames = 120; // About two seconds of nothing coming out.

        private readonly VoipEngine _engine;
        private readonly ShareSettings _settings;
        private readonly Thread _captureThread, _encodeThread;
        private readonly FrameExchange<I420Frame> _exchange = new FrameExchange<I420Frame>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private StreamAudioSender _audio;
        private volatile bool _stop;

        public volatile string Error;
        public volatile int ActualFps;
        public volatile int BitrateKbps;
        public volatile string Method = String.Empty;
        public volatile string EncoderName = String.Empty;
        public volatile int CaptureUs;
        public volatile int EncodeUs;
        /// <summary>Time capture and encoding only while someone has the stats showing.</summary>
        public volatile bool MeasureTimings;
        public Size OutputSize;
        private I420Frame _preview;
        private long _previewAt;
        private I420Frame _previewSpare;
        private readonly object _previewLock = new object();

        /// <summary>A copy of what you're sending, about 10 times a second (for your own preview).</summary>
        public I420Frame LatestPreview { get { lock (_previewLock) return _preview; } }

        /// <summary>Captured frames replaced before the encoder got to them.</summary>
        public long SkippedFrames { get { return _exchange.Replaced; } }

        public ScreenShareSender(VoipEngine engine, ShareSettings settings)
        {
            _engine = engine;
            _settings = settings;
            _captureThread = new Thread(CaptureLoop);
            _captureThread.IsBackground = true;
            _captureThread.Name = "Maela screen capture";
            _captureThread.Priority = ThreadPriority.AboveNormal;
            _encodeThread = new Thread(EncodeLoop);
            _encodeThread.IsBackground = true;
            _encodeThread.Name = "Maela screen encode";
            _encodeThread.SetApartmentState(ApartmentState.MTA); // Media Foundation's encoders are free-threaded.
            _encodeThread.Start();
            _captureThread.Start();
            if (settings.ShareAudio)
            {
                try { _audio = new StreamAudioSender(engine, settings.Source); }
                catch (Exception ex) { _audioProblem = ex.Message; }
            }
        }

        private volatile string _audioProblem;

        /// <summary>Why there's no sound, or null if it's on or wasn't asked for.</summary>
        public string AudioProblem
        {
            get
            {
                StreamAudioSender audio = _audio;
                return audio != null && audio.Problem != null ? audio.Problem : _audioProblem;
            }
        }

        public static int DefaultBitrateKbps(int height, int fps)
        {
            int kbps = height <= 720 ? 2500 : height <= 1080 ? 4500 : height <= 1440 ? 9000 : 16000;
            if (fps >= 60) kbps = kbps * 3 / 2;
            if (fps >= 120) kbps = kbps * 3 / 2;
            return kbps;
        }

        private void CaptureLoop()
        {
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { } // Real pixels on scaled displays (Windows 10 1607+).
            bool fineTimer = false;
            try { timeBeginPeriod(1); fineTimer = true; } catch { } // Otherwise sleeps can run nearly a whole frame long.
            IScreenCapture capture = null;
            try
            {
                capture = ScreenCaptureFactory.Create(_settings.Source);
                Method = capture.Method;
                I420Frame frame = null;
                byte[] scratch = null;
                double interval = 1000.0 / _settings.Fps;
                double next = 0;
                long timeSum = 0, timeCount = 0, timeStart = 0;
                bool published = false;

                while (!_stop)
                {
                    double now = _clock.Elapsed.TotalMilliseconds;
                    if (now < next)
                    {
                        int wait = (int)(next - now);
                        if (wait > 1) Thread.Sleep(wait - 1);
                        continue;
                    }
                    next = Math.Max(next + interval, now - interval); // Don't try to catch up a backlog.

                    bool measure = MeasureTimings;
                    long started = measure ? _clock.ElapsedTicks : 0;
                    I420Frame target = frame;
                    bool fresh = capture.Capture(0, _settings.ShowCursor, delegate(IntPtr bits, int stride)
                    {
                        Size size = FrameScaler.Fit(capture.Width, capture.Height, _settings.MaxHeight);
                        if (target == null || target.Width != (size.Width & ~1) || target.Height != (size.Height & ~1))
                        {
                            target = new I420Frame(size.Width, size.Height);
                            OutputSize = new Size(target.Width, target.Height);
                        }
                        FrameScaler.ToI420(bits, stride, capture.Width, capture.Height, target, ref scratch);
                    });
                    frame = target;
                    if (fresh && frame != null)
                    {
                        if (measure)
                        {
                            timeSum += _clock.ElapsedTicks - started;
                            timeCount++;
                        }
                        if ((long)now - _previewAt >= 100)
                        {
                            _previewAt = (long)now;
                            // Two buffers in turn: a new one 10 times a second keeps the garbage collector busy.
                            I420Frame copy = _previewSpare;
                            if (copy == null || copy.Width != frame.Width || copy.Height != frame.Height)
                                copy = new I420Frame(frame.Width, frame.Height);
                            Buffer.BlockCopy(frame.Y, 0, copy.Y, 0, frame.Y.Length);
                            Buffer.BlockCopy(frame.U, 0, copy.U, 0, frame.U.Length);
                            Buffer.BlockCopy(frame.V, 0, copy.V, 0, frame.V.Length);
                            lock (_previewLock)
                            {
                                _previewSpare = _preview;
                                _preview = copy;
                            }
                        }
                        frame = _exchange.Publish(frame);
                        published = true;
                    }
                    else if (published)
                        _exchange.Tick(); // Nothing changed: viewers still get a steady rate.

                    if ((long)now - timeStart >= 1000)
                    {
                        CaptureUs = timeCount == 0 ? 0 : (int)(timeSum * 1000000 / Stopwatch.Frequency / timeCount);
                        timeSum = 0;
                        timeCount = 0;
                        timeStart = (long)now;
                    }
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                if (capture != null) capture.Dispose();
                if (fineTimer)
                {
                    try { timeEndPeriod(1); } catch { }
                }
                _exchange.Close();
            }
        }

        private void EncodeLoop()
        {
            IVideoEncoder encoder = null;
            try
            {
                int maxKbps = DefaultBitrateKbps(_settings.MaxHeight, _settings.Fps);
                int kbps = maxKbps;
                I420Frame frame = null;
                long lastKeyframeMs = -100000, lastRateChangeMs = 0;
                int framesThisSecond = 0, nothingOut = 0;
                long secondStart = 0, timeSum = 0, timeCount = 0;
                bool allowHardware = true;

                while (!_stop)
                {
                    if (!_exchange.Take(ref frame, 200))
                        continue;
                    if (_engine.StreamViewerCount == 0)
                    {
                        lastKeyframeMs = -100000; // Next viewer gets a keyframe straight away.
                        continue; // Nobody watching: encode and send nothing.
                    }

                    long nowMs = _clock.ElapsedMilliseconds;
                    int suggested = Math.Min(maxKbps, _engine.SuggestedBitrateKbps(maxKbps));
                    if (encoder == null || encoder.Width != (frame.Width & ~1) || encoder.Height != (frame.Height & ~1))
                    {
                        if (encoder != null) encoder.Dispose();
                        kbps = suggested;
                        encoder = VideoEncoderFactory.Create(frame.Width, frame.Height, _settings.Fps, kbps, allowHardware);
                        EncoderName = encoder.Name;
                        lastKeyframeMs = -100000;
                    }
                    else if (Math.Abs(suggested - kbps) > kbps * 0.05 && nowMs - lastRateChangeMs > RateChangeGapMs)
                    {
                        // Restarting an encoder would force a keyframe; changing it in place doesn't.
                        lastRateChangeMs = nowMs;
                        if (encoder.SetBitrate(suggested))
                            kbps = suggested;
                    }
                    BitrateKbps = kbps;

                    if (_engine.ConsumeKeyframeRequest() || nowMs - lastKeyframeMs > PeriodicKeyframeMs)
                    {
                        encoder.ForceKeyframe();
                        lastKeyframeMs = nowMs;
                    }

                    bool measure = MeasureTimings;
                    long started = measure ? _clock.ElapsedTicks : 0;
                    bool keyframe;
                    byte[] data = null;
                    try
                    {
                        data = encoder.Encode(frame, nowMs, out keyframe);
                        nothingOut = data == null ? nothingOut + 1 : 0;
                    }
                    catch (Exception)
                    {
                        keyframe = false;
                        nothingOut = EncoderFailFrames;
                    }
                    if (nothingOut >= EncoderFailFrames)
                    {
                        if (!(encoder is MediaFoundationEncoder))
                            throw new InvalidOperationException("The video encoder stopped working.");
                        // The graphics card gave up: carry on with the processor.
                        allowHardware = false;
                        nothingOut = 0;
                        encoder.Dispose();
                        encoder = null;
                        continue;
                    }
                    if (measure)
                    {
                        timeSum += _clock.ElapsedTicks - started;
                        timeCount++;
                    }
                    if (data != null)
                    {
                        if (keyframe) lastKeyframeMs = nowMs;
                        _engine.SendVideoFrame(data, keyframe);
                        framesThisSecond++;
                    }
                    if (nowMs - secondStart >= 1000)
                    {
                        ActualFps = framesThisSecond;
                        EncodeUs = timeCount == 0 ? 0 : (int)(timeSum * 1000000 / Stopwatch.Frequency / timeCount);
                        framesThisSecond = 0;
                        timeSum = 0;
                        timeCount = 0;
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
            }
        }

        public void Dispose()
        {
            _stop = true;
            _exchange.Close();
            if (_audio != null)
                _audio.Dispose();
            if (_captureThread.IsAlive && _captureThread != Thread.CurrentThread)
                _captureThread.Join(2000);
            if (_encodeThread.IsAlive && _encodeThread != Thread.CurrentThread)
                _encodeThread.Join(2000);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    }
}
