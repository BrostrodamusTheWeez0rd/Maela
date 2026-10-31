using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

[assembly: System.Reflection.AssemblyTitle("m\u00E6la")]
[assembly: System.Reflection.AssemblyProduct("m\u00E6la")]
[assembly: System.Reflection.AssemblyDescription("Private self-hosted voice rooms")]
[assembly: System.Reflection.AssemblyCompany("m\u00E6la")]
// AssemblyVersion / AssemblyFileVersion are generated from the VERSION file by scripts\build_ci.cmd.

namespace Maela
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool justUpdated = false;
            int i;
            for (i = 0; args != null && i < args.Length; i++)
            {
                if (String.Equals(args[i], Updater.UpdatedArgument, StringComparison.OrdinalIgnoreCase))
                    justUpdated = true;
            }

            Updater.CleanupAfterUpdate();
            Application.Run(new MainForm(justUpdated));
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Color Rail = Color.FromArgb(30, 31, 34);
        private static readonly Color Sidebar = Color.FromArgb(43, 45, 49);
        private static readonly Color MainBg = Color.FromArgb(49, 51, 56);
        private static readonly Color UserBg = Color.FromArgb(35, 36, 40);
        private static readonly Color Selected = Color.FromArgb(64, 66, 73);
        private static readonly Color TextMain = Color.FromArgb(242, 243, 245);
        private static readonly Color TextMuted = Color.FromArgb(148, 155, 164);
        private static readonly Color Accent = Color.FromArgb(88, 101, 242);
        private static readonly Color Good = Color.FromArgb(35, 165, 90);
        private static readonly Color Danger = Color.FromArgb(242, 63, 67);
        private static readonly Color Border = Color.FromArgb(63, 65, 71);

        private readonly ListBox _rooms = new ListBox();
        private readonly ListBox _channels = new ListBox();
        private readonly ListBox _members = new ListBox();
        private readonly PictureBox _brandMark = new PictureBox();
        private readonly Label _roomTitle = new Label();
        private readonly Label _roomPresence = new Label();
        private readonly Label _voiceTitle = new Label();
        private readonly Label _connectionStatus = new Label();
        private readonly Label _memberCount = new Label();
        private readonly Label _inviteLabel = new Label();
        private readonly Label _networkLabel = new Label();
        private readonly Label _userName = new Label();
        private readonly Label _userState = new Label();
        private readonly Button _roomAction = new Button();
        private readonly Button _leaveButton = new Button();
        private readonly Button _copyInvite = new Button();
        private readonly Button _muteButton = new Button();
        private readonly Button _settingsButton = new Button();
        private readonly Button _hostRoomButton = new Button();
        private readonly Button _joinRoomButton = new Button();
        private readonly Button _addChannelButton = new Button();
        private readonly Button _stopRoomButton = new Button();
        private readonly ContextMenuStrip _roomMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _channelMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _memberMenu = new ContextMenuStrip();
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();

        private readonly List<SavedRoom> _savedRooms = new List<SavedRoom>();
        private AppPreferences _preferences;
        private VoipEngine _engine;
        private UpnpPortMapping _upnpMapping;
        private SavedRoom _activeSavedRoom;
        private SavedRoom _selectedRoom;
        private RoomMemberInfo _contextMember;
        private VoiceChannelInfo _contextChannel;
        private string _activeRoomName = "Voice Room";
        private string _activeInvite = String.Empty;
        private string _publicIp = String.Empty;
        private string _natStatus = "Router not checked";
        private string _lastMemberSnapshot = String.Empty;
        private string _lastSpeakingSnapshot = String.Empty;
        private string _lastChannelSnapshot = String.Empty;
        private int _networkSetupGeneration;
        private int _presenceGeneration;
        private bool _presenceScanRunning;
        private int _localBoundPort;
        private bool _muted;
        private bool _keepRoomAlive;
        private bool _allowTrueClose;
        private readonly NotifyIcon _notifyIcon = new NotifyIcon();
        private readonly System.Windows.Forms.Timer _updateTimer = new System.Windows.Forms.Timer();
        private readonly bool _justUpdated;
        private bool _updateCheckRunning;
        private bool _updateInstalling;
        private UpdateInfo _pendingUpdate;

        public MainForm() : this(false)
        {
        }

        public MainForm(bool justUpdated)
        {
            _justUpdated = justUpdated;
            Text = "m\u00E6la";
            try { Icon = Branding.LoadAppIcon(); } catch { Icon = SystemIcons.Application; }
            ClientSize = new Size(1040, 680);
            MinimumSize = new Size(940, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = MainBg;
            ForeColor = TextMain;

            _preferences = PreferencesStore.Load();
            _savedRooms.AddRange(SavedRoomStore.Load());
            MigrateLegacyRoomCodes();

            BuildInterface();
            RebuildRoomList();
            UpdateSelectedRoomUi();
            UpdateMuteButton();

            _uiTimer.Interval = 120;
            _uiTimer.Tick += UiTimerTick;
            _uiTimer.Start();
            FormClosing += MainFormClosing;

            _notifyIcon.Text = "m\u00E6la";
            try { _notifyIcon.Icon = Branding.LoadAppIcon(); } catch { _notifyIcon.Icon = SystemIcons.Application; }
            _notifyIcon.Visible = false;
            _notifyIcon.DoubleClick += delegate { RestoreFromTray(); };
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open m\u00E6la", null, delegate { RestoreFromTray(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Stop room and exit", null, delegate
            {
                _allowTrueClose = true;
                StopEngine();
                _notifyIcon.Visible = false;
                Close();
            });
            _notifyIcon.ContextMenuStrip = trayMenu;
            _notifyIcon.BalloonTipClicked += delegate
            {
                RestoreFromTray();
                ShowPendingUpdate();
            };

            // First check shortly after launch, then every six hours (hosts often run for days in the tray).
            _updateTimer.Interval = 4000;
            _updateTimer.Tick += delegate
            {
                _updateTimer.Interval = 6 * 60 * 60 * 1000;
                if (_preferences.AutoUpdate)
                    CheckForUpdates(false);
            };
            Shown += delegate
            {
                _updateTimer.Start();
                if (_justUpdated)
                {
                    _notifyIcon.Visible = true;
                    _notifyIcon.BalloonTipTitle = "m\u00E6la updated";
                    _notifyIcon.BalloonTipText = "You are now running m\u00E6la " + Updater.CurrentVersionText + ".";
                    try { _notifyIcon.ShowBalloonTip(3000); } catch { }
                    System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer();
                    hide.Interval = 6000;
                    hide.Tick += delegate { hide.Stop(); hide.Dispose(); if (Visible) _notifyIcon.Visible = false; };
                    hide.Start();
                }
            };
        }

        private void CheckForUpdates(bool userInitiated)
        {
            if (_updateCheckRunning || _updateInstalling)
                return;
            _updateCheckRunning = true;

            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info = null;
                string error = null;
                try { info = Updater.CheckForUpdate(); }
                catch (Exception ex) { error = ex.Message; }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _updateCheckRunning = false;
                        HandleUpdateCheckResult(info, error, userInitiated);
                    });
                }
                catch
                {
                    _updateCheckRunning = false;
                }
            });
        }

        private void HandleUpdateCheckResult(UpdateInfo info, string error, bool userInitiated)
        {
            if (error != null)
            {
                if (userInitiated)
                    MessageBox.Show(this, error, "m\u00E6la update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (info == null)
            {
                if (userInitiated)
                    MessageBox.Show(this, "You are running the latest version (" + Updater.CurrentVersionText + ").", "m\u00E6la update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!userInitiated && String.Equals(_preferences.SkippedVersion, Updater.FormatVersion(info.Version), StringComparison.Ordinal))
                return;

            _pendingUpdate = info;
            if (!Visible || WindowState == FormWindowState.Minimized)
            {
                // Running in the tray: don't pop a dialog over whatever the user is doing.
                _notifyIcon.Visible = true;
                _notifyIcon.BalloonTipTitle = "m\u00E6la " + Updater.FormatVersion(info.Version) + " is available";
                _notifyIcon.BalloonTipText = "Click here to review and install the update.";
                try { _notifyIcon.ShowBalloonTip(5000); } catch { }
                return;
            }

            ShowPendingUpdate();
        }

        private void ShowPendingUpdate()
        {
            UpdateInfo info = _pendingUpdate;
            if (info == null || _updateInstalling)
                return;
            _pendingUpdate = null;

            bool inCall = _engine != null && _engine.IsRunning;
            DialogResult choice;
            using (UpdateDialog dialog = new UpdateDialog(info, inCall))
                choice = dialog.ShowDialog(this);

            if (choice == UpdateDialog.Skip)
            {
                _preferences.SkippedVersion = Updater.FormatVersion(info.Version);
                PreferencesStore.Save(_preferences);
                return;
            }
            if (choice != DialogResult.OK)
                return;

            if (!Updater.CanInstall(info))
            {
                OpenUrl(info.PageUrl);
                return;
            }

            InstallUpdate(info);
        }

        private void InstallUpdate(UpdateInfo info)
        {
            _updateInstalling = true;
            Cursor = Cursors.WaitCursor;
            string previousTitle = Text;
            Text = "m\u00E6la - downloading update " + Updater.FormatVersion(info.Version) + "...";

            ThreadPool.QueueUserWorkItem(delegate
            {
                string staged = null;
                string error = null;
                try { staged = Updater.DownloadAndVerify(info); }
                catch (Exception ex) { error = ex.Message; }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Cursor = Cursors.Default;
                        Text = previousTitle;
                        if (error == null)
                        {
                            try
                            {
                                // Release the room, microphone and UPnP mapping before the new build starts.
                                StopEngine();
                                PreferencesStore.Save(_preferences);
                                SavedRoomStore.Save(_savedRooms);
                                Updater.InstallAndRestart(staged);
                                _allowTrueClose = true;
                                Close();
                                return;
                            }
                            catch (UnauthorizedAccessException)
                            {
                                error = "m\u00E6la does not have permission to replace its own file in this folder. Move Maela.exe somewhere you can write to (for example your Desktop or Documents) or install the update manually.";
                            }
                            catch (Exception ex)
                            {
                                error = ex.Message;
                            }
                        }

                        _updateInstalling = false;
                        if (MessageBox.Show(this, error + "\r\n\r\nOpen the download page instead?", "m\u00E6la update failed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            OpenUrl(info.PageUrl);
                    });
                }
                catch
                {
                    _updateInstalling = false;
                }
            });
        }

        private static void OpenUrl(string url)
        {
            if (String.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = Updater.ReleasesPage;
            try { Process.Start(url); } catch { }
        }

        private void MigrateLegacyRoomCodes()
        {
            bool changed = false;
            int i;
            for (i = 0; i < _savedRooms.Count; i++)
            {
                SavedRoom room = _savedRooms[i];
                if (room == null || String.IsNullOrWhiteSpace(room.Invite))
                    continue;
                IPAddress decodedIp;
                int decodedPort;
                if (RoomCode.TryDecode(room.Invite, out decodedIp, out decodedPort))
                    continue;
                string host;
                int port;
                if (!InviteParser.TrySplitLegacy(room.Invite, out host, out port))
                    continue;
                IPAddress ip;
                if (IPAddress.TryParse(host, out ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    room.Invite = RoomCode.Encode(ip, port);
                    changed = true;
                }
            }
            if (changed)
                SavedRoomStore.Save(_savedRooms);
        }

        private void BuildInterface()
        {
            Panel rail = new Panel();
            rail.Location = new Point(0, 0);
            rail.Size = new Size(76, ClientSize.Height);
            rail.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            rail.BackColor = Rail;
            Controls.Add(rail);

            _rooms.Location = new Point(0, 10);
            _rooms.Size = new Size(76, ClientSize.Height - 20);
            _rooms.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            _rooms.BackColor = Rail;
            _rooms.BorderStyle = BorderStyle.None;
            _rooms.DrawMode = DrawMode.OwnerDrawFixed;
            _rooms.ItemHeight = 62;
            _rooms.IntegralHeight = false;
            _rooms.ForeColor = TextMain;
            _rooms.DrawItem += RoomsDrawItem;
            _rooms.SelectedIndexChanged += RoomsSelectedIndexChanged;
            _rooms.MouseDown += RoomsMouseDown;
            rail.Controls.Add(_rooms);

            BuildRoomContextMenu();

            Panel sidebar = new Panel();
            sidebar.Location = new Point(76, 0);
            sidebar.Size = new Size(254, ClientSize.Height);
            sidebar.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            sidebar.BackColor = Sidebar;
            Controls.Add(sidebar);

            _brandMark.Location = new Point(14, 10);
            _brandMark.Size = new Size(42, 42);
            _brandMark.SizeMode = PictureBoxSizeMode.Zoom;
            _brandMark.BackColor = Color.Transparent;
            _brandMark.Image = Branding.LoadBrandMark();
            sidebar.Controls.Add(_brandMark);

            _roomTitle.AutoSize = false;
            _roomTitle.Location = new Point(64, 11);
            _roomTitle.Size = new Size(174, 28);
            _roomTitle.Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold, GraphicsUnit.Point);
            _roomTitle.ForeColor = TextMain;
            _roomTitle.Text = "m\u00E6la";
            sidebar.Controls.Add(_roomTitle);

            _roomPresence.AutoSize = false;
            _roomPresence.Location = new Point(64, 39);
            _roomPresence.Size = new Size(174, 22);
            _roomPresence.ForeColor = TextMuted;
            _roomPresence.Text = "Select or create a room";
            sidebar.Controls.Add(_roomPresence);

            StylePrimaryButton(_hostRoomButton, "Host");
            _hostRoomButton.Location = new Point(14, 76);
            _hostRoomButton.Size = new Size(108, 34);
            _hostRoomButton.Click += delegate { ShowHostRoomDialog(null); };
            _toolTip.SetToolTip(_hostRoomButton, "Host a new room");
            sidebar.Controls.Add(_hostRoomButton);

            StyleSecondaryButton(_joinRoomButton, "Join");
            _joinRoomButton.Location = new Point(132, 76);
            _joinRoomButton.Size = new Size(108, 34);
            _joinRoomButton.Click += delegate { ShowJoinRoomDialog(); };
            _toolTip.SetToolTip(_joinRoomButton, "Join with a room code");
            sidebar.Controls.Add(_joinRoomButton);

            Label channels = MakeLabel("VOICE CHANNELS", 8F, FontStyle.Bold, TextMuted);
            channels.Location = new Point(14, 128);
            sidebar.Controls.Add(channels);

            StyleIconButton(_addChannelButton);
            _addChannelButton.Text = "+";
            _addChannelButton.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point);
            _addChannelButton.Location = new Point(207, 118);
            _addChannelButton.Size = new Size(34, 30);
            _addChannelButton.Enabled = false;
            _addChannelButton.Click += AddChannelClicked;
            _toolTip.SetToolTip(_addChannelButton, "Create voice channel");
            sidebar.Controls.Add(_addChannelButton);

            _channels.Location = new Point(8, 151);
            _channels.Size = new Size(238, 136);
            _channels.BackColor = Sidebar;
            _channels.BorderStyle = BorderStyle.None;
            _channels.DrawMode = DrawMode.OwnerDrawFixed;
            _channels.ItemHeight = 36;
            _channels.IntegralHeight = false;
            _channels.ForeColor = TextMain;
            _channels.DrawItem += ChannelsDrawItem;
            _channels.MouseDoubleClick += ChannelsDoubleClick;
            _channels.MouseDown += ChannelsMouseDown;
            sidebar.Controls.Add(_channels);
            BuildChannelContextMenu();

            _connectionStatus.AutoSize = false;
            _connectionStatus.Location = new Point(16, 296);
            _connectionStatus.Size = new Size(220, 36);
            _connectionStatus.ForeColor = TextMuted;
            _connectionStatus.Text = "Not connected";
            sidebar.Controls.Add(_connectionStatus);

            StylePrimaryButton(_roomAction, "Join Room");
            _roomAction.Location = new Point(14, 340);
            _roomAction.Size = new Size(226, 34);
            _roomAction.Click += RoomActionClicked;
            sidebar.Controls.Add(_roomAction);

            StyleSecondaryButton(_leaveButton, "Leave Voice");
            _leaveButton.Location = new Point(14, 382);
            _leaveButton.Size = new Size(226, 32);
            _leaveButton.Visible = false;
            _leaveButton.Click += LeaveClicked;
            sidebar.Controls.Add(_leaveButton);

            StyleSecondaryButton(_stopRoomButton, "Leave Room");
            _stopRoomButton.Location = new Point(14, 421);
            _stopRoomButton.Size = new Size(226, 32);
            _stopRoomButton.Visible = false;
            _stopRoomButton.Click += StopRoomClicked;
            sidebar.Controls.Add(_stopRoomButton);

            Label inviteHeader = MakeLabel("ROOM CODE", 8F, FontStyle.Bold, TextMuted);
            inviteHeader.Location = new Point(14, 468);
            sidebar.Controls.Add(inviteHeader);

            _inviteLabel.AutoSize = false;
            _inviteLabel.Location = new Point(14, 487);
            _inviteLabel.Size = new Size(226, 38);
            _inviteLabel.ForeColor = TextMuted;
            _inviteLabel.Text = "No room code yet";
            sidebar.Controls.Add(_inviteLabel);

            StyleSecondaryButton(_copyInvite, "Copy code");
            _copyInvite.Location = new Point(14, 529);
            _copyInvite.Size = new Size(110, 30);
            _copyInvite.Click += CopyInviteClicked;
            sidebar.Controls.Add(_copyInvite);

            _networkLabel.AutoSize = false;
            _networkLabel.Location = new Point(14, 565);
            _networkLabel.Size = new Size(226, 48);
            _networkLabel.ForeColor = TextMuted;
            _networkLabel.Text = "Network idle";
            sidebar.Controls.Add(_networkLabel);

            Panel userBar = new Panel();
            userBar.Height = 62;
            userBar.Dock = DockStyle.Bottom;
            userBar.BackColor = UserBg;
            sidebar.Controls.Add(userBar);

            Panel avatar = new Panel();
            avatar.Location = new Point(10, 12);
            avatar.Size = new Size(38, 38);
            avatar.BackColor = Color.FromArgb(88, 91, 96);
            avatar.Paint += UserAvatarPaint;
            userBar.Controls.Add(avatar);

            _userName.AutoSize = false;
            _userName.Location = new Point(56, 12);
            _userName.Size = new Size(92, 20);
            _userName.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            _userName.ForeColor = TextMain;
            _userName.Text = _preferences.DisplayName;
            userBar.Controls.Add(_userName);

            _userState.AutoSize = false;
            _userState.Location = new Point(56, 32);
            _userState.Size = new Size(92, 18);
            _userState.ForeColor = TextMuted;
            _userState.Font = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
            _userState.Text = "Ready";
            userBar.Controls.Add(_userState);

            StyleIconButton(_muteButton);
            _muteButton.Location = new Point(154, 13);
            _muteButton.Size = new Size(38, 38);
            _muteButton.Paint += MuteButtonPaint;
            _muteButton.Click += MuteButtonClicked;
            _toolTip.SetToolTip(_muteButton, "Mute microphone");
            userBar.Controls.Add(_muteButton);

            StyleIconButton(_settingsButton);
            _settingsButton.Text = "\u2699";
            _settingsButton.Font = new Font("Segoe UI Symbol", 15F, FontStyle.Regular, GraphicsUnit.Point);
            _settingsButton.Location = new Point(198, 13);
            _settingsButton.Size = new Size(38, 38);
            _settingsButton.Click += SettingsClicked;
            _toolTip.SetToolTip(_settingsButton, "User settings");
            userBar.Controls.Add(_settingsButton);

            Panel main = new Panel();
            main.Location = new Point(330, 0);
            main.Size = new Size(ClientSize.Width - 330, ClientSize.Height);
            main.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            main.BackColor = MainBg;
            Controls.Add(main);

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 58;
            top.BackColor = MainBg;
            top.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(38, 40, 43)))
                    e.Graphics.DrawLine(p, 0, top.Height - 1, top.Width, top.Height - 1);
            };
            main.Controls.Add(top);

            _voiceTitle.AutoSize = true;
            _voiceTitle.Location = new Point(22, 17);
            _voiceTitle.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point);
            _voiceTitle.ForeColor = TextMain;
            _voiceTitle.Text = "General";
            top.Controls.Add(_voiceTitle);

            _memberCount.AutoSize = false;
            _memberCount.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _memberCount.TextAlign = ContentAlignment.MiddleRight;
            _memberCount.Location = new Point(514, 18);
            _memberCount.Size = new Size(180, 20);
            _memberCount.ForeColor = TextMuted;
            _memberCount.Text = "0 connected";
            top.Controls.Add(_memberCount);

            Label memberHeader = MakeLabel("IN VOICE", 8F, FontStyle.Bold, TextMuted);
            memberHeader.Location = new Point(24, 80);
            main.Controls.Add(memberHeader);

            _members.Location = new Point(18, 104);
            _members.Size = new Size(680, ClientSize.Height - 138);
            _members.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _members.BackColor = MainBg;
            _members.ForeColor = TextMain;
            _members.BorderStyle = BorderStyle.None;
            _members.DrawMode = DrawMode.OwnerDrawFixed;
            _members.ItemHeight = 58;
            _members.IntegralHeight = false;
            _members.DrawItem += MembersDrawItem;
            _members.MouseDown += MembersMouseDown;
            main.Controls.Add(_members);
            BuildMemberContextMenu();
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Font = new Font("Segoe UI", size, style, GraphicsUnit.Point);
            label.ForeColor = color;
            return label;
        }

        private static void StylePrimaryButton(Button button, string text)
        {
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.Cursor = Cursors.Hand;
        }

        private static void StyleSecondaryButton(Button button, string text)
        {
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.BorderSize = 1;
            button.BackColor = Sidebar;
            button.ForeColor = TextMain;
            button.Cursor = Cursors.Hand;
        }

        private static void StyleRoundishButton(Button button, string text, Color back, Color fore, float size)
        {
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = back;
            button.ForeColor = fore;
            button.Font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point);
            button.Cursor = Cursors.Hand;
        }

        private static void StyleIconButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = UserBg;
            button.ForeColor = TextMain;
            button.Cursor = Cursors.Hand;
            button.TabStop = false;
        }

        private void BuildRoomContextMenu()
        {
            _roomMenu.BackColor = Sidebar;
            _roomMenu.ForeColor = TextMain;
            _roomMenu.ShowImageMargin = false;
            _roomMenu.Opening += RoomMenuOpening;
        }

        private void BuildChannelContextMenu()
        {
            _channelMenu.BackColor = Sidebar;
            _channelMenu.ForeColor = TextMain;
            _channelMenu.ShowImageMargin = false;
            _channelMenu.Opening += ChannelMenuOpening;
        }

        private void ChannelsMouseDown(object sender, MouseEventArgs e)
        {
            int index = _channels.IndexFromPoint(e.Location);
            if (index < 0 || index >= _channels.Items.Count)
                return;

            _channels.SelectedIndex = index;
            _contextChannel = _channels.Items[index] as VoiceChannelInfo;
            if (e.Button == MouseButtons.Right && _contextChannel != null)
                _channelMenu.Show(_channels, e.Location);
        }

        private void ChannelsDoubleClick(object sender, MouseEventArgs e)
        {
            int index = _channels.IndexFromPoint(e.Location);
            if (index < 0 || index >= _channels.Items.Count || _engine == null || !_engine.IsConnected)
                return;

            VoiceChannelInfo channel = _channels.Items[index] as VoiceChannelInfo;
            if (channel != null)
                _engine.ChangeChannel(channel.Id);
        }

        private void AddChannelClicked(object sender, EventArgs e)
        {
            if (_engine == null || !_engine.IsHost)
                return;

            string name = TextPrompt.Show(this, "Create voice channel", "Channel name", "New Channel");
            if (String.IsNullOrWhiteSpace(name))
                return;

            _engine.CreateChannel(name);
            SaveHostedChannelLayout();
        }

        private void ChannelMenuOpening(object sender, CancelEventArgs e)
        {
            _channelMenu.Items.Clear();
            VoiceChannelInfo channel = _contextChannel;
            VoipEngine engine = _engine;
            if (channel == null || engine == null || !engine.IsConnected)
            {
                e.Cancel = true;
                return;
            }

            ToolStripMenuItem heading = new ToolStripMenuItem(channel.Name);
            heading.Enabled = false;
            _channelMenu.Items.Add(heading);

            ToolStripMenuItem join = new ToolStripMenuItem(engine.LocalChannelId == channel.Id ? "Current channel" : "Join channel");
            join.Enabled = engine.LocalChannelId != channel.Id;
            join.Click += delegate { if (_engine != null) _engine.ChangeChannel(channel.Id); };
            _channelMenu.Items.Add(join);

            if (engine.IsHost)
            {
                _channelMenu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem rename = new ToolStripMenuItem("Rename channel");
                rename.Click += delegate
                {
                    if (_engine == null || !_engine.IsHost)
                        return;
                    string newName = TextPrompt.Show(this, "Rename voice channel", "Channel name", channel.Name);
                    if (!String.IsNullOrWhiteSpace(newName))
                    {
                        _engine.RenameChannel(channel.Id, newName);
                        SaveHostedChannelLayout();
                    }
                };
                _channelMenu.Items.Add(rename);

                ToolStripMenuItem delete = new ToolStripMenuItem("Delete channel");
                delete.ForeColor = Danger;
                delete.Enabled = channel.Id != 1;
                delete.Click += delegate
                {
                    if (_engine == null || !_engine.IsHost || channel.Id == 1)
                        return;
                    if (MessageBox.Show(this, "Delete #" + channel.Name + "? Members in it will be moved to #General.", "Delete channel", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        _engine.DeleteChannel(channel.Id);
                        SaveHostedChannelLayout();
                    }
                };
                _channelMenu.Items.Add(delete);
            }
        }

        private void ChannelsDrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _channels.Items.Count)
                return;

            VoiceChannelInfo channel = _channels.Items[e.Index] as VoiceChannelInfo;
            if (channel == null)
                return;

            bool active = _engine != null && _engine.LocalChannelId == channel.Id;
            Color back = active ? Selected : Sidebar;
            using (SolidBrush b = new SolidBrush(back))
                e.Graphics.FillRectangle(b, e.Bounds);

            Color text = active ? TextMain : Color.FromArgb(181, 186, 193);
            using (Font f = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point))
            using (SolidBrush b = new SolidBrush(text))
                e.Graphics.DrawString("#  " + channel.Name, f, b, e.Bounds.Left + 8, e.Bounds.Top + 9);

            string count = channel.MemberCount.ToString();
            using (Font f = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush b = new SolidBrush(TextMuted))
            {
                SizeF size = e.Graphics.MeasureString(count, f);
                e.Graphics.DrawString(count, f, b, e.Bounds.Right - size.Width - 9, e.Bounds.Top + 10);
            }
        }

        private void SaveHostedChannelLayout()
        {
            if (_engine == null || !_engine.IsHost || _activeSavedRoom == null)
                return;

            VoiceChannelInfo[] channels = _engine.GetChannels();
            _activeSavedRoom.ChannelNames.Clear();
            int i;
            for (i = 0; i < channels.Length; i++)
                _activeSavedRoom.ChannelNames.Add(channels[i].Name);
            SavedRoomStore.Save(_savedRooms);
        }

        private void RoomsMouseDown(object sender, MouseEventArgs e)
        {
            int index = _rooms.IndexFromPoint(e.Location);
            if (index >= 0 && index < _rooms.Items.Count)
            {
                _rooms.SelectedIndex = index;
                _selectedRoom = _rooms.Items[index] as SavedRoom;
                UpdateSelectedRoomUi();
                if (e.Button == MouseButtons.Right && _selectedRoom != null)
                    _roomMenu.Show(_rooms, e.Location);
            }
        }

        private void RoomMenuOpening(object sender, CancelEventArgs e)
        {
            _roomMenu.Items.Clear();
            SavedRoom room = _selectedRoom;
            if (room == null)
            {
                e.Cancel = true;
                return;
            }

            ToolStripMenuItem heading = new ToolStripMenuItem(room.Name);
            heading.Enabled = false;
            _roomMenu.Items.Add(heading);

            ToolStripMenuItem action = new ToolStripMenuItem(room.Owned ? "Host room" : "Join room");
            action.Enabled = _engine == null || !_engine.IsRunning;
            action.Click += delegate { StartSelectedRoom(); };
            _roomMenu.Items.Add(action);

            if (!room.Owned)
            {
                ToolStripMenuItem recheck = new ToolStripMenuItem("Check if online");
                recheck.Enabled = !_presenceScanRunning && !(_engine != null && _engine.IsRunning && Object.ReferenceEquals(room, _activeSavedRoom));
                recheck.Click += delegate { RecheckRoomPresence(room); };
                _roomMenu.Items.Add(recheck);
            }

            ToolStripMenuItem copy = new ToolStripMenuItem("Copy room code");
            copy.Enabled = RoomCode.IsValid(room.Invite);
            copy.Click += delegate { CopyToClipboard(room.Invite); };
            _roomMenu.Items.Add(copy);

            _roomMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem forget = new ToolStripMenuItem("Forget room");
            forget.ForeColor = Danger;
            forget.Click += delegate { ForgetSelectedRoom(); };
            _roomMenu.Items.Add(forget);
        }

        private void RoomsSelectedIndexChanged(object sender, EventArgs e)
        {
            _selectedRoom = _rooms.SelectedItem as SavedRoom;
            UpdateSelectedRoomUi();
        }

        private void RoomsDrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _rooms.Items.Count)
                return;

            SavedRoom room = _rooms.Items[e.Index] as SavedRoom;
            if (room == null)
                return;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Rectangle bounds = e.Bounds;
            if (selected)
            {
                using (SolidBrush bar = new SolidBrush(Color.White))
                    e.Graphics.FillRectangle(bar, bounds.Left, bounds.Top + 9, 4, bounds.Height - 18);
            }

            Rectangle circle = new Rectangle(bounds.Left + 14, bounds.Top + 8, 46, 46);
            using (SolidBrush b = new SolidBrush(selected ? Accent : Color.FromArgb(49, 51, 56)))
                e.Graphics.FillEllipse(b, circle);

            string initial = room.Name.Length > 0 ? room.Name.Substring(0, 1).ToUpperInvariant() : "R";
            using (Font f = new Font("Segoe UI Semibold", 13F, FontStyle.Bold, GraphicsUnit.Point))
            using (SolidBrush b = new SolidBrush(TextMain))
            {
                SizeF s = e.Graphics.MeasureString(initial, f);
                e.Graphics.DrawString(initial, f, b, circle.Left + (circle.Width - s.Width) / 2F, circle.Top + (circle.Height - s.Height) / 2F - 1F);
            }

            Color dotColor = Color.FromArgb(80, 82, 88);
            if (room.Presence == RoomPresence.Online)
                dotColor = Good;
            else if (room.Presence == RoomPresence.Offline)
                dotColor = Color.FromArgb(128, 132, 142);
            else if (room.Presence == RoomPresence.Checking)
                dotColor = Color.FromArgb(250, 166, 26);

            Rectangle dot = new Rectangle(circle.Right - 10, circle.Bottom - 10, 12, 12);
            using (SolidBrush border = new SolidBrush(Rail))
                e.Graphics.FillEllipse(border, dot.Left - 2, dot.Top - 2, dot.Width + 4, dot.Height + 4);
            using (SolidBrush b = new SolidBrush(dotColor))
                e.Graphics.FillEllipse(b, dot);
        }

        private void UserAvatarPaint(object sender, PaintEventArgs e)
        {
            Panel panel = sender as Panel;
            if (panel == null)
                return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(88, 91, 96)))
                e.Graphics.FillEllipse(b, 0, 0, panel.Width - 1, panel.Height - 1);

            string name = _preferences == null ? "R" : _preferences.DisplayName;
            string initial = String.IsNullOrEmpty(name) ? "R" : name.Substring(0, 1).ToUpperInvariant();
            using (Font f = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point))
            using (SolidBrush b = new SolidBrush(TextMain))
            {
                SizeF s = e.Graphics.MeasureString(initial, f);
                e.Graphics.DrawString(initial, f, b, (panel.Width - s.Width) / 2F, (panel.Height - s.Height) / 2F - 1F);
            }
        }

        private void MuteButtonPaint(object sender, PaintEventArgs e)
        {
            Button b = sender as Button;
            if (b == null)
                return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = _muted ? Danger : TextMain;
            using (Pen p = new Pen(c, 2F))
            {
                Rectangle capsule = new Rectangle(15, 8, 8, 15);
                e.Graphics.DrawArc(p, capsule, 180, 180);
                e.Graphics.DrawLine(p, capsule.Left, capsule.Top + 4, capsule.Left, capsule.Bottom - 4);
                e.Graphics.DrawLine(p, capsule.Right, capsule.Top + 4, capsule.Right, capsule.Bottom - 4);
                e.Graphics.DrawArc(p, 11, 15, 16, 13, 0, 180);
                e.Graphics.DrawLine(p, 19, 28, 19, 32);
                e.Graphics.DrawLine(p, 14, 32, 24, 32);
                if (_muted)
                    e.Graphics.DrawLine(p, 9, 7, 29, 31);
            }
        }

        private void MuteButtonClicked(object sender, EventArgs e)
        {
            _muted = !_muted;
            if (_engine != null)
                _engine.Muted = _muted;
            UpdateMuteButton();
        }

        private void UpdateMuteButton()
        {
            _muteButton.BackColor = _muted ? Color.FromArgb(65, 36, 40) : UserBg;
            _muteButton.Invalidate();
            _toolTip.SetToolTip(_muteButton, _muted ? "Unmute microphone" : "Mute microphone");
            _userState.Text = _muted ? "Muted" : ((_engine != null && _engine.IsConnected) ? "Voice connected" : "Ready");
            _userState.ForeColor = _muted ? Danger : TextMuted;
        }

        private void SettingsClicked(object sender, EventArgs e)
        {
            SettingsForm form = new SettingsForm(_preferences, _engine, _publicIp, RoomCode.IsValid(_activeInvite) ? _activeInvite : String.Empty, _natStatus);
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                _preferences = form.Preferences;
                PreferencesStore.Save(_preferences);
                _userName.Text = _preferences.DisplayName;
                if (_engine != null)
                {
                    _engine.MicVolumePercent = _preferences.MicVolumePercent;
                    _engine.MasterVolumePercent = _preferences.MasterVolumePercent;
                    _engine.EchoCancellation = _preferences.EchoCancellation;
                }
                Invalidate(true);
            }
            bool checkNow = form.CheckForUpdatesRequested;
            form.Dispose();
            if (checkNow)
                CheckForUpdates(true);
        }

        private void ShowHostRoomDialog(SavedRoom existing)
        {
            if (_engine != null && _engine.IsRunning)
                return;

            HostRoomDialog dialog = new HostRoomDialog(existing);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                SavedRoom room = existing;
                if (room == null)
                {
                    room = new SavedRoom();
                    room.Name = dialog.RoomName;
                    room.Owned = true;
                    room.PreferredPort = dialog.Port;
                    room.Invite = String.Empty;
                    room.Presence = RoomPresence.Unknown;
                    room.KeepAlive = dialog.KeepRoomAlive;
                    if (room.ChannelNames.Count == 0)
                        room.ChannelNames.Add("General");
                    if (dialog.SaveRoom)
                    {
                        _savedRooms.Add(room);
                        SavedRoomStore.Save(_savedRooms);
                        RebuildRoomList(room);
                    }
                }
                else
                {
                    room.Name = dialog.RoomName;
                    room.PreferredPort = dialog.Port;
                    room.KeepAlive = dialog.KeepRoomAlive;
                    SavedRoomStore.Save(_savedRooms);
                }
                StartHostRoom(room, dialog.Port, dialog.SaveRoom || existing != null);
            }
            dialog.Dispose();
        }

        private void ShowJoinRoomDialog()
        {
            if (_engine != null && _engine.IsRunning)
                return;

            JoinRoomDialog dialog = new JoinRoomDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                SavedRoom room = new SavedRoom();
                room.Name = dialog.RoomName;
                room.Invite = dialog.Invite;
                room.Owned = false;
                room.PreferredPort = 0;
                room.Presence = RoomPresence.Unknown;

                if (dialog.SaveRoom)
                {
                    SavedRoom duplicate = FindSavedByInvite(room.Invite);
                    if (duplicate == null)
                    {
                        _savedRooms.Add(room);
                        SavedRoomStore.Save(_savedRooms);
                    }
                    else
                    {
                        room = duplicate;
                        room.Name = dialog.RoomName;
                        SavedRoomStore.Save(_savedRooms);
                    }
                    RebuildRoomList(room);
                }

                StartJoinRoom(room);
            }
            dialog.Dispose();
        }

        private void RoomActionClicked(object sender, EventArgs e)
        {
            if (_engine != null && _engine.IsRunning)
                return;

            if (_selectedRoom == null)
                return;
            StartSelectedRoom();
        }

        private void StartSelectedRoom()
        {
            if (_selectedRoom == null)
                return;
            if (_selectedRoom.Owned)
                StartHostRoom(_selectedRoom, _selectedRoom.PreferredPort, true);
            else
                StartJoinRoom(_selectedRoom);
        }

        private void StartHostRoom(SavedRoom room, int requestedPort, bool saved)
        {
            StopEngine();
            try
            {
                string displayName = ValidateName(_preferences.DisplayName);
                string roomName = String.IsNullOrWhiteSpace(room.Name) ? "Voice Room" : room.Name.Trim();
                _engine = new VoipEngine(_preferences.InputDeviceId, _preferences.OutputDeviceId);
                ApplyPreferencesToEngine();
                _keepRoomAlive = room.KeepAlive;
                _engine.StartHost(requestedPort, displayName, roomName, room.ChannelNames);
                _localBoundPort = _engine.BoundPort;
                _activeSavedRoom = saved ? room : null;
                _activeRoomName = roomName;
                _activeInvite = String.Empty;
                _publicIp = String.Empty;
                _natStatus = "Checking public address and UPnP...";

                if (saved)
                {
                    room.Owned = true;
                    room.PreferredPort = _localBoundPort;
                    room.Presence = RoomPresence.Online;
                    room.MemberCount = 1;
                    room.MaxMembers = VoipEngine.MaxRoomMembers;
                    SavedRoomStore.Save(_savedRooms);
                }

                _selectedRoom = room;
                SelectRoomInList(room);
                UpdateSelectedRoomUi();
                BeginHostNetworkSetup(_localBoundPort, room, saved);
            }
            catch (Exception ex)
            {
                StopEngine();
                MessageBox.Show(this, ex.Message, "m\u00E6la could not host", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartJoinRoom(SavedRoom room)
        {
            StopEngine();
            try
            {
                string host;
                int port;
                if (!InviteParser.TrySplit(room.Invite, out host, out port))
                    throw new InvalidOperationException("That saved room code is not valid.");

                IPAddress hostIp = InviteParser.ResolveIPv4(host);
                _engine = new VoipEngine(_preferences.InputDeviceId, _preferences.OutputDeviceId);
                ApplyPreferencesToEngine();
                _engine.StartClient(new IPEndPoint(hostIp, port), ValidateName(_preferences.DisplayName));
                _activeSavedRoom = room.Owned ? null : room;
                _activeRoomName = room.Name;
                _activeInvite = room.Invite;
                _selectedRoom = room;
                SelectRoomInList(room);
                UpdateSelectedRoomUi();
            }
            catch (Exception ex)
            {
                StopEngine();
                MessageBox.Show(this, ex.Message, "m\u00E6la could not join", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ApplyPreferencesToEngine()
        {
            if (_engine == null)
                return;
            _engine.Muted = _muted;
            _engine.MicVolumePercent = _preferences.MicVolumePercent;
            _engine.MasterVolumePercent = _preferences.MasterVolumePercent;
            _engine.EchoCancellation = _preferences.EchoCancellation;
        }

        private void LeaveClicked(object sender, EventArgs e)
        {
            if (_engine == null || !_engine.IsRunning)
                return;

            if (_engine.IsHost && !_keepRoomAlive)
            {
                StopEngine();
                return;
            }

            _engine.LeaveVoice();
            _lastMemberSnapshot = String.Empty;
            _lastSpeakingSnapshot = String.Empty;
            _members.Items.Clear();
            UpdateSelectedRoomUi();
        }

        private void StopRoomClicked(object sender, EventArgs e)
        {
            StopEngine();
        }

        private void StopEngine()
        {
            Interlocked.Increment(ref _networkSetupGeneration);
            if (_upnpMapping != null)
            {
                try { _upnpMapping.Dispose(); } catch { }
                _upnpMapping = null;
            }
            if (_engine != null)
            {
                try { _engine.Dispose(); } catch { }
                _engine = null;
            }

            _lastMemberSnapshot = String.Empty;
            _lastSpeakingSnapshot = String.Empty;
            _lastChannelSnapshot = String.Empty;
            _members.Items.Clear();
            _channels.Items.Clear();
            _activeSavedRoom = null;
            _keepRoomAlive = false;
            _localBoundPort = 0;
            _publicIp = String.Empty;
            _natStatus = "Network idle";
            _activeInvite = _selectedRoom == null ? String.Empty : _selectedRoom.Invite;
            UpdateMuteButton();
            UpdateSelectedRoomUi();
        }

        private void BeginHostNetworkSetup(int localPort, SavedRoom room, bool saved)
        {
            int generation = Interlocked.Increment(ref _networkSetupGeneration);
            string localIp = GetOutboundLocalIPv4Address();
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpnpPortMapping mapping = null;
                string publicIp = String.Empty;
                string status;
                try
                {
                    mapping = UpnpPortMapping.TryCreate(localPort, localIp);
                    if (mapping != null)
                    {
                        publicIp = mapping.ExternalIp;
                        if (mapping.ExternalPort == localPort)
                            status = "Public UDP endpoint mapped with UPnP";
                        else
                            status = "Public UDP endpoint mapped with UPnP (router chose port " + mapping.ExternalPort + ")";
                    }
                    else
                        status = "Port not mapped automatically";
                }
                catch
                {
                    status = "UPnP mapping failed";
                }

                if (String.IsNullOrWhiteSpace(publicIp))
                    publicIp = FetchPublicIPv4();

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (generation != _networkSetupGeneration || _engine == null || !_engine.IsHost)
                        {
                            if (mapping != null)
                                mapping.Dispose();
                            return;
                        }

                        if (_upnpMapping != null)
                            _upnpMapping.Dispose();
                        _upnpMapping = mapping;
                        _publicIp = publicIp;
                        _natStatus = status;
                        int publicPort = mapping == null ? localPort : mapping.ExternalPort;
                        IPAddress codeIp;
                        if (!String.IsNullOrWhiteSpace(publicIp) && IPAddress.TryParse(publicIp, out codeIp) && codeIp.AddressFamily == AddressFamily.InterNetwork)
                            _activeInvite = RoomCode.Encode(codeIp, publicPort);
                        else
                        {
                            IPAddress localCodeIp;
                            if (IPAddress.TryParse(GetPrimaryLocalIPv4Address(), out localCodeIp) && localCodeIp.AddressFamily == AddressFamily.InterNetwork)
                            {
                                _activeInvite = RoomCode.Encode(localCodeIp, localPort);
                                _natStatus = "Public address unavailable - room code is local-network only";
                            }
                            else
                                _activeInvite = String.Empty;
                        }

                        if (saved && room != null)
                        {
                            room.Invite = _activeInvite;
                            room.PreferredPort = localPort;
                            room.Presence = RoomPresence.Online;
                            room.MemberCount = _engine == null ? 1 : _engine.MemberCount;
                            room.MaxMembers = VoipEngine.MaxRoomMembers;
                            SavedRoomStore.Save(_savedRooms);
                            RebuildRoomList(room);
                        }
                        UpdateSelectedRoomUi();
                    });
                }
                catch
                {
                    if (mapping != null)
                        mapping.Dispose();
                }
            });
        }

        private void UiTimerTick(object sender, EventArgs e)
        {
            VoipEngine engine = _engine;
            if (engine != null && engine.IsRunning)
            {
                if (engine.IsHost)
                {
                    if (engine.IsInVoice)
                        _connectionStatus.Text = "Room Online\r\nHosting " + engine.MemberCount + "/" + VoipEngine.MaxRoomMembers;
                    else
                        _connectionStatus.Text = "Room Server Running\r\nYou are in the lobby";
                    _connectionStatus.ForeColor = Good;
                }
                else if (engine.IsConnected)
                {
                    _connectionStatus.Text = engine.IsInVoice
                        ? "Room Connected\r\n" + engine.MemberCount + "/" + VoipEngine.MaxRoomMembers + " members"
                        : "Room Connected\r\nYou are in the lobby";
                    _connectionStatus.ForeColor = Good;
                }
                else
                {
                    _connectionStatus.Text = String.IsNullOrEmpty(engine.LastMessage) ? "Connecting..." : engine.LastMessage;
                    _connectionStatus.ForeColor = TextMuted;
                }

                string channelName = engine.LocalChannelId == 0 ? "Room Lobby" : engine.GetChannelName(engine.LocalChannelId);
                if (String.IsNullOrWhiteSpace(channelName))
                    channelName = "Voice";
                _voiceTitle.Text = channelName;
                _memberCount.Text = engine.LocalChannelId == 0
                    ? engine.MemberCount + " in room"
                    : engine.CurrentChannelMemberCount + (engine.CurrentChannelMemberCount == 1 ? " in channel" : " in channel");

                _leaveButton.Visible = engine.IsConnected && engine.IsInVoice;
                _leaveButton.Text = engine.IsHost
                    ? (_keepRoomAlive ? "Leave Voice (keep room up)" : "Leave Voice & Stop Room")
                    : "Leave Voice";
                _stopRoomButton.Visible = engine.IsConnected;
                _stopRoomButton.Text = engine.IsHost ? "Stop Room" : "Leave Room";
                _roomAction.Enabled = false;
                _hostRoomButton.Enabled = false;
                _joinRoomButton.Enabled = false;
                _addChannelButton.Enabled = engine.IsHost;

                if (_muted)
                {
                    _userState.Text = "Muted";
                    _userState.ForeColor = Danger;
                }
                else if (!engine.IsConnected)
                {
                    _userState.Text = "Connecting";
                    _userState.ForeColor = TextMuted;
                }
                else if (!engine.IsInVoice)
                {
                    _userState.Text = engine.IsHost ? "Room server active" : "In room lobby";
                    _userState.ForeColor = Good;
                }
                else
                {
                    _userState.Text = "Voice connected";
                    _userState.ForeColor = Good;
                }

                if (engine.IsHost && _activeSavedRoom != null)
                {
                    _activeSavedRoom.Presence = RoomPresence.Online;
                    _activeSavedRoom.MemberCount = engine.MemberCount;
                    _activeSavedRoom.MaxMembers = VoipEngine.MaxRoomMembers;
                }

                UpdateChannelRows(engine);
                UpdateMemberRows(engine);
            }
            else
            {
                _leaveButton.Visible = false;
                _stopRoomButton.Visible = false;
                _roomAction.Enabled = _selectedRoom != null;
                _hostRoomButton.Enabled = true;
                _joinRoomButton.Enabled = true;
                _addChannelButton.Enabled = false;
                _memberCount.Text = "0 connected";
                _voiceTitle.Text = "Voice";
                if (_members.Items.Count > 0)
                    _members.Items.Clear();
                if (_channels.Items.Count > 0)
                    _channels.Items.Clear();
                if (!_muted)
                {
                    _userState.Text = "Ready";
                    _userState.ForeColor = TextMuted;
                }
            }

            RefreshLocalPresence();
            if (!_presenceScanRunning)
                BeginPresenceScan();
        }

        /// <summary>
        /// Updates presence for rooms we already know about locally (the room we host or are
        /// connected to). Never touches the network.
        /// </summary>
        private void RefreshLocalPresence()
        {
            bool changed = false;
            bool engineUp = _engine != null && _engine.IsRunning;
            int i;
            for (i = 0; i < _savedRooms.Count; i++)
            {
                SavedRoom r = _savedRooms[i];
                bool active = engineUp && Object.ReferenceEquals(r, _activeSavedRoom);
                RoomPresence presence = r.Presence;
                int members = r.MemberCount;
                int max = r.MaxMembers;

                if (active && (_engine.IsHost || _engine.IsConnected))
                {
                    // Live connection: the room's info comes from the session itself.
                    presence = RoomPresence.Online;
                    members = _engine.MemberCount;
                    max = VoipEngine.MaxRoomMembers;
                    r.PresenceChecked = true;
                }
                else if (r.Owned)
                {
                    presence = RoomPresence.Offline;
                    members = 0;
                }
                else
                    continue;

                if (presence != r.Presence || members != r.MemberCount || max != r.MaxMembers)
                {
                    r.Presence = presence;
                    r.MemberCount = members;
                    r.MaxMembers = max;
                    changed = true;
                }
            }

            if (changed)
            {
                _rooms.Invalidate();
                UpdateSelectedRoomUi();
            }
        }

        private void RecheckRoomPresence(SavedRoom room)
        {
            if (room == null || room.Owned)
                return;
            room.PresenceChecked = false;
            if (!_presenceScanRunning)
                BeginPresenceScan();
        }

        private void UpdateChannelRows(VoipEngine engine)
        {
            VoiceChannelInfo[] channels = engine.GetChannels();
            StringBuilder snapshotBuilder = new StringBuilder();
            int i;
            for (i = 0; i < channels.Length; i++)
                snapshotBuilder.Append(channels[i].Id).Append('|').Append(channels[i].Name).Append('|').Append(channels[i].MemberCount).Append(';');
            snapshotBuilder.Append("L=").Append(engine.LocalChannelId);

            string snapshot = snapshotBuilder.ToString();
            if (String.Equals(snapshot, _lastChannelSnapshot, StringComparison.Ordinal))
                return;

            _lastChannelSnapshot = snapshot;
            _channels.BeginUpdate();
            _channels.Items.Clear();
            int selectedIndex = -1;
            for (i = 0; i < channels.Length; i++)
            {
                _channels.Items.Add(channels[i]);
                if (channels[i].Id == engine.LocalChannelId)
                    selectedIndex = i;
            }
            _channels.EndUpdate();
            if (selectedIndex >= 0)
                _channels.SelectedIndex = selectedIndex;
            _channels.Invalidate();

            if (engine.IsHost)
                SaveHostedChannelLayout();
        }

        private void UpdateMemberRows(VoipEngine engine)
        {
            RoomMemberInfo[] members = engine.GetMembers();
            StringBuilder identity = new StringBuilder();
            StringBuilder speaking = new StringBuilder();
            int i;
            for (i = 0; i < members.Length; i++)
            {
                identity.Append(members[i].Id).Append('|').Append(members[i].Name).Append('|')
                    .Append(members[i].ChannelId).Append('|')
                    .Append(members[i].IsLocal ? '1' : '0').Append('|').Append(members[i].IsHost ? '1' : '0').Append(';');
                speaking.Append(members[i].Id).Append('|').Append(members[i].IsSpeaking ? '1' : '0').Append(';');
            }

            string identitySnapshot = identity.ToString();
            string speakingSnapshot = speaking.ToString();
            if (!String.Equals(identitySnapshot, _lastMemberSnapshot, StringComparison.Ordinal))
            {
                _lastMemberSnapshot = identitySnapshot;
                _lastSpeakingSnapshot = speakingSnapshot;
                _members.BeginUpdate();
                _members.Items.Clear();
                for (i = 0; i < members.Length; i++)
                    _members.Items.Add(members[i]);
                _members.EndUpdate();
            }
            else if (!String.Equals(speakingSnapshot, _lastSpeakingSnapshot, StringComparison.Ordinal))
            {
                _lastSpeakingSnapshot = speakingSnapshot;
                for (i = 0; i < members.Length && i < _members.Items.Count; i++)
                {
                    RoomMemberInfo current = _members.Items[i] as RoomMemberInfo;
                    if (current != null && current.Id == members[i].Id && current.IsSpeaking != members[i].IsSpeaking)
                    {
                        current.IsSpeaking = members[i].IsSpeaking;
                        _members.Invalidate(_members.GetItemRectangle(i));
                    }
                }
            }
        }

        /// <summary>
        /// Probes each joined (not hosted) saved room once per session to show whether it is
        /// online. Rooms are not re-probed afterwards; use "Check if online" to refresh one.
        /// </summary>
        private void BeginPresenceScan()
        {
            List<SavedRoom> pending = new List<SavedRoom>();
            int i;
            for (i = 0; i < _savedRooms.Count; i++)
            {
                SavedRoom r = _savedRooms[i];
                if (r.Owned || r.PresenceChecked)
                    continue;
                if (_engine != null && _engine.IsRunning && Object.ReferenceEquals(r, _activeSavedRoom))
                    continue; // Connected: info comes from the live session.
                r.PresenceChecked = true;
                r.Presence = RoomPresence.Checking;
                pending.Add(r);
            }
            if (pending.Count == 0)
                return;

            _presenceScanRunning = true;
            int generation = Interlocked.Increment(ref _presenceGeneration);
            _rooms.Invalidate();
            UpdateSelectedRoomUi();

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ProbeResult> results = new List<ProbeResult>();
                int j;
                for (j = 0; j < pending.Count; j++)
                    results.Add(RoomProbe.Check(pending[j].Invite, 700));

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (generation != _presenceGeneration)
                            return;
                        _presenceScanRunning = false;
                        int k;
                        for (k = 0; k < pending.Count; k++)
                        {
                            SavedRoom room = pending[k];
                            if (_engine != null && _engine.IsRunning && Object.ReferenceEquals(room, _activeSavedRoom))
                                continue; // Joined while the probe was in flight; the session is authoritative.
                            ProbeResult result = results[k];
                            room.Presence = result.Online ? RoomPresence.Online : RoomPresence.Offline;
                            room.MemberCount = result.MemberCount;
                            room.MaxMembers = result.MaxMembers;
                            if (result.Online && !String.IsNullOrWhiteSpace(result.RoomName) && room.Name == "Voice Room")
                                room.Name = result.RoomName;
                        }
                        _rooms.Invalidate();
                        UpdateSelectedRoomUi();
                    });
                }
                catch
                {
                    _presenceScanRunning = false;
                }
            });
        }

        private void UpdateSelectedRoomUi()
        {
            SavedRoom room = _selectedRoom;
            if (room == null && _activeSavedRoom != null)
                room = _activeSavedRoom;

            if (room == null)
            {
                _roomTitle.Text = _engine != null && _engine.IsRunning ? _activeRoomName : "m\u00E6la";
                _roomPresence.Text = _engine != null && _engine.IsRunning ? "Active voice room" : "Use Host or Join to start";
                _roomAction.Text = "Select a saved room";
                _roomAction.Enabled = false;
                _inviteLabel.Text = RoomCode.IsValid(_activeInvite) ? _activeInvite : "No room code yet";
                _copyInvite.Enabled = RoomCode.IsValid(_activeInvite);
            }
            else
            {
                _roomTitle.Text = room.Name;
                if (room.Presence == RoomPresence.Online)
                    _roomPresence.Text = "Online" + (room.MaxMembers > 0 ? "  \u2022  " + room.MemberCount + "/" + room.MaxMembers : String.Empty);
                else if (room.Presence == RoomPresence.Checking)
                    _roomPresence.Text = "Checking...";
                else if (room.Presence == RoomPresence.Offline)
                    _roomPresence.Text = "Offline";
                else
                    _roomPresence.Text = room.Owned ? "Saved hosted room" : "Saved room";
                _roomPresence.ForeColor = room.Presence == RoomPresence.Online ? Good : TextMuted;

                _roomAction.Text = room.Owned ? "Host Voice" : "Join Voice";
                _roomAction.Enabled = _engine == null || !_engine.IsRunning;
                string invite = (_engine != null && _engine.IsHost && Object.ReferenceEquals(room, _activeSavedRoom)) ? _activeInvite : room.Invite;
                _inviteLabel.Text = RoomCode.IsValid(invite) ? invite : (room.Owned ? "Code appears after hosting" : "Legacy room - new code required");
                _copyInvite.Enabled = RoomCode.IsValid(invite);
            }

            if (_engine != null && _engine.IsRunning)
            {
                _roomTitle.Text = _activeRoomName;
                _voiceTitle.Text = "General  \u2022  " + _activeRoomName;
                _networkLabel.Text = _engine.IsHost ? _natStatus : "Connected to room";
            }
            else
            {
                _voiceTitle.Text = "General";
                _connectionStatus.Text = "Not connected";
                _connectionStatus.ForeColor = TextMuted;
                _networkLabel.Text = room == null ? "Network idle" : (room.Owned ? "Host this room to refresh its room code." : "Status is checked once when m\u00E6la starts. Right-click the room to check again.");
            }
        }

        private void RebuildRoomList()
        {
            RebuildRoomList(null);
        }

        private void RebuildRoomList(SavedRoom select)
        {
            _rooms.BeginUpdate();
            _rooms.Items.Clear();
            int i;
            for (i = 0; i < _savedRooms.Count; i++)
                _rooms.Items.Add(_savedRooms[i]);
            _rooms.EndUpdate();

            if (select != null)
                SelectRoomInList(select);
            else if (_rooms.Items.Count > 0 && _rooms.SelectedIndex < 0)
                _rooms.SelectedIndex = 0;
        }

        private void SelectRoomInList(SavedRoom room)
        {
            if (room == null)
                return;
            int i;
            for (i = 0; i < _rooms.Items.Count; i++)
            {
                if (Object.ReferenceEquals(_rooms.Items[i], room))
                {
                    _rooms.SelectedIndex = i;
                    _selectedRoom = room;
                    return;
                }
            }
        }

        private SavedRoom FindSavedByInvite(string invite)
        {
            int i;
            for (i = 0; i < _savedRooms.Count; i++)
            {
                if (String.Equals(_savedRooms[i].Invite, invite, StringComparison.OrdinalIgnoreCase))
                    return _savedRooms[i];
            }
            return null;
        }

        private void ForgetSelectedRoom()
        {
            SavedRoom room = _selectedRoom;
            if (room == null)
                return;
            if (_engine != null && _engine.IsRunning && Object.ReferenceEquals(room, _activeSavedRoom))
            {
                MessageBox.Show(this, "Disconnect before forgetting the room you're currently using.", "Room is active", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _savedRooms.Remove(room);
            SavedRoomStore.Save(_savedRooms);
            _selectedRoom = null;
            RebuildRoomList();
            UpdateSelectedRoomUi();
        }

        private void CopyInviteClicked(object sender, EventArgs e)
        {
            string invite = String.Empty;
            if (_engine != null && _engine.IsHost && !String.IsNullOrWhiteSpace(_activeInvite))
                invite = _activeInvite;
            else if (_selectedRoom != null)
                invite = _selectedRoom.Invite;
            else
                invite = _activeInvite;
            if (RoomCode.IsValid(invite))
                CopyToClipboard(invite);
        }

        private void CopyToClipboard(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return;
            try
            {
                Clipboard.SetText(value.Trim());
                _connectionStatus.Text = "Room code copied";
                _connectionStatus.ForeColor = Good;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not copy room code", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BuildMemberContextMenu()
        {
            _memberMenu.BackColor = Sidebar;
            _memberMenu.ForeColor = TextMain;
            _memberMenu.ShowImageMargin = false;
            _memberMenu.Opening += MemberMenuOpening;
        }

        private void MembersMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
                return;
            int index = _members.IndexFromPoint(e.Location);
            if (index < 0 || index >= _members.Items.Count)
                return;
            _members.SelectedIndex = index;
            _contextMember = _members.Items[index] as RoomMemberInfo;
            if (_contextMember != null)
                _memberMenu.Show(_members, e.Location);
        }

        private void MemberMenuOpening(object sender, CancelEventArgs e)
        {
            _memberMenu.Items.Clear();
            RoomMemberInfo member = _contextMember;
            VoipEngine engine = _engine;
            if (member == null || engine == null)
            {
                e.Cancel = true;
                return;
            }

            ToolStripMenuItem heading = new ToolStripMenuItem(member.Name + (member.IsHost ? "  \u2022 Host" : String.Empty));
            heading.Enabled = false;
            _memberMenu.Items.Add(heading);
            if (member.IsLocal)
            {
                ToolStripMenuItem you = new ToolStripMenuItem("This is you");
                you.Enabled = false;
                _memberMenu.Items.Add(you);
                return;
            }

            ToolStripMenuItem mute = new ToolStripMenuItem("Mute");
            mute.Checked = engine.GetPeerMuted(member.Id);
            mute.CheckOnClick = true;
            mute.Click += delegate
            {
                if (_engine != null)
                {
                    _engine.SetPeerMuted(member.Id, mute.Checked);
                    _members.Invalidate();
                }
            };
            _memberMenu.Items.Add(mute);

            ToolStripMenuItem volume = new ToolStripMenuItem("User volume");
            int[] levels = new int[] { 25, 50, 75, 100, 125, 150, 200 };
            int current = engine.GetPeerVolumePercent(member.Id);
            int i;
            for (i = 0; i < levels.Length; i++)
            {
                int level = levels[i];
                ToolStripMenuItem choice = new ToolStripMenuItem(level + "%");
                choice.Checked = current == level;
                choice.Tag = level;
                choice.Click += PeerVolumeClicked;
                volume.DropDownItems.Add(choice);
            }
            _memberMenu.Items.Add(volume);

            if (engine.IsHost)
            {
                _memberMenu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem move = new ToolStripMenuItem("Move to channel");
                VoiceChannelInfo[] channels = engine.GetChannels();
                int c;
                for (c = 0; c < channels.Length; c++)
                {
                    VoiceChannelInfo targetChannel = channels[c];
                    ToolStripMenuItem moveChoice = new ToolStripMenuItem("# " + targetChannel.Name);
                    moveChoice.Enabled = member.ChannelId != targetChannel.Id;
                    moveChoice.Tag = targetChannel.Id;
                    moveChoice.Click += delegate(object moveSender, EventArgs moveArgs)
                    {
                        ToolStripMenuItem selected = moveSender as ToolStripMenuItem;
                        if (_engine != null && selected != null)
                            _engine.MoveMemberToChannel(member.Id, (int)selected.Tag);
                    };
                    move.DropDownItems.Add(moveChoice);
                }

                ToolStripMenuItem lobbyChoice = new ToolStripMenuItem("Room Lobby");
                lobbyChoice.Enabled = member.ChannelId != 0;
                lobbyChoice.Tag = 0;
                lobbyChoice.Click += delegate(object moveSender, EventArgs moveArgs)
                {
                    ToolStripMenuItem selected = moveSender as ToolStripMenuItem;
                    if (_engine != null && selected != null)
                        _engine.MoveMemberToChannel(member.Id, (int)selected.Tag);
                };
                move.DropDownItems.Add(new ToolStripSeparator());
                move.DropDownItems.Add(lobbyChoice);
                _memberMenu.Items.Add(move);

                ToolStripMenuItem kick = new ToolStripMenuItem("Kick");
                kick.Click += delegate { if (_engine != null) _engine.KickMember(member.Id, false); };
                _memberMenu.Items.Add(kick);
                ToolStripMenuItem ban = new ToolStripMenuItem("Ban for this session");
                ban.ForeColor = Danger;
                ban.Click += delegate { if (_engine != null) _engine.KickMember(member.Id, true); };
                _memberMenu.Items.Add(ban);
            }
        }

        private void PeerVolumeClicked(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null || _contextMember == null || _engine == null)
                return;
            _engine.SetPeerVolumePercent(_contextMember.Id, (int)item.Tag);
            _members.Invalidate();
        }

        private void MembersDrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _members.Items.Count)
                return;
            RoomMemberInfo member = _members.Items[e.Index] as RoomMemberInfo;
            if (member == null)
                return;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color row = selected ? Selected : MainBg;
            using (SolidBrush b = new SolidBrush(row))
                e.Graphics.FillRectangle(b, e.Bounds);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle avatar = new Rectangle(e.Bounds.Left + 12, e.Bounds.Top + 9, 40, 40);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(88, 91, 96)))
                e.Graphics.FillEllipse(b, avatar);
            if (member.IsSpeaking)
            {
                using (Pen p = new Pen(Good, 3F))
                    e.Graphics.DrawEllipse(p, avatar.Left - 2, avatar.Top - 2, avatar.Width + 4, avatar.Height + 4);
            }

            string initial = member.Name.Length > 0 ? member.Name.Substring(0, 1).ToUpperInvariant() : "?";
            using (Font f = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point))
            using (SolidBrush b = new SolidBrush(TextMain))
            {
                SizeF s = e.Graphics.MeasureString(initial, f);
                e.Graphics.DrawString(initial, f, b, avatar.Left + (avatar.Width - s.Width) / 2F, avatar.Top + (avatar.Height - s.Height) / 2F - 1F);
            }

            string label = member.Name + (member.IsLocal ? "  (you)" : String.Empty);
            using (Font f = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point))
            using (SolidBrush b = new SolidBrush(member.IsSpeaking ? TextMain : Color.FromArgb(219, 222, 225)))
                e.Graphics.DrawString(label, f, b, e.Bounds.Left + 66, e.Bounds.Top + 11);

            string sub;
            if (member.IsHost)
                sub = "Host";
            else if (!member.IsLocal && _engine != null)
                sub = _engine.GetPeerMuted(member.Id) ? "Muted locally" : "Volume " + _engine.GetPeerVolumePercent(member.Id) + "%";
            else
                sub = member.IsSpeaking ? "Speaking" : "Connected";

            using (Font f = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush b = new SolidBrush(member.IsSpeaking ? Good : TextMuted))
                e.Graphics.DrawString(sub, f, b, e.Bounds.Left + 66, e.Bounds.Top + 32);
        }

        private void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_allowTrueClose && e.CloseReason == CloseReason.UserClosing &&
                _engine != null && _engine.IsRunning && _engine.IsHost && _keepRoomAlive)
            {
                e.Cancel = true;
                try { _engine.LeaveVoice(); } catch { }
                Hide();
                _notifyIcon.Visible = true;
                _notifyIcon.BalloonTipTitle = "m\u00E6la room still online";
                _notifyIcon.BalloonTipText = "The room server is still running. Double-click the tray icon to reopen m\u00E6la.";
                try { _notifyIcon.ShowBalloonTip(2500); } catch { }
                return;
            }

            _uiTimer.Stop();
            _updateTimer.Stop();
            Interlocked.Increment(ref _presenceGeneration);
            StopEngine();
            PreferencesStore.Save(_preferences);
            SavedRoomStore.Save(_savedRooms);
            _notifyIcon.Visible = false;
            try { _notifyIcon.Dispose(); } catch { }
        }

        private void RestoreFromTray()
        {
            if (!Visible)
                Show();
            WindowState = FormWindowState.Normal;
            Activate();
            _notifyIcon.Visible = false;
            if (_pendingUpdate != null)
                BeginInvoke((MethodInvoker)ShowPendingUpdate);
        }

        private static string ValidateName(string value)
        {
            string name = value == null ? String.Empty : value.Trim();
            if (name.Length == 0)
                name = "Guest";
            if (name.Length > 32)
                name = name.Substring(0, 32);
            return name;
        }

        private static string FetchPublicIPv4()
        {
            HttpWebRequest request = null;
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
                request = (HttpWebRequest)WebRequest.Create("https://api.ipify.org");
                request.Method = "GET";
                request.UserAgent = "Maela/5";
                request.Timeout = 4500;
                request.ReadWriteTimeout = 4500;
                string text;
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    text = reader.ReadToEnd().Trim();
                IPAddress parsed;
                if (IPAddress.TryParse(text, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                    return parsed.ToString();
            }
            catch
            {
                if (request != null)
                {
                    try { request.Abort(); } catch { }
                }
            }
            return String.Empty;
        }

        private static string GetOutboundLocalIPv4Address()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
                    IPEndPoint ep = socket.LocalEndPoint as IPEndPoint;
                    if (ep != null)
                        return ep.Address.ToString();
                }
            }
            catch { }
            return GetPrimaryLocalIPv4Address();
        }

        private static string GetPrimaryLocalIPv4Address()
        {
            try
            {
                IPAddress[] addresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
                int i;
                for (i = 0; i < addresses.Length; i++)
                {
                    if (addresses[i].AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addresses[i]))
                        return addresses[i].ToString();
                }
            }
            catch { }
            return "127.0.0.1";
        }
    }

    internal enum RoomPresence
    {
        Unknown,
        Checking,
        Online,
        Offline
    }

    internal static class Branding
    {
        public static Icon LoadAppIcon()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null)
                    return icon;
            }
            catch { }
            return SystemIcons.Application;
        }

        public static Image LoadBrandMark()
        {
            try
            {
                using (Stream stream = typeof(Branding).Assembly.GetManifestResourceStream("MaelaMark.png"))
                {
                    if (stream != null)
                    {
                        using (Image image = Image.FromStream(stream))
                            return new Bitmap(image);
                    }
                }
            }
            catch { }
            return null;
        }
    }

    internal static class TextPrompt
    {
        public static string Show(IWin32Window owner, string title, string label, string initialValue)
        {
            using (Form form = new Form())
            {
                Color background = Color.FromArgb(49, 51, 56);
                Color panel = Color.FromArgb(43, 45, 49);
                Color input = Color.FromArgb(30, 31, 34);
                Color text = Color.FromArgb(242, 243, 245);
                Color muted = Color.FromArgb(181, 186, 193);
                Color accent = Color.FromArgb(88, 101, 242);

                form.Text = String.IsNullOrWhiteSpace(title) ? "m\u00E6la" : title;
                form.ClientSize = new Size(420, 178);
                form.MinimumSize = form.MaximumSize = form.Size;
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = false;
                form.BackColor = background;
                form.ForeColor = text;
                form.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
                try { form.Icon = Branding.LoadAppIcon(); } catch { }

                Label caption = new Label();
                caption.AutoSize = true;
                caption.Location = new Point(22, 20);
                caption.ForeColor = muted;
                caption.Text = String.IsNullOrWhiteSpace(label) ? "Name" : label;
                form.Controls.Add(caption);

                TextBox box = new TextBox();
                box.Location = new Point(22, 46);
                box.Size = new Size(376, 27);
                box.BackColor = input;
                box.ForeColor = text;
                box.BorderStyle = BorderStyle.FixedSingle;
                box.Text = initialValue ?? String.Empty;
                form.Controls.Add(box);

                Button cancel = new Button();
                cancel.Text = "Cancel";
                cancel.Location = new Point(216, 108);
                cancel.Size = new Size(86, 34);
                cancel.FlatStyle = FlatStyle.Flat;
                cancel.FlatAppearance.BorderColor = Color.FromArgb(78, 80, 88);
                cancel.BackColor = panel;
                cancel.ForeColor = text;
                cancel.DialogResult = DialogResult.Cancel;
                form.Controls.Add(cancel);

                Button ok = new Button();
                ok.Text = "Save";
                ok.Location = new Point(312, 108);
                ok.Size = new Size(86, 34);
                ok.FlatStyle = FlatStyle.Flat;
                ok.FlatAppearance.BorderSize = 0;
                ok.BackColor = accent;
                ok.ForeColor = Color.White;
                ok.DialogResult = DialogResult.OK;
                form.Controls.Add(ok);

                form.AcceptButton = ok;
                form.CancelButton = cancel;
                form.Shown += delegate
                {
                    box.Focus();
                    box.SelectAll();
                };

                DialogResult result = owner == null ? form.ShowDialog() : form.ShowDialog(owner);
                if (result != DialogResult.OK)
                    return null;

                return box.Text.Trim();
            }
        }
    }

    internal sealed class SavedRoom
    {
        public string Name = "Voice Room";
        public string Invite = String.Empty;
        public bool Owned;
        public int PreferredPort;
        public bool KeepAlive = true;
        public readonly List<string> ChannelNames = new List<string>();
        public RoomPresence Presence = RoomPresence.Unknown;
        public int MemberCount;
        public int MaxMembers;

        /// <summary>Session-only: set once this room has been probed, so it is not probed again.</summary>
        public bool PresenceChecked;

        public override string ToString()
        {
            return Name;
        }
    }

    internal static class SavedRoomStore
    {
        private static string FilePath
        {
            get
            {
                return Path.Combine(GetFolder(), "rooms.txt");
            }
        }

        public static List<SavedRoom> Load()
        {
            List<SavedRoom> result = new List<SavedRoom>();
            try
            {
                string loadPath = FilePath;
                if (!File.Exists(loadPath))
                {
                    string legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RyanVoip", "rooms.txt");
                    if (File.Exists(legacyPath))
                        loadPath = legacyPath;
                    else
                        return result;
                }
                string[] lines = File.ReadAllLines(loadPath);
                int i;
                for (i = 0; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('|');
                    if (parts.Length < 4)
                        continue;
                    SavedRoom room = new SavedRoom();
                    room.Owned = parts[0] == "1";
                    int p;
                    Int32.TryParse(parts[1], out p);
                    room.PreferredPort = p;
                    room.Name = Decode(parts[2]);
                    room.Invite = Decode(parts[3]);
                    if (parts.Length >= 5)
                        room.KeepAlive = parts[4] != "0";
                    if (parts.Length >= 6)
                    {
                        string channelText = Decode(parts[5]);
                        string[] channelNames = channelText.Replace("\r\n", "\n").Split('\n');
                        int c;
                        for (c = 0; c < channelNames.Length; c++)
                        {
                            string channelName = channelNames[c].Trim();
                            if (channelName.Length > 0)
                                room.ChannelNames.Add(channelName);
                        }
                    }
                    if (room.Owned && room.ChannelNames.Count == 0)
                        room.ChannelNames.Add("General");
                    if (String.IsNullOrWhiteSpace(room.Name))
                        room.Name = "Voice Room";
                    result.Add(room);
                }
            }
            catch { }
            return result;
        }

        public static void Save(IList<SavedRoom> rooms)
        {
            try
            {
                string folder = GetFolder();
                Directory.CreateDirectory(folder);
                List<string> lines = new List<string>();
                int i;
                for (i = 0; i < rooms.Count; i++)
                {
                    SavedRoom room = rooms[i];
                    string channelText = room.ChannelNames == null ? String.Empty : String.Join("\n", room.ChannelNames.ToArray());
                    lines.Add((room.Owned ? "1" : "0") + "|" + room.PreferredPort + "|" + Encode(room.Name) + "|" + Encode(room.Invite) + "|" + (room.KeepAlive ? "1" : "0") + "|" + Encode(channelText));
                }
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch { }
        }

        private static string GetFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maela");
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value == null ? String.Empty : value));
        }

        private static string Decode(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch { return String.Empty; }
        }
    }

    internal sealed class AppPreferences
    {
        public string DisplayName = Environment.UserName;
        public uint InputDeviceId = NativeMethods.WAVE_MAPPER;
        public uint OutputDeviceId = NativeMethods.WAVE_MAPPER;
        public int MicVolumePercent = 100;
        public int MasterVolumePercent = 100;
        public bool EchoCancellation = true;
        public bool AutoUpdate = true;
        public string SkippedVersion = String.Empty;

        public AppPreferences Clone()
        {
            AppPreferences p = new AppPreferences();
            p.DisplayName = DisplayName;
            p.InputDeviceId = InputDeviceId;
            p.OutputDeviceId = OutputDeviceId;
            p.MicVolumePercent = MicVolumePercent;
            p.MasterVolumePercent = MasterVolumePercent;
            p.EchoCancellation = EchoCancellation;
            p.AutoUpdate = AutoUpdate;
            p.SkippedVersion = SkippedVersion;
            return p;
        }
    }

    internal static class PreferencesStore
    {
        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maela", "settings.txt"); }
        }

        public static AppPreferences Load()
        {
            AppPreferences p = new AppPreferences();
            try
            {
                string loadPath = FilePath;
                if (!File.Exists(loadPath))
                {
                    string legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RyanVoip", "settings.txt");
                    if (File.Exists(legacyPath))
                        loadPath = legacyPath;
                    else
                        return p;
                }
                string[] lines = File.ReadAllLines(loadPath);
                int i;
                for (i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = lines[i].Substring(0, eq);
                    string value = lines[i].Substring(eq + 1);
                    if (key == "name")
                    {
                        try { p.DisplayName = Encoding.UTF8.GetString(Convert.FromBase64String(value)); } catch { }
                    }
                    else if (key == "in")
                    {
                        uint v; if (UInt32.TryParse(value, out v)) p.InputDeviceId = v;
                    }
                    else if (key == "out")
                    {
                        uint v; if (UInt32.TryParse(value, out v)) p.OutputDeviceId = v;
                    }
                    else if (key == "mic")
                    {
                        int v; if (Int32.TryParse(value, out v)) p.MicVolumePercent = Clamp(v, 0, 150);
                    }
                    else if (key == "master")
                    {
                        int v; if (Int32.TryParse(value, out v)) p.MasterVolumePercent = Clamp(v, 0, 150);
                    }
                    else if (key == "aec")
                    {
                        p.EchoCancellation = value != "0";
                    }
                    else if (key == "updates")
                    {
                        p.AutoUpdate = value != "0";
                    }
                    else if (key == "skip")
                    {
                        p.SkippedVersion = value.Trim();
                    }
                }
            }
            catch { }
            return p;
        }

        public static void Save(AppPreferences p)
        {
            if (p == null)
                return;
            try
            {
                string folder = Path.GetDirectoryName(FilePath);
                Directory.CreateDirectory(folder);
                string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.DisplayName == null ? String.Empty : p.DisplayName));
                File.WriteAllLines(FilePath, new string[]
                {
                    "name=" + encoded,
                    "in=" + p.InputDeviceId,
                    "out=" + p.OutputDeviceId,
                    "mic=" + p.MicVolumePercent,
                    "master=" + p.MasterVolumePercent,
                    "aec=" + (p.EchoCancellation ? "1" : "0"),
                    "updates=" + (p.AutoUpdate ? "1" : "0"),
                    "skip=" + (p.SkippedVersion == null ? String.Empty : p.SkippedVersion.Trim())
                });
            }
            catch { }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    internal static class RoomCode
    {
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private static readonly byte[] Mask = new byte[] { 0xA7, 0x3D, 0xC1, 0x58, 0x92, 0xE4, 0x6B, 0xF0 };

        public static string Encode(IPAddress ip, int port)
        {
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Room codes currently require IPv4.");
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException("port");

            byte[] ipBytes = ip.GetAddressBytes();
            byte[] data = new byte[8];
            data[0] = 1;
            Buffer.BlockCopy(ipBytes, 0, data, 1, 4);
            data[5] = (byte)((port >> 8) & 0xFF);
            data[6] = (byte)(port & 0xFF);
            data[7] = Checksum(data, 7);

            int i;
            for (i = 0; i < data.Length; i++)
                data[i] = (byte)(data[i] ^ Mask[i]);

            string raw = Base32Encode(data);
            if (raw.Length != 13)
                throw new InvalidOperationException("Could not create room code.");
            return "M-" + raw.Substring(0, 4) + "-" + raw.Substring(4, 4) + "-" + raw.Substring(8, 5);
        }

        public static bool IsValid(string code)
        {
            IPAddress ip;
            int port;
            return TryDecode(code, out ip, out port);
        }

        public static bool TryDecode(string code, out IPAddress ip, out int port)
        {
            ip = null;
            port = 0;
            if (String.IsNullOrWhiteSpace(code))
                return false;

            StringBuilder cleanBuilder = new StringBuilder();
            string upper = code.Trim().ToUpperInvariant();
            int i;
            for (i = 0; i < upper.Length; i++)
            {
                char c = upper[i];
                if (c == '-' || Char.IsWhiteSpace(c))
                    continue;
                cleanBuilder.Append(c);
            }
            string clean = cleanBuilder.ToString();
            if (clean.Length != 14 || clean[0] != 'M')
                return false;

            string body = clean.Substring(1);
            int lastIndex = Alphabet.IndexOf(body[body.Length - 1]);
            if (lastIndex < 0 || (lastIndex & 1) != 0)
                return false;

            byte[] encoded;
            if (!TryBase32Decode(body, out encoded) || encoded.Length != 8)
                return false;

            for (i = 0; i < encoded.Length; i++)
                encoded[i] = (byte)(encoded[i] ^ Mask[i]);

            if (encoded[0] != 1 || encoded[7] != Checksum(encoded, 7))
                return false;

            int decodedPort = (encoded[5] << 8) | encoded[6];
            if (decodedPort < 1 || decodedPort > 65535)
                return false;

            ip = new IPAddress(new byte[] { encoded[1], encoded[2], encoded[3], encoded[4] });
            port = decodedPort;
            return true;
        }

        private static byte Checksum(byte[] data, int count)
        {
            byte crc = 0x5A;
            int i;
            for (i = 0; i < count; i++)
            {
                crc = (byte)(crc ^ data[i]);
                int bit;
                for (bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x80) != 0)
                        crc = (byte)((crc << 1) ^ 0x1D);
                    else
                        crc = (byte)(crc << 1);
                }
            }
            return crc;
        }

        private static string Base32Encode(byte[] data)
        {
            StringBuilder result = new StringBuilder();
            uint buffer = 0;
            int bits = 0;
            int i;
            for (i = 0; i < data.Length; i++)
            {
                buffer = (buffer << 8) | data[i];
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    result.Append(Alphabet[(int)((buffer >> bits) & 31)]);
                }
            }
            if (bits > 0)
                result.Append(Alphabet[(int)((buffer << (5 - bits)) & 31)]);
            return result.ToString();
        }

        private static bool TryBase32Decode(string value, out byte[] data)
        {
            data = null;
            if (String.IsNullOrEmpty(value))
                return false;
            List<byte> output = new List<byte>();
            uint buffer = 0;
            int bits = 0;
            int i;
            for (i = 0; i < value.Length; i++)
            {
                int index = Alphabet.IndexOf(value[i]);
                if (index < 0)
                    return false;
                buffer = (buffer << 5) | (uint)index;
                bits += 5;
                while (bits >= 8)
                {
                    bits -= 8;
                    output.Add((byte)((buffer >> bits) & 0xFF));
                }
            }
            data = output.ToArray();
            return true;
        }
    }

    internal static class InviteParser
    {
        public static bool TrySplit(string value, out string host, out int port)
        {
            IPAddress codeIp;
            if (RoomCode.TryDecode(value, out codeIp, out port))
            {
                host = codeIp.ToString();
                return true;
            }
            return TrySplitLegacy(value, out host, out port);
        }

        public static bool TrySplitLegacy(string value, out string host, out int port)
        {
            host = String.Empty;
            port = 0;
            if (String.IsNullOrWhiteSpace(value))
                return false;
            string trimmed = value.Trim();
            int colon = trimmed.LastIndexOf(':');
            if (colon <= 0 || colon >= trimmed.Length - 1)
                return false;
            string p = trimmed.Substring(colon + 1).Trim();
            int parsed;
            if (!Int32.TryParse(p, out parsed) || parsed < 1 || parsed > 65535)
                return false;
            host = trimmed.Substring(0, colon).Trim();
            if (host.Length == 0)
                return false;
            port = parsed;
            return true;
        }

        public static IPAddress ResolveIPv4(string host)
        {
            IPAddress parsed;
            if (IPAddress.TryParse(host, out parsed))
            {
                if (parsed.AddressFamily != AddressFamily.InterNetwork)
                    throw new InvalidOperationException("m\u00E6la currently uses IPv4 addresses.");
                return parsed;
            }
            IPAddress[] addresses = Dns.GetHostAddresses(host);
            int i;
            for (i = 0; i < addresses.Length; i++)
            {
                if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
                    return addresses[i];
            }
            throw new InvalidOperationException("Could not resolve an IPv4 address for " + host + ".");
        }
    }

    internal sealed class ProbeResult
    {
        public bool Online;
        public string RoomName = String.Empty;
        public int MemberCount;
        public int MaxMembers;
    }

    internal static class RoomProbe
    {
        private const uint Magic = 0x32564F52;
        private const byte ProtocolVersion = 5;
        private const byte PacketProbe = 8;
        private const byte PacketProbeReply = 9;
        private const int HeaderBytes = 16;

        public static ProbeResult Check(string invite, int timeoutMs)
        {
            ProbeResult result = new ProbeResult();
            string host;
            int port;
            if (!InviteParser.TrySplit(invite, out host, out port))
                return result;
            try
            {
                IPAddress ip = InviteParser.ResolveIPv4(host);
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.ReceiveTimeout = timeoutMs;
                    socket.SendTimeout = timeoutMs;
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    byte[] packet = BuildPacket(PacketProbe, new byte[0]);
                    socket.SendTo(packet, new IPEndPoint(ip, port));
                    byte[] buffer = new byte[512];
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int length = socket.ReceiveFrom(buffer, ref remote);
                    if (length < HeaderBytes || BitConverter.ToUInt32(buffer, 0) != Magic || buffer[4] != ProtocolVersion || buffer[5] != PacketProbeReply)
                        return result;
                    int payloadLength = BitConverter.ToUInt16(buffer, 6);
                    if (HeaderBytes + payloadLength != length || payloadLength < 3)
                        return result;
                    int offset = HeaderBytes;
                    int nameLength = buffer[offset++];
                    if (offset + nameLength + 2 > length)
                        return result;
                    result.RoomName = Encoding.UTF8.GetString(buffer, offset, nameLength);
                    offset += nameLength;
                    result.MemberCount = buffer[offset++];
                    result.MaxMembers = buffer[offset++];
                    result.Online = true;
                    return result;
                }
            }
            catch { }
            return result;
        }

        private static byte[] BuildPacket(byte type, byte[] payload)
        {
            int payloadLength = payload == null ? 0 : payload.Length;
            byte[] data = new byte[HeaderBytes + payloadLength];
            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, data, 0, 4);
            data[4] = ProtocolVersion;
            data[5] = type;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)payloadLength), 0, data, 6, 2);
            if (payloadLength > 0)
                Buffer.BlockCopy(payload, 0, data, HeaderBytes, payloadLength);
            return data;
        }
    }

    internal sealed class HostRoomDialog : Form
    {
        private readonly TextBox _name = new TextBox();
        private readonly NumericUpDown _port = new NumericUpDown();
        private readonly CheckBox _save = new CheckBox();
        private readonly CheckBox _keepAlive = new CheckBox();

        public string RoomName { get { return String.IsNullOrWhiteSpace(_name.Text) ? "Voice Room" : _name.Text.Trim(); } }
        public int Port { get { return Decimal.ToInt32(_port.Value); } }
        public bool SaveRoom { get { return _save.Checked; } }
        public bool KeepRoomAlive { get { return _keepAlive.Checked; } }

        public HostRoomDialog(SavedRoom existing)
        {
            Text = existing == null ? "Host a room" : "Host saved room";
            ClientSize = new Size(420, 370);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(49, 51, 56);
            ForeColor = Color.FromArgb(242, 243, 245);
            Font = new Font("Segoe UI", 9F);

            Label title = new Label();
            title.Text = "Host a room";
            title.Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(20, 18);
            Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "Your PC is the room server while the room is running.";
            subtitle.AutoSize = true;
            subtitle.ForeColor = Color.FromArgb(181, 186, 193);
            subtitle.Location = new Point(22, 51);
            Controls.Add(subtitle);

            AddLabel("Room name", 22, 82);
            _name.Location = new Point(22, 103);
            _name.Size = new Size(374, 26);
            _name.BackColor = Color.FromArgb(30, 31, 34);
            _name.ForeColor = Color.White;
            _name.BorderStyle = BorderStyle.FixedSingle;
            _name.Text = existing == null ? "Voice Room" : existing.Name;
            Controls.Add(_name);

            _port.Minimum = 0;
            _port.Maximum = 65535;
            _port.Value = existing == null ? 0 : Math.Max(0, Math.Min(65535, existing.PreferredPort));

            Label networkNote = new Label();
            networkNote.Text = "Networking is automatic. m\u00E6la creates a shareable room code for you.";
            networkNote.AutoSize = true;
            networkNote.ForeColor = Color.FromArgb(148, 155, 164);
            networkNote.Location = new Point(22, 151);
            Controls.Add(networkNote);

            _keepAlive.Text = "Keep room running when I leave voice or close the window";
            _keepAlive.AutoSize = true;
            _keepAlive.Location = new Point(22, 196);
            _keepAlive.ForeColor = Color.White;
            _keepAlive.Checked = existing == null || existing.KeepAlive;
            Controls.Add(_keepAlive);

            Label keepNote = new Label();
            keepNote.Text = "Closing the window sends m\u00E6la to the tray while the room server stays online.";
            keepNote.AutoSize = true;
            keepNote.ForeColor = Color.FromArgb(148, 155, 164);
            keepNote.Location = new Point(42, 221);
            Controls.Add(keepNote);

            _save.Text = "Save this room";
            _save.AutoSize = true;
            _save.Location = new Point(22, 259);
            _save.ForeColor = Color.White;
            _save.Checked = true;
            _save.Enabled = existing == null;
            Controls.Add(_save);

            Button cancel = MakeDialogButton("Cancel", false);
            cancel.Location = new Point(212, 318);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            Button host = MakeDialogButton("Host", true);
            host.Location = new Point(304, 318);
            host.DialogResult = DialogResult.OK;
            Controls.Add(host);
            AcceptButton = host;
            CancelButton = cancel;
        }

        private void AddLabel(string text, int x, int y)
        {
            Label l = new Label(); l.Text = text; l.AutoSize = true; l.ForeColor = Color.FromArgb(181, 186, 193); l.Location = new Point(x, y); Controls.Add(l);
        }

        private static Button MakeDialogButton(string text, bool primary)
        {
            Button b = new Button(); b.Text = text; b.Size = new Size(82, 32); b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = primary ? 0 : 1; b.BackColor = primary ? Color.FromArgb(88, 101, 242) : Color.FromArgb(43, 45, 49); b.ForeColor = Color.White; return b;
        }
    }

    internal sealed class JoinRoomDialog : Form
    {
        private readonly TextBox _invite = new TextBox();
        private readonly TextBox _name = new TextBox();
        private readonly CheckBox _save = new CheckBox();

        public string Invite { get { return _invite.Text.Trim(); } }
        public string RoomName { get { return String.IsNullOrWhiteSpace(_name.Text) ? "Voice Room" : _name.Text.Trim(); } }
        public bool SaveRoom { get { return _save.Checked; } }

        public JoinRoomDialog()
        {
            Text = "Join a room";
            ClientSize = new Size(420, 292);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(49, 51, 56);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9F);

            Label title = new Label(); title.Text = "Join with room code"; title.Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold); title.AutoSize = true; title.Location = new Point(20, 18); Controls.Add(title);
            AddLabel("Room code", 22, 70);
            _invite.Location = new Point(22, 91); _invite.Size = new Size(374, 26); _invite.BackColor = Color.FromArgb(30, 31, 34); _invite.ForeColor = Color.White; _invite.BorderStyle = BorderStyle.FixedSingle; Controls.Add(_invite);
            AddLabel("Room name", 22, 132);
            _name.Location = new Point(22, 153); _name.Size = new Size(374, 26); _name.BackColor = Color.FromArgb(30, 31, 34); _name.ForeColor = Color.White; _name.BorderStyle = BorderStyle.FixedSingle; _name.Text = "Voice Room"; Controls.Add(_name);
            _save.Text = "Save this room"; _save.AutoSize = true; _save.Location = new Point(22, 197); _save.ForeColor = Color.White; _save.Checked = true; Controls.Add(_save);

            Button cancel = MakeDialogButton("Cancel", false); cancel.Location = new Point(212, 240); cancel.DialogResult = DialogResult.Cancel; Controls.Add(cancel);
            Button join = MakeDialogButton("Join", true); join.Location = new Point(304, 240); join.DialogResult = DialogResult.OK; join.Click += ValidateJoin; Controls.Add(join);
            AcceptButton = join; CancelButton = cancel;
        }

        private void ValidateJoin(object sender, EventArgs e)
        {
            IPAddress ip; int p;
            if (!RoomCode.TryDecode(_invite.Text, out ip, out p))
            {
                DialogResult = DialogResult.None;
                MessageBox.Show(this, "Enter a valid m\u00E6la room code, for example M-7QKD-P9XR-4F2NC.", "Invalid room code", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void AddLabel(string text, int x, int y)
        {
            Label l = new Label(); l.Text = text; l.AutoSize = true; l.ForeColor = Color.FromArgb(181, 186, 193); l.Location = new Point(x, y); Controls.Add(l);
        }

        private static Button MakeDialogButton(string text, bool primary)
        {
            Button b = new Button(); b.Text = text; b.Size = new Size(82, 32); b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = primary ? 0 : 1; b.BackColor = primary ? Color.FromArgb(88, 101, 242) : Color.FromArgb(43, 45, 49); b.ForeColor = Color.White; return b;
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TextBox _name = new TextBox();
        private readonly ComboBox _input = new ComboBox();
        private readonly ComboBox _output = new ComboBox();
        private readonly TrackBar _mic = new TrackBar();
        private readonly TrackBar _master = new TrackBar();
        private readonly Label _micText = new Label();
        private readonly Label _masterText = new Label();
        private readonly Label _network = new Label();
        private readonly CheckBox _echo = new CheckBox();
        private readonly CheckBox _autoUpdate = new CheckBox();
        private readonly VoipEngine _engine;
        private AppPreferences _preferences;
        private bool _checkForUpdates;

        public AppPreferences Preferences { get { return _preferences; } }

        /// <summary>True when the user closed the dialog with "Check for updates".</summary>
        public bool CheckForUpdatesRequested { get { return _checkForUpdates; } }

        public SettingsForm(AppPreferences current, VoipEngine engine, string publicIp, string invite, string natStatus)
        {
            _engine = engine;
            _preferences = current == null ? new AppPreferences() : current.Clone();
            Text = "m\u00E6la Settings";
            ClientSize = new Size(690, 640);
            MinimumSize = new Size(620, 600);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(49, 51, 56);
            ForeColor = Color.FromArgb(242, 243, 245);
            Font = new Font("Segoe UI", 9F);

            Label title = MakeLabel("User Settings", 20F, FontStyle.Bold, Color.White); title.Location = new Point(24, 18); Controls.Add(title);
            Label account = MakeLabel("PROFILE", 8F, FontStyle.Bold, Color.FromArgb(148,155,164)); account.Location = new Point(26, 72); Controls.Add(account);
            AddField("Display name", 26, 96);
            _name.Location = new Point(26, 118); _name.Size = new Size(300, 26); StyleText(_name); _name.Text = _preferences.DisplayName; Controls.Add(_name);

            Label voice = MakeLabel("VOICE & AUDIO", 8F, FontStyle.Bold, Color.FromArgb(148,155,164)); voice.Location = new Point(26, 174); Controls.Add(voice);
            AddField("Input device", 26, 198); AddField("Output device", 352, 198);
            StyleCombo(_input); _input.Location = new Point(26, 220); _input.Size = new Size(300, 26); Controls.Add(_input);
            StyleCombo(_output); _output.Location = new Point(352, 220); _output.Size = new Size(300, 26); Controls.Add(_output);
            LoadDevices();

            AddField("Microphone volume", 26, 270);
            _mic.Location = new Point(22, 292); _mic.Size = new Size(280, 35); _mic.Minimum = 0; _mic.Maximum = 150; _mic.Value = Clamp(_preferences.MicVolumePercent, 0, 150); _mic.TickStyle = TickStyle.None; _mic.BackColor = BackColor; _mic.ValueChanged += delegate { _micText.Text = _mic.Value + "%"; }; Controls.Add(_mic);
            _micText.Text = _mic.Value + "%"; _micText.ForeColor = Color.FromArgb(181,186,193); _micText.AutoSize = true; _micText.Location = new Point(300, 298); Controls.Add(_micText);

            AddField("Output volume", 352, 270);
            _master.Location = new Point(348, 292); _master.Size = new Size(250, 35); _master.Minimum = 0; _master.Maximum = 150; _master.Value = Clamp(_preferences.MasterVolumePercent, 0, 150); _master.TickStyle = TickStyle.None; _master.BackColor = BackColor; _master.ValueChanged += delegate { _masterText.Text = _master.Value + "%"; }; Controls.Add(_master);
            _masterText.Text = _master.Value + "%"; _masterText.ForeColor = Color.FromArgb(181,186,193); _masterText.AutoSize = true; _masterText.Location = new Point(602, 298); Controls.Add(_masterText);

            StyleCheck(_echo, "Echo cancellation - stops friends hearing themselves through your speakers");
            _echo.Location = new Point(26, 336); _echo.Checked = _preferences.EchoCancellation; Controls.Add(_echo);

            Label networkHeader = MakeLabel("NETWORK", 8F, FontStyle.Bold, Color.FromArgb(148,155,164)); networkHeader.Location = new Point(26, 384); Controls.Add(networkHeader);
            _network.AutoSize = false; _network.Location = new Point(26, 408); _network.Size = new Size(410, 70); _network.ForeColor = Color.FromArgb(181,186,193); _network.Text = "Public endpoint: " + (String.IsNullOrWhiteSpace(publicIp) ? "Not checked" : "Detected") + "\r\n" + (String.IsNullOrWhiteSpace(natStatus) ? "Network idle" : natStatus) + "\r\nRoom code: " + (String.IsNullOrWhiteSpace(invite) ? "None" : invite); Controls.Add(_network);

            Button firewall = MakeButton("Allow through Firewall", false); firewall.Location = new Point(470, 404); firewall.Size = new Size(182, 34); firewall.Click += FirewallClicked; Controls.Add(firewall);
            Button clearBans = MakeButton("Clear session bans", false); clearBans.Location = new Point(470, 446); clearBans.Size = new Size(182, 34); clearBans.Enabled = engine != null && engine.IsHost && engine.BanCount > 0; clearBans.Click += delegate { if (_engine != null) { _engine.ClearBans(); clearBans.Enabled = false; } }; Controls.Add(clearBans);

            Label updatesHeader = MakeLabel("UPDATES", 8F, FontStyle.Bold, Color.FromArgb(148,155,164)); updatesHeader.Location = new Point(26, 498); Controls.Add(updatesHeader);
            Label version = MakeLabel("m\u00E6la " + Updater.CurrentVersionText, 9F, FontStyle.Regular, Color.FromArgb(181,186,193)); version.Location = new Point(26, 522); Controls.Add(version);
            StyleCheck(_autoUpdate, "Check GitHub for new releases automatically");
            _autoUpdate.Location = new Point(26, 546); _autoUpdate.Checked = _preferences.AutoUpdate; Controls.Add(_autoUpdate);
            Button checkUpdates = MakeButton("Check for updates", false); checkUpdates.Location = new Point(470, 518); checkUpdates.Size = new Size(182, 34); checkUpdates.Click += delegate { _checkForUpdates = true; SaveClicked(checkUpdates, EventArgs.Empty); }; Controls.Add(checkUpdates);

            Label note = MakeLabel("Input/output device changes apply the next time you connect.", 8F, FontStyle.Regular, Color.FromArgb(148,155,164)); note.Location = new Point(26, 597); Controls.Add(note);

            Button close = MakeButton("Save & Close", true); close.Location = new Point(520, 590); close.Size = new Size(132, 34); close.Click += SaveClicked; Controls.Add(close); AcceptButton = close;
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            _preferences.DisplayName = String.IsNullOrWhiteSpace(_name.Text) ? "Guest" : _name.Text.Trim();
            if (_preferences.DisplayName.Length > 32) _preferences.DisplayName = _preferences.DisplayName.Substring(0, 32);
            AudioDeviceItem input = _input.SelectedItem as AudioDeviceItem; if (input != null) _preferences.InputDeviceId = input.Id;
            AudioDeviceItem output = _output.SelectedItem as AudioDeviceItem; if (output != null) _preferences.OutputDeviceId = output.Id;
            _preferences.MicVolumePercent = _mic.Value;
            _preferences.MasterVolumePercent = _master.Value;
            _preferences.EchoCancellation = _echo.Checked;
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
            uint i;
            for (i = 0; i < NativeMethods.waveInGetNumDevs(); i++)
            {
                WAVEINCAPS caps; if (NativeMethods.waveInGetDevCaps(i, out caps, (uint)Marshal.SizeOf(typeof(WAVEINCAPS))) == NativeMethods.MMSYSERR_NOERROR) _input.Items.Add(new AudioDeviceItem(i, caps.szPname));
            }
            for (i = 0; i < NativeMethods.waveOutGetNumDevs(); i++)
            {
                WAVEOUTCAPS caps; if (NativeMethods.waveOutGetDevCaps(i, out caps, (uint)Marshal.SizeOf(typeof(WAVEOUTCAPS))) == NativeMethods.MMSYSERR_NOERROR) _output.Items.Add(new AudioDeviceItem(i, caps.szPname));
            }
            SelectDevice(_input, _preferences.InputDeviceId); SelectDevice(_output, _preferences.OutputDeviceId);
        }

        private static void SelectDevice(ComboBox combo, uint id)
        {
            int i; for (i = 0; i < combo.Items.Count; i++) { AudioDeviceItem item = combo.Items[i] as AudioDeviceItem; if (item != null && item.Id == id) { combo.SelectedIndex = i; return; } }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private void FirewallClicked(object sender, EventArgs e)
        {
            try
            {
                string exePath = Application.ExecutablePath.Replace("\"", String.Empty);
                string command = "netsh advfirewall firewall delete rule name=\"Maela Room\" >nul 2>&1 & netsh advfirewall firewall add rule name=\"Maela Room\" dir=in action=allow program=\"" + exePath + "\" protocol=UDP enable=yes profile=any";
                ProcessStartInfo info = new ProcessStartInfo(); info.FileName = "cmd.exe"; info.Arguments = "/c " + command; info.UseShellExecute = true; info.Verb = "runas"; info.WindowStyle = ProcessWindowStyle.Hidden;
                Process process = Process.Start(info); if (process == null) throw new InvalidOperationException("Windows did not start the firewall helper."); process.WaitForExit();
                MessageBox.Show(this, process.ExitCode == 0 ? "m\u00E6la is allowed through Windows Firewall." : "Windows did not add the firewall rule.", "Firewall", MessageBoxButtons.OK, process.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void AddField(string text, int x, int y) { Label l = MakeLabel(text, 8.5F, FontStyle.Regular, Color.FromArgb(181,186,193)); l.Location = new Point(x,y); Controls.Add(l); }
        private static Label MakeLabel(string text, float size, FontStyle style, Color color) { Label l = new Label(); l.Text=text; l.AutoSize=true; l.Font=new Font("Segoe UI",size,style); l.ForeColor=color; return l; }
        private static void StyleText(TextBox t) { t.BackColor=Color.FromArgb(30,31,34); t.ForeColor=Color.White; t.BorderStyle=BorderStyle.FixedSingle; }
        private static void StyleCheck(CheckBox c, string text) { c.Text=text; c.AutoSize=true; c.ForeColor=Color.FromArgb(219,222,225); c.BackColor=Color.Transparent; c.FlatStyle=FlatStyle.Flat; c.Cursor=Cursors.Hand; }
        private static void StyleCombo(ComboBox c) { c.DropDownStyle=ComboBoxStyle.DropDownList; c.BackColor=Color.FromArgb(30,31,34); c.ForeColor=Color.White; c.FlatStyle=FlatStyle.Flat; }
        private static Button MakeButton(string text, bool primary) { Button b=new Button(); b.Text=text; b.FlatStyle=FlatStyle.Flat; b.FlatAppearance.BorderSize=primary?0:1; b.BackColor=primary?Color.FromArgb(88,101,242):Color.FromArgb(43,45,49); b.ForeColor=Color.White; return b; }
        private static int Clamp(int value,int min,int max) { if(value<min)return min; if(value>max)return max; return value; }
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

    internal sealed class UpnpPortMapping : IDisposable
    {
        private readonly Uri _controlUrl;
        private readonly string _serviceType;
        private readonly int _externalPort;
        private bool _disposed;

        public string ExternalIp { get; private set; }
        public int ExternalPort { get { return _externalPort; } }

        private UpnpPortMapping(Uri controlUrl, string serviceType, int externalPort, string externalIp)
        {
            _controlUrl = controlUrl;
            _serviceType = serviceType;
            _externalPort = externalPort;
            ExternalIp = externalIp;
        }

        public static UpnpPortMapping TryCreate(int port, string internalClient)
        {
            Uri location = DiscoverGatewayLocation();
            if (location == null)
                return null;

            string serviceType;
            Uri controlUrl;
            if (!FindWanService(location, out serviceType, out controlUrl))
                return null;

            List<int> candidates = new List<int>();
            candidates.Add(port);

            Random random = new Random(unchecked(Environment.TickCount ^ port));
            int attempts;
            for (attempts = 0; attempts < 12; attempts++)
            {
                int candidate = random.Next(40000, 65001);
                if (!candidates.Contains(candidate))
                    candidates.Add(candidate);
            }

            int i;
            for (i = 0; i < candidates.Count; i++)
            {
                int externalPort = candidates[i];
                string body =
                    "<NewRemoteHost></NewRemoteHost>" +
                    "<NewExternalPort>" + externalPort + "</NewExternalPort>" +
                    "<NewProtocol>UDP</NewProtocol>" +
                    "<NewInternalPort>" + port + "</NewInternalPort>" +
                    "<NewInternalClient>" + EscapeXml(internalClient) + "</NewInternalClient>" +
                    "<NewEnabled>1</NewEnabled>" +
                    "<NewPortMappingDescription>Maela</NewPortMappingDescription>" +
                    "<NewLeaseDuration>0</NewLeaseDuration>";

                string ignored;
                if (!Soap(controlUrl, serviceType, "AddPortMapping", body, out ignored))
                    continue;

                string externalIp = String.Empty;
                string response;
                if (Soap(controlUrl, serviceType, "GetExternalIPAddress", String.Empty, out response))
                    externalIp = ExtractSoapValue(response, "NewExternalIPAddress");

                return new UpnpPortMapping(controlUrl, serviceType, externalPort, externalIp);
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                string body =
                    "<NewRemoteHost></NewRemoteHost>" +
                    "<NewExternalPort>" + _externalPort + "</NewExternalPort>" +
                    "<NewProtocol>UDP</NewProtocol>";
                string ignored;
                Soap(_controlUrl, _serviceType, "DeletePortMapping", body, out ignored);
            }
            catch
            {
            }
        }

        private static Uri DiscoverGatewayLocation()
        {
            string[] searchTargets = new string[]
            {
                "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                "urn:schemas-upnp-org:service:WANIPConnection:1",
                "urn:schemas-upnp-org:service:WANPPPConnection:1"
            };

            int i;
            for (i = 0; i < searchTargets.Length; i++)
            {
                Socket socket = null;
                try
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.ReceiveTimeout = 1400;
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                    string request =
                        "M-SEARCH * HTTP/1.1\r\n" +
                        "HOST: 239.255.255.250:1900\r\n" +
                        "MAN: \"ssdp:discover\"\r\n" +
                        "MX: 1\r\n" +
                        "ST: " + searchTargets[i] + "\r\n\r\n";
                    byte[] bytes = Encoding.ASCII.GetBytes(request);
                    socket.SendTo(bytes, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

                    DateTime until = DateTime.UtcNow.AddMilliseconds(1500);
                    byte[] buffer = new byte[8192];
                    while (DateTime.UtcNow < until)
                    {
                        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        int count;
                        try
                        {
                            count = socket.ReceiveFrom(buffer, ref remote);
                        }
                        catch (SocketException ex)
                        {
                            if (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.WouldBlock)
                                break;
                            throw;
                        }

                        string response = Encoding.UTF8.GetString(buffer, 0, count);
                        string location = HeaderValue(response, "LOCATION");
                        Uri parsed;
                        if (!String.IsNullOrWhiteSpace(location) && Uri.TryCreate(location.Trim(), UriKind.Absolute, out parsed))
                            return parsed;
                    }
                }
                catch
                {
                }
                finally
                {
                    if (socket != null)
                    {
                        try { socket.Close(); } catch { }
                    }
                }
            }

            return null;
        }

        private static bool FindWanService(Uri location, out string serviceType, out Uri controlUrl)
        {
            serviceType = null;
            controlUrl = null;

            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
                string xml;
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "Maela/3";
                    xml = wc.DownloadString(location);
                }

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);
                XmlNodeList services = doc.GetElementsByTagName("service");
                int i;
                for (i = 0; i < services.Count; i++)
                {
                    string type = ChildText(services[i], "serviceType");
                    if (String.IsNullOrWhiteSpace(type))
                        continue;
                    if (type.IndexOf("WANIPConnection", StringComparison.OrdinalIgnoreCase) < 0 &&
                        type.IndexOf("WANPPPConnection", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    string path = ChildText(services[i], "controlURL");
                    if (String.IsNullOrWhiteSpace(path))
                        continue;

                    serviceType = type.Trim();
                    controlUrl = new Uri(location, path.Trim());
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static string ChildText(XmlNode parent, string localName)
        {
            XmlNode child;
            for (child = parent.FirstChild; child != null; child = child.NextSibling)
            {
                if (String.Equals(child.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                    return child.InnerText;
            }
            return String.Empty;
        }

        private static bool Soap(Uri controlUrl, string serviceType, string action, string innerBody, out string response)
        {
            response = String.Empty;
            HttpWebRequest request = null;
            try
            {
                string envelope =
                    "<?xml version=\"1.0\"?>" +
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                    "<s:Body><u:" + action + " xmlns:u=\"" + EscapeXml(serviceType) + "\">" + innerBody +
                    "</u:" + action + "></s:Body></s:Envelope>";
                byte[] payload = Encoding.UTF8.GetBytes(envelope);

                request = (HttpWebRequest)WebRequest.Create(controlUrl);
                request.Method = "POST";
                request.ContentType = "text/xml; charset=\"utf-8\"";
                request.Headers["SOAPAction"] = "\"" + serviceType + "#" + action + "\"";
                request.ContentLength = payload.Length;
                request.Timeout = 2500;
                request.ReadWriteTimeout = 2500;

                using (Stream stream = request.GetRequestStream())
                    stream.Write(payload, 0, payload.Length);

                using (WebResponse webResponse = request.GetResponse())
                using (StreamReader reader = new StreamReader(webResponse.GetResponseStream()))
                    response = reader.ReadToEnd();

                return true;
            }
            catch
            {
                if (request != null)
                {
                    try { request.Abort(); } catch { }
                }
                return false;
            }
        }

        private static string ExtractSoapValue(string xml, string name)
        {
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);
                XmlNodeList all = doc.GetElementsByTagName("*");
                int i;
                for (i = 0; i < all.Count; i++)
                {
                    if (String.Equals(all[i].LocalName, name, StringComparison.OrdinalIgnoreCase))
                        return all[i].InnerText.Trim();
                }
            }
            catch
            {
            }
            return String.Empty;
        }

        private static string HeaderValue(string headers, string name)
        {
            string[] lines = headers.Replace("\r\n", "\n").Split('\n');
            int i;
            for (i = 0; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0)
                    continue;
                string key = lines[i].Substring(0, colon).Trim();
                if (String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    return lines[i].Substring(colon + 1).Trim();
            }
            return String.Empty;
        }

        private static string EscapeXml(string value)
        {
            if (value == null)
                return String.Empty;
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }

    internal sealed class VoiceChannelInfo
    {
        public int Id;
        public string Name;
        public int MemberCount;

        public override string ToString()
        {
            return Name == null ? String.Empty : Name;
        }
    }

    internal sealed class RoomMemberInfo
    {
        public uint Id;
        public string Name;
        public int ChannelId;
        public bool IsLocal;
        public bool IsHost;
        public bool IsSpeaking;

        public override string ToString()
        {
            return Name == null ? String.Empty : Name;
        }
    }

    internal sealed class VoipEngine : IDisposable
    {
        public const int MaxRoomMembers = 6;

        private const int SampleRate = 48000;
        private const int BitsPerSample = 16;
        private const int Channels = 1;
        private const int FrameMilliseconds = 10;
        private const int AudioBytesPerFrame = SampleRate * (BitsPerSample / 8) * Channels * FrameMilliseconds / 1000;
        private const int CaptureBufferCount = 8;
        private const int HeaderBytes = 16;
        private const uint Magic = 0x32564F52; // "ROV2" in little-endian memory
        private const byte ProtocolVersion = 5;

        private const byte PacketJoin = 1;
        private const byte PacketWelcome = 2;
        private const byte PacketPeerList = 3;
        private const byte PacketKeepAlive = 4;
        private const byte PacketLeave = 5;
        private const byte PacketReject = 6;
        private const byte PacketKick = 7;
        private const byte PacketProbe = 8;
        private const byte PacketProbeReply = 9;
        private const byte PacketAudio = 10;
        private const byte PacketChannelSwitch = 11;

        private readonly object _stateLock = new object();
        private readonly object _sendLock = new object();
        private readonly Dictionary<string, Participant> _hostParticipants = new Dictionary<string, Participant>();
        private readonly Dictionary<uint, string> _memberNames = new Dictionary<uint, string>();
        private readonly Dictionary<uint, int> _memberChannels = new Dictionary<uint, int>();
        private readonly Dictionary<int, string> _channels = new Dictionary<int, string>();
        private readonly Dictionary<uint, PlaybackStream> _playbackStreams = new Dictionary<uint, PlaybackStream>();
        private readonly Dictionary<uint, int> _peerVolumes = new Dictionary<uint, int>();
        private readonly HashSet<uint> _peerMutes = new HashSet<uint>();
        private readonly Dictionary<uint, DateTime> _lastSpeakingUtc = new Dictionary<uint, DateTime>();
        private readonly HashSet<string> _bannedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<NativeBuffer> _captureBuffers = new List<NativeBuffer>();
        private readonly uint _inputDeviceId;
        private readonly uint _outputDeviceId;
        private readonly FarEndReference _farEnd = new FarEndReference();
        private readonly EchoCanceller _echoCanceller;
        private volatile bool _echoCancellation = true;
        private volatile bool _echoResetPending;

        private Socket _socket;
        private Thread _receiveThread;
        private Thread _maintenanceThread;
        private volatile bool _running;
        private volatile bool _muted;
        private volatile bool _isHost;
        private volatile bool _connected;
        private volatile bool _reconnectAllowed;
        private IPEndPoint _hostEndpoint;
        private uint _localId;
        private uint _hostId;
        private string _localName;
        private string _roomName = "Voice Room";
        private int _localChannelId;
        private int _nextChannelId = 2;
        private int _masterVolumePercent = 100;
        private int _micVolumePercent = 100;
        private uint _txSequence;
        private long _txPackets;
        private long _rxPackets;
        private DateTime _lastHostSeenUtc;
        private DateTime _lastJoinSentUtc;
        private DateTime _lastKeepAliveUtc;
        private DateTime _joinStartedUtc;
        private string _lastMessage;
        private int _boundPort;

        private IntPtr _waveIn = IntPtr.Zero;
        private WaveInProc _waveInProc;

        public bool IsRunning { get { return _running; } }
        public bool IsHost { get { return _isHost; } }
        public bool IsConnected { get { return _isHost || _connected; } }
        public bool IsInVoice { get { return IsConnected && _localChannelId > 0; } }
        public int LocalChannelId { get { return _localChannelId; } }
        public bool Muted { get { return _muted; } set { _muted = value; } }

        /// <summary>Removes speaker audio that leaks back into the microphone.</summary>
        public bool EchoCancellation
        {
            get { return _echoCancellation; }
            set
            {
                if (value && !_echoCancellation)
                    _echoResetPending = true;
                _echoCancellation = value;
            }
        }

        /// <summary>Estimated speaker-to-microphone delay in milliseconds, or -1 while unknown.</summary>
        public int EchoDelayMs { get { return _echoCancellation ? _echoCanceller.EstimatedDelayMs : -1; } }
        public long TxPackets { get { return Interlocked.Read(ref _txPackets); } }
        public long RxPackets { get { return Interlocked.Read(ref _rxPackets); } }
        public string LastMessage { get { lock (_stateLock) { return _lastMessage; } } }
        public int BoundPort { get { return _boundPort; } }
        public int BanCount { get { lock (_stateLock) { return _bannedIps.Count; } } }

        public int MasterVolumePercent
        {
            get { return _masterVolumePercent; }
            set
            {
                int clamped = Clamp(value, 0, 150);
                _masterVolumePercent = clamped;
                UpdateAllPlaybackGains();
            }
        }

        public int MicVolumePercent
        {
            get { return _micVolumePercent; }
            set { _micVolumePercent = Clamp(value, 0, 150); }
        }

        public int MemberCount
        {
            get
            {
                lock (_stateLock)
                    return _memberNames.Count;
            }
        }

        public int CurrentChannelMemberCount
        {
            get
            {
                lock (_stateLock)
                {
                    if (_localChannelId <= 0)
                        return 0;
                    int count = 0;
                    foreach (KeyValuePair<uint, int> pair in _memberChannels)
                    {
                        if (pair.Value == _localChannelId)
                            count++;
                    }
                    return count;
                }
            }
        }

        public VoipEngine() : this(NativeMethods.WAVE_MAPPER, NativeMethods.WAVE_MAPPER)
        {
        }

        public VoipEngine(uint inputDeviceId, uint outputDeviceId)
        {
            _inputDeviceId = inputDeviceId;
            _outputDeviceId = outputDeviceId;
            _echoCanceller = new EchoCanceller(_farEnd);
        }

        public string GetChannelName(int channelId)
        {
            lock (_stateLock)
            {
                string value;
                return _channels.TryGetValue(channelId, out value) ? value : String.Empty;
            }
        }

        public VoiceChannelInfo[] GetChannels()
        {
            lock (_stateLock)
            {
                List<VoiceChannelInfo> result = new List<VoiceChannelInfo>();
                foreach (KeyValuePair<int, string> pair in _channels)
                {
                    VoiceChannelInfo info = new VoiceChannelInfo();
                    info.Id = pair.Key;
                    info.Name = pair.Value;
                    int count = 0;
                    foreach (KeyValuePair<uint, int> member in _memberChannels)
                    {
                        if (member.Value == pair.Key)
                            count++;
                    }
                    info.MemberCount = count;
                    result.Add(info);
                }
                result.Sort(delegate(VoiceChannelInfo a, VoiceChannelInfo b) { return a.Id.CompareTo(b.Id); });
                return result.ToArray();
            }
        }

        public RoomMemberInfo[] GetMembers()
        {
            lock (_stateLock)
            {
                List<RoomMemberInfo> result = new List<RoomMemberInfo>();
                DateTime now = DateTime.UtcNow;
                foreach (KeyValuePair<uint, string> pair in _memberNames)
                {
                    int channelId;
                    if (!_memberChannels.TryGetValue(pair.Key, out channelId))
                        channelId = 0;

                    if (_localChannelId > 0 && channelId != _localChannelId)
                        continue;

                    RoomMemberInfo info = new RoomMemberInfo();
                    info.Id = pair.Key;
                    info.Name = pair.Value;
                    info.ChannelId = channelId;
                    info.IsLocal = pair.Key == _localId;
                    info.IsHost = pair.Key == _hostId;
                    DateTime last;
                    info.IsSpeaking = _lastSpeakingUtc.TryGetValue(pair.Key, out last) && (now - last).TotalMilliseconds < 420.0;
                    result.Add(info);
                }

                result.Sort(delegate(RoomMemberInfo a, RoomMemberInfo b)
                {
                    if (a.IsLocal && !b.IsLocal)
                        return -1;
                    if (!a.IsLocal && b.IsLocal)
                        return 1;
                    if (a.IsHost && !b.IsHost)
                        return -1;
                    if (!a.IsHost && b.IsHost)
                        return 1;
                    return String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                return result.ToArray();
            }
        }

        public void ChangeChannel(int channelId)
        {
            if (!_running || !IsConnected)
                return;

            lock (_stateLock)
            {
                if (channelId != 0 && !_channels.ContainsKey(channelId))
                    return;
            }

            if (_isHost)
            {
                lock (_stateLock)
                {
                    _localChannelId = channelId;
                    _memberChannels[_localId] = channelId;
                }
                DisposePlaybackStreams();
                BroadcastPeerList();
                SetLastMessage(channelId == 0 ? "Left voice. Room server is still running." : "Joined #" + GetChannelName(channelId) + ".");
            }
            else if (_connected && _hostEndpoint != null)
            {
                byte[] payload = BitConverter.GetBytes(channelId);
                SendPacket(_hostEndpoint, PacketChannelSwitch, _localId, 0, payload);
            }
        }

        public void LeaveVoice()
        {
            ChangeChannel(0);
        }

        public int CreateChannel(string name)
        {
            if (!_isHost)
                return 0;

            string clean = CleanChannelName(name);
            if (clean.Length == 0)
                return 0;

            int id;
            lock (_stateLock)
            {
                if (_channels.Count >= 16)
                    return 0;
                id = _nextChannelId++;
                while (_channels.ContainsKey(id))
                    id = _nextChannelId++;
                _channels[id] = clean;
            }

            BroadcastPeerList();
            SetLastMessage("Created #" + clean + ".");
            return id;
        }

        public void RenameChannel(int channelId, string name)
        {
            if (!_isHost)
                return;

            string clean = CleanChannelName(name);
            if (clean.Length == 0)
                return;

            lock (_stateLock)
            {
                if (!_channels.ContainsKey(channelId))
                    return;
                _channels[channelId] = clean;
            }

            BroadcastPeerList();
        }

        public void DeleteChannel(int channelId)
        {
            if (!_isHost || channelId == 1)
                return;

            lock (_stateLock)
            {
                if (!_channels.ContainsKey(channelId))
                    return;
                _channels.Remove(channelId);

                List<uint> ids = new List<uint>(_memberChannels.Keys);
                int i;
                for (i = 0; i < ids.Count; i++)
                {
                    if (_memberChannels[ids[i]] == channelId)
                        _memberChannels[ids[i]] = 1;
                }

                foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                {
                    if (pair.Value.ChannelId == channelId)
                        pair.Value.ChannelId = 1;
                }

                if (_localChannelId == channelId)
                    _localChannelId = 1;
            }

            DisposePlaybackStreams();
            BroadcastPeerList();
        }

        public void MoveMemberToChannel(uint memberId, int channelId)
        {
            if (!_isHost)
                return;

            lock (_stateLock)
            {
                if (channelId != 0 && !_channels.ContainsKey(channelId))
                    return;

                if (memberId == _localId)
                {
                    _localChannelId = channelId;
                    _memberChannels[_localId] = channelId;
                }
                else
                {
                    Participant target = null;
                    foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                    {
                        if (pair.Value.Id == memberId)
                        {
                            target = pair.Value;
                            break;
                        }
                    }
                    if (target == null)
                        return;
                    target.ChannelId = channelId;
                    _memberChannels[memberId] = channelId;
                }
            }

            DisposePlaybackStreams();
            BroadcastPeerList();
        }

        private static string CleanChannelName(string value)
        {
            string clean = value == null ? String.Empty : value.Trim();
            clean = clean.Replace("\r", " ").Replace("\n", " ").Replace("|", " ");
            while (clean.IndexOf("  ", StringComparison.Ordinal) >= 0)
                clean = clean.Replace("  ", " ");
            if (clean.Length > 32)
                clean = clean.Substring(0, 32).Trim();
            return clean;
        }

        public int GetPeerVolumePercent(uint id)
        {
            lock (_stateLock)
            {
                int value;
                return _peerVolumes.TryGetValue(id, out value) ? value : 100;
            }
        }

        public void SetPeerVolumePercent(uint id, int percent)
        {
            lock (_stateLock)
                _peerVolumes[id] = Clamp(percent, 0, 200);
            UpdatePlaybackGain(id);
        }

        public bool GetPeerMuted(uint id)
        {
            lock (_stateLock)
                return _peerMutes.Contains(id);
        }

        public void SetPeerMuted(uint id, bool muted)
        {
            lock (_stateLock)
            {
                if (muted)
                    _peerMutes.Add(id);
                else
                    _peerMutes.Remove(id);
            }
            UpdatePlaybackGain(id);
        }

        public void ClearBans()
        {
            lock (_stateLock)
                _bannedIps.Clear();
            SetLastMessage("Host cleared the session ban list.");
        }

        public void KickMember(uint id, bool ban)
        {
            if (!_isHost || id == 0 || id == _localId)
                return;

            Participant target = null;
            string key = null;
            lock (_stateLock)
            {
                foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                {
                    if (pair.Value.Id == id)
                    {
                        key = pair.Key;
                        target = pair.Value;
                        break;
                    }
                }

                if (target != null && ban)
                    _bannedIps.Add(target.Endpoint.Address.ToString());
            }

            if (target == null || key == null)
                return;

            string reason = ban ? "Banned by host." : "Kicked by host.";
            SendPacket(target.Endpoint, PacketKick, _localId, 0, Encoding.UTF8.GetBytes(reason));
            RemoveHostParticipant(key, true);
            SetLastMessage(target.Name + (ban ? " was banned." : " was kicked."));
        }

        public void StartHost(int port, string name)
        {
            StartHost(port, name, "Voice Room", null);
        }

        public void StartHost(int port, string name, string roomName)
        {
            StartHost(port, name, roomName, null);
        }

        public void StartHost(int port, string name, string roomName, IList<string> channelNames)
        {
            if (_running)
                throw new InvalidOperationException("Voice is already running.");

            ResetState();
            _isHost = true;
            _connected = true;
            _localName = name;
            _roomName = String.IsNullOrWhiteSpace(roomName) ? "Voice Room" : roomName.Trim();
            _localId = CreateParticipantId();
            _hostId = _localId;
            _reconnectAllowed = false;
            _lastMessage = String.Empty;

            lock (_stateLock)
            {
                int nextId = 1;
                if (channelNames != null)
                {
                    int i;
                    for (i = 0; i < channelNames.Count && nextId <= 16; i++)
                    {
                        string clean = CleanChannelName(channelNames[i]);
                        if (clean.Length == 0)
                            continue;
                        _channels[nextId++] = clean;
                    }
                }
                if (_channels.Count == 0)
                {
                    _channels[1] = "General";
                    nextId = 2;
                }
                _nextChannelId = nextId;
                _localChannelId = 1;
                _memberNames[_localId] = _localName;
                _memberChannels[_localId] = _localChannelId;
            }

            try
            {
                CreateSocket();
                _socket.Bind(new IPEndPoint(IPAddress.Any, port));
                IPEndPoint boundEndpoint = _socket.LocalEndPoint as IPEndPoint;
                _boundPort = boundEndpoint == null ? port : boundEndpoint.Port;
                OpenWaveIn();
                StartThreadsAndCapture();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void StartClient(IPEndPoint hostEndpoint, string name)
        {
            if (_running)
                throw new InvalidOperationException("Voice is already running.");

            ResetState();
            _isHost = false;
            _connected = false;
            _hostEndpoint = hostEndpoint;
            _localName = name;
            _localId = 0;
            _hostId = 0;
            _localChannelId = 0;
            _reconnectAllowed = true;
            _lastMessage = "Joining room...";
            _joinStartedUtc = DateTime.UtcNow;

            try
            {
                CreateSocket();
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                OpenWaveIn();
                StartThreadsAndCapture();
                SendJoin();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void ResetState()
        {
            _txSequence = 0;
            _txPackets = 0;
            _rxPackets = 0;
            _lastHostSeenUtc = DateTime.MinValue;
            _lastJoinSentUtc = DateTime.MinValue;
            _lastKeepAliveUtc = DateTime.MinValue;
            _joinStartedUtc = DateTime.MinValue;
            _boundPort = 0;
            _hostId = 0;
            _localChannelId = 0;
            _nextChannelId = 2;
            _reconnectAllowed = true;

            lock (_stateLock)
            {
                _hostParticipants.Clear();
                _memberNames.Clear();
                _memberChannels.Clear();
                _channels.Clear();
                _peerVolumes.Clear();
                _peerMutes.Clear();
                _lastSpeakingUtc.Clear();
                _bannedIps.Clear();
                _lastMessage = String.Empty;
            }

            DisposePlaybackStreams();
        }

        private void CreateSocket()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.ReceiveTimeout = 250;
            _socket.ReceiveBufferSize = 1024 * 1024;
            _socket.SendBufferSize = 1024 * 1024;
        }

        private void StartThreadsAndCapture()
        {
            _running = true;

            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Name = "Maela UDP receive";
            _receiveThread.Start();

            _maintenanceThread = new Thread(MaintenanceLoop);
            _maintenanceThread.IsBackground = true;
            _maintenanceThread.Name = "Maela room maintenance";
            _maintenanceThread.Start();

            int result = NativeMethods.waveInStart(_waveIn);
            CheckMmResult(result, "Could not start the microphone");
        }

        private void ReceiveLoop()
        {
            byte[] receiveBuffer = new byte[2048];

            while (_running)
            {
                EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                int length;

                try
                {
                    length = _socket.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref sender);
                }
                catch (SocketException ex)
                {
                    if (!_running)
                        break;
                    if (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.WouldBlock)
                        continue;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (length < HeaderBytes)
                    continue;

                IPEndPoint senderEndpoint = sender as IPEndPoint;
                if (senderEndpoint == null)
                    continue;

                Packet packet;
                if (!TryParsePacket(receiveBuffer, length, out packet))
                    continue;

                if (_isHost)
                    HandleHostPacket(senderEndpoint, packet);
                else
                    HandleClientPacket(senderEndpoint, packet);
            }
        }

        private void HandleHostPacket(IPEndPoint sender, Packet packet)
        {
            string key = EndpointKey(sender);

            if (packet.Type == PacketProbe)
            {
                SendPacket(sender, PacketProbeReply, _localId, 0, BuildProbePayload());
                return;
            }

            if (packet.Type == PacketJoin)
            {
                lock (_stateLock)
                {
                    if (_bannedIps.Contains(sender.Address.ToString()))
                    {
                        SendPacket(sender, PacketKick, _localId, 0, Encoding.UTF8.GetBytes("Banned by host."));
                        return;
                    }
                }

                string requestedName = DecodeName(packet.Payload);
                if (requestedName.Length == 0)
                    requestedName = "Guest";

                Participant participant = null;
                bool added = false;

                lock (_stateLock)
                {
                    if (_hostParticipants.TryGetValue(key, out participant))
                    {
                        participant.LastSeenUtc = DateTime.UtcNow;
                        participant.Name = requestedName;
                        _memberNames[participant.Id] = participant.Name;
                        if (!_memberChannels.ContainsKey(participant.Id))
                        {
                            participant.ChannelId = 1;
                            _memberChannels[participant.Id] = 1;
                        }
                    }
                    else
                    {
                        if (_memberNames.Count >= MaxRoomMembers)
                        {
                            SendPacket(sender, PacketReject, 0, 0, Encoding.UTF8.GetBytes("Room is full (max " + MaxRoomMembers + ")."));
                            return;
                        }

                        participant = new Participant();
                        participant.Id = CreateUniqueParticipantId();
                        participant.Name = requestedName;
                        participant.Endpoint = new IPEndPoint(sender.Address, sender.Port);
                        participant.LastSeenUtc = DateTime.UtcNow;
                        participant.ChannelId = 1;
                        _hostParticipants[key] = participant;
                        _memberNames[participant.Id] = participant.Name;
                        _memberChannels[participant.Id] = participant.ChannelId;
                        added = true;
                    }
                }

                SendPacket(participant.Endpoint, PacketWelcome, participant.Id, 0, new byte[0]);
                BroadcastPeerList();

                if (added)
                    SetLastMessage(participant.Name + " joined.");
                return;
            }

            Participant known;
            lock (_stateLock)
            {
                if (!_hostParticipants.TryGetValue(key, out known))
                    return;
                if (packet.SenderId != known.Id)
                    return;
                known.LastSeenUtc = DateTime.UtcNow;
            }

            if (packet.Type == PacketKeepAlive)
                return;

            if (packet.Type == PacketLeave)
            {
                RemoveHostParticipant(key, true);
                return;
            }

            if (packet.Type == PacketChannelSwitch)
            {
                if (packet.Payload == null || packet.Payload.Length != 4)
                    return;
                int requestedChannel = BitConverter.ToInt32(packet.Payload, 0);
                lock (_stateLock)
                {
                    if (requestedChannel != 0 && !_channels.ContainsKey(requestedChannel))
                        return;
                    known.ChannelId = requestedChannel;
                    _memberChannels[known.Id] = requestedChannel;
                }
                BroadcastPeerList();
                return;
            }

            if (packet.Type == PacketAudio)
            {
                if (packet.Payload == null || packet.Payload.Length != AudioBytesPerFrame)
                    return;

                int senderChannel;
                lock (_stateLock)
                {
                    if (!_memberChannels.TryGetValue(packet.SenderId, out senderChannel) || senderChannel <= 0)
                        return;
                }

                Interlocked.Increment(ref _rxPackets);

                if (_localChannelId == senderChannel)
                    QueueRemoteAudio(packet.SenderId, packet.Sequence, packet.Payload);

                List<IPEndPoint> targets = new List<IPEndPoint>();
                lock (_stateLock)
                {
                    foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                    {
                        Participant participant = pair.Value;
                        if (participant.Id != packet.SenderId && participant.ChannelId == senderChannel)
                            targets.Add(new IPEndPoint(participant.Endpoint.Address, participant.Endpoint.Port));
                    }
                }

                byte[] raw = BuildPacket(PacketAudio, packet.SenderId, packet.Sequence, packet.Payload);
                int i;
                for (i = 0; i < targets.Count; i++)
                    SendRaw(targets[i], raw);
            }
        }

        private void HandleClientPacket(IPEndPoint sender, Packet packet)
        {
            if (_hostEndpoint == null || !sender.Address.Equals(_hostEndpoint.Address))
                return;

            _hostEndpoint = new IPEndPoint(sender.Address, sender.Port);
            _lastHostSeenUtc = DateTime.UtcNow;

            if (packet.Type == PacketWelcome)
            {
                _localId = packet.SenderId;
                _connected = true;
                _joinStartedUtc = DateTime.MinValue;
                SetLastMessage(String.Empty);
                if (_hostEndpoint != null)
                    SendPacket(_hostEndpoint, PacketChannelSwitch, _localId, 0, BitConverter.GetBytes(1));
                return;
            }

            if (packet.Type == PacketReject)
            {
                _connected = false;
                SetLastMessage(DecodeName(packet.Payload));
                return;
            }

            if (packet.Type == PacketKick)
            {
                _connected = false;
                _reconnectAllowed = false;
                _localId = 0;
                _localChannelId = 0;
                lock (_stateLock)
                {
                    _memberNames.Clear();
                    _memberChannels.Clear();
                    _channels.Clear();
                }
                DisposePlaybackStreams();
                string reason = DecodeName(packet.Payload);
                SetLastMessage(reason.Length == 0 ? "Removed by host." : reason);
                return;
            }

            if (packet.Type == PacketPeerList)
            {
                ParsePeerList(packet.Payload);
                return;
            }

            if (packet.Type == PacketAudio)
            {
                if (!_connected || packet.SenderId == 0 || packet.SenderId == _localId || _localChannelId <= 0)
                    return;
                if (packet.Payload == null || packet.Payload.Length != AudioBytesPerFrame)
                    return;

                int senderChannel;
                lock (_stateLock)
                {
                    if (!_memberChannels.TryGetValue(packet.SenderId, out senderChannel) || senderChannel != _localChannelId)
                        return;
                }

                Interlocked.Increment(ref _rxPackets);
                QueueRemoteAudio(packet.SenderId, packet.Sequence, packet.Payload);
            }
        }

        private void MaintenanceLoop()
        {
            while (_running)
            {
                try
                {
                    if (_isHost)
                        HostMaintenance();
                    else
                        ClientMaintenance();
                }
                catch
                {
                }

                Thread.Sleep(250);
            }
        }

        private void HostMaintenance()
        {
            DateTime now = DateTime.UtcNow;
            List<string> expired = new List<string>();

            lock (_stateLock)
            {
                foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                {
                    if ((now - pair.Value.LastSeenUtc).TotalSeconds > 10.0)
                        expired.Add(pair.Key);
                }
            }

            int i;
            for (i = 0; i < expired.Count; i++)
                RemoveHostParticipant(expired[i], false);

            if (expired.Count > 0)
                BroadcastPeerList();
        }

        private void ClientMaintenance()
        {
            DateTime now = DateTime.UtcNow;

            if (_connected && _lastHostSeenUtc != DateTime.MinValue && (now - _lastHostSeenUtc).TotalSeconds > 8.0)
            {
                _connected = false;
                _localId = 0;
                _localChannelId = 0;
                lock (_stateLock)
                {
                    _memberNames.Clear();
                    _memberChannels.Clear();
                    _channels.Clear();
                }
                DisposePlaybackStreams();
                _joinStartedUtc = now;
                SetLastMessage("Connection lost - reconnecting...");
            }

            if (!_connected)
            {
                if (!_reconnectAllowed)
                    return;

                if (_joinStartedUtc != DateTime.MinValue && (now - _joinStartedUtc).TotalSeconds >= 6.0)
                    SetLastMessage("No response from host - check that the host room is online and Windows Firewall allows m\u00E6la. Retrying...");

                if ((now - _lastJoinSentUtc).TotalMilliseconds >= 1000.0)
                    SendJoin();
            }
            else if ((now - _lastKeepAliveUtc).TotalMilliseconds >= 1500.0)
            {
                _lastKeepAliveUtc = now;
                SendPacket(_hostEndpoint, PacketKeepAlive, _localId, 0, new byte[0]);
            }
        }

        private void SendJoin()
        {
            if (!_running || _hostEndpoint == null)
                return;

            _lastJoinSentUtc = DateTime.UtcNow;
            byte[] payload = Encoding.UTF8.GetBytes(_localName);
            SendPacket(_hostEndpoint, PacketJoin, 0, 0, payload);
        }

        private void RemoveHostParticipant(string key, bool broadcastNow)
        {
            Participant removed = null;
            lock (_stateLock)
            {
                if (_hostParticipants.TryGetValue(key, out removed))
                {
                    _hostParticipants.Remove(key);
                    _memberNames.Remove(removed.Id);
                    _memberChannels.Remove(removed.Id);
                    _peerVolumes.Remove(removed.Id);
                    _peerMutes.Remove(removed.Id);
                    _lastSpeakingUtc.Remove(removed.Id);
                }
            }

            if (removed != null)
            {
                DisposePlaybackStream(removed.Id);
                SetLastMessage(removed.Name + " left.");
                if (broadcastNow)
                    BroadcastPeerList();
            }
        }

        private void BroadcastPeerList()
        {
            byte[] payload = BuildPeerListPayload();
            List<IPEndPoint> targets = new List<IPEndPoint>();

            lock (_stateLock)
            {
                foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                    targets.Add(new IPEndPoint(pair.Value.Endpoint.Address, pair.Value.Endpoint.Port));
            }

            int i;
            for (i = 0; i < targets.Count; i++)
                SendPacket(targets[i], PacketPeerList, _localId, 0, payload);
        }

        private byte[] BuildProbePayload()
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(_roomName == null ? "Voice Room" : _roomName);
            if (nameBytes.Length > 120)
            {
                byte[] trimmed = new byte[120];
                Buffer.BlockCopy(nameBytes, 0, trimmed, 0, trimmed.Length);
                nameBytes = trimmed;
            }
            byte[] payload = new byte[1 + nameBytes.Length + 2];
            payload[0] = (byte)nameBytes.Length;
            if (nameBytes.Length > 0)
                Buffer.BlockCopy(nameBytes, 0, payload, 1, nameBytes.Length);
            payload[1 + nameBytes.Length] = (byte)Math.Min(255, MemberCount);
            payload[2 + nameBytes.Length] = (byte)Math.Min(255, MaxRoomMembers);
            return payload;
        }

        private byte[] BuildPeerListPayload()
        {
            MemoryStream stream = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);

            lock (_stateLock)
            {
                writer.Write(_localId);

                List<int> channelIds = new List<int>(_channels.Keys);
                channelIds.Sort();
                writer.Write((byte)Math.Min(255, channelIds.Count));
                int c;
                for (c = 0; c < channelIds.Count; c++)
                {
                    int channelId = channelIds[c];
                    string channelName = _channels[channelId];
                    byte[] channelBytes = Encoding.UTF8.GetBytes(channelName == null ? String.Empty : channelName);
                    if (channelBytes.Length > 80)
                    {
                        byte[] trimmedChannel = new byte[80];
                        Buffer.BlockCopy(channelBytes, 0, trimmedChannel, 0, 80);
                        channelBytes = trimmedChannel;
                    }
                    writer.Write(channelId);
                    writer.Write((byte)channelBytes.Length);
                    writer.Write(channelBytes);
                }

                writer.Write((byte)Math.Min(255, _memberNames.Count));
                foreach (KeyValuePair<uint, string> pair in _memberNames)
                {
                    byte[] nameBytes = Encoding.UTF8.GetBytes(pair.Value == null ? String.Empty : pair.Value);
                    if (nameBytes.Length > 80)
                    {
                        byte[] trimmed = new byte[80];
                        Buffer.BlockCopy(nameBytes, 0, trimmed, 0, 80);
                        nameBytes = trimmed;
                    }

                    int channelId;
                    if (!_memberChannels.TryGetValue(pair.Key, out channelId))
                        channelId = 0;

                    writer.Write(pair.Key);
                    writer.Write(channelId);
                    writer.Write((byte)nameBytes.Length);
                    writer.Write(nameBytes);
                }
            }

            writer.Flush();
            return stream.ToArray();
        }

        private void ParsePeerList(byte[] payload)
        {
            if (payload == null || payload.Length < 6)
                return;

            Dictionary<int, string> parsedChannels = new Dictionary<int, string>();
            Dictionary<uint, string> parsedNames = new Dictionary<uint, string>();
            Dictionary<uint, int> parsedMemberChannels = new Dictionary<uint, int>();
            int offset = 0;

            if (offset + 4 > payload.Length)
                return;
            uint parsedHostId = BitConverter.ToUInt32(payload, offset);
            offset += 4;

            if (offset >= payload.Length)
                return;
            int channelCount = payload[offset++];
            int i;
            for (i = 0; i < channelCount; i++)
            {
                if (offset + 5 > payload.Length)
                    return;
                int channelId = BitConverter.ToInt32(payload, offset);
                offset += 4;
                int channelNameLength = payload[offset++];
                if (offset + channelNameLength > payload.Length)
                    return;
                string channelName = Encoding.UTF8.GetString(payload, offset, channelNameLength);
                offset += channelNameLength;
                if (channelId > 0)
                    parsedChannels[channelId] = channelName;
            }

            if (offset >= payload.Length)
                return;
            int memberCount = payload[offset++];
            for (i = 0; i < memberCount; i++)
            {
                if (offset + 9 > payload.Length)
                    return;
                uint id = BitConverter.ToUInt32(payload, offset);
                offset += 4;
                int channelId = BitConverter.ToInt32(payload, offset);
                offset += 4;
                int nameLength = payload[offset++];
                if (offset + nameLength > payload.Length)
                    return;
                string name = Encoding.UTF8.GetString(payload, offset, nameLength);
                offset += nameLength;
                parsedNames[id] = name;
                parsedMemberChannels[id] = channelId;
            }

            List<uint> removedStreams = new List<uint>();
            int oldLocalChannel;
            lock (_stateLock)
            {
                oldLocalChannel = _localChannelId;
                foreach (KeyValuePair<uint, string> oldPair in _memberNames)
                {
                    if (!parsedNames.ContainsKey(oldPair.Key) && oldPair.Key != _localId)
                        removedStreams.Add(oldPair.Key);
                }

                for (i = 0; i < removedStreams.Count; i++)
                {
                    _peerVolumes.Remove(removedStreams[i]);
                    _peerMutes.Remove(removedStreams[i]);
                    _lastSpeakingUtc.Remove(removedStreams[i]);
                }

                _channels.Clear();
                foreach (KeyValuePair<int, string> channelPair in parsedChannels)
                    _channels[channelPair.Key] = channelPair.Value;

                _memberNames.Clear();
                _memberChannels.Clear();
                _hostId = parsedHostId;
                foreach (KeyValuePair<uint, string> pair in parsedNames)
                    _memberNames[pair.Key] = pair.Value;
                foreach (KeyValuePair<uint, int> pair in parsedMemberChannels)
                    _memberChannels[pair.Key] = pair.Value;

                int localChannel;
                _localChannelId = _memberChannels.TryGetValue(_localId, out localChannel) ? localChannel : 0;
            }

            if (oldLocalChannel != _localChannelId)
                DisposePlaybackStreams();
            else
            {
                for (i = 0; i < removedStreams.Count; i++)
                    DisposePlaybackStream(removedStreams[i]);
            }
        }

        private void OpenWaveIn()
        {
            WAVEFORMATEX format = CreateFormat();
            _waveInProc = WaveInCallback;

            int result = NativeMethods.waveInOpen(out _waveIn, _inputDeviceId, ref format, _waveInProc, IntPtr.Zero, NativeMethods.CALLBACK_FUNCTION);
            CheckMmResult(result, "Could not open the default microphone at 48 kHz");

            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            int i;
            for (i = 0; i < CaptureBufferCount; i++)
            {
                NativeBuffer buffer = new NativeBuffer(AudioBytesPerFrame, headerSize);
                _captureBuffers.Add(buffer);

                result = NativeMethods.waveInPrepareHeader(_waveIn, buffer.Header, (uint)headerSize);
                CheckMmResult(result, "Could not prepare a microphone buffer");
                buffer.Prepared = true;

                result = NativeMethods.waveInAddBuffer(_waveIn, buffer.Header, (uint)headerSize);
                CheckMmResult(result, "Could not queue a microphone buffer");
            }
        }

        private void WaveInCallback(IntPtr hwi, uint message, IntPtr instance, IntPtr headerPtr, IntPtr reserved)
        {
            if (message != NativeMethods.WIM_DATA || headerPtr == IntPtr.Zero)
                return;

            WAVEHDR header = (WAVEHDR)Marshal.PtrToStructure(headerPtr, typeof(WAVEHDR));

            // The capture clock must advance for every frame, muted or not, so the echo
            // canceller's view of the speaker timeline stays aligned with the microphone.
            long captureEnd = header.dwBytesRecorded > 0 ? _farEnd.AdvanceCapture((int)header.dwBytesRecorded / 2) : _farEnd.CaptureClock;

            if (_running && !_muted && header.dwBytesRecorded > 0)
            {
                int audioLength = (int)header.dwBytesRecorded;
                if (audioLength == AudioBytesPerFrame)
                {
                    byte[] pcm = new byte[audioLength];
                    Marshal.Copy(header.lpData, pcm, 0, audioLength);
                    if (_echoCancellation)
                    {
                        try
                        {
                            if (_echoResetPending)
                            {
                                _echoResetPending = false;
                                _echoCanceller.Reset();
                            }
                            _echoCanceller.Process(pcm, captureEnd);
                        }
                        catch
                        {
                            // Never let audio processing take the microphone down.
                        }
                    }
                    ScalePcm16InPlace(pcm, _micVolumePercent / 100.0f);
                    if (HasVoiceActivity(pcm))
                    {
                        lock (_stateLock)
                            _lastSpeakingUtc[_localId] = DateTime.UtcNow;
                    }
                    SendLocalAudio(pcm);
                }
            }

            if (_running && _waveIn != IntPtr.Zero)
            {
                header.dwBytesRecorded = 0;
                Marshal.StructureToPtr(header, headerPtr, false);
                NativeMethods.waveInAddBuffer(_waveIn, headerPtr, (uint)Marshal.SizeOf(typeof(WAVEHDR)));
            }
        }

        private void SendLocalAudio(byte[] pcm)
        {
            uint senderId = _localId;
            int localChannel = _localChannelId;
            if (senderId == 0 || localChannel <= 0)
                return;

            uint sequence = _txSequence++;
            byte[] raw = BuildPacket(PacketAudio, senderId, sequence, pcm);

            if (_isHost)
            {
                List<IPEndPoint> targets = new List<IPEndPoint>();
                lock (_stateLock)
                {
                    foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                    {
                        if (pair.Value.ChannelId == localChannel)
                            targets.Add(new IPEndPoint(pair.Value.Endpoint.Address, pair.Value.Endpoint.Port));
                    }
                }

                int i;
                for (i = 0; i < targets.Count; i++)
                    SendRaw(targets[i], raw);
            }
            else if (_connected && _hostEndpoint != null)
            {
                SendRaw(_hostEndpoint, raw);
            }
        }

        private void QueueRemoteAudio(uint senderId, uint sequence, byte[] pcm)
        {
            PlaybackStream stream;
            lock (_stateLock)
            {
                if (HasVoiceActivity(pcm))
                    _lastSpeakingUtc[senderId] = DateTime.UtcNow;
                if (!_playbackStreams.TryGetValue(senderId, out stream))
                {
                    try
                    {
                        stream = new PlaybackStream(CreateFormat(), _outputDeviceId, _farEnd);
                        _playbackStreams[senderId] = stream;
                        stream.SetGain(GetEffectivePlaybackGainLocked(senderId));
                    }
                    catch
                    {
                        return;
                    }
                }
            }

            stream.Enqueue(sequence, pcm);
        }

        private float GetEffectivePlaybackGainLocked(uint senderId)
        {
            if (_peerMutes.Contains(senderId))
                return 0.0f;

            int peerVolume;
            if (!_peerVolumes.TryGetValue(senderId, out peerVolume))
                peerVolume = 100;

            return (_masterVolumePercent / 100.0f) * (peerVolume / 100.0f);
        }

        private void UpdatePlaybackGain(uint senderId)
        {
            PlaybackStream stream = null;
            float gain = 1.0f;
            lock (_stateLock)
            {
                gain = GetEffectivePlaybackGainLocked(senderId);
                _playbackStreams.TryGetValue(senderId, out stream);
            }
            if (stream != null)
                stream.SetGain(gain);
        }

        private void UpdateAllPlaybackGains()
        {
            List<KeyValuePair<PlaybackStream, float>> updates = new List<KeyValuePair<PlaybackStream, float>>();
            lock (_stateLock)
            {
                foreach (KeyValuePair<uint, PlaybackStream> pair in _playbackStreams)
                    updates.Add(new KeyValuePair<PlaybackStream, float>(pair.Value, GetEffectivePlaybackGainLocked(pair.Key)));
            }

            int i;
            for (i = 0; i < updates.Count; i++)
                updates[i].Key.SetGain(updates[i].Value);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private static bool HasVoiceActivity(byte[] pcm)
        {
            if (pcm == null)
                return false;

            int peak = 0;
            int i;
            for (i = 0; i + 1 < pcm.Length; i += 8)
            {
                short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
                int magnitude = sample < 0 ? -sample : sample;
                if (magnitude > peak)
                    peak = magnitude;
                if (peak >= 850)
                    return true;
            }
            return false;
        }

        private static void ScalePcm16InPlace(byte[] pcm, float gain)
        {
            if (pcm == null || pcm.Length < 2 || Math.Abs(gain - 1.0f) < 0.001f)
                return;

            int i;
            for (i = 0; i + 1 < pcm.Length; i += 2)
            {
                short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
                int scaled = (int)(sample * gain);
                if (scaled > Int16.MaxValue)
                    scaled = Int16.MaxValue;
                else if (scaled < Int16.MinValue)
                    scaled = Int16.MinValue;

                pcm[i] = (byte)(scaled & 0xFF);
                pcm[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        private void DisposePlaybackStream(uint senderId)
        {
            PlaybackStream stream = null;
            lock (_stateLock)
            {
                if (_playbackStreams.TryGetValue(senderId, out stream))
                    _playbackStreams.Remove(senderId);
            }

            if (stream != null)
                stream.Dispose();
        }

        private void DisposePlaybackStreams()
        {
            List<PlaybackStream> streams = new List<PlaybackStream>();
            lock (_stateLock)
            {
                foreach (KeyValuePair<uint, PlaybackStream> pair in _playbackStreams)
                    streams.Add(pair.Value);
                _playbackStreams.Clear();
            }

            int i;
            for (i = 0; i < streams.Count; i++)
                streams[i].Dispose();
        }

        private void SendPacket(IPEndPoint target, byte type, uint senderId, uint sequence, byte[] payload)
        {
            SendRaw(target, BuildPacket(type, senderId, sequence, payload));
        }

        private void SendRaw(IPEndPoint target, byte[] raw)
        {
            if (!_running || _socket == null || target == null || raw == null)
                return;

            try
            {
                lock (_sendLock)
                {
                    if (_socket != null)
                        _socket.SendTo(raw, target);
                }
                Interlocked.Increment(ref _txPackets);
            }
            catch
            {
            }
        }

        private static byte[] BuildPacket(byte type, uint senderId, uint sequence, byte[] payload)
        {
            int payloadLength = payload == null ? 0 : payload.Length;
            byte[] data = new byte[HeaderBytes + payloadLength];

            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, data, 0, 4);
            data[4] = ProtocolVersion;
            data[5] = type;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)payloadLength), 0, data, 6, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(senderId), 0, data, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, data, 12, 4);

            if (payloadLength > 0)
                Buffer.BlockCopy(payload, 0, data, HeaderBytes, payloadLength);

            return data;
        }

        private static bool TryParsePacket(byte[] data, int length, out Packet packet)
        {
            packet = null;
            if (data == null || length < HeaderBytes)
                return false;
            if (BitConverter.ToUInt32(data, 0) != Magic)
                return false;
            if (data[4] != ProtocolVersion)
                return false;

            int payloadLength = BitConverter.ToUInt16(data, 6);
            if (payloadLength < 0 || HeaderBytes + payloadLength != length)
                return false;

            Packet result = new Packet();
            result.Type = data[5];
            result.SenderId = BitConverter.ToUInt32(data, 8);
            result.Sequence = BitConverter.ToUInt32(data, 12);
            result.Payload = new byte[payloadLength];
            if (payloadLength > 0)
                Buffer.BlockCopy(data, HeaderBytes, result.Payload, 0, payloadLength);
            packet = result;
            return true;
        }

        private uint CreateUniqueParticipantId()
        {
            uint id;
            do
            {
                id = CreateParticipantId();
            }
            while (id == _localId || _memberNames.ContainsKey(id));
            return id;
        }

        private static uint CreateParticipantId()
        {
            byte[] bytes = Guid.NewGuid().ToByteArray();
            uint id = BitConverter.ToUInt32(bytes, 0);
            return id == 0 ? 1U : id;
        }

        private static string DecodeName(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return String.Empty;
            string value = Encoding.UTF8.GetString(payload).Trim();
            if (value.Length > 24)
                value = value.Substring(0, 24);
            return value;
        }

        private static string EndpointKey(IPEndPoint endpoint)
        {
            return endpoint.Address.ToString() + ":" + endpoint.Port;
        }

        private void SetLastMessage(string message)
        {
            lock (_stateLock)
                _lastMessage = message == null ? String.Empty : message;
        }

        private static WAVEFORMATEX CreateFormat()
        {
            WAVEFORMATEX format = new WAVEFORMATEX();
            format.wFormatTag = 1;
            format.nChannels = Channels;
            format.nSamplesPerSec = SampleRate;
            format.wBitsPerSample = BitsPerSample;
            format.nBlockAlign = (ushort)(Channels * BitsPerSample / 8);
            format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;
            format.cbSize = 0;
            return format;
        }

        private static void CheckMmResult(int result, string message)
        {
            if (result != NativeMethods.MMSYSERR_NOERROR)
                throw new InvalidOperationException(message + ". WinMM error code: " + result);
        }

        public void Dispose()
        {
            if (!_isHost && _running && _connected && _hostEndpoint != null && _localId != 0)
            {
                try
                {
                    byte[] leave = BuildPacket(PacketLeave, _localId, 0, new byte[0]);
                    lock (_sendLock)
                    {
                        if (_socket != null)
                            _socket.SendTo(leave, _hostEndpoint);
                    }
                }
                catch
                {
                }
            }

            _running = false;
            _connected = false;

            Socket socket = _socket;
            _socket = null;
            if (socket != null)
            {
                try { socket.Close(); } catch { }
            }

            Thread receiveThread = _receiveThread;
            _receiveThread = null;
            if (receiveThread != null && receiveThread.IsAlive && receiveThread != Thread.CurrentThread)
            {
                try { receiveThread.Join(800); } catch { }
            }

            Thread maintenanceThread = _maintenanceThread;
            _maintenanceThread = null;
            if (maintenanceThread != null && maintenanceThread.IsAlive && maintenanceThread != Thread.CurrentThread)
            {
                try { maintenanceThread.Join(800); } catch { }
            }

            IntPtr waveIn = _waveIn;
            _waveIn = IntPtr.Zero;
            if (waveIn != IntPtr.Zero)
            {
                try { NativeMethods.waveInStop(waveIn); } catch { }
                try { NativeMethods.waveInReset(waveIn); } catch { }

                int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                int i;
                for (i = 0; i < _captureBuffers.Count; i++)
                {
                    NativeBuffer buffer = _captureBuffers[i];
                    if (buffer.Prepared)
                    {
                        try { NativeMethods.waveInUnprepareHeader(waveIn, buffer.Header, (uint)headerSize); } catch { }
                        buffer.Prepared = false;
                    }
                    buffer.Free();
                }
                _captureBuffers.Clear();

                try { NativeMethods.waveInClose(waveIn); } catch { }
            }

            DisposePlaybackStreams();

            lock (_stateLock)
            {
                _hostParticipants.Clear();
                _memberNames.Clear();
                _memberChannels.Clear();
                _channels.Clear();
            }
        }

        private sealed class Packet
        {
            public byte Type;
            public uint SenderId;
            public uint Sequence;
            public byte[] Payload;
        }

        private sealed class Participant
        {
            public uint Id;
            public string Name;
            public IPEndPoint Endpoint;
            public DateTime LastSeenUtc;
            public int ChannelId;
        }
    }

    internal sealed class PlaybackStream : IDisposable
    {
        private const int PrimeFrameCount = 4;
        private const int MaxOutstandingFrames = 50;

        private readonly object _lock = new object();
        private readonly Dictionary<IntPtr, NativeBuffer> _buffers = new Dictionary<IntPtr, NativeBuffer>();
        private readonly List<byte[]> _primeFrames = new List<byte[]>();
        private IntPtr _waveOut = IntPtr.Zero;
        private WaveOutProc _waveOutProc;
        private bool _disposed;
        private bool _primed;
        private bool _haveSequence;
        private uint _lastSequence;
        private int _outstandingFrames;
        private int _frameBytes;
        private volatile float _gain = 1.0f;
        private readonly FarEndReference _reference;
        private long _renderPosition;

        public PlaybackStream(WAVEFORMATEX format, uint outputDeviceId, FarEndReference reference)
        {
            _reference = reference;
            _frameBytes = (int)(format.nAvgBytesPerSec / 100);
            _waveOutProc = WaveOutCallback;
            int result = NativeMethods.waveOutOpen(out _waveOut, outputDeviceId, ref format, _waveOutProc, IntPtr.Zero, NativeMethods.CALLBACK_FUNCTION);
            if (result != NativeMethods.MMSYSERR_NOERROR)
                throw new InvalidOperationException("Could not open the default speakers. WinMM error code: " + result);
        }

        public void SetGain(float gain)
        {
            if (gain < 0.0f)
                gain = 0.0f;
            if (gain > 3.0f)
                gain = 3.0f;
            _gain = gain;
        }

        public void Enqueue(uint sequence, byte[] pcm)
        {
            if (_disposed || pcm == null || pcm.Length == 0)
                return;

            if (_haveSequence)
            {
                if (sequence <= _lastSequence)
                    return;

                uint gap = sequence - _lastSequence - 1;
                if (gap > 0 && gap <= 5)
                {
                    uint i;
                    for (i = 0; i < gap; i++)
                        EnqueueFrame(new byte[_frameBytes]);
                }
            }

            _haveSequence = true;
            _lastSequence = sequence;
            EnqueueFrame(pcm);
        }

        private void EnqueueFrame(byte[] pcm)
        {
            if (!_primed)
            {
                _primeFrames.Add(pcm);
                if (_primeFrames.Count < PrimeFrameCount)
                    return;

                int i;
                for (i = 0; i < _primeFrames.Count; i++)
                    QueueBuffer(_primeFrames[i]);
                _primeFrames.Clear();
                _primed = true;
                return;
            }

            QueueBuffer(pcm);
        }

        private void QueueBuffer(byte[] pcm)
        {
            lock (_lock)
            {
                if (_disposed || _waveOut == IntPtr.Zero)
                    return;
                if (_outstandingFrames >= MaxOutstandingFrames)
                    return;
            }

            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            NativeBuffer buffer = new NativeBuffer(pcm.Length, headerSize);
            float gain = _gain;
            byte[] played = pcm;
            if (Math.Abs(gain - 1.0f) >= 0.001f)
            {
                played = new byte[pcm.Length];
                Buffer.BlockCopy(pcm, 0, played, 0, pcm.Length);
                ScalePcm16InPlace(played, gain);
            }
            Marshal.Copy(played, 0, buffer.Data, played.Length);
            buffer.WaveHandle = _waveOut;

            int result = NativeMethods.waveOutPrepareHeader(_waveOut, buffer.Header, (uint)headerSize);
            if (result != NativeMethods.MMSYSERR_NOERROR)
            {
                buffer.Free();
                return;
            }

            buffer.Prepared = true;
            lock (_lock)
            {
                if (_disposed || _waveOut == IntPtr.Zero)
                {
                    try { NativeMethods.waveOutUnprepareHeader(buffer.WaveHandle, buffer.Header, (uint)headerSize); } catch { }
                    buffer.Free();
                    return;
                }
                if (_reference != null)
                {
                    // WinMM plays queued buffers back to back. When the queue has drained the
                    // next buffer starts "now", so re-anchor to the capture clock.
                    long now = _reference.CaptureClock;
                    if (_outstandingFrames == 0 || _renderPosition < now)
                        _renderPosition = now;
                    _reference.Write(_renderPosition, played);
                    _renderPosition += played.Length / 2;
                }
                _buffers[buffer.Header] = buffer;
                _outstandingFrames++;
            }

            result = NativeMethods.waveOutWrite(_waveOut, buffer.Header, (uint)headerSize);
            if (result != NativeMethods.MMSYSERR_NOERROR)
                CleanupBuffer(buffer.Header);
        }

        private void WaveOutCallback(IntPtr hwo, uint message, IntPtr instance, IntPtr headerPtr, IntPtr reserved)
        {
            if (message != NativeMethods.WOM_DONE || headerPtr == IntPtr.Zero)
                return;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                CleanupBuffer((IntPtr)state);
            }, headerPtr);
        }

        private void CleanupBuffer(IntPtr headerPtr)
        {
            NativeBuffer buffer = null;
            lock (_lock)
            {
                if (_buffers.TryGetValue(headerPtr, out buffer))
                {
                    _buffers.Remove(headerPtr);
                    if (_outstandingFrames > 0)
                        _outstandingFrames--;
                }
            }

            if (buffer == null)
                return;

            if (buffer.Prepared && buffer.WaveHandle != IntPtr.Zero)
            {
                try { NativeMethods.waveOutUnprepareHeader(buffer.WaveHandle, buffer.Header, (uint)Marshal.SizeOf(typeof(WAVEHDR))); } catch { }
                buffer.Prepared = false;
            }
            buffer.Free();
        }

        private static void ScalePcm16InPlace(byte[] pcm, float gain)
        {
            int i;
            for (i = 0; i + 1 < pcm.Length; i += 2)
            {
                short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
                int scaled = (int)(sample * gain);
                if (scaled > Int16.MaxValue)
                    scaled = Int16.MaxValue;
                else if (scaled < Int16.MinValue)
                    scaled = Int16.MinValue;
                pcm[i] = (byte)(scaled & 0xFF);
                pcm[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        public void Dispose()
        {
            IntPtr waveOut;
            List<NativeBuffer> buffers;

            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                waveOut = _waveOut;
                _waveOut = IntPtr.Zero;
                buffers = new List<NativeBuffer>(_buffers.Values);
                _buffers.Clear();
                _outstandingFrames = 0;
                _primeFrames.Clear();
            }

            if (waveOut != IntPtr.Zero)
            {
                try { NativeMethods.waveOutReset(waveOut); } catch { }

                int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                int i;
                for (i = 0; i < buffers.Count; i++)
                {
                    NativeBuffer buffer = buffers[i];
                    if (buffer.Prepared)
                    {
                        try { NativeMethods.waveOutUnprepareHeader(waveOut, buffer.Header, (uint)headerSize); } catch { }
                        buffer.Prepared = false;
                    }
                    buffer.Free();
                }

                try { NativeMethods.waveOutClose(waveOut); } catch { }
            }
        }
    }

    internal sealed class NativeBuffer
    {
        public IntPtr Data;
        public IntPtr Header;
        public IntPtr WaveHandle;
        public bool Prepared;

        public NativeBuffer(int dataLength, int headerSize)
        {
            Data = Marshal.AllocHGlobal(dataLength);
            Header = Marshal.AllocHGlobal(headerSize);
            WaveHandle = IntPtr.Zero;
            Prepared = false;

            byte[] zeros = new byte[dataLength];
            Marshal.Copy(zeros, 0, Data, dataLength);

            WAVEHDR header = new WAVEHDR();
            header.lpData = Data;
            header.dwBufferLength = (uint)dataLength;
            header.dwBytesRecorded = 0;
            header.dwUser = IntPtr.Zero;
            header.dwFlags = 0;
            header.dwLoops = 0;
            header.lpNext = IntPtr.Zero;
            header.reserved = IntPtr.Zero;
            Marshal.StructureToPtr(header, Header, false);
        }

        public void Free()
        {
            if (Header != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Header);
                Header = IntPtr.Zero;
            }
            if (Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct WAVEINCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    internal delegate void WaveInProc(IntPtr hwi, uint message, IntPtr instance, IntPtr param1, IntPtr param2);
    internal delegate void WaveOutProc(IntPtr hwo, uint message, IntPtr instance, IntPtr param1, IntPtr param2);

    internal static class NativeMethods
    {
        public const uint WAVE_MAPPER = 0xFFFFFFFF;
        public const uint CALLBACK_FUNCTION = 0x00030000;
        public const uint WIM_DATA = 0x03C0;
        public const uint WOM_DONE = 0x03BD;
        public const int MMSYSERR_NOERROR = 0;

        [DllImport("winmm.dll")]
        public static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        public static extern int waveInGetDevCaps(uint deviceId, out WAVEINCAPS caps, uint size);

        [DllImport("winmm.dll")]
        public static extern uint waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        public static extern int waveOutGetDevCaps(uint deviceId, out WAVEOUTCAPS caps, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveInOpen(out IntPtr waveIn, uint deviceId, ref WAVEFORMATEX format, WaveInProc callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        public static extern int waveInPrepareHeader(IntPtr waveIn, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveInUnprepareHeader(IntPtr waveIn, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveInAddBuffer(IntPtr waveIn, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveInStart(IntPtr waveIn);

        [DllImport("winmm.dll")]
        public static extern int waveInStop(IntPtr waveIn);

        [DllImport("winmm.dll")]
        public static extern int waveInReset(IntPtr waveIn);

        [DllImport("winmm.dll")]
        public static extern int waveInClose(IntPtr waveIn);

        [DllImport("winmm.dll")]
        public static extern int waveOutOpen(out IntPtr waveOut, uint deviceId, ref WAVEFORMATEX format, WaveOutProc callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(IntPtr waveOut, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(IntPtr waveOut, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(IntPtr waveOut, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        public static extern int waveOutReset(IntPtr waveOut);

        [DllImport("winmm.dll")]
        public static extern int waveOutClose(IntPtr waveOut);
    }
}
