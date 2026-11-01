using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>Screen sharing in the main window: going live, watching, and the bar while you're live.</summary>
    internal sealed partial class MainForm
    {
        private ScreenShareSender _shareSender;
        private ShareSettings _shareSettings;
        private readonly Dictionary<uint, StreamViewerHost> _viewers = new Dictionary<uint, StreamViewerHost>();
        private VoipEngine _streamEngine;
        private bool _toolInstallRunning;

        /// <summary>Hooks the engine's video into the viewer helpers (once per room).</summary>
        private void AttachStreams(VoipEngine engine)
        {
            if (engine == null || Object.ReferenceEquals(engine, _streamEngine))
                return;
            _streamEngine = engine;
            engine.AllowDirectStreams = _preferences.AllowDirectStreams;
            engine.VideoFrameReceived = delegate(uint sharer, byte[] frame, bool keyframe)
            {
                StreamViewerHost viewer;
                lock (_viewers)
                    _viewers.TryGetValue(sharer, out viewer);
                if (viewer != null)
                    viewer.Push(frame, keyframe);
            };
        }

        /// <summary>Stops sharing and closes every viewer (leaving a room, quitting).</summary>
        private void StopAllStreams()
        {
            StopSharing();
            List<StreamViewerHost> viewers;
            lock (_viewers)
            {
                viewers = new List<StreamViewerHost>(_viewers.Values);
                _viewers.Clear();
            }
            foreach (StreamViewerHost v in viewers)
                v.Dispose();
            _streamEngine = null;
        }

        // ----- Sharing -----

        private void ShareScreenClicked(object sender, EventArgs e)
        {
            VoipEngine engine = _engine;
            if (engine == null || !engine.IsInVoice)
                return;
            if (_shareSender != null)
            {
                StopSharing();
                return;
            }
            if (!ConfirmDirectNotice())
                return;
            using (ShareScreenDialog dialog = new ShareScreenDialog(_preferences.ShareHeight, _preferences.ShareFps))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Settings.Source == null)
                    return;
                _shareSettings = dialog.Settings;
                _preferences.ShareHeight = dialog.Settings.MaxHeight;
                _preferences.ShareFps = dialog.Settings.Fps;
                PreferencesStore.Save(_preferences);
            }
            WithVideoTools(delegate { StartSharing(); });
        }

        private void StartSharing()
        {
            VoipEngine engine = _engine;
            if (engine == null || !engine.IsInVoice || _shareSettings == null)
                return;
            Size size = _shareSettings.Source.IsWindow
                ? FrameScaler.Fit(_shareSettings.Source.Bounds.Width, _shareSettings.Source.Bounds.Height, _shareSettings.MaxHeight)
                : FrameScaler.Fit(_shareSettings.Source.Bounds.Width, _shareSettings.Source.Bounds.Height, _shareSettings.MaxHeight);
            int width = Math.Max(160, size.Width), height = Math.Max(120, size.Height);
            if (!engine.StartScreenShare(width, height, _shareSettings.Fps))
            {
                MessageBox.Show(this, "That screen size isn't supported for sharing.", "Share screen", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _shareSender = new ScreenShareSender(engine, _shareSettings);
            PlaySound(SoundCue.Unmute);
            RefreshShell(engine);
        }

        private SharePreviewForm _preview;

        private void ShowSharePreview()
        {
            if (_shareSender == null)
                return;
            if (_preview != null && !_preview.IsDisposed) { _preview.Activate(); return; }
            ScreenShareSender sender = _shareSender;
            _preview = new SharePreviewForm(delegate { return sender.LatestPreview; });
            _preview.Show(this);
        }

        private void StopSharing()
        {
            if (_preview != null && !_preview.IsDisposed) _preview.Close();
            if (_shareSender != null)
            {
                _shareSender.Dispose();
                _shareSender = null;
                PlaySound(SoundCue.Mute);
            }
            if (_engine != null && _engine.IsSharingScreen)
                _engine.StopScreenShare();
        }

        /// <summary>UI timer: show problems from the share thread and keep the live bar current.</summary>
        private void RefreshSharing(VoipEngine engine)
        {
            if (_shareSender != null && (engine == null || !engine.IsSharingScreen || !engine.IsInVoice))
                StopSharing(); // Left voice or the room: the stream has ended.
            if (_shareSender != null && _shareSender.Error != null)
            {
                string error = _shareSender.Error;
                StopSharing();
                MessageBox.Show(this, "Screen sharing stopped: " + error, "Share screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            bool live = _shareSender != null;
            _liveBar.Visible = live;
            if (live)
            {
                int viewers = engine.StreamViewerCount;
                Size s = _shareSender.OutputSize;
                _liveBar.Text = "You're live · " + (s.Height > 0 ? s.Height + "p" : _shareSettings.MaxHeight + "p") + _shareSettings.Fps
                    + " · " + (viewers == 0 ? "nobody watching yet" : viewers + " watching")
                    + (viewers > 0 && _shareSender.ActualFps > 0 ? " · " + _shareSender.ActualFps + " fps, " + (_shareSender.BitrateKbps / 1000.0).ToString("0.#") + " Mbps" : String.Empty);
                _liveBar.Invalidate();
            }
            _voiceCard.ScreenButton.Toggled = live;
        }

        // ----- Watching -----

        private void WatchStream(RoomMemberInfo member)
        {
            VoipEngine engine = _engine;
            if (engine == null || member == null || engine.GetLiveStream(member.Id) == null)
                return;
            lock (_viewers)
            {
                StreamViewerHost existing;
                if (_viewers.TryGetValue(member.Id, out existing))
                    return; // Already open.
            }
            if (!ConfirmDirectNotice())
                return;
            WithVideoTools(delegate { OpenViewer(member.Id, member.Name); });
        }

        private void OpenViewer(uint sharerId, string name)
        {
            VoipEngine engine = _engine;
            if (engine == null)
                return;
            StreamViewerHost viewer;
            try
            {
                viewer = new StreamViewerHost(sharerId, name + "'s screen · mæla");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't open the stream viewer: " + ex.Message, "Watch stream", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            viewer.NeedKeyframe += delegate { VoipEngine e = _engine; if (e != null) e.RequestStreamKeyframe(sharerId); };
            viewer.Closed += delegate
            {
                lock (_viewers)
                {
                    StreamViewerHost current;
                    if (_viewers.TryGetValue(sharerId, out current) && Object.ReferenceEquals(current, viewer))
                        _viewers.Remove(sharerId);
                }
                VoipEngine e = _engine;
                if (e != null)
                    e.StopWatching(sharerId);
            };
            lock (_viewers)
                _viewers[sharerId] = viewer;
            if (!engine.WatchStream(sharerId))
            {
                viewer.Dispose();
                lock (_viewers) _viewers.Remove(sharerId);
            }
        }

        /// <summary>UI timer: close viewers whose stream ended.</summary>
        private void RefreshViewers(VoipEngine engine)
        {
            List<uint> ended = new List<uint>();
            lock (_viewers)
            {
                foreach (uint sharer in _viewers.Keys)
                    if (engine == null || engine.GetLiveStream(sharer) == null) ended.Add(sharer);
            }
            foreach (uint sharer in ended)
            {
                StreamViewerHost viewer;
                lock (_viewers)
                {
                    if (!_viewers.TryGetValue(sharer, out viewer)) continue;
                    _viewers.Remove(sharer);
                }
                viewer.Dispose();
            }
        }

        // ----- Shared helpers -----

        /// <summary>
        /// First time only: explain that a direct connection shows the other person your IP
        /// address, and offer to always go through the host instead.
        /// </summary>
        private bool ConfirmDirectNotice()
        {
            if (_preferences.StreamNoticeShown || !_preferences.AllowDirectStreams)
                return true;
            DialogResult answer = MessageBox.Show(this,
                "Screen sharing connects you straight to the people sharing or watching, for the best quality. " +
                "That means they can see your IP address (nobody else in the room can).\r\n\r\n" +
                "Yes: connect directly (recommended).\r\nNo: always go through the host instead (lower quality, keeps your IP private).\r\n\r\n" +
                "You can change this later in Settings.",
                "Screen sharing", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            if (answer == DialogResult.Cancel)
                return false;
            _preferences.StreamNoticeShown = true;
            _preferences.AllowDirectStreams = answer == DialogResult.Yes;
            PreferencesStore.Save(_preferences);
            if (_engine != null)
                _engine.AllowDirectStreams = _preferences.AllowDirectStreams;
            return true;
        }

        /// <summary>Makes sure OpenH264 is installed (downloading it from Cisco, after asking, if not), then runs <paramref name="then"/>.</summary>
        private void WithVideoTools(MethodInvoker then)
        {
            if (OpenH264Pack.Verify())
            {
                then();
                return;
            }
            if (IntPtr.Size != 8)
            {
                MessageBox.Show(this, "Screen sharing needs 64-bit Windows.", "Screen sharing", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_toolInstallRunning)
                return;
            if (MessageBox.Show(this,
                "Screen sharing uses OpenH264, Cisco's open-source video codec (BSD licence; Cisco pays the H.264 patent fees).\r\n\r\n" +
                "mæla will download it once from Cisco's official server (about 1 MB) and check it against a fingerprint built into mæla before using it.",
                "Download OpenH264?", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;
            _toolInstallRunning = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { OpenH264Pack.Install(); }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _toolInstallRunning = false;
                        if (error != null)
                            MessageBox.Show(this, "OpenH264 couldn't be installed: " + error, "Screen sharing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        else
                            then();
                    });
                }
                catch { }
            });
        }
    }

    /// <summary>Pick what to share and how.</summary>
    internal sealed class ShareScreenDialog : ThemedDialog
    {
        private readonly ListBox _sources = new ListBox();
        private readonly ComboBox _quality = new ComboBox();
        private readonly ComboBox _rate = new ComboBox();
        private readonly ToggleSwitch _cursor;
        public readonly ShareSettings Settings = new ShareSettings();

        public ShareScreenDialog(int height, int fps) : base("Share screen", 560, 560)
        {
            AddHeading("Share your screen", "People in your voice channel can choose to watch. Sharing one window keeps everything else private.");
            AddFieldLabel("What to share", Margin_X, Cursor_Y);
            Cursor_Y += 22;
            _sources.SetBounds(Margin_X, Cursor_Y, ClientSize.Width - Margin_X * 2, 200);
            _sources.BorderStyle = BorderStyle.None;
            _sources.BackColor = Theme.Current.Surface;
            _sources.ForeColor = Theme.Current.Text;
            _sources.Font = Ui.Body;
            _sources.IntegralHeight = false;
            _sources.ItemHeight = 26;
            _sources.DrawMode = DrawMode.OwnerDrawFixed;
            _sources.DrawItem += DrawSource;
            foreach (CaptureSource s in CaptureSource.Monitors()) _sources.Items.Add(s);
            List<CaptureSource> windows = CaptureSource.Windows();
            if (windows.Count > 0) _sources.Items.Add("WINDOWS");
            foreach (CaptureSource s in windows) _sources.Items.Add(s);
            _sources.SelectedIndex = 0;
            _sources.DoubleClick += delegate { if (_sources.SelectedItem is CaptureSource) { DialogResult = DialogResult.OK; } };
            Controls.Add(_sources);
            Cursor_Y += 212;

            int half = (ClientSize.Width - Margin_X * 2 - 16) / 2;
            AddFieldLabel("Quality", Margin_X, Cursor_Y);
            AddFieldLabel("Frame rate", Margin_X + half + 16, Cursor_Y);
            Cursor_Y += 22;
            foreach (string q in new string[] { "720p", "1080p", "1440p", "4K (2160p)" }) _quality.Items.Add(q);
            _quality.SelectedIndex = height <= 720 ? 0 : height <= 1080 ? 1 : height <= 1440 ? 2 : 3;
            foreach (string r in new string[] { "30 fps", "60 fps", "120 fps" }) _rate.Items.Add(r);
            _rate.SelectedIndex = fps <= 30 ? 0 : fps <= 60 ? 1 : 2;
            Style(_quality); _quality.SetBounds(Margin_X, Cursor_Y, half, 28); Controls.Add(_quality);
            Style(_rate); _rate.SetBounds(Margin_X + half + 16, Cursor_Y, half, 28); Controls.Add(_rate);
            Cursor_Y += 44;
            _cursor = AddToggle("Show my mouse pointer", null, true);
            AddText("Higher quality needs more upload speed: about 10 Mbps per viewer at 1080p60. mæla lowers quality automatically if a viewer can't keep up.",
                Theme.Current.Muted, ClientSize.Width - Margin_X * 2).Font = Ui.Small;

            FlatButton cancel = new FlatButton(); cancel.Text = "Cancel"; cancel.DialogResult = DialogResult.Cancel;
            FlatButton go = new FlatButton(); go.Text = "Go live"; go.DialogResult = DialogResult.OK;
            AddButtons(cancel, go);
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (DialogResult != DialogResult.OK) return;
                CaptureSource source = _sources.SelectedItem as CaptureSource;
                if (source == null) { e.Cancel = true; return; }
                Settings.Source = source;
                Settings.MaxHeight = new int[] { 720, 1080, 1440, 2160 }[_quality.SelectedIndex];
                Settings.Fps = new int[] { 30, 60, 120 }[_rate.SelectedIndex];
                Settings.ShowCursor = _cursor.Checked;
            };
        }

        private static void Style(ComboBox box)
        {
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.FlatStyle = FlatStyle.Flat;
            box.BackColor = Theme.Current.Surface;
            box.ForeColor = Theme.Current.Text;
        }

        private void DrawSource(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Theme t = Theme.Current;
            object item = _sources.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0 && item is CaptureSource;
            using (SolidBrush b = new SolidBrush(selected ? t.AccentSoft : t.Surface))
                e.Graphics.FillRectangle(b, e.Bounds);
            CaptureSource source = item as CaptureSource;
            if (source == null)
            {
                Ui.DrawText(e.Graphics, item.ToString(), Ui.Tiny, new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, e.Bounds.Width - 20, e.Bounds.Height), t.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                return;
            }
            Icons.Draw(e.Graphics, source.IsWindow ? IconKind.Copy : IconKind.Screen, new RectangleF(e.Bounds.Left + 10, e.Bounds.Top + 5, 16, 16), t.Text2);
            Ui.DrawText(e.Graphics, source.Name, Ui.Body, new Rectangle(e.Bounds.Left + 34, e.Bounds.Top, e.Bounds.Width - 44, e.Bounds.Height), t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    /// <summary>Your own stream, as sent (drawn from your own frames; nothing is decoded).</summary>
    internal sealed class SharePreviewForm : Form
    {
        private readonly Func<I420Frame> _source;
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private Bitmap _bitmap;
        private I420Frame _shown;

        public SharePreviewForm(Func<I420Frame> source)
        {
            _source = source;
            Text = "Your stream · preview";
            BackColor = Color.Black;
            ClientSize = new Size(480, 270);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            try { Icon = Branding.LoadAppIcon(); } catch { }
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _timer.Interval = 100;
            _timer.Tick += delegate { Refresh(); };
            _timer.Start();
            FormClosed += delegate { _timer.Dispose(); if (_bitmap != null) _bitmap.Dispose(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            I420Frame f = _source();
            if (f != null && !Object.ReferenceEquals(f, _shown))
            {
                _shown = f;
                if (_bitmap == null || _bitmap.Width != f.Width || _bitmap.Height != f.Height)
                {
                    if (_bitmap != null) _bitmap.Dispose();
                    _bitmap = new Bitmap(f.Width, f.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                }
                System.Drawing.Imaging.BitmapData d = _bitmap.LockBits(new Rectangle(0, 0, f.Width, f.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try { ColorConvert.I420ToBgra(f, d.Scan0, d.Stride); }
                finally { _bitmap.UnlockBits(d); }
            }
            if (_bitmap == null)
                return;
            double s = Math.Min((double)ClientSize.Width / _bitmap.Width, (double)ClientSize.Height / _bitmap.Height);
            int w = (int)(_bitmap.Width * s), h = (int)(_bitmap.Height * s);
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            e.Graphics.DrawImage(_bitmap, (ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }
    }

    /// <summary>Red bar across the top while you're live, with Stop.</summary>
    internal sealed class LiveBar : Control
    {
        public readonly FlatButton StopButton = new FlatButton();
        public readonly FlatButton PreviewButton = new FlatButton();

        public LiveBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 40;
            StopButton.Look = ButtonLook.Secondary;
            StopButton.Text = "Stop sharing";
            StopButton.Size = new Size(120, 28);
            Controls.Add(StopButton);
            PreviewButton.Look = ButtonLook.Secondary;
            PreviewButton.Text = "Preview";
            PreviewButton.Size = new Size(90, 28);
            Controls.Add(PreviewButton);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            StopButton.Location = new Point(Width - StopButton.Width - 12, 6);
            PreviewButton.Location = new Point(StopButton.Left - PreviewButton.Width - 8, 6);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            e.Graphics.Clear(t.Danger);
            StopButton.Underlay = t.Danger;
            PreviewButton.Underlay = t.Danger;
            using (SolidBrush dot = new SolidBrush(Color.White))
                e.Graphics.FillEllipse(dot, 16, 15, 10, 10);
            Ui.DrawText(e.Graphics, Text, Ui.BodyBold, new Rectangle(34, 0, Width - 280, Height), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }
}
