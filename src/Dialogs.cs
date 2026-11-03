using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>Base for all dialogs: theme colours, fonts, title-bar styling and layout helpers.</summary>
    internal class ThemedDialog : Form
    {
        protected int Cursor_Y = 20;
        protected const int Margin_X = 24;

        public ThemedDialog(string title, int width, int height)
        {
            Text = String.IsNullOrWhiteSpace(title) ? "mæla" : title;
            ClientSize = new Size(width, height);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = Ui.Body;
            BackColor = Theme.Current.Main;
            ForeColor = Theme.Current.Text;
            try { Icon = Branding.LoadAppIcon(); } catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.StyleWindowFrame(this);
        }

        protected Label AddHeading(string text, string subtitle)
        {
            Label title = new Label();
            title.AutoSize = true;
            title.Font = Ui.Heading;
            title.ForeColor = Theme.Current.Text;
            title.Text = text;
            title.Location = new Point(Margin_X - 2, Cursor_Y);
            Controls.Add(title);
            Cursor_Y += 40;
            if (!String.IsNullOrEmpty(subtitle))
            {
                Label sub = AddText(subtitle, Theme.Current.Muted, ClientSize.Width - Margin_X * 2);
                sub.Top -= 4;
            }
            Cursor_Y += 6;
            return title;
        }

        protected Label AddText(string text, Color color, int width)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Font = Ui.Body;
            label.ForeColor = color;
            label.Text = text;
            label.Location = new Point(Margin_X, Cursor_Y);
            Size size = TextRenderer.MeasureText(text, Ui.Body, new Size(width, 0), TextFormatFlags.WordBreak);
            label.Size = new Size(width, size.Height + 2);
            Controls.Add(label);
            Cursor_Y += label.Height + 6;
            return label;
        }

        protected void AddSection(string caption)
        {
            Cursor_Y += 10;
            SectionLabel label = new SectionLabel();
            label.Text = caption.ToUpperInvariant();
            label.SetBounds(Margin_X, Cursor_Y, ClientSize.Width - Margin_X * 2, 16);
            Controls.Add(label);
            Cursor_Y += 22;
        }

        protected Label AddFieldLabel(string text, int x, int y)
        {
            Label label = new Label();
            label.AutoSize = true;
            label.Font = Ui.SmallBold;
            label.ForeColor = Theme.Current.Text2;
            label.Text = text;
            label.Location = new Point(x, y);
            Controls.Add(label);
            return label;
        }

        protected RoundField AddField(string label, string value, bool secret)
        {
            AddFieldLabel(label, Margin_X, Cursor_Y);
            Cursor_Y += 22;
            RoundField field = new RoundField(IconKind.None);
            field.Fill = Theme.Current.Surface;
            field.ApplyTheme();
            field.Box.Text = value ?? String.Empty;
            field.Box.UseSystemPasswordChar = secret;
            field.SetBounds(Margin_X, Cursor_Y, ClientSize.Width - Margin_X * 2, 40);
            Controls.Add(field);
            Cursor_Y += 52;
            return field;
        }

        protected ToggleSwitch AddToggle(string text, string detail, bool value)
        {
            ToggleSwitch toggle = new ToggleSwitch();
            toggle.Text = text;
            toggle.Detail = detail;
            toggle.Checked = value;
            toggle.SetBounds(Margin_X, Cursor_Y, ClientSize.Width - Margin_X * 2, String.IsNullOrEmpty(detail) ? 30 : 46);
            Controls.Add(toggle);
            Cursor_Y += toggle.Height + 8;
            return toggle;
        }

        protected void AddButtons(FlatButton cancel, FlatButton ok)
        {
            int y = ClientSize.Height - 58;
            int x = ClientSize.Width - Margin_X;
            if (ok != null)
            {
                ok.Look = ButtonLook.Primary;
                ok.Size = new Size(Math.Max(96, TextRenderer.MeasureText(ok.Text, Ui.SmallBold).Width + 36), 38);
                x -= ok.Width;
                ok.Location = new Point(x, y);
                Controls.Add(ok);
                AcceptButton = ok;
                x -= 10;
            }
            if (cancel != null)
            {
                cancel.Look = ButtonLook.Secondary;
                cancel.Size = new Size(96, 38);
                x -= cancel.Width;
                cancel.Location = new Point(x, y);
                Controls.Add(cancel);
                CancelButton = cancel;
            }
        }

        protected static FlatButton Button(string text, DialogResult result)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.DialogResult = result;
            b.Font = Ui.SmallBold;
            return b;
        }
    }

    /// <summary>Modern on/off switch with a label and optional description.</summary>
    internal sealed class ToggleSwitch : Control
    {
        private bool _checked;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public string Detail { get; set; }
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : t.Main);
            Rectangle track = new Rectangle(0, 3, 40, 22);
            Ui.FillRound(g, _checked ? t.Accent : Ui.Mix(t.Main, t.Text, 0.22f), track, 11);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush knob = new SolidBrush(Color.White))
                g.FillEllipse(knob, _checked ? track.Right - 19 : track.X + 3, track.Y + 3, 16, 16);
            Ui.DrawText(g, Text, Ui.Body, new Rectangle(52, 4, Width - 52, 20), t.Text, TextFormatFlags.Left);
            if (!String.IsNullOrEmpty(Detail))
                Ui.DrawText(g, Detail, Ui.Small, new Rectangle(52, 25, Width - 52, 18), t.Muted, TextFormatFlags.Left);
        }
    }

    /// <summary>A clickable preview of a layout (Dynamic or Lightweight), drawn in the current colours.</summary>
    internal sealed class LayoutCard : Control
    {
        public readonly bool Classic;
        private bool _hover;

        public LayoutCard(bool classic)
        {
            Classic = classic;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Size = new Size(300, 156);
        }

        public bool Selected { get; set; }

        /// <summary>Tagged as the one that suits this PC.</summary>
        public bool Recommended { get; set; }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : t.Main);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle preview = new Rectangle(2, 2, Width - 4, 120);
            using (GraphicsPath clip = Ui.RoundRect(preview, 10))
            {
                g.SetClip(clip);
                if (Classic)
                    PaintClassic(g, t, preview);
                else
                    PaintSpatial(g, t, preview);
                g.ResetClip();
            }
            if (Recommended)
            {
                Size text = Ui.Measure(g, "Recommended", Ui.Tiny);
                Rectangle tag = new Rectangle(Width - text.Width - 26, 10, text.Width + 16, 20);
                Ui.FillRound(g, t.Accent, tag, 10);
                Ui.DrawText(g, "Recommended", Ui.Tiny, new Point(tag.X + 8, tag.Y + 3), t.OnAccent);
            }
            Color border = Selected ? t.Accent : (_hover ? t.Text2 : t.Line);
            Ui.StrokeRound(g, border, new RectangleF(1, 1, Width - 3, 121), 10, Selected ? 2.5f : 1.2f);
            Font font = Selected ? Ui.BodyBold : Ui.Body;
            Color ink = Selected ? t.Text : t.Text2;
            // The two layouts. Themes (Spatial is the first Dynamic one) are chosen within a layout.
            Ui.DrawText(g, Classic ? "Lightweight" : "Dynamic", font, new Rectangle(0, 130, Width, 22), ink, TextFormatFlags.HorizontalCenter);
        }

        /// <summary>Room rail, channel list and chat.</summary>
        private static void PaintClassic(Graphics g, Theme t, Rectangle r)
        {
            using (SolidBrush b = new SolidBrush(t.Main)) g.FillRectangle(b, r);
            using (SolidBrush b = new SolidBrush(t.Rail)) g.FillRectangle(b, r.X, r.Y, 30, r.Height);
            using (SolidBrush b = new SolidBrush(t.Side)) g.FillRectangle(b, r.X + 30, r.Y, 74, r.Height);
            int i;
            for (i = 0; i < 3; i++)
                Ui.FillRound(g, i == 0 ? t.Accent : t.Surface2, new RectangleF(r.X + 7, r.Y + 10 + i * 22, 16, 16), 5);
            Ui.FillRound(g, Ui.Mix(t.Side, t.Accent, 0.3f), new RectangleF(r.X + 36, r.Y + 12, 60, 8), 3);
            for (i = 0; i < 4; i++)
                Ui.FillRound(g, t.Surface2, new RectangleF(r.X + 38, r.Y + 30 + i * 13, 40 + (i % 2) * 14, 6), 3);
            for (i = 0; i < 3; i++)
            {
                using (SolidBrush b = new SolidBrush(Ui.ColorForName(i == 0 ? "a" : i == 1 ? "kael" : "neon")))
                    g.FillEllipse(b, r.X + 112, r.Y + 14 + i * 26, 12, 12);
                Ui.FillRound(g, t.Text2, new RectangleF(r.X + 130, r.Y + 15 + i * 26, 50, 4), 2);
                Ui.FillRound(g, t.Muted, new RectangleF(r.X + 130, r.Y + 23 + i * 26, 90 + (i % 2) * 30, 4), 2);
            }
            Ui.FillRound(g, t.Surface, new RectangleF(r.X + 110, r.Bottom - 22, r.Width - 118, 14), 5);
            Ui.FillRound(g, t.Accent, new RectangleF(r.Right - 22, r.Bottom - 20, 12, 10), 3);
        }

        /// <summary>Rooms floating in a circle, with the list on the left and the dock along the bottom.</summary>
        private static void PaintSpatial(Graphics g, Theme t, Rectangle r)
        {
            Color space = Ui.Mix(t.Rail, Color.Black, 0.35f);
            using (SolidBrush b = new SolidBrush(space)) g.FillRectangle(b, r);
            int i;
            System.Random dots = new System.Random(4);
            using (SolidBrush b = new SolidBrush(Ui.Mix(space, t.Accent, 0.5f)))
                for (i = 0; i < 26; i++)
                    g.FillEllipse(b, r.X + dots.Next(r.Width), r.Y + dots.Next(r.Height - 18), 2, 2);

            // The list panel, and the tabs above it.
            Ui.FillRound(g, t.Accent, new RectangleF(r.X + 8, r.Y + 7, 26, 8), 3);
            Ui.FillRound(g, t.Surface, new RectangleF(r.X + 37, r.Y + 7, 22, 8), 3);
            Ui.FillRound(g, Ui.Mix(space, t.Side, 0.8f), new RectangleF(r.X + 8, r.Y + 20, 52, r.Height - 44), 5);
            for (i = 0; i < 4; i++)
                Ui.FillRound(g, t.Surface2, new RectangleF(r.X + 13, r.Y + 27 + i * 12, 34 + (i % 2) * 8, 5), 2);

            // A circle of rooms, one of them the room you're in.
            RectangleF circle = new RectangleF(r.X + 112, r.Y + 10, 120, 82);
            using (Pen ring = new Pen(Color.FromArgb(110, t.Text2), 1f))
            {
                ring.DashStyle = DashStyle.Dash;
                g.DrawEllipse(ring, circle);
            }
            PointF[] orbs = { new PointF(150, 40), new PointF(192, 30), new PointF(176, 66), new PointF(212, 58) };
            for (i = 0; i < orbs.Length; i++)
            {
                float x = r.X + orbs[i].X, y = r.Y + orbs[i].Y, size = i == 0 ? 20 : 15;
                Color colour = i == 0 ? t.Accent : Ui.ColorForName("room" + i);
                using (SolidBrush glow = new SolidBrush(Color.FromArgb(60, colour)))
                    g.FillEllipse(glow, x - size / 2 - 4, y - size / 2 - 4, size + 8, size + 8);
                using (SolidBrush b = new SolidBrush(colour))
                    g.FillEllipse(b, x - size / 2, y - size / 2, size, size);
                if (i == 0)
                    using (Pen here = new Pen(t.Good, 1.2f))
                        g.DrawEllipse(here, x - size / 2 - 3, y - size / 2 - 3, size + 6, size + 6);
            }

            // The dock, with your controls and the call.
            using (SolidBrush b = new SolidBrush(t.Side)) g.FillRectangle(b, r.X, r.Bottom - 18, r.Width, 18);
            using (SolidBrush b = new SolidBrush(Ui.ColorForName("you")))
                g.FillEllipse(b, r.X + 8, r.Bottom - 15, 12, 12);
            Ui.FillRound(g, t.Good, new RectangleF(r.X + r.Width / 2 - 20, r.Bottom - 12, 40, 5), 2);
            Ui.FillRound(g, t.Danger, new RectangleF(r.X + r.Width / 2 + 26, r.Bottom - 14, 10, 10), 3);
        }
    }

    /// <summary>A small square showing one colour setting: its background and both accents, named underneath.</summary>
    internal sealed class ColourSwatch : Control
    {
        public readonly Theme Colours;
        private bool _hover;

        public ColourSwatch(Theme colours)
        {
            Colours = colours;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Size = new Size(84, 112);
        }

        public bool Selected { get; set; }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme cur = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : cur.Main);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF square = new RectangleF(2, 2, Width - 4, Width - 4);
            Ui.FillRound(g, Colours.Rail, square, 10);
            // The two accents, overlapping, as they meet in the space.
            float big = square.Width * 0.46f, small = square.Width * 0.34f;
            using (SolidBrush b = new SolidBrush(Colours.Second))
                g.FillEllipse(b, square.Right - small - 12, square.Bottom - small - 12, small, small);
            using (SolidBrush b = new SolidBrush(Colours.Logo))
                g.FillEllipse(b, square.X + 12, square.Y + 12, big, big);
            Color border = Selected ? cur.Accent : (_hover ? cur.Text2 : cur.Line);
            Ui.StrokeRound(g, border, new RectangleF(1, 1, Width - 3, Width - 3), 10, Selected ? 2.5f : 1.2f);
            Ui.DrawText(g, Colours.Name, Selected ? Ui.SmallBold : Ui.Small, new Rectangle(0, Width + 4, Width, 18), Selected ? cur.Text : cur.Text2, TextFormatFlags.HorizontalCenter);
        }
    }

    /// <summary>A small triangle that pages a strip one way or the other.</summary>
    internal sealed class ArrowButton : Control
    {
        private readonly bool _forward;
        private bool _hover;

        public ArrowButton(bool forward)
        {
            _forward = forward;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : t.Main);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width / 2f, cy = Height / 2f, half = 7;
            PointF[] triangle = _forward
                ? new[] { new PointF(cx - half * 0.6f, cy - half), new PointF(cx + half * 0.8f, cy), new PointF(cx - half * 0.6f, cy + half) }
                : new[] { new PointF(cx + half * 0.6f, cy - half), new PointF(cx - half * 0.8f, cy), new PointF(cx + half * 0.6f, cy + half) };
            Color colour = !Enabled ? Ui.Mix(t.Main, t.Muted, 0.35f) : (_hover ? t.Accent : t.Text2);
            using (SolidBrush b = new SolidBrush(colour))
                g.FillPolygon(b, triangle);
        }
    }

    /// <summary>
    /// Every colour option as a square, more than fit on one line: the triangles at each end, or
    /// the mouse wheel over the strip, browse through them.
    /// </summary>
    internal sealed class ColourStrip : Control
    {
        private const int ArrowWidth = 24;
        private const int Gap = 12;
        private readonly System.Collections.Generic.List<ColourSwatch> _swatches = new System.Collections.Generic.List<ColourSwatch>();
        private readonly ArrowButton _back = new ArrowButton(false);
        private readonly ArrowButton _next = new ArrowButton(true);
        private int _first;
        private int _selected;
        private bool _shownSelected;

        public event Action<Theme> Picked;

        public ColourStrip(string selectedId, int width)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            int i;
            for (i = 0; i < Theme.All.Length; i++)
            {
                ColourSwatch swatch = new ColourSwatch(Theme.All[i]);
                swatch.Selected = Theme.All[i].Id == selectedId;
                if (swatch.Selected)
                    _selected = i;
                swatch.Click += SwatchClicked;
                swatch.MouseWheel += Wheel;
                _swatches.Add(swatch);
                Controls.Add(swatch);
            }
            _back.Click += delegate { Browse(-1); };
            _next.Click += delegate { Browse(1); };
            _back.MouseWheel += Wheel;
            _next.MouseWheel += Wheel;
            Controls.Add(_back);
            Controls.Add(_next);
            MouseWheel += Wheel;
            Size = new Size(width, _swatches[0].Height);
        }

        private int PerView
        {
            get
            {
                int room = Width - 2 * ArrowWidth - 2 * Gap + Gap;
                return Math.Max(1, room / (_swatches[0].Width + Gap));
            }
        }

        private void Wheel(object sender, MouseEventArgs e)
        {
            Browse(e.Delta > 0 ? -1 : 1);
        }

        private void Browse(int by)
        {
            _first += by;
            PerformLayout();
        }

        private void SwatchClicked(object sender, EventArgs e)
        {
            int i;
            for (i = 0; i < _swatches.Count; i++)
            {
                _swatches[i].Selected = Object.ReferenceEquals(_swatches[i], sender);
                if (_swatches[i].Selected)
                    _selected = i;
                _swatches[i].Invalidate();
            }
            Action<Theme> picked = Picked;
            if (picked != null)
                picked(((ColourSwatch)sender).Colours);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_swatches.Count == 0)
                return;
            int perView = PerView, square = _swatches[0].Width;
            if (!_shownSelected && Width > 0)
            {
                // Open with the chosen colour in view.
                _shownSelected = true;
                if (_selected >= perView)
                    _first = _selected - perView + 1;
            }
            _first = Math.Max(0, Math.Min(_first, _swatches.Count - perView));
            int used = perView * square + (perView - 1) * Gap;
            int left = ArrowWidth + Gap + Math.Max(0, (Width - 2 * ArrowWidth - 2 * Gap - used) / 2);
            int i;
            for (i = 0; i < _swatches.Count; i++)
            {
                bool shown = i >= _first && i < _first + perView;
                _swatches[i].Visible = shown;
                if (shown)
                    _swatches[i].Location = new Point(left + (i - _first) * (square + Gap), 0);
            }
            _back.SetBounds(0, 0, ArrowWidth, square);
            _next.SetBounds(Width - ArrowWidth, 0, ArrowWidth, square);
            _back.Enabled = _first > 0;
            _next.Enabled = _first + perView < _swatches.Count;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Current.Main);
        }
    }

    /// <summary>
    /// Shown once, the first time mæla opens after this update: a short tour of what's new, a page
    /// at a time, ending with a choice of layout, with the one picked for this PC already selected.
    /// </summary>
    internal sealed class WhatsNewDialog : ThemedDialog
    {
        private readonly LayoutCard _spatialCard = new LayoutCard(false);
        private readonly LayoutCard _lightweightCard = new LayoutCard(true);
        private readonly List<Panel> _pages = new List<Panel>();
        private readonly FlatButton _back = new FlatButton();
        private readonly FlatButton _next = new FlatButton();
        private readonly FlatButton _skip = new FlatButton();
        private readonly Label _where = new Label();
        private int _page;

        public bool SpatialChosen { get { return _spatialCard.Selected; } }

        public WhatsNewDialog(bool spatial, string graphicsCard) : base("What's new in mæla", 680, 600)
        {
            bool hasCard = graphicsCard != null;

            Page("mæla has a new look",
                "Meet the Dynamic layout, and Spatial, its first theme, with more to come. It's a whole new way to get around your rooms and friends.",
                "Your rooms and friends float together in one space. Drag the background to move around, and scroll to zoom.",
                "Click a room to fly into it, or scroll in on it. Scroll back out, or press Esc, to leave.",
                "Rooms and friends are glass orbs. They glow when they're online (amber when someone's away, red when they're busy) and look switched off when they're not.",
                "Orbs without a picture each get their own swirl of colour made from their name, so they're easy to tell apart.",
                "Your voice controls stay in the dock at the bottom wherever you are, and right-click opens options on almost anything.");

            Page("Rooms and friends, your way",
                "Sort everything into circles. They're kept on your PC only.",
                "Rooms start in Rooms and friends in Friends. Favourites and circles of your own can hold both.",
                "Drop a room or friend anywhere: into a circle, where it stays put (the circle grows to fit), or out on its own.",
                "Hold an orb and scroll to make it bigger or smaller. Drag a circle's name to move the whole circle.",
                "Right-click empty space to make a circle right there, or use + New in the side panel to host a room, join with a code or add a friend.",
                "Right-click a circle's name to tidy it back up, rename it or delete it.");

            Page("Inside a room",
                "Channels spread out round the room, with everyone in them.",
                "Click a voice channel to move into it, and double-click any channel to open its chat.",
                "A line runs from each person to their channel. It turns into a live waveform when they talk, bigger the louder they are.",
                "Rest on someone to see their card, and click them for their volume and mute. Hosts also get move, kick and ban.",
                "Hosts can drag people between channels, drag channels round the room, and hold and scroll to resize them. Everyone sees it live, and it's saved with the room.",
                "Click the room's own orb to pull its channels in, and again to let them out.");

            Page("Your profile",
                "Make your card your own in Edit profile.",
                "Give your card a banner: a colour, a gradient or a picture.",
                "Add a card deco (Glow, Sparkles or Aurora) that people see when they open your card.",
                "Add a picture deco (Orbit, Halo or Ripple) that circles your picture on your card and wherever you appear.",
                "Click your own picture in the dock and your card grows up out of it.");

            Page("Chat and voice",
                "Clearer calls and richer chat.",
                "Send pictures in rooms and friend chats with the picture button, or paste one (a screenshot too) with Ctrl+V. Click one to see it full size or save it.",
                "Pictures are rebuilt from their pixels on your PC, so nothing hidden in the file (location, camera details, its name) is ever sent.",
                "YouTube links show as a card. Nothing is loaded from YouTube unless you ask for a preview.",
                "Emoji show in colour in the Dynamic layout too.");

            Page("Make it yours",
                "Everything's in Settings.",
                "Nine colours, each with two accents. mæla's icons, even your desktop shortcuts, take your colour.",
                "Fonts: Outfit to start with, or OpenDyslexic, Atkinson Hyperlegible, Lexend, Space Grotesk or Windows' own.",
                "Text size, Reduce motion for a calmer space, and how smoothly the Dynamic layout moves.",
                "Dynamic effects decides whether you see the decos people choose for their cards.");

            // The last page: which layout.
            Panel last = BeginPage();
            AddHeading("Choose your layout", "You can switch at any time in Settings, under Layout, without leaving your room or call.");
            int width = ClientSize.Width - Margin_X * 2;
            _spatialCard.Location = new Point(Margin_X, Cursor_Y);
            _lightweightCard.Location = new Point(Margin_X + width - _lightweightCard.Width, Cursor_Y);
            _spatialCard.Selected = spatial;
            _lightweightCard.Selected = !spatial;
            _spatialCard.Recommended = hasCard;
            _lightweightCard.Recommended = !hasCard;
            _spatialCard.Click += CardClicked;
            _lightweightCard.Click += CardClicked;
            Controls.Add(_spatialCard);
            Controls.Add(_lightweightCard);
            Cursor_Y += _spatialCard.Height + 12;
            string card = String.IsNullOrEmpty(graphicsCard) ? "graphics card" : graphicsCard;
            AddText(hasCard
                ? "Dynamic is recommended for this PC: your " + card + " can run it smoothly. Lightweight is the window you already know."
                : "Lightweight is recommended for this PC: mæla couldn't find a graphics card, and Lightweight runs well anywhere. You can still try Dynamic, but it may feel slow.", Theme.Current.Muted, width);
            EndPage(last);

            // Every page the same size: the tallest.
            int height = 0;
            foreach (Panel page in _pages)
                height = Math.Max(height, page.Height);
            ClientSize = new Size(ClientSize.Width, height + 84);
            foreach (Panel page in _pages)
                page.Height = height;

            _where.AutoSize = false;
            _where.Font = Ui.Small;
            _where.ForeColor = Theme.Current.Muted;
            _where.SetBounds(Margin_X, ClientSize.Height - 48, 120, 20);
            Controls.Add(_where);
            // Straight to the choice of layout, for anyone who'd rather not read the tour.
            _skip.Text = "Skip tour";
            _skip.Look = ButtonLook.Ghost;
            _skip.SetBounds(ClientSize.Width - Margin_X - 180 - 10 - 96 - 10 - 110, ClientSize.Height - 58, 110, 38);
            _skip.Click += delegate { Show(_pages.Count - 1); };
            Controls.Add(_skip);
            _back.Text = "Back";
            _back.Look = ButtonLook.Secondary;
            _back.SetBounds(ClientSize.Width - Margin_X - 96 - 10 - 180, ClientSize.Height - 58, 96, 38);
            _back.Click += delegate { Show(_page - 1); };
            Controls.Add(_back);
            _next.Look = ButtonLook.Primary;
            _next.SetBounds(ClientSize.Width - Margin_X - 180, ClientSize.Height - 58, 180, 38);
            _next.Click += delegate
            {
                if (_page < _pages.Count - 1)
                    Show(_page + 1);
                else
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            Controls.Add(_next);
            AcceptButton = _next;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Left && _page > 0) Show(_page - 1);
                else if (e.KeyCode == Keys.Right && _page < _pages.Count - 1) Show(_page + 1);
            };
            Show(0);
        }

        /// <summary>A page of points: a heading, a line under it, then each point.</summary>
        private void Page(string title, string subtitle, params string[] points)
        {
            Panel page = BeginPage();
            AddHeading(title, subtitle);
            int width = ClientSize.Width - Margin_X * 2;
            foreach (string point in points)
            {
                AddText("•  " + point, Theme.Current.Text2, width);
                Cursor_Y += 4;
            }
            EndPage(page);
        }

        private int _pageStart;

        /// <summary>Starts a page: what's added until EndPage goes onto it.</summary>
        private Panel BeginPage()
        {
            Cursor_Y = 20;
            _pageStart = Controls.Count;
            return new Panel();
        }

        private void EndPage(Panel page)
        {
            List<Control> added = new List<Control>();
            for (int i = _pageStart; i < Controls.Count; i++)
                added.Add(Controls[i]);
            foreach (Control c in added)
            {
                Controls.Remove(c);
                page.Controls.Add(c);
            }
            page.BackColor = Theme.Current.Main;
            page.SetBounds(0, 0, ClientSize.Width, Cursor_Y + 8);
            page.Visible = false;
            Controls.Add(page);
            _pages.Add(page);
        }

        private void Show(int index)
        {
            _page = Math.Max(0, Math.Min(_pages.Count - 1, index));
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].Visible = i == _page;
            _back.Visible = _page > 0;
            _skip.Visible = _page < _pages.Count - 1;
            _next.Text = _page == _pages.Count - 1 ? "Start using mæla" : "Next";
            _next.Invalidate();
            _where.Text = (_page + 1) + " of " + _pages.Count;
        }

        private void CardClicked(object sender, EventArgs e)
        {
            _spatialCard.Selected = Object.ReferenceEquals(sender, _spatialCard);
            _lightweightCard.Selected = !_spatialCard.Selected;
            _spatialCard.Invalidate();
            _lightweightCard.Invalidate();
        }
    }

    /// <summary>A short heads-up with a "Don't show me again" switch.</summary>
    internal sealed class NoticeDialog : ThemedDialog
    {
        private readonly ToggleSwitch _dontShow;

        public NoticeDialog(string title, string text) : base(title, 440, 224)
        {
            AddHeading(title, text);
            _dontShow = AddToggle("Don't show me again", null, false);
            AddButtons(Button("Cancel", DialogResult.Cancel), Button("Continue", DialogResult.OK));
        }

        public bool DontShowAgain { get { return _dontShow.Checked; } }
    }

    internal static class TextPrompt
    {
        public static string Show(IWin32Window owner, string title, string label, string initialValue)
        {
            return Show(owner, title, label, initialValue, false);
        }

        /// <param name="secret">Mask the input (passwords). Secrets are returned untrimmed.</param>
        public static string Show(IWin32Window owner, string title, string label, string initialValue, bool secret)
        {
            using (PromptDialog dialog = new PromptDialog(title, label, initialValue, secret))
            {
                DialogResult result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                if (result != DialogResult.OK)
                    return null;
                return secret ? dialog.Value : dialog.Value.Trim();
            }
        }

        private sealed class PromptDialog : ThemedDialog
        {
            private readonly RoundField _field;

            public PromptDialog(string title, string label, string value, bool secret) : base(title, 460, 214)
            {
                AddHeading(String.IsNullOrWhiteSpace(title) ? "mæla" : title, null);
                _field = AddField(String.IsNullOrWhiteSpace(label) ? "Name" : label, value, secret);
                AddButtons(Button("Cancel", DialogResult.Cancel), Button("Save", DialogResult.OK));
                Shown += delegate { _field.Box.Focus(); _field.Box.SelectAll(); };
            }

            public string Value { get { return _field.Box.Text; } }
        }
    }

    internal sealed class HostRoomDialog : ThemedDialog
    {
        private readonly RoundField _name;
        private readonly RoundField _password;
        private readonly ToggleSwitch _keepAlive;
        private readonly ToggleSwitch _save;
        private readonly int _port;

        public string RoomName { get { return String.IsNullOrWhiteSpace(_name.Box.Text) ? "Voice Room" : _name.Box.Text.Trim(); } }
        public int Port { get { return _port; } }
        public bool SaveRoom { get { return _save.Checked; } }
        public bool KeepRoomAlive { get { return _keepAlive.Checked; } }
        public string Password { get { return _password.Box.Text; } }

        public HostRoomDialog(SavedRoom existing) : base(existing == null ? "Host a room" : "Host saved room", 480, 520)
        {
            _port = existing == null ? 0 : Math.Max(0, Math.Min(65535, existing.PreferredPort));
            AddHeading("Host a room", "Your PC runs the room. Friends join with the room code; voice and chat are encrypted.");
            _name = AddField("Room name", existing == null ? "Voice Room" : existing.Name, false);
            _password = AddField("Room password (optional)", existing == null ? String.Empty : existing.Password, true);
            Cursor_Y += 4;
            _keepAlive = AddToggle("Keep the room running", "When you leave voice or close the window, it stays up in the tray.", existing == null || existing.KeepAlive);
            _save = AddToggle("Save this room", "Keeps its name, channels and room code for next time.", true);
            _save.Enabled = existing == null;
            AddButtons(Button("Cancel", DialogResult.Cancel), Button("Host room", DialogResult.OK));
        }
    }

    internal sealed class JoinRoomDialog : ThemedDialog
    {
        private readonly RoundField _invite;
        private readonly RoundField _password;
        private readonly ToggleSwitch _save;

        public string Invite { get { return _invite.Box.Text.Trim(); } }
        public bool SaveRoom { get { return _save.Checked; } }
        public string Password { get { return _password.Box.Text; } }

        public JoinRoomDialog() : base("Join a room", 480, 400)
        {
            AddHeading("Join a room", "Paste the room code your friend sent you.");
            _invite = AddField("Room code", String.Empty, false);
            _invite.Box.Font = Ui.Mono;
            _password = AddField("Room password (if the host set one)", String.Empty, true);
            _save = AddToggle("Save this room", null, true);
            FlatButton join = Button("Join", DialogResult.OK);
            join.Click += ValidateJoin;
            AddButtons(Button("Cancel", DialogResult.Cancel), join);
        }

        private void ValidateJoin(object sender, EventArgs e)
        {
            if (RoomCode.IsValid(_invite.Box.Text))
                return;
            DialogResult = DialogResult.None;
            if (RoomCode.IsOldFormat(_invite.Box.Text))
                MessageBox.Show(this, "That's a room code from an older mæla. Rooms are encrypted now, so the host needs to send you their new code (it's longer).", "Old room code", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(this, "Enter a valid mæla room code. Copy and paste it exactly as the host sent it.", "Invalid room code", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    internal sealed class AudioDeviceItem
    {
        public readonly uint Id;
        public readonly string Name;

        public AudioDeviceItem(uint id, string name)
        {
            Id = id;
            Name = String.IsNullOrWhiteSpace(name) ? "Unknown device" : name;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>All settings on one scrolling page.</summary>
    internal sealed class SettingsForm : ThemedDialog
    {
        private readonly Panel _page = new Panel();
        private readonly RoundField _name;
        private readonly ComboBox _input = new ComboBox();
        private readonly ComboBox _output = new ComboBox();
        private readonly TrackBar _mic = new TrackBar();
        private readonly TrackBar _master = new TrackBar();
        private readonly ToggleSwitch _echo;
        private readonly ToggleSwitch _noise;
        private readonly LayoutCard _spatialCard = new LayoutCard(false);
        private readonly LayoutCard _classicCard = new LayoutCard(true);
        private readonly ToggleSwitch _dynamicEffects;
        private readonly ComboBox _frameRate = new ComboBox();
        private readonly ToggleSwitch _pauseInBackground;
        private readonly ComboBox _readingFont = new ComboBox();
        private readonly ComboBox _textScale = new ComboBox();
        private readonly ToggleSwitch _reduceMotion;
        private bool _followingLayout;
        private readonly ToggleSwitch _sounds;
        private readonly ToggleSwitch _agc;
        private readonly ToggleSwitch _autoUpdate;
        private readonly ToggleSwitch _closeToTray;
        private readonly ToggleSwitch _directStreams;
        private readonly ComboBox _streamDecode = new ComboBox();
        private readonly Label _downloadStatus = new Label();
        private readonly FlatButton _repair = new FlatButton();
        private readonly VoipEngine _engine;
        private readonly string _originalTheme;
        private AppPreferences _preferences;
        private bool _checkForUpdates;

        public AppPreferences Preferences { get { return _preferences; } }

        /// <summary>True when the user closed the dialog with "Check for updates".</summary>
        public bool CheckForUpdatesRequested { get { return _checkForUpdates; } }

        public SettingsForm(AppPreferences current, VoipEngine engine, string publicIp, string invite, string natStatus) : base("Settings", 680, 720)
        {
            _engine = engine;
            _preferences = current == null ? new AppPreferences() : current.Clone();
            _originalTheme = Theme.Current.Id;
            Theme theme = Theme.Current;

            AddHeading("Settings", null);

            AddSection("Profile");
            _name = AddField("Display name", _preferences.DisplayName, false);

            // Two layouts, Dynamic and Lightweight. Each will get its own themes to choose from once
            // there's more than one; for now each has one, and Colours go on either.
            AddSection("Layout");
            _spatialCard.Location = new Point(Margin_X, Cursor_Y);
            _classicCard.Location = new Point(Margin_X + 316, Cursor_Y);
            _spatialCard.Selected = !_preferences.ClassicLayout;
            _classicCard.Selected = _preferences.ClassicLayout;
            _spatialCard.Click += LayoutCardClicked;
            _classicCard.Click += LayoutCardClicked;
            Controls.Add(_spatialCard);
            Controls.Add(_classicCard);
            Cursor_Y += _spatialCard.Height + 8;
            AddFieldLabel("Colours", Margin_X, Cursor_Y);
            Cursor_Y += 22;
            ColourStrip colours = new ColourStrip(_preferences.Theme, ClientSize.Width - Margin_X * 2);
            colours.Location = new Point(Margin_X, Cursor_Y);
            colours.Picked += ColoursPicked;
            Controls.Add(colours);
            Cursor_Y += colours.Height + 12;
            _dynamicEffects = AddToggle("Dynamic effects", "Shows the effects people choose for their profile cards. On with the Dynamic layout to start with.", _preferences.ShowDynamicEffects);
            _dynamicEffects.CheckedChanged += delegate
            {
                if (_followingLayout)
                    return;
                _preferences.DynamicEffects = _dynamicEffects.Checked; // Your choice now, whichever theme.
                _preferences.DynamicEffectsSet = true;
            };
            AddFieldLabel("Motion in the Dynamic layout", Margin_X, Cursor_Y);
            Cursor_Y += 22;
            foreach (string rate in new string[] { "60 a second", "120 a second, for fast screens", "Light: 30 a second, for battery" })
                _frameRate.Items.Add(rate);
            StyleCombo(_frameRate);
            _frameRate.SelectedIndex = Math.Max(0, Math.Min(2, _preferences.FrameRate));
            _frameRate.SetBounds(Margin_X, Cursor_Y, ClientSize.Width - Margin_X * 2, 28);
            Controls.Add(_frameRate);
            Cursor_Y += 40;
            _pauseInBackground = AddToggle("Pause in the background", "Stops the Dynamic layout's motion while another window is in front, to save power. Off: it keeps moving.", _preferences.PauseInBackground);

            AddSection("Accessibility");
            AddFieldLabel("Font", Margin_X, Cursor_Y);
            AddFieldLabel("Text size", Margin_X + 316, Cursor_Y);
            Cursor_Y += 22;
            foreach (AppFont font in ReadingFonts.All)
                _readingFont.Items.Add(font.Name + ": " + font.Description);
            StyleCombo(_readingFont);
            _readingFont.SelectedIndex = Math.Max(0, Math.Min(ReadingFonts.All.Length - 1, _preferences.ReadingFont));
            _readingFont.DropDownWidth = 380;
            _readingFont.SetBounds(Margin_X, Cursor_Y, 300, 28);
            Controls.Add(_readingFont);
            foreach (string size in new string[] { "100%", "115%", "130%" })
                _textScale.Items.Add(size);
            StyleCombo(_textScale);
            _textScale.SelectedIndex = _preferences.TextScale == 130 ? 2 : _preferences.TextScale == 115 ? 1 : 0;
            _textScale.SetBounds(Margin_X + 316, Cursor_Y, 300, 28);
            Controls.Add(_textScale);
            Cursor_Y += 36;
            AddText("The font and text size apply in the Dynamic layout. Lightweight keeps Windows' font, which its older drawing can't change.",
                Theme.Current.Muted, ClientSize.Width - Margin_X * 2).Font = Ui.Small;
            _reduceMotion = AddToggle("Reduce motion", "No moving dot effects, growing orbs, card decos or swirls. Moving around still works.", _preferences.ReduceMotion);

            AddSection("Voice & audio");
            AddFieldLabel("Input device", Margin_X, Cursor_Y);
            AddFieldLabel("Output device", Margin_X + 316, Cursor_Y);
            Cursor_Y += 22;
            StyleCombo(_input); _input.SetBounds(Margin_X, Cursor_Y, 300, 28); Controls.Add(_input);
            StyleCombo(_output); _output.SetBounds(Margin_X + 316, Cursor_Y, 300, 28); Controls.Add(_output);
            LoadDevices();
            Cursor_Y += 40;
            AddFieldLabel("Microphone volume", Margin_X, Cursor_Y);
            AddFieldLabel("Output volume", Margin_X + 316, Cursor_Y);
            Cursor_Y += 18;
            StyleSlider(_mic, _preferences.MicVolumePercent); _mic.SetBounds(Margin_X - 4, Cursor_Y, 300, 36); Controls.Add(_mic);
            StyleSlider(_master, _preferences.MasterVolumePercent); _master.SetBounds(Margin_X + 312, Cursor_Y, 300, 36); Controls.Add(_master);
            Cursor_Y += 44;
            _echo = AddToggle("Echo cancellation", "Stops friends hearing themselves through your speakers.", _preferences.EchoCancellation);
            _noise = AddToggle("Noise filter", "Removes background noise: fans, hum, typing, clicks and more.", _preferences.NoiseSuppression);
            _agc = AddToggle("Automatic mic level", "Evens out your volume so friends hear you clearly.", _preferences.AutoGain);
            _sounds = AddToggle("Sound effects", "Chimes for joining, leaving, mute, deafen and new messages.", _preferences.SoundEffects);

            AddSection("Downloads");
            _downloadStatus.AutoSize = false;
            _downloadStatus.SetBounds(Margin_X, Cursor_Y, 420, 40);
            _downloadStatus.ForeColor = theme.Text2;
            Controls.Add(_downloadStatus);
            _repair.Text = "Repair downloads";
            _repair.Icon = IconKind.Download;
            _repair.SetBounds(ClientSize.Width - Margin_X - 180, Cursor_Y, 180, 36);
            _repair.Click += RepairClicked;
            Controls.Add(_repair);
            UpdateDownloadStatus(null);
            Cursor_Y += 48;

            AddSection("Network");
            Label network = AddText("Public address: " + (String.IsNullOrWhiteSpace(publicIp) ? "not checked" : "detected") + "  ·  " + (String.IsNullOrWhiteSpace(natStatus) ? "Network idle" : natStatus), theme.Text2, 420);
            FlatButton firewall = new FlatButton();
            firewall.Text = "Allow through Firewall";
            firewall.SetBounds(ClientSize.Width - Margin_X - 180, network.Top - 6, 180, 36);
            firewall.Click += FirewallClicked;
            Controls.Add(firewall);
            if (engine != null && engine.IsHost && engine.BanCount > 0)
            {
                FlatButton clearBans = new FlatButton();
                clearBans.Text = "Clear session bans";
                clearBans.SetBounds(ClientSize.Width - Margin_X - 180, network.Top + 36, 180, 36);
                clearBans.Click += delegate { if (_engine != null) { _engine.ClearBans(); clearBans.Enabled = false; } };
                Controls.Add(clearBans);
                Cursor_Y += 30;
            }
            FlatButton removeFirewall = new FlatButton();
            removeFirewall.Text = "Remove firewall rule";
            removeFirewall.SetBounds(Margin_X, Cursor_Y, 200, 36);
            removeFirewall.Click += delegate { RemoveFirewallClicked(); };
            Controls.Add(removeFirewall);
            Cursor_Y += 48;
            AddText("If Windows Defender wrongly flags m\u00E6la, you can exclude this file from scanning. Only do this if you got m\u00E6la from its official GitHub page.", theme.Text2, ClientSize.Width - Margin_X * 2);
            FlatButton exclude = new FlatButton();
            exclude.Text = "Add Defender exclusion";
            exclude.SetBounds(Margin_X, Cursor_Y, 200, 36);
            exclude.Click += delegate { DefenderClicked(true); };
            Controls.Add(exclude);
            FlatButton include = new FlatButton();
            include.Text = "Remove exclusion";
            include.SetBounds(Margin_X + 210, Cursor_Y, 170, 36);
            include.Click += delegate { DefenderClicked(false); };
            Controls.Add(include);
            Cursor_Y += 48;

            AddSection("App");
            _closeToTray = AddToggle("Keep running in the tray when closed", "Closing the window keeps you in your room. Right-click the tray icon to quit.", _preferences.CloseToTray);
            _directStreams = AddToggle("Connect directly for screen sharing", "Best quality. Only share or watch with people you trust. Off: goes through the host.", _preferences.AllowDirectStreams);
            AddFieldLabel("Use the graphics card to decode streams you watch", Margin_X, Cursor_Y);
            Cursor_Y += 22;
            foreach (string mode in new string[] { "Automatic: at 1080p and above", "Always: fastest, at every size", "Never: processor only, best isolated" })
                _streamDecode.Items.Add(mode);
            _streamDecode.SelectedIndex = (int)_preferences.StreamDecode;
            _streamDecode.DropDownStyle = ComboBoxStyle.DropDownList;
            _streamDecode.FlatStyle = FlatStyle.Flat;
            _streamDecode.BackColor = Theme.Current.Surface;
            _streamDecode.ForeColor = Theme.Current.Text;
            _streamDecode.SetBounds(Margin_X, Cursor_Y, ClientSize.Width - Margin_X * 2, 28);
            Controls.Add(_streamDecode);
            Cursor_Y += 36;
            AddText("Faster, but video then reaches your graphics driver, which the viewer's sandbox protects less. Anything that fails goes back to the processor.",
                Theme.Current.Muted, ClientSize.Width - Margin_X * 2).Font = Ui.Small;

            AddSection("Updates");
            _autoUpdate = AddToggle("Check for updates automatically", "mæla " + Updater.CurrentVersionText + " · updates come from GitHub releases", _preferences.AutoUpdate);
            FlatButton check = new FlatButton();
            check.Text = "Check now";
            check.SetBounds(ClientSize.Width - Margin_X - 120, _autoUpdate.Top + 4, 120, 36);
            check.Click += delegate { _checkForUpdates = true; SaveAndClose(); };
            Controls.Add(check);
            FlatButton licenses = new FlatButton();
            licenses.Look = ButtonLook.Ghost;
            licenses.Text = "Licenses";
            licenses.SetBounds(Margin_X - 8, Cursor_Y, 170, 30);
            licenses.Click += delegate { ShowLicenses(); };
            Controls.Add(licenses);
            Cursor_Y += 40;

            // Everything so far scrolls; the buttons stay put.
            int contentHeight = Cursor_Y + 10;
            _page.AutoScroll = true;
            _page.SetBounds(0, 0, ClientSize.Width, ClientSize.Height - 74);
            _page.BackColor = theme.Main;
            Control[] content = new Control[Controls.Count];
            Controls.CopyTo(content, 0);
            foreach (Control c in content)
            {
                Controls.Remove(c);
                _page.Controls.Add(c);
            }
            Controls.Add(_page);
            Panel spacer = new Panel();
            spacer.SetBounds(0, contentHeight, 1, 1);
            _page.Controls.Add(spacer);

            Panel footer = new Panel();
            footer.SetBounds(0, ClientSize.Height - 74, ClientSize.Width, 1);
            footer.BackColor = theme.Line;
            Controls.Add(footer);
            FlatButton save = Button("Save", DialogResult.None);
            save.Click += delegate { SaveAndClose(); };
            AddButtons(Button("Cancel", DialogResult.Cancel), save);
            FormClosed += delegate
            {
                if (DialogResult != DialogResult.OK)
                    Theme.Apply(Theme.ById(_originalTheme)); // Cancel undoes the live preview.
            };
        }

        private void LayoutCardClicked(object sender, EventArgs e)
        {
            _preferences.ClassicLayout = Object.ReferenceEquals(sender, _classicCard);
            _preferences.LayoutChosen = true; // From now on, theirs to keep.
            _spatialCard.Selected = !_preferences.ClassicLayout;
            _classicCard.Selected = _preferences.ClassicLayout;
            _spatialCard.Invalidate();
            _classicCard.Invalidate();
            // Until it's been switched by hand, it follows the theme.
            _followingLayout = true;
            _dynamicEffects.Checked = _preferences.ShowDynamicEffects;
            _followingLayout = false;
        }

        private void ColoursPicked(Theme picked)
        {
            _preferences.Theme = picked.Id;
            Theme.Apply(picked); // Live preview in the app, and in both cards.
            _spatialCard.Invalidate();
            _classicCard.Invalidate();
        }

        private void UpdateDownloadStatus(string message)
        {
            if (message != null)
            {
                _downloadStatus.Text = message;
                return;
            }
            _downloadStatus.Text = EmojiPack.Available
                ? "Colour emoji: " + EmojiPack.Name + " " + EmojiPack.Version + " (" + EmojiPack.License + ") — installed and verified."
                : "Colour emoji: not installed. Repair downloads them from the official " + EmojiPack.Name + " project and checks every file.";
        }

        private void RepairClicked(object sender, EventArgs e)
        {
            _repair.Enabled = false;
            UpdateDownloadStatus("Downloading and verifying…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try
                {
                    EmojiPack.Install(delegate(int done, int total)
                    {
                        try { BeginInvoke((MethodInvoker)delegate { UpdateDownloadStatus("Downloading and verifying… " + done + " / " + total); }); } catch { }
                    }, null);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _repair.Enabled = true;
                        UpdateDownloadStatus(error == null ? null : "Download failed: " + error);
                    });
                }
                catch { }
            });
        }

        private void SaveAndClose()
        {
            _preferences.DisplayName = String.IsNullOrWhiteSpace(_name.Box.Text) ? "Guest" : _name.Box.Text.Trim();
            if (_preferences.DisplayName.Length > 32) _preferences.DisplayName = _preferences.DisplayName.Substring(0, 32);
            AudioDeviceItem input = _input.SelectedItem as AudioDeviceItem; if (input != null) _preferences.InputDeviceId = input.Id;
            AudioDeviceItem output = _output.SelectedItem as AudioDeviceItem; if (output != null) _preferences.OutputDeviceId = output.Id;
            _preferences.MicVolumePercent = _mic.Value;
            _preferences.MasterVolumePercent = _master.Value;
            _preferences.EchoCancellation = _echo.Checked;
            _preferences.NoiseSuppression = _noise.Checked;
            _preferences.SoundEffects = _sounds.Checked;
            _preferences.AutoGain = _agc.Checked;
            _preferences.CloseToTray = _closeToTray.Checked;
            if (_preferences.AllowDirectStreams != _directStreams.Checked)
                _preferences.StreamNoticeShown = true;
            _preferences.AllowDirectStreams = _directStreams.Checked;
            _preferences.StreamDecode = (StreamDecodeMode)_streamDecode.SelectedIndex;
            _preferences.FrameRate = _frameRate.SelectedIndex;
            _preferences.PauseInBackground = _pauseInBackground.Checked;
            _preferences.ReadingFont = _readingFont.SelectedIndex;
            _preferences.TextScale = _textScale.SelectedIndex == 2 ? 130 : _textScale.SelectedIndex == 1 ? 115 : 100;
            _preferences.ReduceMotion = _reduceMotion.Checked;
            if (_autoUpdate.Checked && !_preferences.AutoUpdate)
                _preferences.SkippedVersion = String.Empty;
            _preferences.AutoUpdate = _autoUpdate.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void LoadDevices()
        {
            _input.Items.Add(new AudioDeviceItem(NativeMethods.WAVE_MAPPER, "Windows default"));
            _output.Items.Add(new AudioDeviceItem(NativeMethods.WAVE_MAPPER, "Windows default"));
            try
            {
                uint i;
                for (i = 0; i < NativeMethods.waveInGetNumDevs(); i++)
                {
                    WAVEINCAPS caps;
                    if (NativeMethods.waveInGetDevCaps(i, out caps, (uint)Marshal.SizeOf(typeof(WAVEINCAPS))) == NativeMethods.MMSYSERR_NOERROR)
                        _input.Items.Add(new AudioDeviceItem(i, caps.szPname));
                }
                for (i = 0; i < NativeMethods.waveOutGetNumDevs(); i++)
                {
                    WAVEOUTCAPS caps;
                    if (NativeMethods.waveOutGetDevCaps(i, out caps, (uint)Marshal.SizeOf(typeof(WAVEOUTCAPS))) == NativeMethods.MMSYSERR_NOERROR)
                        _output.Items.Add(new AudioDeviceItem(i, caps.szPname));
                }
            }
            catch
            {
                // No audio API (only happens outside Windows).
            }
            SelectDevice(_input, _preferences.InputDeviceId);
            SelectDevice(_output, _preferences.OutputDeviceId);
        }

        private static void SelectDevice(ComboBox combo, uint id)
        {
            int i;
            for (i = 0; i < combo.Items.Count; i++)
            {
                AudioDeviceItem item = combo.Items[i] as AudioDeviceItem;
                if (item != null && item.Id == id) { combo.SelectedIndex = i; return; }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static void StyleCombo(ComboBox c)
        {
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Theme.Current.Surface;
            c.ForeColor = Theme.Current.Text;
            c.Font = Ui.Body;
        }

        private static void StyleSlider(TrackBar t, int value)
        {
            t.Minimum = 0;
            t.Maximum = 150;
            t.TickStyle = TickStyle.None;
            t.Value = Math.Max(0, Math.Min(150, value));
            t.BackColor = Theme.Current.Main;
        }

        private void FirewallClicked(object sender, EventArgs e)
        {
            ElevatedResult result = WindowsSecurity.AllowThroughFirewall(Application.ExecutablePath);
            if (result == ElevatedResult.Cancelled)
                return;
            MessageBox.Show(this, result == ElevatedResult.Done ? "m\u00E6la is allowed through Windows Firewall." : "Windows did not add the firewall rule.", "Firewall", MessageBoxButtons.OK, result == ElevatedResult.Done ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void RemoveFirewallClicked()
        {
            string path = Application.ExecutablePath;
            if (WindowsSecurity.FirewallAllows(path) == false)
            {
                MessageBox.Show(this, "m\u00E6la doesn't have a firewall rule allowing it.", "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "Without the rule, friend requests from new people and members joining your rooms may be blocked. Windows will ask you to approve this.\r\n\r\nRemove it?", "Firewall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            ElevatedResult result = WindowsSecurity.RemoveFromFirewall(path);
            if (result == ElevatedResult.Cancelled)
                return;
            MessageBox.Show(this, result == ElevatedResult.Done ? "The firewall rule has been removed." : "Windows did not remove the firewall rule.", "Firewall", MessageBoxButtons.OK, result == ElevatedResult.Done ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void DefenderClicked(bool exclude)
        {
            if (exclude && MessageBox.Show(this, "Windows Defender will stop scanning this copy of m\u00E6la. Windows will ask you to approve this.\r\n\r\nContinue?", "Defender exclusion", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            ElevatedResult result = WindowsSecurity.SetDefenderExclusion(Application.ExecutablePath, exclude);
            if (result == ElevatedResult.Cancelled)
                return;
            string manual = "Windows Security > Virus & threat protection > Manage settings > Exclusions";
            string message;
            switch (result)
            {
                case ElevatedResult.Done:
                    message = exclude ? "m\u00E6la is now excluded from Defender scans." : "The exclusion has been removed.";
                    break;
                case ElevatedResult.DidNotPersist:
                    message = "Windows didn't keep the change, probably because of Tamper Protection. You can do it by hand in " + manual + ".";
                    break;
                case ElevatedResult.Unavailable:
                    message = "Defender didn't accept the change. It may not be your active antivirus. If it is, you can do it by hand in " + manual + ".";
                    break;
                default:
                    message = "That didn't work. You can do it by hand in " + manual + ".";
                    break;
            }
            MessageBox.Show(this, message, "Defender exclusion", MessageBoxButtons.OK, result == ElevatedResult.Done ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private static string ReadResource(string name, string fallback)
        {
            try
            {
                using (System.IO.Stream stream = typeof(SettingsForm).Assembly.GetManifestResourceStream(name))
                {
                    if (stream != null)
                        using (System.IO.StreamReader reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                            return reader.ReadToEnd();
                }
            }
            catch { }
            return fallback;
        }

        private void ShowLicenses()
        {
            string text = ReadResource("LICENSE.txt", "mæla is released under the MIT License.")
                + "\n\n==============================================================================\n\n"
                + ReadResource("THIRD_PARTY_NOTICES.txt", "mæla includes the Opus audio codec (Concentus C# port), BSD 3-clause license.");
            using (ThemedDialog form = new ThemedDialog("Licenses", 640, 480))
            {
                TextBox box = new TextBox();
                box.Multiline = true;
                box.ReadOnly = true;
                box.ScrollBars = ScrollBars.Vertical;
                box.Dock = DockStyle.Fill;
                box.BackColor = Theme.Current.Surface;
                box.ForeColor = Theme.Current.Text2;
                box.BorderStyle = BorderStyle.None;
                box.Font = Ui.Mono;
                box.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                form.Controls.Add(box);
                form.ShowDialog(this);
            }
        }
    }

    /// <summary>
    /// Welcome screen on first run: name, theme, and downloading the open-source colour emoji
    /// from their official source (verified). Skipping is fine; it asks again next launch.
    /// </summary>
    internal sealed class SetupDialog : ThemedDialog
    {
        private readonly RoundField _name;
        private readonly Label _status;
        private readonly ProgressStrip _progress = new ProgressStrip();
        private readonly FlatButton _start;
        private readonly FlatButton _skip;
        private readonly AppPreferences _preferences;
        private bool _busy;
        private bool _failed;

        public SetupDialog(AppPreferences preferences) : base("Welcome to mæla", 640, 700)
        {
            _preferences = preferences;
            PictureBox logo = new PictureBox();
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            try { logo.Image = Branding.LoadBrandMark(); } catch { }
            logo.SetBounds(Margin_X, 22, 56, 56);
            Controls.Add(logo);
            Cursor_Y = 24;
            Label title = AddHeading("Welcome to mæla", null);
            title.Left = Margin_X + 70;
            Label sub = new Label();
            sub.AutoSize = true;
            sub.ForeColor = Theme.Current.Muted;
            sub.Text = "Private voice and chat rooms, hosted by you and your friends.";
            sub.Location = new Point(Margin_X + 72, 60);
            Controls.Add(sub);
            Cursor_Y = 98;

            _name = AddField("What should friends call you?", preferences.DisplayName, false);

            AddSection("Pick a look (you can change it later)");
            ColourStrip colours = new ColourStrip(preferences.Theme, ClientSize.Width - Margin_X * 2);
            colours.Location = new Point(Margin_X, Cursor_Y);
            colours.Picked += delegate(Theme picked)
            {
                Theme.Apply(picked);
                RepaintForTheme();
            };
            Controls.Add(colours);
            Cursor_Y += 122;

            AddSection("What mæla uses");
            AddUse("Windows .NET Framework 4", "Runs mæla. Already part of Windows 10 and 11.", "Nothing to install");
            AddUse("Opus voice codec (Concentus)", "Voice. BSD 3-clause, built into mæla from its official source.", "Nothing to download");
            AddUse(EmojiPack.Name + " colour emoji", EmojiPack.License + ". " + EmojiPack.All.Length + " images from the official project; every file is checked against a fingerprint built into mæla.", "Download now, about 115 KB");
            AddUse("OpenH264 (Cisco)", "Screen sharing. OpenH264 Video Codec provided by Cisco Systems, Inc. BSD 2-clause; Cisco pays the H.264 patent fees.", "Asks first, when you share");
            Cursor_Y += 2;
            _status = AddText("Nothing else is downloaded without asking you first. Settings > Licenses has the full license texts.",
                Theme.Current.Muted, ClientSize.Width - Margin_X * 2);
            _progress.SetBounds(Margin_X, Cursor_Y + 2, ClientSize.Width - Margin_X * 2, 8);
            _progress.Visible = false;
            Controls.Add(_progress);

            _skip = Button("Skip for now", DialogResult.None);
            _skip.Click += delegate { if (!_busy) { SaveBasics(); DialogResult = DialogResult.Cancel; Close(); } };
            _start = Button("Get started", DialogResult.None);
            _start.Click += delegate { GetStarted(); };
            AddButtons(_skip, _start);
            _skip.Width = 120;
            _skip.Left = _start.Left - 130;
        }

        /// <summary>One row of the "What mæla uses" list: name, what it's for, and what happens.</summary>
        private void AddUse(string name, string detail, string action)
        {
            int width = ClientSize.Width - Margin_X * 2;
            Label title = new Label();
            title.AutoSize = false;
            title.Font = Ui.SmallBold;
            title.ForeColor = Theme.Current.Text;
            title.Text = name;
            title.SetBounds(Margin_X, Cursor_Y, width - 170, 18);
            Controls.Add(title);
            Label tag = new Label();
            tag.AutoSize = false;
            tag.Font = Ui.Small;
            tag.ForeColor = Theme.Current.Accent2;
            tag.TextAlign = ContentAlignment.TopRight;
            tag.Text = action;
            tag.SetBounds(Margin_X + width - 170, Cursor_Y, 170, 18);
            Controls.Add(tag);
            Label text = new Label();
            text.AutoSize = false;
            text.Font = Ui.Small;
            text.ForeColor = Theme.Current.Text2;
            text.Text = detail;
            Size size = TextRenderer.MeasureText(detail, Ui.Small, new Size(width, 0), TextFormatFlags.WordBreak);
            text.SetBounds(Margin_X, Cursor_Y + 19, width, size.Height + 2);
            Controls.Add(text);
            Cursor_Y += 19 + size.Height + 10;
        }

        private void RepaintForTheme()
        {
            BackColor = Theme.Current.Main;
            Ui.StyleWindowFrame(this);
            _name.Fill = Theme.Current.Surface;
            _name.ApplyTheme();
            foreach (Control c in Controls)
            {
                if (c is Label) ((Label)c).BackColor = Theme.Current.Main;
                c.Invalidate();
            }
            Invalidate(true);
        }

        private void SaveBasics()
        {
            string name = _name.Box.Text.Trim();
            _preferences.DisplayName = name.Length == 0 ? "Guest" : (name.Length > 32 ? name.Substring(0, 32) : name);
            _preferences.Theme = Theme.Current.Id;
            PreferencesStore.Save(_preferences);
        }

        private void GetStarted()
        {
            if (_busy)
                return;
            SaveBasics();
            if (_failed || EmojiPack.Verify())
            {
                Finish();
                return;
            }
            _busy = true;
            _start.Enabled = false;
            _skip.Enabled = false;
            _progress.Visible = true;
            _status.Text = "Downloading from " + EmojiPack.Homepage + " and verifying…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try
                {
                    EmojiPack.Install(delegate(int done, int total)
                    {
                        try { BeginInvoke((MethodInvoker)delegate { _progress.Value = done * 100 / Math.Max(1, total); }); } catch { }
                    }, null);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _busy = false;
                        if (error == null)
                        {
                            Finish();
                            return;
                        }
                        _status.Text = "Couldn't download the emoji: " + error + "\r\nYou can continue without them (they show in plain text) and try again from Settings → Downloads.";
                        _status.ForeColor = Theme.Current.Danger;
                        _start.Text = "Continue";
                        _start.Enabled = true;
                        _skip.Enabled = true;
                        _failed = true; // "Continue" now just carries on.
                        _preferences.SetupDone = true; // Don't block the user on a network problem.
                        PreferencesStore.Save(_preferences);
                    });
                }
                catch { }
            });
        }

        private void Finish()
        {
            _preferences.SetupDone = true;
            PreferencesStore.Save(_preferences);
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Thin rounded progress bar.</summary>
    internal sealed class ProgressStrip : Control
    {
        private int _value;

        public ProgressStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public int Value
        {
            get { return _value; }
            set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : t.Main);
            Ui.FillRound(e.Graphics, t.Surface2, new RectangleF(0, 0, Width, Height), Height / 2f);
            if (_value > 0)
                Ui.FillRound(e.Graphics, t.Accent, new RectangleF(0, 0, Math.Max(Height, Width * _value / 100f), Height), Height / 2f);
        }
    }
}
