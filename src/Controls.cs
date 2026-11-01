using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Maela
{
    internal enum ButtonLook { Primary, Secondary, Danger, Ghost, Soft }

    /// <summary>Rounded, theme-aware button with optional icon. Works as a dialog Accept/Cancel button.</summary>
    internal class FlatButton : Control, IButtonControl
    {
        private bool _hover;
        private bool _pressed;
        private ButtonLook _look = ButtonLook.Secondary;
        private IconKind _icon = IconKind.None;
        private bool _toggled;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Radius = 9;
            IconSize = 16;
            Size = new Size(110, 34);
            Font = Ui.SmallBold;
            TabStop = false;
        }

        public ButtonLook Look { get { return _look; } set { _look = value; Invalidate(); } }
        public IconKind Icon { get { return _icon; } set { _icon = value; Invalidate(); } }
        public int Radius { get; set; }
        public int IconSize { get; set; }

        /// <summary>For toggles such as mute: shows the "on" (warning) state.</summary>
        public bool Toggled { get { return _toggled; } set { if (_toggled != value) { _toggled = value; Invalidate(); } } }

        /// <summary>Colour behind the button (so rounded corners blend). Defaults to the parent's colour.</summary>
        public Color Underlay { get; set; }

        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value) { }

        public void PerformClick()
        {
            if (Enabled)
                OnClick(EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (DialogResult != DialogResult.None)
            {
                Form form = FindForm();
                if (form != null)
                    form.DialogResult = DialogResult;
            }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            Color under = Underlay.A > 0 ? Underlay : (Parent != null ? Parent.BackColor : t.Main);
            g.Clear(under);

            Color back;
            Color fore;
            switch (_look)
            {
                case ButtonLook.Primary:
                    back = t.Accent;
                    fore = t.OnAccent;
                    if (_hover) back = Ui.Mix(back, Color.White, 0.10f);
                    if (_pressed) back = Ui.Mix(t.Accent, Color.Black, 0.12f);
                    break;
                case ButtonLook.Danger:
                    back = Ui.Mix(under, t.Danger, t.Dark ? 0.20f : 0.12f);
                    fore = t.Dark ? Ui.Mix(t.Danger, Color.White, 0.25f) : t.Danger;
                    if (_hover) { back = t.Danger; fore = Color.White; }
                    break;
                case ButtonLook.Soft:
                    back = Ui.Mix(under, t.Accent, t.Dark ? 0.18f : 0.12f);
                    fore = t.Accent2;
                    if (_hover) back = Ui.Mix(under, t.Accent, t.Dark ? 0.26f : 0.18f);
                    break;
                case ButtonLook.Ghost:
                    back = _hover || _pressed ? Ui.Mix(under, t.Text, t.Dark ? 0.08f : 0.07f) : under;
                    fore = t.Text2;
                    break;
                default:
                    back = Ui.Mix(under, t.Text, t.Dark ? 0.08f : 0.06f);
                    fore = t.Text;
                    if (_hover) back = Ui.Mix(under, t.Text, t.Dark ? 0.13f : 0.10f);
                    break;
            }
            if (_toggled)
            {
                back = Ui.Mix(under, t.Danger, t.Dark ? 0.20f : 0.12f);
                fore = t.Dark ? Ui.Mix(t.Danger, Color.White, 0.20f) : t.Danger;
            }
            if (!Enabled)
            {
                back = Ui.Mix(under, back, 0.5f);
                fore = Ui.Mix(under, fore, 0.45f);
            }

            if (back != under)
                Ui.FillRound(g, back, new RectangleF(0, 0, Width, Height), Radius);

            string text = Text ?? String.Empty;
            Size textSize = text.Length > 0 ? Ui.Measure(g, text, Font) : Size.Empty;
            int gap = text.Length > 0 && _icon != IconKind.None ? 7 : 0;
            int iconWidth = _icon != IconKind.None ? IconSize : 0;
            int total = iconWidth + gap + textSize.Width;
            int x = (Width - total) / 2;
            if (_icon != IconKind.None)
                Icons.Draw(g, _icon, new RectangleF(x, (Height - IconSize) / 2f, IconSize, IconSize), fore);
            if (text.Length > 0)
                Ui.DrawText(g, text, Font, new Point(x + iconWidth + gap, (Height - textSize.Height) / 2), fore);
        }
    }

    /// <summary>A text box drawn as a rounded field (the TextBox itself sits inside).</summary>
    internal sealed class RoundField : Control
    {
        public readonly TextBox Box = new TextBox();
        private IconKind _icon;

        public RoundField(IconKind icon)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _icon = icon;
            Box.BorderStyle = BorderStyle.None;
            Box.Font = Ui.Body;
            Box.HandleCreated += delegate { ApplyPlaceholder(); };
            Controls.Add(Box);
            Cursor = Cursors.IBeam;
            Click += delegate { Box.Focus(); };
            ApplyTheme();
        }

        /// <summary>Colour of the field itself.</summary>
        public Color Fill { get; set; }

        private void ApplyPlaceholder()
        {
            if (String.IsNullOrEmpty(Placeholder) || !Box.IsHandleCreated)
                return;
            try { ChatNativeMethods.SendMessage(Box.Handle, ChatNativeMethods.EM_SETCUEBANNER, (IntPtr)1, Placeholder); }
            catch { }
        }

        public void ApplyTheme()
        {
            Theme t = Theme.Current;
            if (Fill.A == 0)
                Fill = t.Surface;
            Box.BackColor = Fill;
            Box.ForeColor = t.Text;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int left = _icon == IconKind.None ? 12 : 34;
            int h = Box.PreferredHeight;
            Box.SetBounds(left, (Height - h) / 2 + 1, Math.Max(10, Width - left - 10), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : t.Main);
            Ui.FillRound(e.Graphics, Fill, new RectangleF(0, 0, Width, Height), Math.Min(10, Height / 2));
            if (Box.Focused)
                Ui.StrokeRound(e.Graphics, Ui.Mix(Fill, t.Accent, 0.6f), new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Math.Min(10, Height / 2), 1.2f);
            if (_icon != IconKind.None)
                Icons.Draw(e.Graphics, _icon, new RectangleF(11, (Height - 16) / 2f, 16, 16), t.Muted);
        }

        /// <summary>Hint shown in the field while it's empty (drawn by Windows on the TextBox).</summary>
        public string Placeholder { get; set; }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
        }
    }

    /// <summary>Vertical strip of room tiles (Discord-style server list).</summary>
    internal sealed class RoomRail : Control
    {
        private const int Tile = 46;
        private const int Gap = 10;
        private const int TopPad = 12;
        private readonly ToolTip _tip = new ToolTip();
        private List<SavedRoom> _rooms = new List<SavedRoom>();
        private int _hover = -2;
        private int _scroll;
        private string _tipFor = String.Empty;
        private Image _mark;

        public RoomRail()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Width = 72;
            try { _mark = Branding.LoadBrandMark(); } catch { }
        }

        public SavedRoom Selected { get; set; }

        /// <summary>Returns true if a room is live (hosted by us right now or seen online).</summary>
        public Func<SavedRoom, bool> IsLive { get; set; }

        public event Action<SavedRoom> RoomClicked;
        public event Action<SavedRoom, Point> RoomMenuRequested;
        public event Action<Point> AddClicked;

        public void SetRooms(IList<SavedRoom> rooms)
        {
            _rooms = new List<SavedRoom>(rooms);
            Invalidate();
        }

        private Rectangle TileRect(int index)
        {
            // index -1 = brand mark, 0..n-1 = rooms, n = add button.
            int y = TopPad + (index + 1) * (Tile + Gap) + (index >= 0 ? 12 : 0) - _scroll;
            return new Rectangle((Width - Tile) / 2, y, Tile, Tile);
        }

        private int HitTest(Point p)
        {
            int i;
            for (i = -1; i <= _rooms.Count; i++)
            {
                if (TileRect(i).Contains(p))
                    return i;
            }
            return -2;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = HitTest(e.Location);
            if (hit != _hover)
            {
                _hover = hit;
                Invalidate();
                string tip = hit >= 0 && hit < _rooms.Count ? _rooms[hit].Name : (hit == _rooms.Count ? "Host or join a room" : String.Empty);
                if (tip != _tipFor)
                {
                    _tipFor = tip;
                    _tip.SetToolTip(this, tip);
                }
            }
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -2;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int content = TileRect(_rooms.Count).Bottom + _scroll + 12;
            _scroll = Math.Max(0, Math.Min(Math.Max(0, content - Height), _scroll - e.Delta / 3));
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int hit = HitTest(e.Location);
            if (hit >= 0 && hit < _rooms.Count)
            {
                if (e.Button == MouseButtons.Right)
                {
                    if (RoomMenuRequested != null) RoomMenuRequested(_rooms[hit], e.Location);
                }
                else if (RoomClicked != null)
                    RoomClicked(_rooms[hit]);
            }
            else if (hit == _rooms.Count && AddClicked != null)
                AddClicked(new Point(TileRect(hit).Right + 6, TileRect(hit).Top));
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(t.Rail);

            Rectangle mark = TileRect(-1);
            if (_mark != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_mark, mark);
            }
            using (SolidBrush sep = new SolidBrush(t.Line))
                g.FillRectangle(sep, (Width - 30) / 2, mark.Bottom + 10, 30, 2);

            int i;
            for (i = 0; i < _rooms.Count; i++)
            {
                SavedRoom room = _rooms[i];
                Rectangle r = TileRect(i);
                if (r.Bottom < 0 || r.Top > Height)
                    continue;
                bool selected = Object.ReferenceEquals(room, Selected);
                Color baseColor = Ui.ColorForName(room.Name);
                using (GraphicsPath path = Ui.RoundRect(r, selected || _hover == i ? 15 : 18))
                using (LinearGradientBrush brush = new LinearGradientBrush(r, Ui.Mix(baseColor, Color.White, 0.18f), Ui.Mix(baseColor, Color.Black, 0.18f), 45f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(brush, path);
                }
                Ui.DrawText(g, Initials(room.Name), Ui.BodyBold, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                if (selected || _hover == i)
                {
                    int h = selected ? 36 : 18;
                    Ui.FillRound(g, t.Text, new RectangleF(-4, r.Top + (Tile - h) / 2f, 8, h), 4);
                }
                if (IsLive != null && IsLive(room))
                {
                    using (SolidBrush ring = new SolidBrush(t.Rail))
                        g.FillEllipse(ring, r.Right - 12, r.Bottom - 12, 18, 18);
                    using (SolidBrush dot = new SolidBrush(t.Good))
                        g.FillEllipse(dot, r.Right - 9, r.Bottom - 9, 12, 12);
                }
            }

            Rectangle add = TileRect(_rooms.Count);
            if (_hover == _rooms.Count)
                Ui.FillRound(g, t.Accent, add, 15);
            else
                Ui.StrokeRound(g, t.Line, new RectangleF(add.X + 0.5f, add.Y + 0.5f, add.Width - 1, add.Height - 1), 18, 1.5f);
            Icons.Draw(g, IconKind.Plus, new RectangleF(add.X + 13, add.Y + 13, 20, 20), _hover == _rooms.Count ? Color.White : t.Accent2);
        }

        private static string Initials(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
                return "?";
            List<string> words = new List<string>();
            foreach (string w in name.Trim().Split(new char[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Char.IsLetterOrDigit(w[0]))
                    words.Add(w);
            }
            if (words.Count == 0)
                return name.Trim().Substring(0, 1).ToUpperInvariant();
            if (words.Count >= 2)
                return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
            return words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpperInvariant();
        }
    }

    /// <summary>
    /// Connection card at the bottom of the sidebar: noise filter, Leave voice (stay in the
    /// room) and Disconnect (leave the room). Leave voice only shows while you're in voice.
    /// </summary>
    internal sealed class VoiceCard : Control
    {
        public readonly FlatButton NoiseButton = new FlatButton();
        public readonly FlatButton LeaveButton = new FlatButton();
        public readonly FlatButton DisconnectButton = new FlatButton();
        private bool _inVoice;
        private string _status = String.Empty;
        private string _detail = String.Empty;
        private int _ping = -1;
        private bool _connected;

        public VoiceCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 118;
            NoiseButton.Look = ButtonLook.Secondary;
            NoiseButton.Icon = IconKind.Waves;
            NoiseButton.Text = "Noise filter";
            NoiseButton.Height = 32;
            LeaveButton.Look = ButtonLook.Secondary;
            LeaveButton.Icon = IconKind.Leave;
            LeaveButton.IconSize = 18;
            LeaveButton.Text = String.Empty;
            LeaveButton.Size = new Size(36, 32);
            DisconnectButton.Look = ButtonLook.Danger;
            DisconnectButton.Icon = IconKind.Disconnect;
            DisconnectButton.IconSize = 18;
            DisconnectButton.Text = String.Empty;
            DisconnectButton.Size = new Size(36, 32);
            Controls.Add(NoiseButton);
            Controls.Add(LeaveButton);
            Controls.Add(DisconnectButton);
        }

        public void SetState(string status, string detail, int pingMs, bool connected, bool noiseOn, bool inVoice)
        {
            if (inVoice != _inVoice || LeaveButton.Visible != inVoice)
            {
                _inVoice = inVoice;
                LeaveButton.Visible = inVoice;
                NoiseButton.Visible = inVoice;
                PerformLayout();
                Invalidate();
            }
            if (status == _status && detail == _detail && pingMs == _ping && connected == _connected && NoiseButton.Look == (noiseOn ? ButtonLook.Soft : ButtonLook.Secondary))
                return;
            _status = status;
            _detail = detail;
            _ping = pingMs;
            _connected = connected;
            NoiseButton.Look = noiseOn ? ButtonLook.Soft : ButtonLook.Secondary;
            NoiseButton.Icon = noiseOn ? IconKind.Waves : IconKind.WavesOff;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            // [ Noise filter ......... ] [Leave voice] [Disconnect]
            int right = Width - 12;
            if (!_inVoice)
            {
                DisconnectButton.SetBounds(right - 36, 12, 36, 32); // Beside the status line.
                return;
            }
            DisconnectButton.SetBounds(right - 36, 52, 36, 32);
            right -= 36 + 6;
            if (_inVoice)
            {
                LeaveButton.SetBounds(right - 36, 52, 36, 32);
                right -= 36 + 8;
            }
            NoiseButton.SetBounds(12, 52, Math.Max(40, right - 12), 32);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(t.Side);
            Ui.FillRound(g, t.Surface, new RectangleF(0, 0, Width, Height), 14);
            NoiseButton.Underlay = t.Surface;
            LeaveButton.Underlay = t.Surface;
            DisconnectButton.Underlay = t.Surface;

            Color signal = !_connected ? t.Muted : (_ping < 0 || _ping < 90 ? t.Good : (_ping < 180 ? Color.FromArgb(240, 178, 50) : t.Danger));
            Icons.Draw(g, IconKind.Signal, new RectangleF(12, 13, 18, 18), signal);
            Ui.DrawText(g, _status, Ui.SmallBold, new Point(38, 10), _connected ? t.Good : t.Text2);
            string detail = _detail + (_ping >= 0 ? "  ·  " + _ping + " ms" : String.Empty);
            Ui.DrawText(g, detail, Ui.Small, new Rectangle(38, 28, Width - (_inVoice ? 46 : 94), 16), t.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            int lockY = Height - 25;
            Icons.Draw(g, IconKind.Lock, new RectangleF(12, lockY + 1, 12, 12), t.Muted);
            Ui.DrawText(g, _inVoice ? "Encrypted · echo cancellation" : "Encrypted", Ui.Small, new Rectangle(28, lockY, Width - 36, 16), t.Muted, TextFormatFlags.Left);
        }
    }

    /// <summary>Bottom-left bar: you, plus mute / deafen / settings.</summary>
    internal sealed class UserBar : Control
    {
        /// <summary>Your profile picture (null: your initial).</summary>
        public Image Picture;
        /// <summary>Raised when you click your picture or name.</summary>
        public event EventHandler ProfileClicked;

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && e.X < Width - 124 && ProfileClicked != null)
                ProfileClicked(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = e.X < Width - 124 ? Cursors.Hand : Cursors.Default;
        }
        public readonly FlatButton MuteButton = new FlatButton();
        public readonly FlatButton DeafenButton = new FlatButton();
        public readonly FlatButton SettingsButton = new FlatButton();
        private string _name = String.Empty;
        private string _status = String.Empty;
        private Color _statusColor;
        private bool _speaking;

        public UserBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 62;
            Setup(MuteButton, IconKind.Mic);
            Setup(DeafenButton, IconKind.Headphones);
            Setup(SettingsButton, IconKind.Gear);
        }

        private void Setup(FlatButton b, IconKind icon)
        {
            b.Look = ButtonLook.Ghost;
            b.Icon = icon;
            b.IconSize = 19;
            b.Size = new Size(34, 34);
            Controls.Add(b);
        }

        public void SetState(string name, string status, Color statusColor, bool speaking, bool muted, bool deafened)
        {
            MuteButton.Toggled = muted;
            MuteButton.Icon = muted ? IconKind.MicOff : IconKind.Mic;
            DeafenButton.Toggled = deafened;
            DeafenButton.Icon = deafened ? IconKind.HeadphonesOff : IconKind.Headphones;
            if (name == _name && status == _status && statusColor == _statusColor && speaking == _speaking)
                return;
            _name = name;
            _status = status;
            _statusColor = statusColor;
            _speaking = speaking;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            SettingsButton.Location = new Point(Width - 44, 14);
            DeafenButton.Location = new Point(Width - 82, 14);
            MuteButton.Location = new Point(Width - 120, 14);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(t.Rail);
            using (Pen line = new Pen(t.Line))
                g.DrawLine(line, 0, 0, Width, 0);
            Ui.DrawAvatar(g, new Rectangle(12, 13, 36, 36), _name, _speaking, t.Rail, Picture);
            int textWidth = Width - 120 - 58;
            Ui.DrawText(g, _name, Ui.SmallBold, new Rectangle(58, 15, textWidth, 18), t.Text, TextFormatFlags.Left);
            Ui.DrawText(g, _status, Ui.Small, new Rectangle(58, 33, textWidth, 16), _statusColor, TextFormatFlags.Left);
        }
    }

    /// <summary>Top of the chat: channel name, who's here, message search and the invite pill.</summary>
    internal sealed class ChannelHeader : Control
    {
        public readonly RoundField Search = new RoundField(IconKind.Search);
        public readonly FlatButton CopyButton = new FlatButton();
        private string _title = String.Empty;
        private string _detail = String.Empty;
        private string _code = String.Empty;
        private string[] _avatars = new string[0];
        private bool _isChannel;

        public ChannelHeader()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 62;
            Search.Size = new Size(170, 34);
            Search.Placeholder = "Search messages";
            Controls.Add(Search);
            CopyButton.Look = ButtonLook.Primary;
            CopyButton.Icon = IconKind.Copy;
            CopyButton.IconSize = 14;
            CopyButton.Text = "Copy invite";
            CopyButton.Radius = 7;
            CopyButton.Size = new Size(112, 28);
            Controls.Add(CopyButton);
        }

        public void SetState(string title, string detail, bool isChannel, string[] avatars, string code)
        {
            string key = String.Join("|", avatars ?? new string[0]);
            if (title == _title && detail == _detail && isChannel == _isChannel && code == _code && key == String.Join("|", _avatars))
                return;
            _title = title;
            _detail = detail;
            _isChannel = isChannel;
            _avatars = avatars ?? new string[0];
            _code = code ?? String.Empty;
            CopyButton.Visible = _code.Length > 0;
            PerformLayout();
            Invalidate();
        }

        private int CodePillWidth { get { return _code.Length > 0 ? 330 : 0; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int right = Width - 22;
            if (_code.Length > 0)
            {
                CopyButton.Location = new Point(right - CopyButton.Width - 4, (Height - CopyButton.Height) / 2);
                right -= CodePillWidth + 12;
            }
            Search.Location = new Point(right - Search.Width, (Height - Search.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(t.Main);
            using (Pen line = new Pen(t.Line))
                g.DrawLine(line, 0, Height - 1, Width, Height - 1);

            int x = 22;
            if (_isChannel)
            {
                Icons.Draw(g, IconKind.Speaker, new RectangleF(x, (Height - 20) / 2f, 20, 20), t.Accent2);
                x += 30;
            }
            Size titleSize = Ui.Measure(g, _title, Ui.Title);
            Ui.DrawText(g, _title, Ui.Title, new Point(x, (Height - titleSize.Height) / 2), t.Text);
            x += titleSize.Width + 14;
            if (_detail.Length > 0)
            {
                using (Pen sep = new Pen(t.Line))
                    g.DrawLine(sep, x, 22, x, Height - 22);
                x += 14;
            }

            int limit = Search.Left - 16;
            if (_avatars.Length > 0)
                limit -= 18 * Math.Min(5, _avatars.Length) + 14;
            Ui.DrawText(g, _detail, Ui.Small, new Rectangle(x, 0, Math.Max(0, limit - x), Height), t.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

            int ax = Search.Left - 16 - 18 * Math.Min(5, _avatars.Length);
            int i;
            for (i = 0; i < Math.Min(5, _avatars.Length); i++)
            {
                Rectangle r = new Rectangle(ax + i * 18, (Height - 28) / 2, 28, 28);
                using (SolidBrush ring = new SolidBrush(t.Main))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillEllipse(ring, r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6);
                }
                Ui.DrawAvatar(g, r, _avatars[i], false, t.Main);
            }

            if (_code.Length > 0)
            {
                Rectangle pill = new Rectangle(Width - 22 - CodePillWidth, (Height - 36) / 2, CodePillWidth, 36);
                Ui.FillRound(g, t.Surface, pill, 10);
                CopyButton.Underlay = t.Surface;
                Ui.DrawText(g, _code, Ui.Mono, new Rectangle(pill.X + 12, pill.Y, CopyButton.Left - pill.X - 18, pill.Height), t.Text2, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }
    }

    /// <summary>Pop-up grid of colour emoji. Closes when you pick one or click elsewhere.</summary>
    internal sealed class EmojiPicker : Form
    {
        private const int Cell = 38;
        private const int Columns = 9;
        private readonly List<EmojiEntry> _visible = new List<EmojiEntry>();
        private string _group = EmojiPack.Groups[0];
        private int _hover = -1;

        public event Action<EmojiEntry> Picked;

        public EmojiPicker()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            DoubleBuffered = true;
            ClientSize = new Size(Columns * Cell + 20, 44 + 7 * Cell + 12);
            Deactivate += delegate { Close(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            SelectGroup(_group);
        }

        private void SelectGroup(string group)
        {
            _group = group;
            _visible.Clear();
            foreach (EmojiEntry entry in EmojiPack.All)
            {
                if (entry.Group == group)
                    _visible.Add(entry);
            }
            Invalidate();
        }

        private Rectangle TabRect(int i) { return new Rectangle(10 + i * 40, 6, 36, 32); }
        private Rectangle CellRect(int i) { return new Rectangle(10 + (i % Columns) * Cell, 44 + (i / Columns) * Cell, Cell, Cell); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = -1;
            int i;
            for (i = 0; i < _visible.Count; i++)
                if (CellRect(i).Contains(e.Location)) hit = i;
            if (hit != _hover)
            {
                _hover = hit;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i;
            for (i = 0; i < EmojiPack.Groups.Length; i++)
            {
                if (TabRect(i).Contains(e.Location))
                {
                    SelectGroup(EmojiPack.Groups[i]);
                    return;
                }
            }
            for (i = 0; i < _visible.Count; i++)
            {
                if (CellRect(i).Contains(e.Location))
                {
                    if (Picked != null)
                        Picked(_visible[i]);
                    Close();
                    return;
                }
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            using (Pen border = new Pen(t.Line))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            int i;
            for (i = 0; i < EmojiPack.Groups.Length; i++)
            {
                Rectangle tab = TabRect(i);
                if (EmojiPack.Groups[i] == _group)
                    Ui.FillRound(g, t.Surface2, tab, 8);
                EmojiEntry first = null;
                foreach (EmojiEntry entry in EmojiPack.All)
                {
                    if (entry.Group == EmojiPack.Groups[i]) { first = entry; break; }
                }
                DrawEmoji(g, first, new Rectangle(tab.X + 8, tab.Y + 6, 20, 20));
            }
            using (Pen sep = new Pen(t.Line))
                g.DrawLine(sep, 10, 40, Width - 10, 40);

            for (i = 0; i < _visible.Count; i++)
            {
                Rectangle r = CellRect(i);
                if (i == _hover)
                    Ui.FillRound(g, t.Surface2, r, 8);
                DrawEmoji(g, _visible[i], new Rectangle(r.X + 6, r.Y + 6, 26, 26));
            }
        }

        public static void DrawEmoji(Graphics g, EmojiEntry entry, Rectangle r)
        {
            if (entry == null)
                return;
            Image image = EmojiPack.GetImage(entry);
            if (image != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, r);
            }
            else
            {
                using (Font f = new Font("Segoe UI Emoji", r.Height * 0.62f, FontStyle.Regular, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, entry.Text, f, r, Theme.Current.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
    }
}
