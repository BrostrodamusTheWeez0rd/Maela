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
        private const int MaxQueuedFrames = 40;
        private readonly uint _sharerId;
        private readonly Process _process;
        private readonly IntPtr _job;
        private readonly AnonymousPipeServerStream _pipe;
        private readonly Queue<byte[]> _queue = new Queue<byte[]>();
        private readonly Thread _writer;
        private volatile bool _closed;
        private bool _skipUntilKeyframe;

        public uint SharerId { get { return _sharerId; } }
        public bool Sandboxed { get; private set; }
        public event EventHandler Closed;
        /// <summary>Raised (writer thread) when frames had to be dropped and a keyframe is needed.</summary>
        public event EventHandler NeedKeyframe;

        public StreamViewerHost(uint sharerId, string title)
        {
            _sharerId = sharerId;
            _pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable, 1 << 20);
            string handle = _pipe.GetClientHandleAsString();
            string exe = Application.ExecutablePath;
            string args = "\"" + exe + "\" --watch " + handle + " \"" + title.Replace("\"", "'") + "\"";
            bool sandboxed;
            _process = SandboxLauncher.Start(exe, args, out _job, out sandboxed);
            Sandboxed = sandboxed;
            _pipe.DisposeLocalCopyOfClientHandle();
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
                message[4] = keyframe ? (byte)1 : (byte)0;
                Buffer.BlockCopy(frame, 0, message, 5, frame.Length);
                _queue.Enqueue(message);
                Monitor.Pulse(_queue);
            }
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

    /// <summary>The viewer window, running inside the locked-down helper process.</summary>
    internal sealed class StreamViewerForm : Form
    {
        private readonly AnonymousPipeClientStream _pipe;
        private readonly Thread _reader;
        private readonly object _frameLock = new object();
        private byte[] _pixels;
        private int _width, _height;
        private bool _ended;
        private int _fps, _framesThisSecond;
        private DateTime _secondStart = DateTime.UtcNow;
        private FormWindowState _restoreState;
        private FormBorderStyle _restoreBorder;
        private bool _fullScreen;

        /// <summary>Entry point for "Maela.exe --watch handle title".</summary>
        public static void RunHelper(string pipeHandle, string title)
        {
            Application.EnableVisualStyles();
            Application.Run(new StreamViewerForm(pipeHandle, title));
        }

        private StreamViewerForm(string pipeHandle, string title)
        {
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
            _reader.Start();
            DoubleClick += delegate { ToggleFullScreen(); };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F11) ToggleFullScreen();
                else if (e.KeyCode == Keys.Escape && _fullScreen) ToggleFullScreen();
            };
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
            H264Decoder decoder = null;
            try
            {
                decoder = new H264Decoder();
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
                    I420Frame frame = decoder.Decode(data);
                    if (frame == null)
                        continue;
                    byte[] pixels = new byte[frame.Width * frame.Height * 4];
                    GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try { ColorConvert.I420ToBgra(frame, pin.AddrOfPinnedObject(), frame.Width * 4); }
                    finally { pin.Free(); }
                    lock (_frameLock)
                    {
                        _pixels = pixels;
                        _width = frame.Width;
                        _height = frame.Height;
                        _framesThisSecond++;
                        if ((DateTime.UtcNow - _secondStart).TotalSeconds >= 1)
                        {
                            _fps = _framesThisSecond;
                            _framesThisSecond = 0;
                            _secondStart = DateTime.UtcNow;
                        }
                    }
                    try { BeginInvoke((MethodInvoker)Invalidate); } catch { break; }
                }
            }
            catch
            {
                // Anything unexpected: end the viewer, never the main app.
            }
            finally
            {
                if (decoder != null) decoder.Dispose();
            }
            _ended = true;
            try { BeginInvoke((MethodInvoker)Invalidate); } catch { }
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
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            byte[] pixels;
            int w, h, fps;
            lock (_frameLock) { pixels = _pixels; w = _width; h = _height; fps = _fps; }
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
            if (_ended)
                TextRenderer.DrawText(g, "The stream has ended.", Font, new Rectangle(0, 8, ClientSize.Width, 24), Color.White, TextFormatFlags.HorizontalCenter);
            else if (!_fullScreen)
                TextRenderer.DrawText(g, w + "×" + h + " · " + fps + " fps · double-click for full screen", Font, new Rectangle(8, ClientSize.Height - 22, ClientSize.Width - 16, 20), Color.FromArgb(150, 150, 150), TextFormatFlags.Left);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")]
        private static extern int StretchDIBits(IntPtr hdc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, byte[] bits, ref BITMAPINFOHEADER info, uint usage, int rop);
    }
}
