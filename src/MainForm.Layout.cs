using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>Window layout, theming and the UI state shown for the current room.</summary>
    internal sealed partial class MainForm
    {
        private static Color Rail { get { return Theme.Current.Rail; } }
        private static Color Sidebar { get { return Theme.Current.Side; } }
        private static Color MainBg { get { return Theme.Current.Main; } }
        private static Color TextMain { get { return Theme.Current.Text; } }
        private static Color TextMuted { get { return Theme.Current.Muted; } }
        private static Color Accent { get { return Theme.Current.Accent; } }
        private static Color Good { get { return Theme.Current.Good; } }
        private static Color Danger { get { return Theme.Current.Danger; } }

        private readonly RoomRail _rail = new RoomRail();
        private readonly Panel _sidebar = new Panel();
        private readonly Panel _main = new Panel();
        private readonly SidebarHeader _roomHeader = new SidebarHeader();
        private readonly SectionLabel _channelsLabel = new SectionLabel();
        private readonly FlatButton _addChannelButton = new FlatButton();
        private readonly ListBox _channels = new ListBox();
        private readonly ActionPanel _actionPanel = new ActionPanel();
        private readonly VoiceCard _voiceCard = new VoiceCard();
        private readonly Panel _cardHost = new Panel();
        private readonly LiveBar _liveBar = new LiveBar();
        private readonly UserBar _userBar = new UserBar();
        private readonly ChannelHeader _header = new ChannelHeader();
        private readonly HomePanel _home = new HomePanel();
        private readonly ContextMenuStrip _roomMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _channelMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _memberMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _addMenu = new ContextMenuStrip();
        private readonly ToolTip _toolTip = new ToolTip();
        private bool _deafened;
        private string _lastHeaderTip = String.Empty;
        private bool _wasConnected;
        private int _lastLocalChannel = -1;
        private DateTime _connectedAtUtc = DateTime.MinValue;

        private void BuildInterface()
        {
            SuspendLayout();
            Font = Ui.Body;

            // Main area (filled first so the docked sidebar and rail take their space).
            _main.Dock = DockStyle.Fill;
            Controls.Add(_main);

            _sidebar.Dock = DockStyle.Left;
            _sidebar.Width = 268;
            Controls.Add(_sidebar);

            _rail.Dock = DockStyle.Left;
            _rail.IsLive = delegate(SavedRoom room)
            {
                return (_engine != null && _engine.IsRunning && Object.ReferenceEquals(room, _activeSavedRoom)) || room.Presence == RoomPresence.Online;
            };
            _rail.RoomClicked += delegate(SavedRoom room)
            {
                // One room at a time: opening another room leaves the current one.
                if (_engine != null && _engine.IsRunning && !Object.ReferenceEquals(room, _activeSavedRoom))
                {
                    string current = String.IsNullOrEmpty(_activeRoomName) ? "this room" : _activeRoomName;
                    string question = _engine.IsHost
                        ? "Stop " + current + " and open " + room.Name + "?" + (_engine.MemberCount > 1 ? " Everyone in " + current + " will be disconnected." : String.Empty)
                        : "Leave " + current + " and join " + room.Name + "?";
                    if (MessageBox.Show(this, question, "Switch room", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return;
                    StopEngine();
                    _selectedRoom = room;
                    _rail.Selected = room;
                    _rail.Invalidate();
                    StartSelectedRoom();
                    return;
                }
                _selectedRoom = room;
                _rail.Selected = room;
                _rail.Invalidate();
                UpdateSelectedRoomUi();
            };
            _rail.RoomMenuRequested += delegate(SavedRoom room, Point at)
            {
                _selectedRoom = room;
                _rail.Selected = room;
                _rail.Invalidate();
                UpdateSelectedRoomUi();
                _roomMenu.Show(_rail, at);
            };
            _rail.AddClicked += delegate(Point at) { _addMenu.Show(_rail, at); };
            Controls.Add(_rail);

            // Sidebar, bottom-up for docking: user bar, voice card / actions, channels.
            _userBar.Dock = DockStyle.Bottom;
            _userBar.MuteButton.Click += MuteButtonClicked;
            _userBar.DeafenButton.Click += DeafenButtonClicked;
            _userBar.SettingsButton.Click += SettingsClicked;
            _userBar.ProfileClicked += delegate { ShowMyProfileCard(); };
            _userBar.Picture = MyPicture();
            _toolTip.SetToolTip(_userBar.DeafenButton, "Deafen: hear nobody and mute your mic");
            _toolTip.SetToolTip(_userBar.SettingsButton, "Settings");
            Panel cardHost = _cardHost;
            cardHost.Dock = DockStyle.Bottom;
            cardHost.Height = 132;
            cardHost.Padding = new Padding(10, 4, 10, 10);
            _sidebar.Controls.Add(cardHost);
            _voiceCard.Dock = DockStyle.Fill;
            _voiceCard.NoiseButton.Click += delegate { ToggleNoiseSuppression(); };
            _voiceCard.LeaveButton.Click += LeaveClicked;
            _voiceCard.DisconnectButton.Click += DisconnectClicked;
            _voiceCard.ScreenButton.Click += ShareScreenClicked;
            _toolTip.SetToolTip(_voiceCard.ScreenButton, "Share your screen");
            _toolTip.SetToolTip(_voiceCard.LeaveButton, "Leave voice");
            _toolTip.SetToolTip(_voiceCard.NoiseButton, "Filter out steady background noise like fans or hum");
            cardHost.Controls.Add(_voiceCard);
            _actionPanel.Dock = DockStyle.Fill;
            _actionPanel.PrimaryButton.Click += RoomActionClicked;
            _actionPanel.SecondaryButton.Click += delegate { StopEngine(); };
            cardHost.Controls.Add(_actionPanel);
            BuildFriendsBar();
            _sidebar.Controls.Add(_userBar); // Added after the card so it docks at the very bottom.

            _roomHeader.Dock = DockStyle.Top;
            _roomHeader.Click += delegate
            {
                if (_selectedRoom != null)
                    _roomMenu.Show(_roomHeader, new Point(16, _roomHeader.Height - 6));
            };

            Panel channelHead = new Panel();
            channelHead.Dock = DockStyle.Top;
            channelHead.Height = 42;
            _channelsLabel.Text = "CHANNELS";
            _channelsLabel.SetBounds(18, 16, 150, 18);
            channelHead.Controls.Add(_channelsLabel);
            _addChannelButton.Look = ButtonLook.Ghost;
            _addChannelButton.Icon = IconKind.Plus;
            _addChannelButton.Size = new Size(26, 26);
            _addChannelButton.Radius = 7;
            _addChannelButton.Click += AddChannelClicked;
            _toolTip.SetToolTip(_addChannelButton, "Create channel");
            channelHead.Controls.Add(_addChannelButton);
            // Position from the row's real width whenever it changes (it only gets its final
            // width once docked, so a fixed position at build time can end up off-screen).
            channelHead.Resize += delegate { _addChannelButton.Location = new Point(channelHead.Width - 38, 11); };

            _channels.Dock = DockStyle.Fill;
            _channels.BorderStyle = BorderStyle.None;
            _channels.DrawMode = DrawMode.OwnerDrawVariable;
            _channels.IntegralHeight = false;
            _channels.MeasureItem += ChannelsMeasureItem;
            _channels.DrawItem += ChannelsDrawItem;
            _channels.MouseDoubleClick += ChannelsDoubleClick;
            _channels.MouseDown += ChannelsMouseDown;
            Panel channelPad = new Panel();
            channelPad.Dock = DockStyle.Fill;
            channelPad.Padding = new Padding(8, 0, 8, 0);
            channelPad.Controls.Add(_channels);
            _sidebar.Controls.Add(channelPad);
            _sidebar.Controls.Add(channelHead);
            _sidebar.Controls.Add(_roomHeader);

            BuildRoomContextMenu();
            BuildChannelContextMenu();
            BuildMemberContextMenu();
            BuildAddMenu();

            // Main area: header, chat, composer, plus the home screen when nothing is running.
            _header.Dock = DockStyle.Top;
            _header.CopyButton.Click += CopyInviteClicked;
            _header.Search.Box.TextChanged += delegate { SetChatFilter(_header.Search.Box.Text); };
            _header.Search.Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    _header.Search.Box.Clear();
                    e.SuppressKeyPress = true;
                }
            };
            BuildChatArea(_main);
            _main.Controls.Add(_header);
            _header.SendToBack();
            _liveBar.Dock = DockStyle.Top;
            _liveBar.Visible = false;
            _liveBar.StopButton.Click += delegate { StopSharing(); RefreshShell(_engine); };
            _liveBar.PreviewButton.Click += delegate { ShowSharePreview(); };
            _main.Controls.Add(_liveBar);
            _liveBar.SendToBack(); // Above the header.

            _home.Dock = DockStyle.Fill;
            _home.HostButton.Click += delegate { ShowHostRoomDialog(null); };
            _home.JoinButton.Click += delegate { ShowJoinRoomDialog(); };
            _home.StartButton.Click += RoomActionClicked;
            _main.Controls.Add(_home);
            _home.BringToFront();

            ResumeLayout(true);
            Theme.Changed += delegate { ApplyTheme(); };
            ApplyTheme();
        }

        private void BuildAddMenu()
        {
            _addMenu.Items.Clear();
            _addMenu.Items.Add("Host a room…", null, delegate { ShowHostRoomDialog(null); });
            _addMenu.Items.Add("Join with a room code…", null, delegate { ShowJoinRoomDialog(); });
        }

        /// <summary>Repaints everything in the current theme.</summary>
        private void ApplyTheme()
        {
            Theme t = Theme.Current;
            BackColor = t.Main;
            ForeColor = t.Text;
            _main.BackColor = t.Main;
            _sidebar.BackColor = t.Side;
            foreach (Control c in _sidebar.Controls)
                c.BackColor = c is UserBar ? t.Rail : t.Side;
            _channels.BackColor = t.Side;
            _channels.ForeColor = t.Text;
            _channelsLabel.BackColor = t.Side;
            _header.Search.Fill = t.Surface;
            _header.Search.ApplyTheme();
            ToolStripRenderer renderer = new ThemedMenuRenderer();
            _roomMenu.Renderer = renderer;
            _channelMenu.Renderer = renderer;
            _memberMenu.Renderer = renderer;
            _addMenu.Renderer = renderer;
            ApplyChatTheme();
            Ui.StyleWindowFrame(this);
            Invalidate(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.StyleWindowFrame(this);
        }

        /// <summary>Per-tick UI state for the current room: header, sidebar, voice card, you.</summary>
        private void RefreshShell(VoipEngine engine)
        {
            Theme t = Theme.Current;
            bool running = engine != null && engine.IsRunning;
            bool connected = running && engine.IsConnected;
            bool inVoice = connected && engine.IsInVoice;
            SavedRoom room = _selectedRoom ?? _activeSavedRoom;

            _home.Visible = !running;
            _composer.Visible = running;
            if (!running)
                _home.SetRoom(room, room == null ? String.Empty : PresenceText(room));
            _addChannelButton.Visible = running && engine.IsHost;

            // Sidebar header.
            if (running)
            {
                string line = !connected
                    ? (String.IsNullOrEmpty(engine.LastMessage) ? "Connecting…" : engine.LastMessage)
                    : "Online · " + engine.VoiceMemberCount + " in voice" + (engine.IsHost ? " · hosted by you" : String.Empty);
                _roomHeader.SetState(_activeRoomName, line, connected, connected);
            }
            else if (room != null)
                _roomHeader.SetState(room.Name, PresenceText(room), room.Presence == RoomPresence.Online, false);
            else
                _roomHeader.SetState("mæla", "Host a room or join one with a code", false, false);

            // Connection card (while connected) vs. action panel.
            _voiceCard.Visible = connected;
            _actionPanel.Visible = !connected;
            int cardHeight = connected && !inVoice ? 92 : 132; // Compact card when not in voice.
            if (_cardHost.Height != cardHeight)
                _cardHost.Height = cardHeight;
            if (connected)
            {
                string detail = inVoice ? engine.GetChannelName(engine.LocalChannelId) + " · " + _activeRoomName : _activeRoomName;
                _voiceCard.SetState(inVoice ? "Voice connected" : "In the room", detail, engine.PingMs, true, engine.NoiseSuppression, inVoice);
                string disconnectTip = engine.IsHost ? "Stop room" : "Disconnect";
                if (disconnectTip != _lastDisconnectTip)
                {
                    _lastDisconnectTip = disconnectTip;
                    _toolTip.SetToolTip(_voiceCard.DisconnectButton, disconnectTip);
                }
            }
            else if (running)
                _actionPanel.SetState(engine.IsHost ? "Starting room…" : "Connecting…", engine.LastMessage, null, false, "Cancel");
            else if (room != null)
                _actionPanel.SetState(room.Owned ? "Your room" : "Saved room", PresenceText(room), room.Owned ? "Host room" : "Join room", true, null);
            else
                _actionPanel.SetState("No room selected", "Use + to host or join a room.", null, false, null);

            // You.
            string status;
            Color statusColor;
            if (_deafened) { status = "Deafened"; statusColor = t.Danger; }
            else if (_muted) { status = "Muted"; statusColor = t.Danger; }
            else if (inVoice && IsLocalSpeaking(engine)) { status = "● Speaking"; statusColor = t.Good; }
            else if (inVoice) { status = "Connected"; statusColor = t.Good; }
            else if (connected) { status = "Not in voice"; statusColor = t.Muted; }
            else { status = "Ready"; statusColor = t.Muted; }
            _userBar.SetState(_preferences.DisplayName, status, statusColor, inVoice && IsLocalSpeaking(engine), _muted, _deafened);
            _toolTip.SetToolTip(_userBar.MuteButton, _muted ? "Unmute" : "Mute");
            _toolTip.SetToolTip(_userBar.DeafenButton, _deafened ? "Undeafen" : "Deafen: hear nobody and mute your mic");

            // Chat header.
            string code = RoomCode.IsValid(_activeInvite) ? _activeInvite : String.Empty;
            if (running && engine.IsHost && code.Length == 0)
                code = String.Empty;
            int open = connected ? engine.ChatChannelId : 0;
            if (open > 0)
            {
                string channelName = engine.GetChannelName(open);
                string sub;
                if (engine.IsTextChannel(open))
                    sub = "Chat-only channel";
                else if (open == engine.LocalChannelId)
                    sub = engine.CurrentChannelMemberCount + " in voice";
                else
                    sub = "Double-click to join voice";
                _header.SetState(channelName, sub, open == engine.LocalChannelId, open == engine.LocalChannelId ? ChannelMemberNames(engine) : new string[0], code);
            }
            else if (running)
                _header.SetState(_activeRoomName, connected ? String.Empty : "Connecting…", false, new string[0], code);
            else
                _header.SetState(room == null ? "mæla" : room.Name, String.Empty, false, new string[0], String.Empty);
            _header.Search.Visible = open > 0;

            string tip = running && engine.IsHost ? "Room code for invites · " + _natStatus : "Room code";
            if (tip != _lastHeaderTip)
            {
                _lastHeaderTip = tip;
                _toolTip.SetToolTip(_header, tip);
            }
        }

        private static string FirstChannelName(VoipEngine engine)
        {
            VoiceChannelInfo[] channels = engine.GetChannels();
            return channels.Length > 0 ? channels[0].Name : "voice";
        }

        private static string[] ChannelMemberNames(VoipEngine engine)
        {
            RoomMemberInfo[] members = engine.GetMembers();
            List<string> names = new List<string>();
            int i;
            for (i = 0; i < members.Length; i++)
                names.Add(members[i].Name);
            return names.ToArray();
        }

        private static bool IsLocalSpeaking(VoipEngine engine)
        {
            RoomMemberInfo[] members = engine.GetMembers();
            int i;
            for (i = 0; i < members.Length; i++)
            {
                if (members[i].IsLocal)
                    return members[i].IsSpeaking;
            }
            return false;
        }

        private static string PresenceText(SavedRoom room)
        {
            if (room.Presence == RoomPresence.Online)
                return "Online" + (room.MaxMembers > 0 ? " · " + room.MemberCount + "/" + room.MaxMembers : String.Empty);
            if (room.Presence == RoomPresence.Checking)
                return "Checking…";
            if (room.Presence == RoomPresence.Offline)
                return room.Owned ? "Offline · host it to open the room" : "Offline";
            return room.Owned ? "Hosted by you" : "Saved room";
        }

        private void UpdateSelectedRoomUi()
        {
            RefreshShell(_engine);
        }

        private void RebuildRoomList()
        {
            RebuildRoomList(null);
        }

        private void RebuildRoomList(SavedRoom select)
        {
            _rail.SetRooms(_savedRooms);
            if (select != null)
                SelectRoomInList(select);
            else if (_selectedRoom == null && _savedRooms.Count > 0)
                SelectRoomInList(_savedRooms[0]);
            _rail.Invalidate();
        }

        private void SelectRoomInList(SavedRoom room)
        {
            if (room == null)
                return;
            _selectedRoom = room;
            _rail.Selected = room;
            _rail.Invalidate();
        }

        private void MuteButtonClicked(object sender, EventArgs e)
        {
            if (_deafened)
            {
                // Like Discord: unmuting while deafened undeafens too.
                SetDeafened(false);
                _muted = false;
            }
            else
                _muted = !_muted;
            if (_engine != null)
                _engine.Muted = _muted;
            PlaySound(_muted ? SoundCue.Mute : SoundCue.Unmute);
            RefreshShell(_engine);
        }

        private void DeafenButtonClicked(object sender, EventArgs e)
        {
            SetDeafened(!_deafened);
            PlaySound(_deafened ? SoundCue.Deafen : SoundCue.Undeafen);
            RefreshShell(_engine);
        }

        /// <summary>Plays a sound effect (if enabled) through the chosen output device.</summary>
        private void PlaySound(SoundCue cue)
        {
            if (!_preferences.SoundEffects)
                return;
            byte[] pcm = SoundFx.Render(cue);
            VoipEngine engine = _engine;
            if (engine != null && engine.IsRunning)
            {
                engine.PlayCue(pcm);
                return;
            }
            // Not in a room (for example just after leaving): play on a short-lived stream.
            try
            {
                WAVEFORMATEX format = new WAVEFORMATEX();
                format.wFormatTag = 1;
                format.nChannels = 1;
                format.nSamplesPerSec = SoundFx.SampleRate;
                format.wBitsPerSample = 16;
                format.nBlockAlign = 2;
                format.nAvgBytesPerSec = SoundFx.SampleRate * 2;
                PlaybackStream stream = new PlaybackStream(format, _preferences.OutputDeviceId, null);
                stream.SetGain(_preferences.MasterVolumePercent / 100.0f);
                stream.PlayClip(pcm);
                System.Threading.Timer cleanup = null;
                cleanup = new System.Threading.Timer(delegate
                {
                    stream.Dispose();
                    if (cleanup != null) cleanup.Dispose();
                }, null, pcm.Length * 1000 / (SoundFx.SampleRate * 2) + 400, System.Threading.Timeout.Infinite);
            }
            catch
            {
                // No audio device: sounds are optional.
            }
        }

        /// <summary>Connection and channel sounds, from state changes seen by the UI timer.</summary>
        private void PlayTransitionSounds(VoipEngine engine)
        {
            bool connected = engine != null && engine.IsRunning && engine.IsConnected;
            int channel = connected ? engine.LocalChannelId : -1;
            if (connected && !_wasConnected)
            {
                PlaySound(SoundCue.Connected);
                _connectedAtUtc = DateTime.UtcNow;
            }
            else if (!connected && _wasConnected)
            {
                PlaySound(SoundCue.Disconnected);
            }
            else if (connected && channel != _lastLocalChannel && (DateTime.UtcNow - _connectedAtUtc).TotalSeconds > 1.5)
            {
                // Moving into a channel (or between channels) vs. stepping out to the lobby.
                PlaySound(channel > 0 ? SoundCue.JoinVoice : SoundCue.LeaveVoice);
            }
            _wasConnected = connected;
            _lastLocalChannel = channel;
        }

        private void SetDeafened(bool value)
        {
            _deafened = value;
            if (_engine != null)
                _engine.Deafened = value;
        }

        private void ToggleNoiseSuppression()
        {
            _preferences.NoiseSuppression = !_preferences.NoiseSuppression;
            PreferencesStore.Save(_preferences);
            if (_engine != null)
                _engine.NoiseSuppression = _preferences.NoiseSuppression;
            RefreshShell(_engine);
        }

        private void UpdateMuteButton()
        {
            RefreshShell(_engine);
        }
    }

    /// <summary>Small uppercase section caption ("CHANNELS").</summary>
    internal sealed class SectionLabel : Control
    {
        public SectionLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Current.Side);
            Ui.DrawText(e.Graphics, Text, Ui.Tiny, new Point(2, 1), Theme.Current.Muted);
        }
    }

    /// <summary>Room name and status at the top of the sidebar. Click for the room menu.</summary>
    internal sealed class SidebarHeader : Control
    {
        private string _title = String.Empty;
        private string _line = String.Empty;
        private bool _online;
        private bool _secure;
        private bool _hover;

        public SidebarHeader()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 72;
            Cursor = Cursors.Hand;
        }

        public void SetState(string title, string line, bool online, bool secure)
        {
            if (title == _title && line == _line && online == _online && secure == _secure)
                return;
            _title = title ?? String.Empty;
            _line = line ?? String.Empty;
            _online = online;
            _secure = secure;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(_hover ? Ui.Mix(t.Side, t.Text, 0.04f) : t.Side);
            using (Pen line = new Pen(t.Line))
                g.DrawLine(line, 0, Height - 1, Width, Height - 1);

            int badgeWidth = 0;
            if (_secure)
            {
                Size s = Ui.Measure(g, "Encrypted", Ui.Tiny);
                badgeWidth = s.Width + 26;
                Rectangle badge = new Rectangle(Width - badgeWidth - 14, 16, badgeWidth, 20);
                Ui.FillRound(g, Ui.Mix(t.Side, t.Accent, t.Dark ? 0.18f : 0.12f), badge, 10);
                Icons.Draw(g, IconKind.Lock, new RectangleF(badge.X + 7, badge.Y + 5, 10, 10), t.Accent2);
                Ui.DrawText(g, "Encrypted", Ui.Tiny, new Point(badge.X + 20, badge.Y + 3), t.Accent2);
            }
            Ui.DrawText(g, _title, Ui.Title, new Rectangle(16, 12, Width - 32 - badgeWidth, 26), t.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            int x = 16;
            if (_online)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush glow = new SolidBrush(Ui.Mix(t.Side, t.Good, 0.25f)))
                    g.FillEllipse(glow, x - 2, 44, 12, 12);
                using (SolidBrush dot = new SolidBrush(t.Good))
                    g.FillEllipse(dot, x, 46, 8, 8);
                x += 14;
            }
            Ui.DrawText(g, _line, Ui.Small, new Rectangle(x, 40, Width - x - 14, 18), t.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>Bottom-of-sidebar card when you're not in voice: status plus the main actions.</summary>
    internal sealed class ActionPanel : Control
    {
        public readonly FlatButton PrimaryButton = new FlatButton();
        public readonly FlatButton SecondaryButton = new FlatButton();
        private string _title = String.Empty;
        private string _detail = String.Empty;

        public ActionPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            PrimaryButton.Look = ButtonLook.Primary;
            PrimaryButton.Height = 34;
            SecondaryButton.Look = ButtonLook.Danger;
            SecondaryButton.Height = 34;
            Controls.Add(PrimaryButton);
            Controls.Add(SecondaryButton);
        }

        public void SetState(string title, string detail, string primary, bool primaryEnabled, string secondary)
        {
            PrimaryButton.Visible = primary != null;
            if (primary != null)
            {
                PrimaryButton.Text = primary;
                PrimaryButton.Enabled = primaryEnabled;
            }
            SecondaryButton.Visible = secondary != null;
            if (secondary != null)
                SecondaryButton.Text = secondary;
            if (title != _title || detail != _detail)
            {
                _title = title ?? String.Empty;
                _detail = detail ?? String.Empty;
                Invalidate();
            }
            PerformLayout();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int y = Height - 34 - 12;
            if (PrimaryButton.Visible && SecondaryButton.Visible)
            {
                int w = (Width - 24 - 8) / 2;
                PrimaryButton.SetBounds(12, y, w, 34);
                SecondaryButton.SetBounds(12 + w + 8, y, w, 34);
            }
            else if (PrimaryButton.Visible)
                PrimaryButton.SetBounds(12, y, Width - 24, 34);
            else if (SecondaryButton.Visible)
                SecondaryButton.SetBounds(12, y, Width - 24, 34);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(t.Side);
            Ui.FillRound(g, t.Surface, new RectangleF(0, 0, Width, Height), 14);
            PrimaryButton.Underlay = t.Surface;
            SecondaryButton.Underlay = t.Surface;
            Ui.DrawText(g, _title, Ui.SmallBold, new Rectangle(14, 11, Width - 28, 18), t.Text, TextFormatFlags.Left);
            Ui.DrawText(g, _detail, Ui.Small, new Rectangle(14, 30, Width - 28, 34), t.Muted, TextFormatFlags.Left | TextFormatFlags.WordBreak);
        }
    }

    /// <summary>What the main area shows before you're in a room.</summary>
    internal sealed class HomePanel : Control
    {
        public readonly FlatButton StartButton = new FlatButton();
        public readonly FlatButton HostButton = new FlatButton();
        public readonly FlatButton JoinButton = new FlatButton();
        private Image _logo;
        private SavedRoom _room;
        private string _presence = String.Empty;

        public HomePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { _logo = Branding.LoadBrandMark(); } catch { }
            StartButton.Look = ButtonLook.Primary;
            StartButton.Size = new Size(180, 42);
            StartButton.Radius = 12;
            StartButton.Font = Ui.BodyBold;
            HostButton.Look = ButtonLook.Primary;
            HostButton.Text = "Host a room";
            HostButton.Icon = IconKind.Plus;
            HostButton.Size = new Size(170, 42);
            HostButton.Radius = 12;
            HostButton.Font = Ui.BodyBold;
            JoinButton.Look = ButtonLook.Secondary;
            JoinButton.Text = "Join with a code";
            JoinButton.Size = new Size(170, 42);
            JoinButton.Radius = 12;
            JoinButton.Font = Ui.BodyBold;
            Controls.Add(StartButton);
            Controls.Add(HostButton);
            Controls.Add(JoinButton);
        }

        public void SetRoom(SavedRoom room, string presence)
        {
            if (Object.ReferenceEquals(room, _room) && presence == _presence && (room == null || StartButton.Text == (room.Owned ? "Host room" : "Join room")))
                return;
            _room = room;
            _presence = presence ?? String.Empty;
            StartButton.Visible = room != null;
            if (room != null)
                StartButton.Text = room.Owned ? "Host room" : "Join room";
            PerformLayout();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int cy = Height / 2;
            if (_room != null)
            {
                StartButton.Location = new Point((Width - StartButton.Width) / 2, cy + 70);
                HostButton.Visible = false;
                JoinButton.Visible = false;
            }
            else
            {
                HostButton.Visible = true;
                JoinButton.Visible = true;
                HostButton.Location = new Point(Width / 2 - HostButton.Width - 6, cy + 70);
                JoinButton.Location = new Point(Width / 2 + 6, cy + 70);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(t.Main);
            foreach (Control c in Controls)
                if (c is FlatButton) ((FlatButton)c).Underlay = t.Main;
            int cy = Height / 2;
            if (_logo != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_logo, new Rectangle((Width - 84) / 2, cy - 120, 84, 84));
            }
            string title = _room == null ? "Welcome to mæla" : _room.Name;
            string line = _room == null ? "Private voice and chat rooms, hosted by you and your friends." : _presence;
            Ui.DrawText(g, title, Ui.Heading, new Rectangle(0, cy - 22, Width, 36), t.Text, TextFormatFlags.HorizontalCenter);
            Ui.DrawText(g, line, Ui.Body, new Rectangle(40, cy + 20, Width - 80, 40), t.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }
    }
}
