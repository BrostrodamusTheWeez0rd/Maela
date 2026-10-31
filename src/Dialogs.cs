using System;
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

    /// <summary>A clickable preview of a theme.</summary>
    internal sealed class ThemeCard : Control
    {
        public readonly Theme Theme;
        private bool _hover;

        public ThemeCard(Theme theme)
        {
            Theme = theme;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Size = new Size(138, 112);
        }

        public bool Selected { get; set; }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme cur = Maela.Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : cur.Main);
            Rectangle preview = new Rectangle(2, 2, Width - 4, 76);
            using (GraphicsPath clip = Ui.RoundRect(preview, 10))
            {
                g.SetClip(clip);
                using (SolidBrush b = new SolidBrush(Theme.Main)) g.FillRectangle(b, preview);
                using (SolidBrush b = new SolidBrush(Theme.Rail)) g.FillRectangle(b, preview.X, preview.Y, 18, preview.Height);
                using (SolidBrush b = new SolidBrush(Theme.Side)) g.FillRectangle(b, preview.X + 18, preview.Y, 36, preview.Height);
                Ui.FillRound(g, Theme.Accent, new RectangleF(preview.X + 4, preview.Y + 8, 10, 10), 3);
                Ui.FillRound(g, Ui.Mix(Theme.Side, Theme.Accent, 0.25f), new RectangleF(preview.X + 22, preview.Y + 10, 28, 7), 3);
                Ui.FillRound(g, Theme.Surface2, new RectangleF(preview.X + 22, preview.Y + 22, 22, 5), 2);
                Ui.FillRound(g, Theme.Surface2, new RectangleF(preview.X + 22, preview.Y + 31, 26, 5), 2);
                Ui.FillRound(g, Theme.Text2, new RectangleF(preview.X + 62, preview.Y + 14, 40, 5), 2);
                Ui.FillRound(g, Theme.Muted, new RectangleF(preview.X + 62, preview.Y + 24, 56, 4), 2);
                Ui.FillRound(g, Theme.Muted, new RectangleF(preview.X + 62, preview.Y + 32, 48, 4), 2);
                Ui.FillRound(g, Theme.Surface, new RectangleF(preview.X + 60, preview.Bottom - 20, preview.Width - 66, 13), 5);
                Ui.FillRound(g, Theme.Accent, new RectangleF(preview.Right - 18, preview.Bottom - 18, 10, 9), 3);
                g.ResetClip();
            }
            Color border = Selected ? cur.Accent : (_hover ? cur.Text2 : cur.Line);
            Ui.StrokeRound(g, border, new RectangleF(1, 1, Width - 3, 77), 10, Selected ? 2.5f : 1.2f);
            Ui.DrawText(g, Theme.Name, Selected ? Ui.SmallBold : Ui.Small, new Rectangle(0, 84, Width, 20), Selected ? cur.Text : cur.Text2, TextFormatFlags.HorizontalCenter);
        }
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
        private readonly RoundField _name;
        private readonly RoundField _password;
        private readonly ToggleSwitch _save;

        public string Invite { get { return _invite.Box.Text.Trim(); } }
        public string RoomName { get { return String.IsNullOrWhiteSpace(_name.Box.Text) ? "Voice Room" : _name.Box.Text.Trim(); } }
        public bool SaveRoom { get { return _save.Checked; } }
        public string Password { get { return _password.Box.Text; } }

        public JoinRoomDialog() : base("Join a room", 480, 480)
        {
            AddHeading("Join a room", "Paste the room code your friend sent you.");
            _invite = AddField("Room code", String.Empty, false);
            _invite.Box.Font = Ui.Mono;
            _name = AddField("Name it", "Voice Room", false);
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
        private readonly ToggleSwitch _sounds;
        private readonly ToggleSwitch _agc;
        private readonly ToggleSwitch _autoUpdate;
        private readonly ToggleSwitch _closeToTray;
        private readonly Label _downloadStatus = new Label();
        private readonly FlatButton _repair = new FlatButton();
        private readonly System.Collections.Generic.List<ThemeCard> _themeCards = new System.Collections.Generic.List<ThemeCard>();
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

            AddSection("Appearance");
            int x = Margin_X;
            foreach (Theme option in Theme.All)
            {
                ThemeCard card = new ThemeCard(option);
                card.Location = new Point(x, Cursor_Y);
                card.Selected = option.Id == _preferences.Theme;
                card.Click += ThemeCardClicked;
                Controls.Add(card);
                _themeCards.Add(card);
                x += card.Width + 12;
            }
            Cursor_Y += 122;

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
            _noise = AddToggle("Noise filter", "Removes steady background noise like fans, hum and hiss.", _preferences.NoiseSuppression);
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

            AddSection("App");
            _closeToTray = AddToggle("Keep running in the tray when closed", "Closing the window keeps you in your room. Right-click the tray icon to quit.", _preferences.CloseToTray);

            AddSection("Updates");
            _autoUpdate = AddToggle("Check for updates automatically", "mæla " + Updater.CurrentVersionText + " · updates come from GitHub releases", _preferences.AutoUpdate);
            FlatButton check = new FlatButton();
            check.Text = "Check now";
            check.SetBounds(ClientSize.Width - Margin_X - 120, _autoUpdate.Top + 4, 120, 36);
            check.Click += delegate { _checkForUpdates = true; SaveAndClose(); };
            Controls.Add(check);
            FlatButton licenses = new FlatButton();
            licenses.Look = ButtonLook.Ghost;
            licenses.Text = "Third-party licenses";
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

        private void ThemeCardClicked(object sender, EventArgs e)
        {
            ThemeCard picked = (ThemeCard)sender;
            foreach (ThemeCard card in _themeCards)
            {
                card.Selected = Object.ReferenceEquals(card, picked);
                card.Invalidate();
            }
            _preferences.Theme = picked.Theme.Id;
            Theme.Apply(picked.Theme); // Live preview in the main window.
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
            try
            {
                string exePath = Application.ExecutablePath.Replace("\"", String.Empty);
                string command = "netsh advfirewall firewall delete rule name=\"Maela Room\" >nul 2>&1 & netsh advfirewall firewall add rule name=\"Maela Room\" dir=in action=allow program=\"" + exePath + "\" protocol=UDP enable=yes profile=any";
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = "cmd.exe";
                info.Arguments = "/c " + command;
                info.UseShellExecute = true;
                info.Verb = "runas";
                info.WindowStyle = ProcessWindowStyle.Hidden;
                Process process = Process.Start(info);
                if (process == null) throw new InvalidOperationException("Windows did not start the firewall helper.");
                process.WaitForExit();
                MessageBox.Show(this, process.ExitCode == 0 ? "mæla is allowed through Windows Firewall." : "Windows did not add the firewall rule.", "Firewall", MessageBoxButtons.OK, process.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowLicenses()
        {
            string text = "mæla includes the Opus audio codec (Concentus C# port), BSD 3-clause license.";
            try
            {
                using (System.IO.Stream stream = typeof(SettingsForm).Assembly.GetManifestResourceStream("THIRD_PARTY_NOTICES.txt"))
                {
                    if (stream != null)
                        using (System.IO.StreamReader reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                            text = reader.ReadToEnd();
                }
            }
            catch { }
            using (ThemedDialog form = new ThemedDialog("Third-party licenses", 640, 480))
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
        private readonly System.Collections.Generic.List<ThemeCard> _cards = new System.Collections.Generic.List<ThemeCard>();
        private readonly Label _status;
        private readonly ProgressStrip _progress = new ProgressStrip();
        private readonly FlatButton _start;
        private readonly FlatButton _skip;
        private readonly AppPreferences _preferences;
        private bool _busy;
        private bool _failed;

        public SetupDialog(AppPreferences preferences) : base("Welcome to mæla", 640, 610)
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
            int x = Margin_X;
            foreach (Theme option in Theme.All)
            {
                ThemeCard card = new ThemeCard(option);
                card.Location = new Point(x, Cursor_Y);
                card.Selected = option.Id == preferences.Theme;
                card.Click += delegate(object s, EventArgs e)
                {
                    foreach (ThemeCard c in _cards) { c.Selected = Object.ReferenceEquals(c, s); }
                    Theme.Apply(((ThemeCard)s).Theme);
                    RepaintForTheme();
                };
                Controls.Add(card);
                _cards.Add(card);
                x += card.Width + 12;
            }
            Cursor_Y += 122;

            AddSection("Colour emoji");
            _status = AddText("mæla downloads " + (EmojiPack.All.Length) + " open-source emoji images (about 115 KB) from the official "
                + EmojiPack.Name + " project (" + EmojiPack.License + ") and checks every file against a fingerprint built into the app before using it.",
                Theme.Current.Text2, ClientSize.Width - Margin_X * 2);
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
