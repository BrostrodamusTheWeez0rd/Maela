using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>
    /// Watching a screen share happens in a separate, locked-down copy of mæla, because decoding
    /// video someone else sent is the riskiest thing the app does. The helper:
    ///   - runs at Low integrity: Windows stops it writing to your files, settings or registry,
    ///     and from controlling other apps' windows;
    ///   - runs in a Windows job that can't start other programs, read or change the clipboard,
    ///     change system settings, or outlive mæla;
    ///   - gets the video through a private pipe handed to it at start (no name to find it by).
    /// If the decoder ever met a crafted stream, the damage stays inside that helper.
    /// </summary>
    internal sealed class StreamViewerHost : IDisposable
    {
        private const int MaxQueuedFrames = 12; // Beyond this it's lag, not a cushion: the viewer has its own buffer.
        private const byte FlagKeyframe = 1, FlagStats = 2;
        private readonly uint _sharerId;
        private readonly Process _process;
        private readonly IntPtr _job;
        private readonly AnonymousPipeServerStream _pipe;
        private readonly AnonymousPipeServerStream _statsFlagPipe;
        private volatile bool _wantsStats;
        private readonly Queue<byte[]> _queue = new Queue<byte[]>();
        private readonly Thread _writer;
        private volatile bool _closed;
        private bool _skipUntilKeyframe;

        public uint SharerId { get { return _sharerId; } }
        public bool Sandboxed { get; private set; }
        /// <summary>The viewer has its stats showing, so it's worth telling it about the network.</summary>
        public bool WantsStats { get { return _wantsStats; } }
        public event EventHandler Closed;
        /// <summary>Raised (writer thread) when frames had to be dropped and a keyframe is needed.</summary>
        public event EventHandler NeedKeyframe;
        /// <summary>Raised when the person watching changes the sound in the viewer: 0 to 100, 0 being muted.</summary>
        public event Action<int> VolumeChanged;

        public StreamViewerHost(uint sharerId, string title, bool gpuDecode)
        {
            _sharerId = sharerId;
            _pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable, 1 << 20);
            string handle = _pipe.GetClientHandleAsString();
            _statsFlagPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable, 16);
            string exe = Application.ExecutablePath;
            string args = "\"" + exe + "\" --watch " + handle + " \"" + title.Replace("\"", "'") + "\" " + (gpuDecode ? "gpu" : "cpu") + " " + _statsFlagPipe.GetClientHandleAsString();
            bool sandboxed;
            _process = SandboxLauncher.Start(exe, args, out _job, out sandboxed);
            Sandboxed = sandboxed;
            _pipe.DisposeLocalCopyOfClientHandle();
            _statsFlagPipe.DisposeLocalCopyOfClientHandle();
            Thread flagReader = new Thread(ReadStatsFlag);
            flagReader.IsBackground = true;
            flagReader.Name = "Maela viewer stats flag";
            flagReader.Start();
            _process.EnableRaisingEvents = true;
            _process.Exited += delegate { Close(); };
            _writer = new Thread(WriteLoop);
            _writer.IsBackground = true;
            _writer.Name = "Maela stream to viewer";
            _writer.Start();
        }

        /// <summary>Queues one received frame for the viewer (receive thread).</summary>
        public void Push(byte[] frame, bool keyframe)
        {
            if (_closed)
                return;
            lock (_queue)
            {
                if (_skipUntilKeyframe && !keyframe)
                    return;
                _skipUntilKeyframe = false;
                if (_queue.Count >= MaxQueuedFrames)
                {
                    // The viewer can't keep up: drop the backlog and restart from a keyframe.
                    _queue.Clear();
                    _skipUntilKeyframe = !keyframe;
                    if (!keyframe && NeedKeyframe != null) NeedKeyframe(this, EventArgs.Empty);
                    if (!keyframe) return;
                }
                byte[] message = new byte[5 + frame.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(frame.Length), 0, message, 0, 4);
                message[4] = keyframe ? FlagKeyframe : (byte)0;
                Buffer.BlockCopy(frame, 0, message, 5, frame.Length);
                _queue.Enqueue(message);
                Monitor.Pulse(_queue);
            }
        }

        /// <summary>Tells the viewer's stats readout how the network is doing.</summary>
        public void PushStats(string text)
        {
            if (_closed || text == null)
                return;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            byte[] message = new byte[5 + bytes.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(bytes.Length), 0, message, 0, 4);
            message[4] = FlagStats;
            Buffer.BlockCopy(bytes, 0, message, 5, bytes.Length);
            lock (_queue)
            {
                _queue.Enqueue(message);
                Monitor.Pulse(_queue);
            }
        }

        /// <summary>
        /// All the sandboxed viewer can say back, one byte at a time: stats off (0) or on (1), or
        /// the sound level (100 to 200). Anything else is ignored. Sleeps until there's something.
        /// </summary>
        private void ReadStatsFlag()
        {
            try
            {
                int value;
                while ((value = _statsFlagPipe.ReadByte()) >= 0)
                {
                    if (value <= 1)
                        _wantsStats = value == 1;
                    else if (value >= 100 && value <= 200)
                    {
                        Action<int> changed = VolumeChanged;
                        if (changed != null)
                            changed(value - 100);
                    }
                }
            }
            catch
            {
            }
            _wantsStats = false;
        }

        private void WriteLoop()
        {
            try
            {
                while (!_closed)
                {
                    byte[] message;
                    lock (_queue)
                    {
                        while (_queue.Count == 0 && !_closed)
                            Monitor.Wait(_queue, 250);
                        if (_closed)
                            break;
                        message = _queue.Dequeue();
                    }
                    _pipe.Write(message, 0, message.Length);
                    _pipe.Flush();
                }
            }
            catch
            {
                // Pipe closed: the viewer went away.
            }
            Close();
        }

        private void Close()
        {
            if (_closed)
                return;
            _closed = true;
            lock (_queue) Monitor.PulseAll(_queue);
            try { _pipe.Dispose(); } catch { }
            try { _statsFlagPipe.Dispose(); } catch { }
            EventHandler closed = Closed;
            if (closed != null)
                closed(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Close();
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            if (_job != IntPtr.Zero)
                SandboxLauncher.CloseHandle(_job); // Kills anything left in the job.
        }
    }

    /// <summary>Starts a process at Low integrity inside a restricted job (falls back to just the job).</summary>
    internal static class SandboxLauncher
    {
        public static Process Start(string exe, string commandLine, out IntPtr job, out bool lowIntegrity)
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job != IntPtr.Zero)
            {
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = 0x2000 | 0x8 | 0x400; // KILL_ON_JOB_CLOSE | ACTIVE_PROCESS | DIE_ON_UNHANDLED_EXCEPTION
                limits.BasicLimitInformation.ActiveProcessLimit = 1;            // No child processes.
                SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                int ui = 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x80; // No clipboard, system/display settings, global atoms, desktop switching, logoff.
                SetInformationJobObject(job, 4, ref ui, 4);
            }

            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;
            lowIntegrity = false;
            IntPtr token = LowIntegrityToken();
            bool started = false;
            if (token != IntPtr.Zero)
            {
                started = CreateProcessAsUser(token, exe, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x4 /* CREATE_SUSPENDED */, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi);
                CloseHandle(token);
                if (started)
                    lowIntegrity = true;
                else
                    pi = new PROCESS_INFORMATION();
            }
            else
                pi = new PROCESS_INFORMATION();
            if (!started && !CreateProcess(exe, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x4, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't start the stream viewer.");
            if (job != IntPtr.Zero)
                AssignProcessToJobObject(job, pi.hProcess);
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);
            Process process = Process.GetProcessById(pi.dwProcessId);
            CloseHandle(pi.hProcess);
            return process;
        }

        /// <summary>A copy of our own token lowered to Low integrity (S-1-16-4096), or zero.</summary>
        private static IntPtr LowIntegrityToken()
        {
            IntPtr token, primary = IntPtr.Zero, sid = IntPtr.Zero;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0002 | 0x0008 | 0x0080 | 0x0001, out token))
                return IntPtr.Zero;
            try
            {
                if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out primary))
                    return IntPtr.Zero;
                if (!ConvertStringSidToSid("S-1-16-4096", out sid))
                {
                    CloseHandle(primary);
                    return IntPtr.Zero;
                }
                TOKEN_MANDATORY_LABEL label = new TOKEN_MANDATORY_LABEL();
                label.Sid = sid;
                label.Attributes = 0x20; // SE_GROUP_INTEGRITY
                if (!SetTokenInformation(primary, 25, ref label, Marshal.SizeOf(typeof(TOKEN_MANDATORY_LABEL)) + GetLengthSid(sid)))
                {
                    CloseHandle(primary);
                    return IntPtr.Zero;
                }
                return primary;
            }
            finally
            {
                CloseHandle(token);
                if (sid != IntPtr.Zero) LocalFree(sid);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct TOKEN_MANDATORY_LABEL { public IntPtr Sid; public int Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public int LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public int ActiveProcessLimit;
            public UIntPtr Affinity; public int PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, int access, IntPtr attributes, int level, int type, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertStringSidToSid(string sid, out IntPtr pSid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref TOKEN_MANDATORY_LABEL info, int length);
        [DllImport("advapi32.dll")] private static extern int GetLengthSid(IntPtr sid);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, int flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, int flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref int info, int length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern int ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
    }

    /// <summary>Where a graphics-card picture goes. Direct3D owns its pixels; nothing paints here.</summary>
    internal sealed class VideoSurface : Control
    {
        public VideoSurface()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            BackColor = Color.Black;
        }
    }

    /// <summary>
    /// The viewer window, running inside the locked-down helper process. Draws with the graphics
    /// card when it can. Decodes there too if asked, after checking the stream; if anything goes
    /// wrong it carries on with OpenH264, and if the card can't draw at all, with GDI.
    /// </summary>
    internal sealed class StreamViewerForm : Form
    {
        private readonly AnonymousPipeClientStream _pipe;
        private readonly AnonymousPipeClientStream _statsFlag;
        private readonly Thread _reader;
        private readonly object _frameLock = new object();
        private readonly object _bufferLock = new object();
        private readonly JitterBuffer<ViewerFrame> _buffer = new JitterBuffer<ViewerFrame>(1000.0 / 60, 12);
        private const long MaxReplayBytes = 64 * 1024 * 1024;
        private const int GpuNoOutputLimit = 60;
        private readonly bool _wantGpuDecode;
        private readonly VideoSurface _surface = new VideoSurface();
        private readonly Label _status = new Label();
        private readonly System.Windows.Forms.Timer _statusTimer = new System.Windows.Forms.Timer();
        private volatile GpuVideoRenderer _renderer;
        private volatile bool _rendererFailed;
        private volatile string _decodeName = "OpenH264";
        private volatile string _gpuFallback = String.Empty;
        private bool _started;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Thread _player;
        private byte[] _pixels, _spare;
        private int _width, _height;
        private volatile bool _ended;
        private int _paintPending;
        private int _fps, _framesThisSecond, _receivedFps, _receivedThisSecond;
        private DateTime _secondStart = DateTime.UtcNow;
        private volatile bool _showStats;
        private int _volume = 100, _volumeBeforeMute = 100;
        private DateTime _volumeShownUntil = DateTime.MinValue;
        private double _decodeMs, _paintMs;
        private string _networkStats = String.Empty;
        private FormWindowState _restoreState;
        private FormBorderStyle _restoreBorder;
        private bool _fullScreen;

        /// <summary>Entry point for "Maela.exe --watch handle title".</summary>
        public static void RunHelper(string pipeHandle, string title, bool gpuDecode, string statsFlagHandle)
        {
            Application.EnableVisualStyles();
            Application.Run(new StreamViewerForm(pipeHandle, title, gpuDecode, statsFlagHandle));
        }

        private StreamViewerForm(string pipeHandle, string title, bool gpuDecode, string statsFlagHandle)
        {
            _wantGpuDecode = gpuDecode;
            if (statsFlagHandle != null)
            {
                try { _statsFlag = new AnonymousPipeClientStream(PipeDirection.Out, statsFlagHandle); }
                catch { } // The network line just stays empty.
            }
            _buffer.Discard = delegate(ViewerFrame dropped) { dropped.Dispose(); };
            Text = title;
            BackColor = Color.Black;
            ClientSize = new Size(1280, 720);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = Branding.LoadAppIcon(); } catch { }
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _pipe = new AnonymousPipeClientStream(PipeDirection.In, pipeHandle);
            _reader = new Thread(ReadLoop);
            _reader.IsBackground = true;
            _reader.SetApartmentState(ApartmentState.MTA); // Media Foundation's decoder is free-threaded.
            _player = new Thread(PlayLoop);
            _player.IsBackground = true;
            _player.Priority = ThreadPriority.AboveNormal;
            _status.Dock = DockStyle.Bottom;
            _status.Height = 22;
            _status.ForeColor = Color.FromArgb(150, 150, 150);
            _status.BackColor = Color.Black;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Text = "Connecting to the stream…";
            _surface.Dock = DockStyle.Fill;
            Controls.Add(_surface);
            Controls.Add(_status);
            _surface.DoubleClick += delegate { ToggleFullScreen(); };
            _status.DoubleClick += delegate { ToggleFullScreen(); };
            _surface.Resize += delegate
            {
                GpuVideoRenderer renderer = _renderer;
                if (renderer != null)
                    renderer.Resize(_surface.ClientSize.Width, _surface.ClientSize.Height);
            };
            _statusTimer.Interval = 500;
            _statusTimer.Tick += delegate { UpdateStatus(); };
            _statusTimer.Start();
            FormClosed += delegate { _statusTimer.Dispose(); };
            DoubleClick += delegate { ToggleFullScreen(); };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F11) ToggleFullScreen();
                else if (e.KeyCode == Keys.M) SetVolume(_volume > 0 ? 0 : _volumeBeforeMute);
                else if (e.KeyCode == Keys.Up) SetVolume(Math.Min(100, _volume + 10));
                else if (e.KeyCode == Keys.Down) SetVolume(Math.Max(0, _volume - 10));
                else if (e.KeyCode == Keys.F3)
                {
                    _showStats = !_showStats;
                    if (!_showStats)
                    {
                        lock (_frameLock)
                            _networkStats = String.Empty;
                    }
                    if (_statsFlag != null)
                    {
                        try { _statsFlag.WriteByte(_showStats ? (byte)1 : (byte)0); _statsFlag.Flush(); }
                        catch { }
                    }
                    UpdateStatus();
                    Invalidate();
                }
                else if (e.KeyCode == Keys.Escape && _fullScreen) ToggleFullScreen();
            };
        }

        private void SetVolume(int volume)
        {
            if (_volume > 0)
                _volumeBeforeMute = _volume;
            _volume = volume;
            _volumeShownUntil = DateTime.UtcNow.AddSeconds(2);
            if (_statsFlag != null)
            {
                try { _statsFlag.WriteByte((byte)(100 + volume)); _statsFlag.Flush(); }
                catch { }
            }
            UpdateStatus();
            Invalidate();
        }

        private string SoundText()
        {
            return _volume == 0 ? "sound muted" : "sound " + _volume + "%";
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_started)
                return;
            _started = true;
            _renderer = GpuVideoRenderer.TryCreate(_surface.Handle, _surface.ClientSize.Width, _surface.ClientSize.Height);
            if (_renderer == null)
                UseGdi();
            _reader.Start();
            _player.Start();
        }

        /// <summary>The graphics card can't draw: the picture is painted on the window itself instead.</summary>
        private void UseGdi()
        {
            _surface.Visible = false;
            _status.Visible = false;
            _statusTimer.Stop();
            Invalidate();
        }

        private void UpdateStatus()
        {
            if (_renderer == null)
                return;
            int w, h, fps;
            lock (_frameLock) { w = _width; h = _height; fps = _fps; }
            string text;
            if (_ended)
                text = "The stream has ended.";
            else if (w == 0)
                text = "Connecting to the stream…";
            else if (_showStats)
                text = w + "×" + h + " · " + StatsLine();
            else
                text = w + "×" + h + " · " + fps + " fps · " + SoundText() + " (M, ↑ ↓) · double-click for full screen · F3 for stats";
            int height = _showStats ? 58 : 22;
            if (_status.Height != height)
                _status.Height = height;
            bool show = !_fullScreen || _showStats || _ended || DateTime.UtcNow < _volumeShownUntil;
            if (_status.Visible != show)
                _status.Visible = show;
            if (_status.Text != text)
                _status.Text = text;
        }

        private void ToggleFullScreen()
        {
            if (!_fullScreen)
            {
                _restoreState = WindowState;
                _restoreBorder = FormBorderStyle;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = Screen.FromControl(this).Bounds;
                _fullScreen = true;
            }
            else
            {
                FormBorderStyle = _restoreBorder;
                WindowState = _restoreState;
                _fullScreen = false;
            }
        }

        private void ReadLoop()
        {
            H264Decoder software = null;
            GpuH264Decoder gpu = null;
            List<byte[]> sinceKeyframe = new List<byte[]>();
            long sinceKeyframeBytes = 0;
            bool replayable = false;
            int width = 0, height = 0, noOutput = 0;
            List<ViewerFrame> decoded = new List<ViewerFrame>();
            try
            {
                if (_wantGpuDecode && _renderer != null)
                {
                    try
                    {
                        gpu = new GpuH264Decoder(_renderer.Device);
                        _decodeName = "Graphics card";
                    }
                    catch (Exception ex)
                    {
                        _gpuFallback = ex.Message;
                    }
                }
                if (gpu == null)
                    software = new H264Decoder();
                byte[] header = new byte[5];
                while (true)
                {
                    if (!ReadExactly(header, 5))
                        break;
                    int length = BitConverter.ToInt32(header, 0);
                    if (length <= 0 || length > 8 * 1024 * 1024)
                        break; // Never trust sizes.
                    byte[] data = new byte[length];
                    if (!ReadExactly(data, length))
                        break;
                    if ((header[4] & 2) != 0)
                    {
                        lock (_frameLock)
                            _networkStats = length > 512 ? String.Empty : System.Text.Encoding.UTF8.GetString(data);
                        continue;
                    }
                    bool keyframe = (header[4] & 1) != 0;
                    bool measure = _showStats;
                    long started = measure ? _clock.ElapsedTicks : 0;
                    decoded.Clear();

                    if (gpu != null)
                    {
                        // Keep what OpenH264 would need to pick up mid-stream: the helper can't ask for a keyframe.
                        if (keyframe)
                        {
                            sinceKeyframe.Clear();
                            sinceKeyframeBytes = 0;
                            replayable = true;
                        }
                        if (replayable)
                        {
                            sinceKeyframe.Add(data);
                            sinceKeyframeBytes += length;
                            if (sinceKeyframeBytes > MaxReplayBytes)
                            {
                                sinceKeyframe.Clear();
                                replayable = false;
                            }
                        }

                        string problem = null;
                        if (_rendererFailed)
                            problem = "Drawing stopped working";
                        else
                        {
                            byte[] clean;
                            if (!H264StreamCheck.Check(data, ref width, ref height, out clean))
                                problem = "The stream didn't pass the safety check";
                            else
                            {
                                try { gpu.Decode(clean, width, height, decoded); }
                                catch (Exception ex) { problem = ex.Message; }
                                noOutput = decoded.Count == 0 ? noOutput + 1 : 0;
                                if (noOutput >= GpuNoOutputLimit)
                                    problem = "No pictures came out";
                            }
                        }

                        if (problem != null)
                        {
                            _gpuFallback = problem;
                            _decodeName = "OpenH264";
                            foreach (ViewerFrame frame in decoded)
                                frame.Dispose();
                            decoded.Clear();
                            DiscardBuffered();
                            gpu.Dispose();
                            gpu = null;
                            software = new H264Decoder();
                            // Catch up from the last keyframe; only the newest picture is worth showing.
                            I420Frame newest = null;
                            foreach (byte[] old in sinceKeyframe)
                            {
                                I420Frame picture = software.Decode(old);
                                if (picture != null)
                                    newest = picture;
                            }
                            sinceKeyframe.Clear();
                            sinceKeyframeBytes = 0;
                            if (newest != null)
                                decoded.Add(Wrap(newest));
                        }
                    }
                    else
                    {
                        I420Frame picture = software.Decode(data);
                        if (picture != null)
                            decoded.Add(Wrap(picture));
                    }

                    if (decoded.Count == 0)
                        continue;
                    if (measure)
                    {
                        double decodeMs = (_clock.ElapsedTicks - started) * 1000.0 / Stopwatch.Frequency;
                        lock (_frameLock)
                        {
                            _decodeMs += (decodeMs - _decodeMs) * 0.1;
                            _receivedThisSecond += decoded.Count;
                        }
                    }
                    lock (_bufferLock)
                    {
                        foreach (ViewerFrame frame in decoded)
                            _buffer.Push(_clock.ElapsedMilliseconds, frame);
                    }
                }
            }
            catch
            {
                // Anything unexpected: end the viewer, never the main app.
            }
            finally
            {
                _ended = true;
                DiscardBuffered();
                if (software != null) software.Dispose();
                if (_player.IsAlive)
                    _player.Join(1000); // It may still be drawing a graphics-card picture.
                if (gpu != null) gpu.Dispose();
            }
            try { BeginInvoke((MethodInvoker)delegate { UpdateStatus(); Invalidate(); }); } catch { }
        }

        private static ViewerFrame Wrap(I420Frame picture)
        {
            ViewerFrame frame = new ViewerFrame();
            frame.Software = picture;
            frame.Width = picture.Width;
            frame.Height = picture.Height;
            return frame;
        }

        private void DiscardBuffered()
        {
            lock (_bufferLock)
            {
                ViewerFrame frame;
                while ((frame = _buffer.Pop(long.MaxValue / 2)) != null)
                    frame.Dispose();
            }
        }

        /// <summary>Shows decoded frames on an even beat, whatever pace they arrived at.</summary>
        private void PlayLoop()
        {
            bool fineTimer = false;
            try { timeBeginPeriod(1); fineTimer = true; } catch { }
            try
            {
                while (!_ended)
                {
                    ViewerFrame frame;
                    lock (_bufferLock)
                        frame = _buffer.Pop(_clock.ElapsedMilliseconds);
                    if (frame == null)
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    try { Show(frame); }
                    finally { frame.Dispose(); }
                }
            }
            catch
            {
                // Anything unexpected: end the viewer, never the main app.
            }
            finally
            {
                if (fineTimer)
                {
                    try { timeEndPeriod(1); } catch { }
                }
            }
        }

        private void Show(ViewerFrame frame)
        {
            GpuVideoRenderer renderer = _renderer;
            if (renderer != null)
            {
                bool measure = _showStats;
                long started = measure ? _clock.ElapsedTicks : 0;
                try
                {
                    renderer.Render(frame);
                }
                catch
                {
                    // The card went away (driver update, sleep, remote session): paint it ourselves.
                    _rendererFailed = true;
                    _renderer = null;
                    try { BeginInvoke((MethodInvoker)UseGdi); } catch { }
                    return;
                }
                lock (_frameLock)
                {
                    _width = frame.Width;
                    _height = frame.Height;
                    if (measure)
                        _paintMs += ((_clock.ElapsedTicks - started) * 1000.0 / Stopwatch.Frequency - _paintMs) * 0.1;
                    CountShownFrame();
                }
                return;
            }
            if (frame.Software == null)
                return; // A graphics-card picture with no way to show it; the reader is switching to OpenH264.

            I420Frame picture = frame.Software;
            byte[] pixels;
            int size = picture.Width * picture.Height * 4;
            lock (_frameLock)
            {
                pixels = _spare != null && _spare.Length == size ? _spare : new byte[size];
                _spare = null;
            }
            GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { ColorConvert.I420ToBgra(picture, pin.AddrOfPinnedObject(), picture.Width * 4); }
            finally { pin.Free(); }
            lock (_frameLock)
            {
                _spare = _pixels;
                _pixels = pixels;
                _width = picture.Width;
                _height = picture.Height;
            }
            // One paint at a time: if the window is slow, a frame is skipped rather than queued.
            if (Interlocked.CompareExchange(ref _paintPending, 1, 0) == 0)
            {
                try { BeginInvoke((MethodInvoker)Invalidate); }
                catch { }
            }
        }

        private void CountShownFrame()
        {
            _framesThisSecond++;
            if ((DateTime.UtcNow - _secondStart).TotalSeconds >= 1)
            {
                _fps = _framesThisSecond;
                _receivedFps = _receivedThisSecond;
                _framesThisSecond = 0;
                _receivedThisSecond = 0;
                _secondStart = DateTime.UtcNow;
            }
        }

        private string StatsLine()
        {
            int delay, count;
            long dropped, underruns;
            double jitter;
            lock (_bufferLock)
            {
                delay = _buffer.TargetDelayMs;
                count = _buffer.Count;
                dropped = _buffer.Dropped;
                underruns = _buffer.Underruns;
                jitter = _buffer.JitterMs;
            }
            return "decode: " + _decodeName + (_gpuFallback.Length > 0 ? " (" + _gpuFallback + ")" : String.Empty) + " · draw: " + (_renderer != null ? "Graphics card" : "GDI") + " · received " + _receivedFps + " fps · shown " + _fps + " fps · decode " + _decodeMs.ToString("0.0") + " ms · draw " + _paintMs.ToString("0.0") + " ms · jitter " + jitter.ToString("0") + " ms · buffer " + delay + " ms (" + count + " frames) · ran dry " + underruns + " · dropped " + dropped
                + (_networkStats.Length > 0 ? "\n" + _networkStats : String.Empty);
        }

        private bool ReadExactly(byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = _pipe.Read(buffer, read, count - read);
                if (n <= 0)
                    return false;
                read += n;
            }
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Interlocked.Exchange(ref _paintPending, 0);
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            bool measure = _showStats;
            long started = measure ? _clock.ElapsedTicks : 0;
            int w, h, fps;
            lock (_frameLock)
            {
                byte[] pixels = _pixels;
                w = _width;
                h = _height;
                fps = _fps;
                if (pixels == null)
                {
                    TextRenderer.DrawText(g, _ended ? "The stream has ended." : "Connecting to the stream…", Font, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                // Letterbox: keep the picture's shape.
                double scale = Math.Min((double)ClientSize.Width / w, (double)ClientSize.Height / h);
                int dw = (int)(w * scale), dh = (int)(h * scale);
                Rectangle target = new Rectangle((ClientSize.Width - dw) / 2, (ClientSize.Height - dh) / 2, dw, dh);
                IntPtr hdc = g.GetHdc();
                try
                {
                    BITMAPINFOHEADER info = new BITMAPINFOHEADER();
                    info.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                    info.biWidth = w;
                    info.biHeight = -h;
                    info.biPlanes = 1;
                    info.biBitCount = 32;
                    SetStretchBltMode(hdc, scale < 1 ? 4 /* HALFTONE */ : 3 /* COLORONCOLOR */);
                    StretchDIBits(hdc, target.X, target.Y, target.Width, target.Height, 0, 0, w, h, pixels, ref info, 0, 0x00CC0020);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
                CountShownFrame();
                if (measure)
                    _paintMs += ((_clock.ElapsedTicks - started) * 1000.0 / Stopwatch.Frequency - _paintMs) * 0.1;
            }
            if (_ended)
                TextRenderer.DrawText(g, "The stream has ended.", Font, new Rectangle(0, 8, ClientSize.Width, 24), Color.White, TextFormatFlags.HorizontalCenter);
            else if (_showStats)
            {
                string stats;
                lock (_frameLock)
                    stats = w + "×" + h + " · " + StatsLine();
                Rectangle area = new Rectangle(8, 8, ClientSize.Width - 16, 60);
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    g.FillRectangle(shade, area);
                TextRenderer.DrawText(g, stats, Font, area, Color.White, TextFormatFlags.Left | TextFormatFlags.WordBreak);
            }
            else if (!_fullScreen)
                TextRenderer.DrawText(g, w + "×" + h + " · " + fps + " fps · " + SoundText() + " (M, ↑ ↓) · double-click for full screen · F3 for stats", Font, new Rectangle(8, ClientSize.Height - 22, ClientSize.Width - 16, 20), Color.FromArgb(150, 150, 150), TextFormatFlags.Left);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")]
        private static extern int StretchDIBits(IntPtr hdc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, byte[] bits, ref BITMAPINFOHEADER info, uint usage, int rop);
    }
}
