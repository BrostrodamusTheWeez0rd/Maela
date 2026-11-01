using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>Your status, the friends button and everything that connects FriendService to the app.</summary>
    internal sealed partial class MainForm
    {
        private FriendService _friends;
        private FriendsWindow _friendsWindow;
        private UpnpPortMapping _friendMapping;
        private int _friendNetworkGeneration;
        private int _friendsVersionShown = -1;
        private bool _friendAddressChanged;
        private string _sentFriendName;
        private string _callFriendId;
        private string _callPassword;
        private bool _callRoomSent;
        private bool _callHosting;
        private bool _callVoiceJoined;
        private bool _callHadCompany;
        private DateTime _callRoomStageUtc = DateTime.MinValue;
        private IncomingCallForm _incomingCall;
        private readonly FlatButton _friendsButton = new FlatButton();
        private readonly FlatButton _statusButton = new FlatButton();
        private readonly System.Windows.Forms.Timer _friendAddressTimer = new System.Windows.Forms.Timer();

        private void BuildFriendsBar()
        {
            Panel row = new Panel();
            row.Dock = DockStyle.Bottom;
            row.Height = 42;
            _friendsButton.Look = ButtonLook.Ghost;
            _friendsButton.Text = "Friends";
            _friendsButton.Click += delegate { ShowFriendsWindow(); };
            _statusButton.Look = ButtonLook.Ghost;
            _statusButton.Click += delegate
            {
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new ThemedMenuRenderer();
                AddStatusItems(menu.Items);
                menu.Show(_statusButton, new Point(0, _statusButton.Height));
            };
            _toolTip.SetToolTip(_statusButton, "Your status. Offline turns friends off completely.");
            row.Controls.Add(_friendsButton);
            row.Controls.Add(_statusButton);
            row.Resize += delegate
            {
                _friendsButton.SetBounds(10, 4, 112, 34);
                _statusButton.SetBounds(row.Width - 136, 4, 126, 34);
            };
            _sidebar.Controls.Add(row);
        }

        private void AddStatusItems(ToolStripItemCollection items)
        {
            FriendStatus current = _friends == null ? FriendStatus.Offline : _friends.Status;
            foreach (FriendStatus status in new FriendStatus[] { FriendStatus.Online, FriendStatus.Away, FriendStatus.Busy, FriendStatus.Offline })
            {
                FriendStatus chosen = status;
                ToolStripMenuItem item = new ToolStripMenuItem(StatusLabel(status) + StatusDetail(status), null, delegate { SetMyStatus(chosen); });
                item.Checked = status == current;
                items.Add(item);
            }
        }

        internal static string StatusLabel(FriendStatus status)
        {
            switch (status)
            {
                case FriendStatus.Online: return "Online";
                case FriendStatus.Away: return "Away";
                case FriendStatus.Busy: return "Busy";
                default: return "Offline";
            }
        }

        private static string StatusDetail(FriendStatus status)
        {
            switch (status)
            {
                case FriendStatus.Busy: return " (messages arrive, no pings)";
                case FriendStatus.Offline: return " (friends off, nothing can reach you)";
                default: return String.Empty;
            }
        }

        private void InitFriends()
        {
            try
            {
                _friends = new FriendService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maela"));
            }
            catch
            {
                _friendsButton.Enabled = false;
                _statusButton.Enabled = false;
                return;
            }
            _friends.MessageReceived += delegate(string id, string text) { OnFriendEvent(delegate { FriendMessageArrived(id); }); };
            _friends.RequestReceived += delegate(string name) { OnFriendEvent(delegate { FriendAlert("Friend request from " + name); }); };
            _friends.RequestAccepted += delegate(string name) { OnFriendEvent(delegate { FriendAlert(name + " accepted your friend request"); }); };
            _friends.CallEvent += delegate(FriendCallEvent kind, string id, string code, string password)
            {
                OnFriendEvent(delegate { HandleCallEvent(kind, id, code, password); });
            };
            _uiTimer.Tick += delegate
            {
                UpdateFriendsBar();
                TickFriendCall();
                string name = _preferences.DisplayName;
                if (name != _sentFriendName)
                {
                    _sentFriendName = name;
                    _friends.SetName(name);
                }
            };
            UpdateFriendsBar();

            // Your router can be asked for your address without telling anyone else.
            _friendAddressTimer.Interval = 15 * 60 * 1000;
            _friendAddressTimer.Tick += delegate { RefreshFriendAddress(); };
            NetworkChange.NetworkAddressChanged += OnNetworkChangedForFriends;
        }

        /// <summary>Warnings about your identity, and going back to the status you left on.</summary>
        private void ResumeFriends()
        {
            if (_friends == null)
                return;
            if (_friends.IdentityWasLost)
                MessageBox.Show(this, "Your friends list and friend code couldn't be opened. This usually happens after reinstalling Windows or on a different Windows account.\r\n\r\nThe old file has been kept, and you have a new friend code. Your friends will need to add you again.", "Friends", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (_preferences.FriendStatus != FriendStatus.Offline)
                SetMyStatus(_preferences.FriendStatus);
        }

        private void ShutdownFriends()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChangedForFriends;
            _friendAddressTimer.Stop();
            Interlocked.Increment(ref _friendNetworkGeneration);
            if (_friendsWindow != null)
                _friendsWindow.Close();
            if (_friendMapping != null)
            {
                try { _friendMapping.Dispose(); } catch { }
                _friendMapping = null;
            }
            if (_friends != null)
                _friends.Dispose();
        }

        private void OnFriendEvent(MethodInvoker action)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(action);
            }
            catch { }
        }

        internal void SetMyStatus(FriendStatus status)
        {
            if (_friends == null)
                return;
            if (status != FriendStatus.Offline && !_preferences.FriendsNoticeShown)
            {
                DialogResult answer = MessageBox.Show(this,
                    "Going online opens a small listener on your PC so friends can reach you, and asks your router to open a port for it. If your router can't tell mæla your public address, mæla asks api.ipify.org, the same as when you host a room.\r\n\r\n" +
                    "Your friend code contains your IP address, so share it like a phone number. Without your code, nothing answers. With it, someone can only send a friend request, and can't message you until you accept.\r\n\r\n" +
                    "Appear Offline at any time to turn all of this off. Go online?",
                    "Friends", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes)
                    return;
                _preferences.FriendsNoticeShown = true;
            }

            bool wasRunning = _friends.Status != FriendStatus.Offline;
            if (!_friends.SetStatus(status, _preferences.DisplayName))
            {
                MessageBox.Show(this, "mæla couldn't open your friends file, so it won't go online and risk replacing your friend code. Try restarting mæla.", "Friends", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _preferences.FriendStatus = status;
            PreferencesStore.Save(_preferences);
            if (status == FriendStatus.Offline)
            {
                Interlocked.Increment(ref _friendNetworkGeneration);
                _friendAddressTimer.Stop();
                if (_friendMapping != null)
                {
                    try { _friendMapping.Dispose(); } catch { }
                    _friendMapping = null;
                }
            }
            else if (!wasRunning)
            {
                BeginFriendNetworkSetup();
                _friendAddressTimer.Start();
            }
            UpdateFriendsBar();
        }

        /// <summary>The same steps as hosting: open a port, then work out your public address.</summary>
        private void BeginFriendNetworkSetup()
        {
            int generation = Interlocked.Increment(ref _friendNetworkGeneration);
            int port = _friends.Port;
            string localIp = GetOutboundLocalIPv4Address();
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpnpPortMapping mapping = null;
                string publicIp = String.Empty;
                try
                {
                    string failure;
                    mapping = UpnpPortMapping.TryCreate(port, localIp, out failure);
                    if (mapping != null && !UpnpPortMapping.IsNonPublic(mapping.ExternalIp))
                        publicIp = mapping.ExternalIp;
                }
                catch { }
                if (String.IsNullOrWhiteSpace(publicIp))
                    publicIp = FetchPublicIPv4();

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (generation != _friendNetworkGeneration || _friends.Status == FriendStatus.Offline)
                        {
                            if (mapping != null)
                                mapping.Dispose();
                            return;
                        }
                        if (_friendMapping != null)
                            _friendMapping.Dispose();
                        _friendMapping = mapping;
                        UseFriendAddress(publicIp, localIp, mapping == null ? port : mapping.ExternalPort, port);
                    });
                }
                catch
                {
                    if (mapping != null)
                        mapping.Dispose();
                }
            });
        }

        private void UseFriendAddress(string publicIp, string localIp, int publicPort, int localPort)
        {
            IPAddress ip;
            bool changed;
            if (IPAddress.TryParse(publicIp, out ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                changed = _friends.SetAddress(ip, publicPort);
            else if (IPAddress.TryParse(localIp, out ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                changed = _friends.SetAddress(ip, localPort); // No public address: only works on this network.
            else
                return;
            if (changed)
            {
                _friendAddressChanged = true;
                FriendAlert("Your address changed. Friends who were offline may need your new code.");
                if (_friendsWindow != null)
                    _friendsWindow.ShowAddressChanged();
            }
        }

        private void OnNetworkChangedForFriends(object sender, EventArgs e)
        {
            OnFriendEvent(delegate
            {
                if (_friends != null && _friends.Status != FriendStatus.Offline)
                    BeginFriendNetworkSetup();
            });
        }

        private void RefreshFriendAddress()
        {
            UpnpPortMapping mapping = _friendMapping;
            if (mapping == null || _friends == null)
                return;
            int generation = _friendNetworkGeneration;
            int localPort = _friends.Port;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string ip = mapping.RefreshExternalIp();
                if (String.IsNullOrWhiteSpace(ip) || UpnpPortMapping.IsNonPublic(ip))
                    return;
                OnFriendEvent(delegate
                {
                    if (generation == _friendNetworkGeneration && _friends.Status != FriendStatus.Offline)
                        UseFriendAddress(ip, String.Empty, mapping.ExternalPort, localPort);
                });
            });
        }

        private void UpdateFriendsBar()
        {
            if (_friends == null || _friends.Version == _friendsVersionShown)
                return;
            _friendsVersionShown = _friends.Version;
            int waiting = 0;
            foreach (FriendInfo person in _friends.GetPeople())
            {
                waiting += person.Unread;
                if (person.State == FriendState.RequestReceived)
                    waiting++;
            }
            _friendsButton.Text = waiting > 0 ? "Friends (" + waiting + ")" : "Friends";
            _statusButton.Text = "● " + StatusLabel(_friends.Status);
        }

        private void FriendMessageArrived(string id)
        {
            if (_friendsWindow != null && _friendsWindow.IsReading(id))
            {
                _friends.MarkRead(id);
                return;
            }
            foreach (FriendInfo person in _friends.GetPeople())
            {
                if (person.Id == id)
                    FriendAlert("New message from " + person.Name);
            }
        }

        /// <summary>Busy: everything still arrives, with no sound or flashing.</summary>
        private void FriendAlert(string text)
        {
            if (_friends.Status == FriendStatus.Busy)
                return;
            PlaySound(SoundCue.Message);
            if (!Visible)
                ShowTrayTip("mæla", text); // Who it's from, never what it says.
            else
                FlashForAttention();
        }

        // ----- Calls: ringing goes through FriendService, the call itself is a private room. -----

        private string FriendName(string id)
        {
            foreach (FriendInfo person in _friends.GetPeople())
            {
                if (person.Id == id)
                    return person.Name;
            }
            return "Your friend";
        }

        /// <summary>A call replaces the room you're in, so that's asked first.</summary>
        private bool OkToLeaveRoomForCall()
        {
            VoipEngine engine = _engine;
            if (engine == null || !engine.IsRunning)
                return true;
            string room = String.IsNullOrEmpty(_activeRoomName) ? "this room" : _activeRoomName;
            string question = engine.IsHost && engine.MemberCount > 1
                ? "Calling stops " + room + " for everyone in it. Continue?"
                : "Calling takes you out of " + room + ". Continue?";
            return MessageBox.Show(this, question, "Call", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        internal void StartFriendCall(string id)
        {
            if (_callFriendId != null)
            {
                if (MessageBox.Show(this, "End the call with " + FriendName(_callFriendId) + "?", "Call", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    FinishCall("The call ended.");
                return;
            }
            if (!OkToLeaveRoomForCall())
                return;
            if (!_friends.StartCall(id, _friendMapping != null))
            {
                MessageBox.Show(this, FriendName(id) + " can't be called right now.", "Call", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _callFriendId = id;
            ResetCallProgress();
            ShowTrayOrFlash("Calling " + FriendName(id) + "\u2026");
        }

        private void ResetCallProgress()
        {
            _callPassword = null;
            _callRoomSent = false;
            _callHosting = false;
            _callVoiceJoined = false;
            _callHadCompany = false;
            _callRoomStageUtc = DateTime.MinValue;
        }

        private void ShowTrayOrFlash(string text)
        {
            if (_friendsWindow != null)
                _friendsWindow.ShowCallStatus(text);
        }

        private void HandleCallEvent(FriendCallEvent kind, string id, string code, string password)
        {
            switch (kind)
            {
                case FriendCallEvent.Incoming:
                    if (_friends.Status == FriendStatus.Busy)
                    {
                        // Busy: no ringing. Your friend is told you didn't take it, and you find it in the chat.
                        _friends.EndCall();
                        _friends.NoteMissedCall(id);
                        return;
                    }
                    _incomingCall = new IncomingCallForm(FriendName(id));
                    _incomingCall.Answered += delegate(bool accept)
                    {
                        _incomingCall = null;
                        if (!accept || !OkToLeaveRoomForCall())
                        {
                            _friends.EndCall();
                            return;
                        }
                        _callFriendId = id;
                        ResetCallProgress();
                        _friends.AcceptCall(_friendMapping != null);
                    };
                    _incomingCall.Ring += delegate { PlaySound(SoundCue.Message); };
                    if (!Visible)
                        ShowTrayTip("m\u00E6la", FriendName(id) + " is calling");
                    FlashForAttention();
                    _incomingCall.Show();
                    break;

                case FriendCallEvent.HostNow:
                    if (_callFriendId != id)
                        return;
                    _callRoomStageUtc = DateTime.UtcNow;
                    ShowTrayOrFlash(null);
                    HostCallRoom(id);
                    break;

                case FriendCallEvent.JoinRoom:
                    if (_callFriendId != id)
                        return;
                    _callRoomStageUtc = DateTime.UtcNow;
                    ShowTrayOrFlash(null);
                    SavedRoom joined = new SavedRoom();
                    joined.Name = "Call with " + FriendName(id);
                    joined.Invite = code;
                    joined.Password = password;
                    StartJoinRoom(joined);
                    break;

                case FriendCallEvent.Declined:
                    FinishCall(FriendName(id) + " didn't take the call.");
                    break;

                case FriendCallEvent.Ended:
                    if (_incomingCall != null)
                    {
                        _incomingCall.Close();
                        _incomingCall = null;
                        _friends.NoteMissedCall(id);
                    }
                    if (_callFriendId == id)
                        FinishCall("The call ended.");
                    break;
            }
        }

        private void HostCallRoom(string id)
        {
            SavedRoom room = new SavedRoom();
            room.Name = "Call with " + FriendName(id);
            room.Owned = true;
            room.KeepAlive = false;
            room.ChannelNames.Add("Call");
            // A one-off room and a one-off password, known only to you two.
            room.Password = BitConverter.ToString(CryptoUtil.Random(16)).Replace("-", String.Empty);
            _callPassword = room.Password;
            _callHosting = true;
            StartHostRoom(room, 0, false);
        }

        /// <summary>Hands over the room once it has a code, and ends the call when the other person goes.</summary>
        private void TickFriendCall()
        {
            if (_callFriendId == null)
                return;
            VoipEngine engine = _engine;
            if (_callHosting && !_callRoomSent && engine != null && engine.IsRunning && RoomCode.IsValid(_activeInvite))
            {
                _callRoomSent = true;
                _friends.CallRoomIsReady(_activeInvite, _callPassword);
            }
            if (engine == null || !engine.IsRunning)
            {
                if (_callRoomStageUtc != DateTime.MinValue)
                    FinishCall(null);
                return;
            }
            // Handed over, but nobody got through: routers on both sides, usually.
            if (!_callHadCompany && _callRoomStageUtc != DateTime.MinValue && (DateTime.UtcNow - _callRoomStageUtc).TotalSeconds > 45.0)
            {
                FinishCall("The call couldn't connect. Your routers may not allow a direct connection.");
                return;
            }
            if (engine.IsConnected && !engine.IsInVoice && !_callVoiceJoined)
            {
                _callVoiceJoined = true;
                engine.ChangeChannel(1);
            }
            if (engine.IsConnected && engine.MemberCount >= 2)
                _callHadCompany = true;
            if (_callHadCompany && (!engine.IsConnected || engine.MemberCount < 2))
                FinishCall(null);
        }

        private void FinishCall(string message)
        {
            string id = _callFriendId;
            _callFriendId = null;
            ResetCallProgress();
            _friends.EndCall();
            bool callRoom = _activeSavedRoom == null && !String.IsNullOrEmpty(_activeRoomName) && _activeRoomName.StartsWith("Call with ", StringComparison.Ordinal);
            if (id != null && callRoom && _engine != null && _engine.IsRunning)
                StopEngine();
            // Back to no room selected: a finished call isn't a room you can host again.
            if (_selectedRoom != null && !_savedRooms.Contains(_selectedRoom) && _selectedRoom.Name.StartsWith("Call with ", StringComparison.Ordinal))
            {
                _selectedRoom = null;
                _rail.Selected = null;
                _rail.Invalidate();
                UpdateSelectedRoomUi();
            }
            ShowTrayOrFlash(message);
        }

        internal void ShowFriendsWindow()
        {
            if (_friends == null)
                return;
            if (_friendsWindow == null || _friendsWindow.IsDisposed)
            {
                _friendsWindow = new FriendsWindow(_friends);
                _friendsWindow.ChangeStatus = SetMyStatus;
                _friendsWindow.Call = StartFriendCall;
                _friendsWindow.FormClosed += delegate { _friendsWindow = null; };
                if (_friendAddressChanged)
                    _friendsWindow.ShowAddressChanged();
            }
            RestoreFromTrayIfHidden();
            _friendsWindow.Show();
            if (_friendsWindow.WindowState == FormWindowState.Minimized)
                _friendsWindow.WindowState = FormWindowState.Normal;
            _friendsWindow.Activate();
            _friendAddressChanged = false;
        }

        private void RestoreFromTrayIfHidden()
        {
            if (!Visible)
                RestoreFromTray();
        }
    }

    /// <summary>Your friends on the left; on the right, the one chat you clicked.</summary>
    internal sealed class FriendsWindow : Form
    {
        private readonly FriendService _friends;
        private readonly Panel _left = new Panel();
        private readonly Panel _right = new Panel();
        private readonly ListBox _list = new ListBox();
        private readonly FlatButton _codeButton = new FlatButton();
        private readonly FlatButton _addButton = new FlatButton();
        private readonly Label _title = new Label();
        private readonly Label _subtitle = new Label();
        private readonly Label _notice = new Label();
        private readonly FlatButton _acceptButton = new FlatButton();
        private readonly FlatButton _blockButton = new FlatButton();
        private readonly FlatButton _updateCodeButton = new FlatButton();
        private readonly RichTextBox _chat = new RichTextBox();
        private readonly Panel _composer = new Panel();
        private readonly RoundField _input = new RoundField(IconKind.None);
        private readonly FlatButton _sendButton = new FlatButton();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private string _selectedId;
        private int _versionShown = -1;
        private int _chatCountShown = -1;
        private int _chatDeliveredShown = -1;
        private string _chatIdShown;
        private bool _addressChanged;
        private string _callStatus;
        private readonly FlatButton _backupButton = new FlatButton();

        /// <summary>Goes through the app, which also opens and closes the router port.</summary>
        public Action<FriendStatus> ChangeStatus;
        public Action<string> Call;
        private readonly FlatButton _callButton = new FlatButton();

        public FriendsWindow(FriendService friends)
        {
            _friends = friends;
            Text = "Friends";
            ClientSize = new Size(860, 580);
            MinimumSize = new Size(680, 440);
            StartPosition = FormStartPosition.CenterParent;
            Font = Ui.Body;
            try { Icon = Branding.LoadAppIcon(); } catch { }

            _right.Dock = DockStyle.Fill;
            Controls.Add(_right);
            _left.Dock = DockStyle.Left;
            _left.Width = 270;
            Controls.Add(_left);

            Panel leftTop = new Panel();
            leftTop.Dock = DockStyle.Top;
            leftTop.Height = 142;
            _codeButton.Text = "Copy my friend code";
            _codeButton.Icon = IconKind.Copy;
            _codeButton.SetBounds(12, 12, 246, 36);
            _codeButton.Click += delegate { CopyCode(); };
            _addButton.Text = "Add a friend";
            _addButton.Icon = IconKind.Plus;
            _addButton.Look = ButtonLook.Primary;
            _addButton.SetBounds(12, 54, 246, 36);
            _addButton.Click += delegate { AddFriend(); };
            _backupButton.Text = "Backup and restore";
            _backupButton.Look = ButtonLook.Ghost;
            _backupButton.SetBounds(12, 96, 246, 36);
            _backupButton.Click += delegate
            {
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new ThemedMenuRenderer();
                menu.Items.Add("Back up my account\u2026", null, delegate { BackUp(); });
                menu.Items.Add("Restore from a backup\u2026", null, delegate { Restore(); });
                menu.Show(_backupButton, new Point(0, _backupButton.Height));
            };
            leftTop.Controls.Add(_backupButton);
            leftTop.Controls.Add(_codeButton);
            leftTop.Controls.Add(_addButton);
            _list.Dock = DockStyle.Fill;
            _list.BorderStyle = BorderStyle.None;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = 52;
            _list.IntegralHeight = false;
            _list.DrawItem += DrawPerson;
            _list.SelectedIndexChanged += delegate { SelectionChanged(); };
            _list.MouseDown += ListMouseDown;
            _left.Controls.Add(_list);
            _left.Controls.Add(leftTop);

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 62;
            _title.Font = Ui.BodyBold;
            _title.AutoEllipsis = true;
            _title.SetBounds(20, 12, 360, 22);
            _subtitle.Font = Ui.Small;
            _subtitle.AutoEllipsis = true;
            _subtitle.SetBounds(20, 34, 360, 18);
            head.Controls.Add(_title);
            head.Controls.Add(_subtitle);
            _acceptButton.Text = "Accept";
            _acceptButton.Look = ButtonLook.Primary;
            _acceptButton.Size = new Size(84, 34);
            _acceptButton.Click += delegate { if (_selectedId != null) _friends.AcceptRequest(_selectedId); };
            _blockButton.Text = "Decline and block";
            _blockButton.Look = ButtonLook.Danger;
            _blockButton.Size = new Size(136, 34);
            _blockButton.Click += delegate { if (_selectedId != null) _friends.Block(_selectedId); };
            _updateCodeButton.Text = "Paste a new code";
            _updateCodeButton.Size = new Size(130, 34);
            _updateCodeButton.Click += delegate { AddFriend(); };
            head.Controls.Add(_acceptButton);
            head.Controls.Add(_blockButton);
            head.Controls.Add(_updateCodeButton);
            _callButton.Text = "Call";
            _callButton.Icon = IconKind.Mic;
            _callButton.Look = ButtonLook.Primary;
            _callButton.Size = new Size(84, 34);
            _callButton.Click += delegate { if (_selectedId != null && Call != null) Call(_selectedId); };
            head.Controls.Add(_callButton);
            head.Resize += delegate
            {
                _blockButton.Location = new Point(head.Width - 150, 14);
                _acceptButton.Location = new Point(head.Width - 244, 14);
                _updateCodeButton.Location = new Point(head.Width - 144, 14);
                _callButton.Location = new Point(head.Width - 98, 14);
                // Stop short of the buttons, so long names can't cover them.
                _title.Width = Math.Max(80, head.Width - 270);
                _subtitle.Width = _title.Width;
            };

            _notice.Dock = DockStyle.Top;
            _notice.Font = Ui.Small;
            _notice.Padding = new Padding(20, 6, 20, 6);
            _notice.Height = 28;
            _notice.Visible = false;
            _notice.Cursor = Cursors.Hand;
            _notice.Click += delegate
            {
                if (_callStatus != null)
                {
                    _callStatus = null;
                    RefreshNotice(null);
                }
                else if (_addressChanged)
                {
                    _addressChanged = false;
                    CopyCode();
                    RefreshNotice(null);
                }
                else if (!_friends.BackedUp && _friends.GetPeople().Count > 0)
                    BackUp();
            };

            _composer.Dock = DockStyle.Bottom;
            _composer.Height = 62;
            _input.Box.MaxLength = FriendService.MaxMessageChars;
            _input.Placeholder = "Message";
            _input.Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SendCurrent();
                }
            };
            _sendButton.Icon = IconKind.Send;
            _sendButton.Look = ButtonLook.Primary;
            _sendButton.Click += delegate { SendCurrent(); };
            _composer.Controls.Add(_input);
            _composer.Controls.Add(_sendButton);
            _composer.Resize += delegate
            {
                _input.SetBounds(16, 10, _composer.Width - 84, 42);
                _sendButton.SetBounds(_composer.Width - 58, 10, 42, 42);
            };

            _chat.Dock = DockStyle.Fill;
            _chat.ReadOnly = true;
            _chat.BorderStyle = BorderStyle.None;
            _chat.DetectUrls = false; // Text from other people is only ever shown.
            _chat.TabStop = false;
            _chat.Font = Ui.Body;
            Panel chatHost = new Panel();
            chatHost.Dock = DockStyle.Fill;
            chatHost.Padding = new Padding(20, 6, 12, 6);
            chatHost.Controls.Add(_chat);
            _right.Controls.Add(chatHost);
            _right.Controls.Add(_composer);
            _right.Controls.Add(_notice);
            _right.Controls.Add(head);

            Theme.Changed += ThemeChanged;
            FormClosed += delegate
            {
                Theme.Changed -= ThemeChanged;
                _timer.Stop();
            };
            ApplyColors();

            _timer.Interval = 200;
            _timer.Tick += delegate { RefreshAll(false); };
            _timer.Start();
            RefreshAll(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.StyleWindowFrame(this);
        }

        private void ThemeChanged(object sender, EventArgs e)
        {
            ApplyColors();
            RefreshAll(true);
        }

        private void ApplyColors()
        {
            Theme t = Theme.Current;
            BackColor = t.Main;
            ForeColor = t.Text;
            _left.BackColor = t.Side;
            foreach (Control c in _left.Controls)
                c.BackColor = t.Side;
            _list.BackColor = t.Side;
            _list.ForeColor = t.Text;
            _right.BackColor = t.Main;
            foreach (Control c in _right.Controls)
                c.BackColor = t.Main;
            _title.ForeColor = t.Text;
            _title.BackColor = t.Main;
            _subtitle.ForeColor = t.Muted;
            _subtitle.BackColor = t.Main;
            _notice.BackColor = t.Surface;
            _notice.ForeColor = t.Text2;
            _chat.BackColor = t.Main;
            _chat.ForeColor = t.Text;
            _input.Fill = t.Surface;
            _input.ApplyTheme();
            _chatIdShown = null;
            Invalidate(true);
        }

        public bool IsReading(string id)
        {
            return Visible && WindowState != FormWindowState.Minimized && ContainsFocus && id == _selectedId;
        }

        /// <summary>Shows how a call is going, under the friend's name.</summary>
        public void ShowCallStatus(string text)
        {
            _callStatus = text;
            RefreshAll(true);
        }

        public void ShowAddressChanged()
        {
            _addressChanged = true;
            RefreshAll(true);
        }

        private void CopyCode()
        {
            string code = _friends.Code;
            if (code.Length == 0)
            {
                MessageBox.Show(this, _friends.Status == FriendStatus.Offline
                    ? "You're appearing offline. Choose Online first, then copy your code."
                    : "mæla is still working out your address. Try again in a few seconds.", "Friend code", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Clipboard.SetText(code);
                _codeButton.Text = "Copied";
                System.Windows.Forms.Timer reset = new System.Windows.Forms.Timer();
                reset.Interval = 1500;
                reset.Tick += delegate
                {
                    reset.Stop();
                    reset.Dispose();
                    if (!IsDisposed)
                        _codeButton.Text = "Copy my friend code";
                };
                reset.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not copy friend code", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BackUp()
        {
            string password;
            using (BackupPasswordDialog dialog = new BackupPasswordDialog(true))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                password = dialog.Password;
            }
            using (SaveFileDialog save = new SaveFileDialog())
            {
                save.Title = "Save your m\u00E6la backup";
                save.FileName = "maela-account.maelakey";
                save.Filter = "m\u00E6la backup (*.maelakey)|*.maelakey";
                if (save.ShowDialog(this) != DialogResult.OK)
                    return;
                Cursor = Cursors.WaitCursor;
                try
                {
                    _friends.ExportBackup(save.FileName, password);
                    MessageBox.Show(this, "Backup saved. Keep the file and your password somewhere safe, away from this PC. m\u00E6la can't recover a forgotten password.\r\n\r\nFriends you add after today won't be in it, so back up again now and then.", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not save the backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }
            RefreshAll(true);
        }

        private void Restore()
        {
            string path;
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Title = "Choose your m\u00E6la backup";
                open.Filter = "m\u00E6la backup (*.maelakey)|*.maelakey";
                if (open.ShowDialog(this) != DialogResult.OK)
                    return;
                path = open.FileName;
            }
            string warning = "This replaces your friend code and friends list on this PC with the ones in the backup. Messages waiting to send from this PC will be lost. The old account file is kept.\r\n\r\nDon't run the same account on two PCs at once: stop using it on the other PC first.\r\n\r\nContinue?";
            if (MessageBox.Show(this, warning, "Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            string password;
            using (BackupPasswordDialog dialog = new BackupPasswordDialog(false))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                password = dialog.Password;
            }

            FriendStatus previous = _friends.Status;
            if (ChangeStatus != null)
                ChangeStatus(FriendStatus.Offline);
            Cursor = Cursors.WaitCursor;
            RestoreResult result = _friends.RestoreBackup(path, password);
            Cursor = Cursors.Default;
            if (ChangeStatus != null && previous != FriendStatus.Offline)
                ChangeStatus(previous);
            switch (result)
            {
                case RestoreResult.Restored:
                    MessageBox.Show(this, "Your account is back. Your address has probably changed, so friends may need your new code: copy it and send it to them. If a friend pastes it, you're connected again with no new request.", "Restore", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
                case RestoreResult.WrongPassword:
                    MessageBox.Show(this, "That password doesn't open this backup, or the file has been changed. Nothing was replaced.", "Restore", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                case RestoreResult.NotABackup:
                    MessageBox.Show(this, "That isn't a m\u00E6la backup. Nothing was replaced.", "Restore", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                default:
                    MessageBox.Show(this, "m\u00E6la couldn't replace your account file. Nothing was changed. Try again after restarting m\u00E6la.", "Restore", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
            }
            RefreshAll(true);
        }

        private void AddFriend()
        {
            string code = TextPrompt.Show(this, "Add a friend", "Their friend code", String.Empty);
            if (String.IsNullOrWhiteSpace(code))
                return;
            string message = null;
            switch (_friends.AddByCode(code))
            {
                case AddFriendResult.InvalidCode: message = "That isn't a valid friend code. Copy and paste it exactly as they sent it."; break;
                case AddFriendResult.ThatsYou: message = "That's your own friend code."; break;
                case AddFriendResult.Blocked: message = "You've blocked this person. Remove them from your list first (right-click their name)."; break;
                case AddFriendResult.NotRunning: message = "You're appearing offline. Choose Online first, then add your friend."; break;
                case AddFriendResult.AddressUpdated: message = "Got it. mæla will try them at their new address."; break;
            }
            if (message != null)
                MessageBox.Show(this, message, "Add a friend", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SendCurrent()
        {
            if (_selectedId == null)
                return;
            if (_friends.SendMessage(_selectedId, _input.Box.Text))
                _input.Box.Clear();
        }

        private void ListMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
                return;
            int index = _list.IndexFromPoint(e.Location);
            if (index < 0)
                return;
            _list.SelectedIndex = index;
            FriendInfo person = (FriendInfo)_list.Items[index];
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ThemedMenuRenderer();
            if (person.State != FriendState.Blocked)
                menu.Items.Add("Block", null, delegate
                {
                    if (MessageBox.Show(this, "Block " + person.Name + "? They won't be able to reach you or send requests, and won't be told.", "Block", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        _friends.Block(person.Id);
                });
            menu.Items.Add(person.State == FriendState.Blocked ? "Unblock and remove from list" : "Remove", null, delegate
            {
                string question = person.State == FriendState.Blocked
                    ? "Unblock " + person.Name + "? They'll be able to send you a friend request again."
                    : "Remove " + person.Name + "? Your chat with them will be deleted from this PC.";
                if (MessageBox.Show(this, question, "Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    _friends.Remove(person.Id);
            });
            menu.Show(_list, e.Location);
        }

        private static int Rank(FriendInfo person)
        {
            switch (person.State)
            {
                case FriendState.RequestReceived: return 0;
                case FriendState.Friend: return person.Status == FriendStatus.Offline ? 2 : 1;
                case FriendState.RequestSent: return 3;
                default: return 4;
            }
        }

        private void RefreshAll(bool force)
        {
            if (!force && _friends.Version == _versionShown)
                return;
            _versionShown = _friends.Version;

            List<FriendInfo> people = _friends.GetPeople();
            people.Sort(delegate(FriendInfo a, FriendInfo b)
            {
                int rank = Rank(a).CompareTo(Rank(b));
                return rank != 0 ? rank : String.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            string keep = _selectedId;
            _list.BeginUpdate();
            _list.Items.Clear();
            FriendInfo selected = null;
            foreach (FriendInfo person in people)
            {
                _list.Items.Add(person);
                if (person.Id == keep)
                {
                    selected = person;
                    _list.SelectedItem = person;
                }
            }
            _list.EndUpdate();
            _selectedId = selected == null ? null : selected.Id;
            ShowPerson(selected);
        }

        private void SelectionChanged()
        {
            FriendInfo person = _list.SelectedItem as FriendInfo;
            string id = person == null ? null : person.Id;
            if (id == _selectedId)
                return;
            _selectedId = id;
            ShowPerson(person);
        }

        private void ShowPerson(FriendInfo person)
        {
            bool isFriend = person != null && person.State == FriendState.Friend;
            bool isRequest = person != null && person.State == FriendState.RequestReceived;
            bool longUnreachable = isFriend && person.Status == FriendStatus.Offline && (DateTime.UtcNow - person.LastSeenUtc).TotalDays >= 2.0;
            _composer.Visible = isFriend;
            _chat.Visible = person != null;
            _acceptButton.Visible = isRequest;
            _blockButton.Visible = isRequest;
            _updateCodeButton.Visible = longUnreachable;
            _callButton.Visible = isFriend && !longUnreachable && person.Status != FriendStatus.Offline;
            _title.Text = person == null ? "Friends" : person.Name;
            _subtitle.Text = person == null ? "Click a friend to open your chat with them." : Describe(person) + "  ·  ID " + person.ShortId;
            RefreshNotice(person);

            if (!isFriend)
            {
                _chat.Clear();
                _chatIdShown = null;
                if (isRequest)
                {
                    _chat.SelectionColor = Theme.Current.Text2;
                    _chat.AppendText(person.Name + " wants to be your friend. They can't message you or see your status unless you accept.\n\nNames can be anything. If you're expecting someone, ask them to confirm this ID: " + person.ShortId);
                }
                else if (person != null && person.State == FriendState.RequestSent)
                {
                    _chat.SelectionColor = Theme.Current.Text2;
                    _chat.AppendText("Waiting for them to accept. You can't message each other until they do. mæla keeps trying while you're both online.");
                }
                return;
            }

            if (ContainsFocus && person.Unread > 0)
                _friends.MarkRead(person.Id);
            ShowChat(person);
        }

        private void RefreshNotice(FriendInfo person)
        {
            string text = null;
            if (_callStatus != null)
                text = _callStatus;
            else if (_addressChanged)
                text = "Your address changed. Friends who were offline may need your new code. Click to copy it.";
            else if (_friends.Status == FriendStatus.Offline)
                text = "You're appearing offline. Friends can't reach you, and anything you write waits here until you're both online.";
            else if (_updateCodeButton.Visible)
                text = "Haven't reached them in a while. If this seems wrong, ask for their current code.";
            else if (!_friends.BackedUp && _friends.GetPeople().Count > 0)
                text = "Your account isn't backed up. If you reinstall Windows, it can't be recovered. Click to back up.";
            _notice.Text = text ?? String.Empty;
            _notice.Visible = text != null;
        }

        private void ShowChat(FriendInfo person)
        {
            List<FriendChatEntry> chat = _friends.GetChat(person.Id);
            int delivered = 0;
            foreach (FriendChatEntry entry in chat)
            {
                if (entry.Delivered)
                    delivered++;
            }
            if (_chatIdShown == person.Id && _chatCountShown == chat.Count && _chatDeliveredShown == delivered)
                return;
            _chatIdShown = person.Id;
            _chatCountShown = chat.Count;
            _chatDeliveredShown = delivered;

            Theme t = Theme.Current;
            _chat.SuspendLayout();
            _chat.Clear();
            foreach (FriendChatEntry entry in chat)
            {
                _chat.SelectionFont = Ui.SmallBold;
                _chat.SelectionColor = entry.Mine ? t.Accent2 : t.Text;
                _chat.AppendText(entry.Mine ? "You" : person.Name);
                _chat.SelectionFont = Ui.Small;
                _chat.SelectionColor = t.Muted;
                string when = "  " + entry.TimeUtc.ToLocalTime().ToString("g");
                if (entry.Mine && !entry.Delivered)
                    when += "  ·  waiting to send";
                _chat.AppendText(when + "\n");
                _chat.SelectionFont = Ui.Body;
                _chat.SelectionColor = t.Text;
                _chat.AppendText(entry.Text + "\n\n");
            }
            _chat.SelectionStart = _chat.TextLength;
            _chat.ScrollToCaret();
            _chat.ResumeLayout();
        }

        private static string Describe(FriendInfo person)
        {
            switch (person.State)
            {
                case FriendState.RequestReceived: return "Wants to be your friend";
                case FriendState.RequestSent: return "Request sent";
                case FriendState.Blocked: return "Blocked";
                default: return MainForm.StatusLabel(person.Status);
            }
        }

        private void DrawPerson(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _list.Items.Count)
                return;
            FriendInfo person = (FriendInfo)_list.Items[e.Index];
            Theme t = Theme.Current;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Graphics g = e.Graphics;
            using (SolidBrush back = new SolidBrush(t.Side))
                g.FillRectangle(back, e.Bounds);
            Rectangle row = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 2, e.Bounds.Width - 16, e.Bounds.Height - 4);
            if (selected)
                Ui.FillRound(g, t.AccentSoftSide, row, 8);
            Ui.DrawAvatar(g, new Rectangle(row.X + 8, row.Y + 7, 34, 34), person.Name, false, selected ? t.AccentSoftSide : t.Side);

            Color dot;
            switch (person.Status)
            {
                case FriendStatus.Online: dot = t.Good; break;
                case FriendStatus.Away: dot = Color.FromArgb(240, 178, 50); break;
                case FriendStatus.Busy: dot = t.Danger; break;
                default: dot = t.Muted; break;
            }
            if (person.State == FriendState.Friend)
                Ui.FillRound(g, dot, new RectangleF(row.X + 32, row.Y + 31, 10, 10), 5);

            int textWidth = row.Width - 60 - (person.Unread > 0 ? 30 : 0);
            Ui.DrawText(g, person.Name, Ui.SmallBold, new Rectangle(row.X + 50, row.Y + 7, textWidth, 18), t.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Ui.DrawText(g, Describe(person), Ui.Small, new Rectangle(row.X + 50, row.Y + 25, textWidth, 16), t.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (person.Unread > 0 || person.State == FriendState.RequestReceived)
            {
                Rectangle badge = new Rectangle(row.Right - 30, row.Y + 14, 22, 20);
                Ui.FillRound(g, t.Accent, badge, 10);
                string count = person.State == FriendState.RequestReceived ? "!" : (person.Unread > 99 ? "99" : person.Unread.ToString());
                Ui.DrawText(g, count, Ui.SmallBold, badge, t.OnAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// <summary>Asks for the backup password, twice when it's a new one.</summary>
    internal sealed class BackupPasswordDialog : ThemedDialog
    {
        private readonly RoundField _password;
        private readonly RoundField _confirm;

        public BackupPasswordDialog(bool creating) : base(creating ? "Back up your account" : "Restore your account", 480, creating ? 372 : 262)
        {
            AddHeading(creating ? "Back up your account" : "Enter your backup password",
                creating ? "This password protects your key and friends list. m\u00E6la can't recover it if you forget it." : null);
            _password = AddField("Password" + (creating ? " (at least " + FriendBackup.MinPasswordLength + " characters)" : String.Empty), String.Empty, true);
            if (creating)
                _confirm = AddField("Password again", String.Empty, true);
            FlatButton ok = Button(creating ? "Continue" : "Restore", DialogResult.None);
            ok.Click += delegate
            {
                string problem = null;
                if (creating && _password.Box.Text.Length < FriendBackup.MinPasswordLength)
                    problem = "Use at least " + FriendBackup.MinPasswordLength + " characters. Anyone who gets this file can try passwords as fast as their PC allows.";
                else if (creating && _password.Box.Text != _confirm.Box.Text)
                    problem = "The two passwords don't match.";
                if (problem != null)
                {
                    MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            AddButtons(Button("Cancel", DialogResult.Cancel), ok);
            Shown += delegate { _password.Box.Focus(); };
        }

        public string Password { get { return _password.Box.Text; } }
    }

    /// <summary>A friend is calling. Stays on top, rings gently, and does nothing until you click.</summary>
    internal sealed class IncomingCallForm : ThemedDialog
    {
        private readonly System.Windows.Forms.Timer _ring = new System.Windows.Forms.Timer();
        private bool _answered;

        public event Action<bool> Answered;
        public event EventHandler Ring;

        public IncomingCallForm(string name) : base("Incoming call", 380, 170)
        {
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            AddHeading(name, "is calling you");
            FlatButton decline = Button("Decline", DialogResult.None);
            FlatButton accept = Button("Accept", DialogResult.None);
            decline.Click += delegate { Finish(false); };
            accept.Click += delegate { Finish(true); };
            AddButtons(decline, accept);
            CancelButton = null;
            AcceptButton = null; // A stray Enter mustn't answer a call.
            FormClosing += delegate { Finish(false); };
            _ring.Interval = 2000;
            _ring.Tick += delegate { if (Ring != null) Ring(this, EventArgs.Empty); };
            Shown += delegate
            {
                if (Ring != null) Ring(this, EventArgs.Empty);
                _ring.Start();
            };
        }

        /// <summary>The call ended before you answered.</summary>
        public new void Close()
        {
            _answered = true;
            _ring.Stop();
            base.Close();
        }

        private void Finish(bool accept)
        {
            if (_answered)
                return;
            _answered = true;
            _ring.Stop();
            if (Answered != null)
                Answered(accept);
            BeginInvoke((MethodInvoker)delegate { base.Close(); });
        }
    }
}
