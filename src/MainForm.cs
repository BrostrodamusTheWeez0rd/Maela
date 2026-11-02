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


namespace Maela
{
    internal sealed partial class MainForm : Form
    {
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
        private DateTime _copiedUntilUtc = DateTime.MinValue;

        public MainForm() : this(false)
        {
        }

        public MainForm(bool justUpdated)
        {
            _justUpdated = justUpdated;
            Text = "m\u00E6la";
            try { Icon = Branding.LoadAppIcon(); } catch { Icon = SystemIcons.Application; }
            ClientSize = new Size(1200, 760);
            MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;

            _preferences = PreferencesStore.Load();
            Theme.Apply(Theme.ById(_preferences.Theme));
            BackColor = MainBg;
            ForeColor = TextMain;
            _savedRooms.AddRange(SavedRoomStore.Load());

            BuildInterface();
            InitFriends();
            RebuildRoomList();
            UpdateSelectedRoomUi();
            UpdateMuteButton();

            _uiTimer.Interval = 120;
            _uiTimer.Tick += UiTimerTick;
            _uiTimer.Tick += delegate { SingleInstance.AnswerPing(); };
            _uiTimer.Start();
            FormClosing += MainFormClosing;
            Program.LeaveRoomOnCrash = delegate
            {
                VoipEngine engine = _engine;
                if (engine != null)
                    engine.Dispose();
            };

            _notifyIcon.Text = "m\u00E6la";
            try { _notifyIcon.Icon = Branding.LoadAppIcon(); } catch { _notifyIcon.Icon = SystemIcons.Application; }
            _notifyIcon.Visible = true;
            _notifyIcon.MouseClick += delegate(object s, MouseEventArgs me) { if (me.Button == MouseButtons.Left) RestoreFromTray(); };
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Opening += delegate { BuildTrayMenu(trayMenu); };
            BuildTrayMenu(trayMenu);
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
                if (!_preferences.SetupDone)
                {
                    using (SetupDialog setup = new SetupDialog(_preferences))
                        setup.ShowDialog(this);
                    Theme.Apply(Theme.ById(_preferences.Theme));
                    _chatViewKey = String.Empty;
                    RefreshShell(_engine);
                }
                if (SavedRoomStore.LoadFailed)
                    MessageBox.Show(this, "Your saved rooms couldn't be opened. This usually happens after reinstalling Windows or on a different Windows account.\r\n\r\nThe old file has been kept, and mæla is starting a new list. To get back into a room, ask its host for the room code.", "Saved rooms", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ResumeFriends();
                VerifyDownloadsInBackground();
                // Once per version (and after every update): make Windows reload mæla's icon,
                // so desktops and shortcuts don't keep showing an older one.
                if (_justUpdated || _preferences.IconRefreshedFor != Updater.CurrentVersionText)
                {
                    _preferences.IconRefreshedFor = Updater.CurrentVersionText;
                    PreferencesStore.Save(_preferences);
                    ThreadPool.QueueUserWorkItem(delegate { Updater.RefreshShellIcon(); });
                }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    // Render every sound once in the background (~50 ms) so the first chime is instant.
                    foreach (SoundCue cue in Enum.GetValues(typeof(SoundCue)))
                        SoundFx.Render(cue);
                });
                _updateTimer.Start();
                if (_justUpdated)
                {
                    _notifyIcon.Visible = true;
                    _notifyIcon.BalloonTipTitle = "m\u00E6la updated";
                    _notifyIcon.BalloonTipText = "You are now running m\u00E6la " + Updater.CurrentVersionText + ".";
                    try { _notifyIcon.ShowBalloonTip(3000); } catch { }
                }
            };
        }

        /// <summary>
        /// Checks the downloaded emoji pack on every launch. If it is missing, damaged or out of
        /// date (a new app version pins a new pack), it is fetched again from the official source,
        /// verified, and swapped in. Nothing unverified is ever used.
        /// </summary>
        private void VerifyDownloadsInBackground()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (EmojiPack.Verify() || !_preferences.SetupDone || !_preferences.AutoUpdate)
                {
                    RefreshChatSoon();
                    return;
                }
                try { EmojiPack.Install(null, null); }
                catch { /* Offline or the source is down: emoji show as text; Settings > Repair retries. */ }
                RefreshChatSoon();
            });
        }

        private void RefreshChatSoon()
        {
            try { BeginInvoke((MethodInvoker)delegate { _chatViewKey = String.Empty; _rail.Invalidate(); }); } catch { }
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

            object item = _channels.Items[index];
            _contextChannel = item as VoiceChannelInfo;
            _contextMember = item as RoomMemberInfo;
            if (e.Button == MouseButtons.Left && _contextMember != null)
            {
                ShowProfileCard(_contextMember, _channels.PointToScreen(e.Location));
                return;
            }
            if (e.Button == MouseButtons.Left && _contextChannel != null && _engine != null && _engine.IsConnected)
            {
                // One click opens the channel's chat; double-click joins its voice.
                _engine.ViewChannel(_contextChannel.Id);
                _channels.Invalidate();
                RefreshShell(_engine);
                return;
            }
            if (e.Button != MouseButtons.Right)
                return;
            if (_contextMember != null)
                _memberMenu.Show(_channels, e.Location);
            else if (_contextChannel != null && _contextChannel.Id > 0)
                _channelMenu.Show(_channels, e.Location);
        }

        private void ChannelsDoubleClick(object sender, MouseEventArgs e)
        {
            int index = _channels.IndexFromPoint(e.Location);
            if (index < 0 || index >= _channels.Items.Count || _engine == null || !_engine.IsConnected)
                return;

            object item = _channels.Items[index];
            VoiceChannelInfo channel = item as VoiceChannelInfo;
            RoomMemberInfo member = item as RoomMemberInfo;
            if (member != null && !member.IsLocal && _engine.GetLiveStream(member.Id) != null && member.ChannelId == _engine.LocalChannelId)
            {
                WatchStream(member); // Double-click someone who's live in your channel to watch.
                return;
            }
            int target = channel != null ? channel.Id : (member != null ? member.ChannelId : -1);
            if (target > 0 && target != _engine.LocalChannelId && !_engine.IsTextChannel(target))
                _engine.ChangeChannel(target); // Double-click a voice channel or a person to join them.
        }

        private void ChannelsMeasureItem(object sender, MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _channels.Items.Count)
                return;
            e.ItemHeight = _channels.Items[e.Index] is VoiceChannelInfo ? 38 : 34;
        }

        private void AddChannelClicked(object sender, EventArgs e)
        {
            if (_engine == null || !_engine.IsHost)
                return;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ThemedMenuRenderer();
            menu.ShowImageMargin = false;
            menu.Items.Add("Voice channel", null, delegate { CreateChannelPrompt(false); });
            menu.Items.Add("Chat-only channel", null, delegate { CreateChannelPrompt(true); });
            menu.Closed += delegate { BeginInvoke((MethodInvoker)menu.Dispose); };
            menu.Show(_addChannelButton, new Point(0, _addChannelButton.Height));
        }

        private void CreateChannelPrompt(bool textOnly)
        {
            if (_engine == null || !_engine.IsHost)
                return;
            string name = TextPrompt.Show(this, textOnly ? "Create chat-only channel" : "Create voice channel", "Channel name", textOnly ? "chat" : "New Channel");
            if (String.IsNullOrWhiteSpace(name))
                return;

            int id = _engine.CreateChannel(name, textOnly);
            if (id > 0)
                _engine.ViewChannel(id);
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

            if (!channel.IsText)
            {
                ToolStripMenuItem join = new ToolStripMenuItem(engine.LocalChannelId == channel.Id ? "In voice here" : "Join voice");
                join.Enabled = engine.LocalChannelId != channel.Id;
                join.Click += delegate { if (_engine != null) _engine.ChangeChannel(channel.Id); };
                _channelMenu.Items.Add(join);
            }

            if (engine.IsHost)
            {
                _channelMenu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem rename = new ToolStripMenuItem("Rename channel");
                rename.Click += delegate
                {
                    if (_engine == null || !_engine.IsHost)
                        return;
                    string newName = TextPrompt.Show(this, "Rename channel", "Channel name", channel.Name);
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
                    if (MessageBox.Show(this, "Delete #" + channel.Name + "?" + (channel.IsText ? " Its chat history will be lost." : " People in it will be moved to the first channel."), "Delete channel", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
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
            if (e.Index < 0 || e.Index >= _channels.Items.Count)
                return;
            object item = _channels.Items[e.Index];
            Graphics g = e.Graphics;

            VoiceChannelInfo channel = item as VoiceChannelInfo;
            if (channel != null)
            {
                DrawChannelRow(g, e.Bounds, channel);
                return;
            }

            RoomMemberInfo member = item as RoomMemberInfo;
            using (SolidBrush b = new SolidBrush(Sidebar))
                g.FillRectangle(b, e.Bounds);
            if (member != null)
                DrawMemberRow(g, e.Bounds, member);
        }

        private void DrawChannelRow(Graphics g, Rectangle bounds, VoiceChannelInfo channel)
        {
            Theme t = Theme.Current;
            // Open (highlighted) channel: filled; channel you're in voice in: accent icon.
            bool inVoice = _engine != null && _engine.LocalChannelId == channel.Id;
            bool open = _engine != null && _engine.ChatChannelId == channel.Id;
            using (SolidBrush b = new SolidBrush(t.Side))
                g.FillRectangle(b, bounds);

            if (open)
                Ui.FillRound(g, t.AccentSoftSide, new RectangleF(bounds.Left, bounds.Top + 2, bounds.Width, bounds.Height - 4), 9);

            Color text = open || inVoice ? t.Text : t.Text2;
            Icons.Draw(g, channel.IsText ? IconKind.Hash : IconKind.Speaker, new RectangleF(bounds.Left + 10, bounds.Top + (bounds.Height - 18) / 2f, 18, 18), inVoice ? t.Accent2 : t.Muted);
            Ui.DrawText(g, channel.Name, Ui.BodyBold, new Rectangle(bounds.Left + 36, bounds.Top, bounds.Width - 70, bounds.Height), text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            if (channel.MemberCount > 0 && !channel.IsText)
                Ui.DrawText(g, channel.MemberCount.ToString(), Ui.Small, new Rectangle(bounds.Right - 34, bounds.Top, 24, bounds.Height), t.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        private void DrawMemberRow(Graphics g, Rectangle bounds, RoomMemberInfo member)
        {
            Theme t = Theme.Current;
            int left = bounds.Left + 36;
            Rectangle avatar = new Rectangle(left, bounds.Top + (bounds.Height - 24) / 2, 24, 24);
            Ui.DrawAvatar(g, avatar, member.Name, member.IsSpeaking, t.Side, member.IsLocal ? MyPicture() : AvatarImage(member.Id));

            int right = bounds.Right - 8;
            if (member.Deafened)
            {
                Icons.Draw(g, IconKind.HeadphonesOff, new RectangleF(right - 16, bounds.Top + (bounds.Height - 16) / 2f, 16, 16), t.Danger);
                right -= 22;
            }
            else if (member.Muted)
            {
                Icons.Draw(g, IconKind.MicOff, new RectangleF(right - 16, bounds.Top + (bounds.Height - 16) / 2f, 16, 16), t.Danger);
                right -= 22;
            }
            if (!member.IsLocal && _engine != null && _engine.GetPeerMuted(member.Id))
            {
                Ui.DrawText(g, "muted by you", Ui.Tiny, new Rectangle(right - 80, bounds.Top, 80, bounds.Height), t.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                right -= 86;
            }
            else if (member.IsHost)
            {
                Icons.Draw(g, IconKind.Crown, new RectangleF(right - 15, bounds.Top + (bounds.Height - 15) / 2f, 15, 15), Color.FromArgb(240, 178, 50));
                right -= 21;
            }
            if (_engine != null && _engine.GetLiveStream(member.Id) != null)
            {
                // LIVE badge: double-click (or open their card) to watch.
                RectangleF pill = new RectangleF(right - 34, bounds.Top + (bounds.Height - 16) / 2f, 32, 16);
                Ui.FillRound(g, t.Danger, pill, 5);
                Ui.DrawText(g, "LIVE", Ui.Tiny, Rectangle.Round(pill), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                right -= 40;
            }

            Color nameColor = member.IsSpeaking || member.IsLocal ? t.Text : t.Text2;
            Font font = member.IsLocal ? Ui.BodyBold : Ui.Body;
            Ui.DrawText(g, member.Name, font, new Rectangle(avatar.Right + 10, bounds.Top, Math.Max(10, right - avatar.Right - 14), bounds.Height), nameColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        private void SaveHostedChannelLayout()
        {
            if (_engine == null || !_engine.IsHost || _activeSavedRoom == null)
                return;

            VoiceChannelInfo[] channels = _engine.GetChannels();
            _activeSavedRoom.ChannelNames.Clear();
            int i;
            for (i = 0; i < channels.Length; i++)
                _activeSavedRoom.ChannelNames.Add((channels[i].IsText ? VoipEngine.TextChannelMarker : String.Empty) + channels[i].Name);
            SavedRoomStore.Save(_savedRooms);
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

            if (room.Owned)
            {
                _roomMenu.Items.Add(new ToolStripMenuItem("Rename room...", null, delegate { RenameRoom(room); }));
                _roomMenu.Items.Add(new ToolStripMenuItem("Set room picture...", null, delegate { ChooseRoomArt(room, RoomArt.Icon); }));
                if (room.IconBlob != null)
                    _roomMenu.Items.Add(new ToolStripMenuItem("Remove room picture", null, delegate { SetOwnRoomArt(room, null, room.BannerBlob); }));
                _roomMenu.Items.Add(new ToolStripMenuItem("Set room banner...", null, delegate { ChooseRoomArt(room, RoomArt.Banner); }));
                if (room.BannerBlob != null)
                    _roomMenu.Items.Add(new ToolStripMenuItem("Remove room banner", null, delegate { SetOwnRoomArt(room, room.IconBlob, null); }));
            }

            ToolStripMenuItem password = new ToolStripMenuItem(room.Owned ? "Set room password..." : "Room password...");
            password.Click += delegate
            {
                string value = TextPrompt.Show(this, room.Owned ? "Room password" : "Password for " + room.Name,
                    room.Owned ? "Password people need to join (leave empty for none). Applies next time you host." : "Password the host gave you", room.Password, true);
                if (value == null)
                    return;
                room.Password = value;
                SavedRoomStore.Save(_savedRooms);
            };
            _roomMenu.Items.Add(password);

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

        private void SettingsClicked(object sender, EventArgs e)
        {
            SettingsForm form = new SettingsForm(_preferences, _engine, _publicIp, RoomCode.IsValid(_activeInvite) ? _activeInvite : String.Empty, _natStatus);
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                _preferences = form.Preferences;
                PreferencesStore.Save(_preferences);
                ApplyPreferencesToEngine();
                Theme.Apply(Theme.ById(_preferences.Theme));
                RefreshShell(_engine);
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
                    room.Password = dialog.Password;
                    room.HostKey = X25519.GeneratePrivateKey();
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
                    room.Password = dialog.Password;
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
                SavedRoom room = new SavedRoom(); // Named by the host once connected.
                room.Invite = dialog.Invite;
                room.Password = dialog.Password;
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
                        // Same room (same host key), possibly with a new code: keep one entry, up to date.
                        duplicate.Invite = room.Invite;
                        if (!String.IsNullOrEmpty(room.Password))
                            duplicate.Password = room.Password;
                        room = duplicate;
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
            ShowRoomsView();
            StopEngine();
            try
            {
                string displayName = ValidateName(_preferences.DisplayName);
                string roomName = String.IsNullOrWhiteSpace(room.Name) ? "Voice Room" : room.Name.Trim();
                _engine = new VoipEngine(_preferences.InputDeviceId, _preferences.OutputDeviceId);
                ApplyPreferencesToEngine();
                _keepRoomAlive = room.KeepAlive;
                if (room.HostKey == null)
                {
                    // The key is what keeps this room's code stable; create it once and keep it.
                    room.HostKey = X25519.GeneratePrivateKey();
                    if (saved)
                        SavedRoomStore.Save(_savedRooms);
                }
                _engine.StartHost(requestedPort, displayName, roomName, room.ChannelNames, room.HostKey, room.Password);
                _engine.SetRoomArt(room.IconBlob, room.BannerBlob);
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
            ShowRoomsView();
            StopEngine();
            try
            {
                IPAddress hostIp;
                int port;
                byte[] fingerprint;
                if (!RoomCode.TryDecode(room.Invite, out hostIp, out port, out fingerprint))
                    throw new InvalidOperationException(RoomCode.IsOldFormat(room.Invite)
                        ? "This is an old room code from before encryption. Ask the host for their new code."
                        : "That saved room code is not valid.");

                _engine = new VoipEngine(_preferences.InputDeviceId, _preferences.OutputDeviceId);
                ApplyPreferencesToEngine();
                _engine.SetRoomArt(room.IconBlob, room.BannerBlob); // A saved copy: only fetched again if it changed.
                _engine.StartClient(new IPEndPoint(hostIp, port), ValidateName(_preferences.DisplayName), fingerprint, room.Password);
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

        /// <summary>The member with this id in the current room, or null.</summary>
        private RoomMemberInfo FindMember(uint id)
        {
            if (_engine == null)
                return null;
            foreach (RoomMemberInfo m in _engine.GetAllMembers())
                if (m.Id == id) return m;
            return null;
        }

        /// <summary>Your own card: works even when you're not in a room.</summary>
        private void ShowMyProfileCard()
        {
            RoomMemberInfo me = _engine != null && _engine.IsConnected ? FindMember(_engine.LocalMemberId) : null;
            if (me == null)
            {
                me = new RoomMemberInfo();
                me.Name = _preferences.DisplayName;
                me.IsLocal = true;
            }
            ShowProfileCard(me, Cursor.Position);
        }

        private void ApplyPreferencesToEngine()
        {
            if (_engine == null)
                return;
            _engine.Muted = _muted;
            _engine.Deafened = _deafened;
            _engine.MicVolumePercent = _preferences.MicVolumePercent;
            _engine.MasterVolumePercent = _preferences.MasterVolumePercent;
            _engine.EchoCancellation = _preferences.EchoCancellation;
            _engine.NoiseSuppression = _preferences.NoiseSuppression;
            _engine.AutoGain = _preferences.AutoGain;
            _engine.AllowDirectStreams = _preferences.AllowDirectStreams;
        }

        private void LeaveClicked(object sender, EventArgs e)
        {
            if (_engine == null || !_engine.IsRunning)
                return;

            // Leave voice only: you stay in the room and can keep reading and typing in the
            // highlighted channel. Use Disconnect to leave the room.
            _engine.LeaveVoice();
            _lastMemberSnapshot = String.Empty;
            _lastSpeakingSnapshot = String.Empty;
            UpdateSelectedRoomUi();
        }

        private string _lastDisconnectTip = String.Empty;

        /// <summary>
        /// Leaves the room completely. A host stops the room, after asking if anyone else is in it.
        /// </summary>
        private void DisconnectClicked(object sender, EventArgs e)
        {
            VoipEngine engine = _engine;
            if (engine == null || !engine.IsRunning)
                return;
            if (engine.IsHost && engine.MemberCount > 1 &&
                MessageBox.Show(this, "Stop the room for everyone? " + (engine.MemberCount - 1) + " other " + (engine.MemberCount == 2 ? "person is" : "people are") + " in it.",
                    "Stop room", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            StopEngine();
        }

        private void StopRoomClicked(object sender, EventArgs e)
        {
            StopEngine();
        }

        private void StopEngine()
        {
            Interlocked.Increment(ref _networkSetupGeneration);
            StopAllStreams();
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
            _channels.Items.Clear();
            // After leaving a joined room, probe it once more so its status and
            // member count reflect the room without us.
            if (_activeSavedRoom != null && !_activeSavedRoom.Owned)
                _activeSavedRoom.PresenceChecked = false;
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
                    string failure;
                    mapping = UpnpPortMapping.TryCreate(localPort, localIp, out failure);
                    if (mapping != null)
                    {
                        publicIp = mapping.ExternalIp;
                        if (mapping.ExternalPort == localPort)
                            status = "Port opened on your router automatically";
                        else
                            status = "Port opened on your router automatically (router chose port " + mapping.ExternalPort + ")";
                        if (UpnpPortMapping.IsNonPublic(publicIp))
                        {
                            // A private address in the room code could never work from outside.
                            publicIp = String.Empty;
                            status = "Port opened on your router, but another router or your internet provider sits in front of it, so friends may not reach you. Port forwarding on that device or a VPN like Tailscale can help";
                        }
                    }
                    else
                        status = "Your router didn't open the port (" + failure + "). Try Settings > Allow through Firewall, or turn on UPnP/NAT-PMP on your router";
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
                            _activeInvite = RoomCode.Encode(codeIp, publicPort, _engine.HostFingerprint);
                        else
                        {
                            IPAddress localCodeIp;
                            if (IPAddress.TryParse(GetPrimaryLocalIPv4Address(), out localCodeIp) && localCodeIp.AddressFamily == AddressFamily.InterNetwork)
                            {
                                _activeInvite = RoomCode.Encode(localCodeIp, localPort, _engine.HostFingerprint);
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
                if (engine.IsHost && _activeSavedRoom != null)
                {
                    _activeSavedRoom.Presence = RoomPresence.Online;
                    _activeSavedRoom.MemberCount = engine.MemberCount;
                    _activeSavedRoom.MaxMembers = VoipEngine.MaxRoomMembers;
                }
                UpdateChannelRows(engine);
            }
            else if (_channels.Items.Count > 0)
            {
                _channels.Items.Clear();
            }

            _header.CopyButton.Text = DateTime.UtcNow < _copiedUntilUtc ? "Copied!" : "Copy invite";
            _header.CopyButton.Icon = DateTime.UtcNow < _copiedUntilUtc ? IconKind.Check : IconKind.Copy;
            PlayTransitionSounds(engine);
            RefreshShell(engine);
            RefreshChat(engine);
            RefreshProfiles(engine);
            if (engine != null && engine.IsRunning)
                AttachStreams(engine);
            RefreshSharing(engine);
            RefreshViewers(engine);
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
                _rail.Invalidate();
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

        /// <summary>
        /// Builds the sidebar tree: each channel, with the people in its voice underneath.
        /// People connected but not in voice aren't shown.
        /// </summary>
        private void UpdateChannelRows(VoipEngine engine)
        {
            VoiceChannelInfo[] channels = engine.GetChannels();
            RoomMemberInfo[] members = engine.GetAllMembers();
            TrackChannelMembership(engine, members);

            StringBuilder channelSnapshot = new StringBuilder();
            StringBuilder identity = new StringBuilder();
            StringBuilder speaking = new StringBuilder();
            int i;
            for (i = 0; i < channels.Length; i++)
                channelSnapshot.Append(channels[i].Id).Append('|').Append(channels[i].Name).Append(';');
            identity.Append(channelSnapshot).Append("L=").Append(engine.LocalChannelId).Append(";V=").Append(engine.ChatChannelId).Append(';');
            for (i = 0; i < channels.Length; i++)
                identity.Append(channels[i].IsText ? 't' : 'v');
            for (i = 0; i < members.Length; i++)
            {
                identity.Append(members[i].Id).Append('|').Append(members[i].Name).Append('|')
                    .Append(members[i].ChannelId).Append('|')
                    .Append(members[i].IsLocal ? '1' : '0').Append(members[i].IsHost ? '1' : '0')
                    .Append(members[i].Muted ? 'm' : '-').Append(members[i].Deafened ? 'd' : '-').Append(';');
                speaking.Append(members[i].Id).Append('|').Append(members[i].IsSpeaking ? '1' : '0').Append(';');
            }

            string channelKey = channelSnapshot.ToString();
            string identityKey = identity.ToString();
            string speakingKey = speaking.ToString();

            if (!String.Equals(identityKey, _lastMemberSnapshot, StringComparison.Ordinal))
            {
                _lastMemberSnapshot = identityKey;
                _lastSpeakingSnapshot = speakingKey;
                int topIndex = _channels.TopIndex;
                _channels.BeginUpdate();
                _channels.Items.Clear();
                for (i = 0; i < channels.Length; i++)
                {
                    _channels.Items.Add(channels[i]);
                    AddMembersInChannel(members, channels[i].Id);
                }
                // People who left voice aren't listed: to everyone else they've left.
                _channels.EndUpdate();
                if (topIndex > 0 && topIndex < _channels.Items.Count)
                    _channels.TopIndex = topIndex;
            }
            else if (!String.Equals(speakingKey, _lastSpeakingSnapshot, StringComparison.Ordinal))
            {
                _lastSpeakingSnapshot = speakingKey;
                Dictionary<uint, bool> speakingById = new Dictionary<uint, bool>();
                for (i = 0; i < members.Length; i++)
                    speakingById[members[i].Id] = members[i].IsSpeaking;
                for (i = 0; i < _channels.Items.Count; i++)
                {
                    RoomMemberInfo row = _channels.Items[i] as RoomMemberInfo;
                    bool now;
                    if (row != null && speakingById.TryGetValue(row.Id, out now) && row.IsSpeaking != now)
                    {
                        row.IsSpeaking = now;
                        _channels.Invalidate(_channels.GetItemRectangle(i));
                    }
                }
            }

            if (!String.Equals(channelKey, _lastChannelSnapshot, StringComparison.Ordinal))
            {
                _lastChannelSnapshot = channelKey;
                if (engine.IsHost)
                    SaveHostedChannelLayout();
            }
        }

        private void AddMembersInChannel(RoomMemberInfo[] members, int channelId)
        {
            int i;
            for (i = 0; i < members.Length; i++)
            {
                if (members[i].ChannelId == channelId)
                    _channels.Items.Add(members[i]);
            }
        }

        /// <summary>
        /// Probes each joined (not hosted) saved room once per session to show whether it is
        /// online, plus once more after leaving it. There is no periodic re-probing; use
        /// "Check if online" to refresh one.
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
            _rail.Invalidate();
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
                            // Probes only reveal "online"; member counts arrive once you join.
                            room.MemberCount = 0;
                            room.MaxMembers = 0;
                        }
                        _rail.Invalidate();
                        UpdateSelectedRoomUi();
                    });
                }
                catch
                {
                    _presenceScanRunning = false;
                }
            });
        }

        /// <summary>
        /// Finds a saved room by its code, or by the host's key fingerprint: if a host's address
        /// or port changed, their new code still names the same room, so the saved entry is
        /// updated instead of leaving a dead copy behind.
        /// </summary>
        private SavedRoom FindSavedByInvite(string invite)
        {
            int i;
            for (i = 0; i < _savedRooms.Count; i++)
            {
                if (String.Equals(_savedRooms[i].Invite, invite, StringComparison.OrdinalIgnoreCase))
                    return _savedRooms[i];
            }

            IPAddress ip;
            int port;
            byte[] fingerprint;
            if (!RoomCode.TryDecode(invite, out ip, out port, out fingerprint))
                return null;
            for (i = 0; i < _savedRooms.Count; i++)
            {
                SavedRoom room = _savedRooms[i];
                IPAddress oldIp;
                int oldPort;
                byte[] oldFingerprint;
                if (room.Owned || !RoomCode.TryDecode(room.Invite, out oldIp, out oldPort, out oldFingerprint))
                    continue;
                if (SameBytes(fingerprint, oldFingerprint))
                    return room;
            }
            return null;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            int i;
            for (i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
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
                _copiedUntilUtc = DateTime.UtcNow.AddSeconds(2);
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
                    _channels.Invalidate();
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
                    if (targetChannel.IsText)
                        continue;
                    ToolStripMenuItem moveChoice = new ToolStripMenuItem(targetChannel.Name);
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

                ToolStripMenuItem lobbyChoice = new ToolStripMenuItem("Remove from voice");
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
            _channels.Invalidate();
        }

        private void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            bool keepHostedRoom = _engine != null && _engine.IsRunning && _engine.IsHost && _keepRoomAlive;
            if (!_allowTrueClose && e.CloseReason == CloseReason.UserClosing && (_preferences.CloseToTray || keepHostedRoom))
            {
                e.Cancel = true;
                if (!_preferences.CloseToTray)
                {
                    // Tray is off, but this room is set to stay online: keep the server, leave voice.
                    try { _engine.LeaveVoice(); } catch { }
                    ShowTrayTip("m\u00E6la room still online", "The room server is still running. Click the tray icon to reopen m\u00E6la.");
                }
                else if (!_preferences.TrayHintShown)
                {
                    _preferences.TrayHintShown = true;
                    PreferencesStore.Save(_preferences);
                    ShowTrayTip("m\u00E6la is still running", "You're still in your room. Click the tray icon to reopen, or right-click it and choose Quit.");
                }
                Hide();
                return;
            }

            _uiTimer.Stop();
            _updateTimer.Stop();
            Interlocked.Increment(ref _presenceGeneration);
            StopEngine();
            ShutdownFriends();
            PreferencesStore.Save(_preferences);
            SavedRoomStore.Save(_savedRooms);
            _notifyIcon.Visible = false;
            try { _notifyIcon.Dispose(); } catch { }
        }

        private void ShowTrayTip(string title, string text)
        {
            _notifyIcon.Visible = true;
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            try { _notifyIcon.ShowBalloonTip(3000); } catch { }
        }

        /// <summary>Right-click menu on the tray icon, rebuilt each time so it shows the current state.</summary>
        private void BuildTrayMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            ToolStripItem open = menu.Items.Add("Open m\u00E6la", null, delegate { RestoreFromTray(); });
            open.Font = new Font(open.Font, FontStyle.Bold);
            bool inVoice = _engine != null && _engine.IsRunning && _engine.IsInVoice;
            if (inVoice)
            {
                menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem mute = new ToolStripMenuItem("Mute", null, delegate { MuteButtonClicked(null, EventArgs.Empty); });
                mute.Checked = _muted || _deafened;
                menu.Items.Add(mute);
                ToolStripMenuItem deafen = new ToolStripMenuItem("Deafen", null, delegate { DeafenButtonClicked(null, EventArgs.Empty); });
                deafen.Checked = _deafened;
                menu.Items.Add(deafen);
                menu.Items.Add("Leave voice", null, delegate { LeaveClicked(null, EventArgs.Empty); });
            }
            menu.Items.Add(new ToolStripSeparator());
            AddStatusItems(menu.Items);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_engine != null && _engine.IsRunning && _engine.IsHost ? "Stop room and quit" : "Quit m\u00E6la", null, delegate { QuitApp(); });
        }

        /// <summary>Really exits: leaves the room properly (or stops a hosted one) and closes.</summary>
        internal void QuitApp()
        {
            _allowTrueClose = true;
            StopEngine();
            Close();
        }

        /// <summary>Another copy of mæla was started: bring this one to the front instead.</summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == SingleInstance.ShowMessage)
            {
                RestoreFromTray();
                return;
            }
            base.WndProc(ref m);
        }

        private void RestoreFromTray()
        {
            if (!Visible)
                Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
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
}
