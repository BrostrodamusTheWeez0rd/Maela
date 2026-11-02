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
        private FriendsView _friendsView;
        private UpnpPortMapping _friendMapping;
        private int _friendNetworkGeneration;
        private int _friendsVersionShown = -1;
        private bool _friendAddressChanged;
        private bool _friendsNoticeThisRun;
        private string _sentFriendName;
        private string _callFriendId;
        private string _callPassword;
        private bool _callRoomSent;
        private bool _callHosting;
        private bool _callVoiceJoined;
        private bool _callHadCompany;
        private bool _callRingingOut;
        private DateTime _nextRingbackUtc = DateTime.MinValue;
        private DateTime _callRoomStageUtc = DateTime.MinValue;
        private IncomingCallForm _incomingCall;
        private readonly System.Windows.Forms.Timer _friendAddressTimer = new System.Windows.Forms.Timer();

        /// <summary>
        /// Friends take the place of the channels in the sidebar, and the chat takes the main area.
        /// The room card and your name bar stay. Panels only hide and show: their order sets how
        /// they dock, so don't reorder them here.
        /// </summary>
        internal void ShowFriendsView()
        {
            if (_friendsView == null)
                return;
            SetFriendsVisible(true);
            if (_callFriendId != null && _callRoomStageUtc != DateTime.MinValue)
                _friendsView.ShowCallInProgress();
            if (_friendAddressChanged)
                _friendsView.ShowAddressChanged();
            _friendAddressChanged = false;
        }

        internal void ShowRoomsView()
        {
            if (_friendsView == null || !_rail.FriendsSelected)
                return;
            SetFriendsVisible(false);
        }

        private void SetFriendsVisible(bool friends)
        {
            _main.Visible = !friends;
            _friendsView.Visible = friends;
            _friendsView.ListPanel.Visible = friends;
            _rail.FriendsSelected = friends;
            _rail.Invalidate();
            UpdateSidebarParts();
        }

        /// <summary>The room header, channels and room card only show while you're in or hosting a room.</summary>
        private void UpdateSidebarParts()
        {
            VoipEngine engine = _engine;
            bool inRoom = engine != null && engine.IsRunning;
            bool showChannels = inRoom && !_rail.FriendsSelected;
            _roomHeader.Visible = showChannels;
            _channelHead.Visible = showChannels;
            _channelPad.Visible = showChannels;
            _cardHost.Visible = inRoom;
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
                return;
            }
            _friendsView = new FriendsView(_friends);
            _friendsView.ChangeStatus = SetMyStatus;
            _friendsView.Call = StartFriendCall;
            _friendsView.ReturnToCall = ShowRoomsView;
            _friendsView.Dock = DockStyle.Fill;
            _friendsView.Visible = false;
            Controls.Add(_friendsView);
            Controls.SetChildIndex(_friendsView, 0); // Docks last, in the space the rail and sidebar leave.
            _friendsView.PeopleChanged += delegate(List<FriendInfo> people)
            {
                _rail.SelectedFriendId = _friendsView.SelectedId;
                _rail.SetFriends(people);
            };
            _rail.FriendClicked += delegate(string id)
            {
                _friendsView.SelectPerson(id);
                _rail.SelectedFriendId = id;
                _rail.Invalidate();
            };
            _rail.FriendMenuRequested += delegate(string id, Point at) { _friendsView.ShowPersonMenu(id, _rail, at); };
            _rail.AddFriendClicked += delegate { _friendsView.AddFriend(); };
            _friendsView.RefreshNow();
            _friendsView.ListPanel.Dock = DockStyle.Fill;
            _friendsView.ListPanel.Visible = false;
            _sidebar.Controls.Add(_friendsView.ListPanel);
            _sidebar.Controls.SetChildIndex(_friendsView.ListPanel, 0); // Fills what the card and name bar leave.
            _friends.MessageReceived += delegate(string id, string text) { OnFriendEvent(delegate { FriendMessageArrived(id); }); };
            _friends.RequestReceived += delegate(string id, string name) { OnFriendEvent(delegate { ShowFriendRequest(id, name); }); };
            _friends.RequestAccepted += delegate(string name) { OnFriendEvent(delegate { FriendAlert(name + " accepted your friend request"); }); };
            _friends.CallEvent += delegate(FriendCallEvent kind, string id, string code, string password)
            {
                OnFriendEvent(delegate { HandleCallEvent(kind, id, code, password); });
            };
            _uiTimer.Tick += delegate
            {
                UpdateFriendsBar();
                TickFriendCall();
                UseHostRoomName();
                UseHostRoomArt();
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
            OfferWindowsSetup();
            if (_friends == null)
                return;
            if (_friends.IdentityWasLost)
                MessageBox.Show(this, "Your friends list and friend code couldn't be opened. This usually happens after reinstalling Windows or on a different Windows account.\r\n\r\nThe old file has been kept, and you have a new friend code. Your friends will need to add you again.", "Friends", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (_preferences.FriendStatus != FriendStatus.Offline)
            {
                _friendsNoticeThisRun = true; // You already chose this; don't ask at every launch.
                SetMyStatus(_preferences.FriendStatus);
            }
        }

        private void ShutdownFriends()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChangedForFriends;
            _friendAddressTimer.Stop();
            Interlocked.Increment(ref _friendNetworkGeneration);
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
            if (status != FriendStatus.Offline && !_preferences.FriendsNoticeShown && !_friendsNoticeThisRun)
            {
                using (NoticeDialog notice = new NoticeDialog("Friends", "Only share your friend code with people you trust."))
                {
                    if (notice.ShowDialog(this) != DialogResult.OK)
                        return;
                    _friendsNoticeThisRun = true;
                    _preferences.FriendsNoticeShown = notice.DontShowAgain;
                }
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

        /// <summary>Asked once, at first-time setup or the first launch of this version. Nothing changes unless you choose it.</summary>
        private void OfferWindowsSetup()
        {
            if (_preferences.WindowsSetupOffered)
                return;
            _preferences.WindowsSetupOffered = true;
            PreferencesStore.Save(_preferences);
            string path = Application.ExecutablePath;
            using (WindowsSetupDialog dialog = new WindowsSetupDialog(WindowsSecurity.FirewallAllows(path)))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                string problems = String.Empty;
                if (dialog.AllowFirewall)
                {
                    ElevatedResult firewall = WindowsSecurity.AllowThroughFirewall(path);
                    if (firewall != ElevatedResult.Done && firewall != ElevatedResult.Cancelled)
                        problems += "Windows didn't add the firewall rule. You can try again in Settings > Network.\r\n\r\n";
                }
                if (dialog.ExcludeFromDefender)
                {
                    ElevatedResult defender = WindowsSecurity.SetDefenderExclusion(path, true);
                    if (defender != ElevatedResult.Done && defender != ElevatedResult.Cancelled)
                        problems += "Windows didn't keep the Defender exclusion. You can add it by hand in Windows Security > Virus & threat protection > Manage settings > Exclusions.";
                }
                if (problems.Length > 0)
                    MessageBox.Show(this, problems.Trim(), "Windows setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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
                if (_friendsView != null && _rail.FriendsSelected)
                {
                    _friendsView.ShowAddressChanged();
                    _friendAddressChanged = false;
                }
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
            _rail.FriendsBadge = waiting;
            _rail.Invalidate();
        }

        private void FriendMessageArrived(string id)
        {
            if ((_friendsView != null && _friendsView.IsReading(id)) || (SpatialMode && id == _spatialFriendId && AppFocused))
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
            if (!AppOnScreen)
                ShowTrayTip("mæla", text); // Who it's from, never what it says.
            else
                FlashForAttention();
        }

        /// <summary>Asks right away. Busy: no pop-up, and the request waits in your friends list.</summary>
        private void ShowFriendRequest(string id, string name)
        {
            if (_friends.Status == FriendStatus.Busy)
                return;
            FriendAlert("Friend request from " + name);
            string shortId = id.Substring(0, 4).ToUpperInvariant() + "-" + id.Substring(4, 4).ToUpperInvariant();
            FriendRequestForm form = new FriendRequestForm(name, shortId);
            form.Answered += delegate(bool accept)
            {
                if (accept)
                    _friends.AcceptRequest(id);
                else
                    _friends.Block(id);
            };
            form.Show();
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
            _callRingingOut = true;
            _nextRingbackUtc = DateTime.MinValue;
            ShowTrayOrFlash("Calling " + FriendName(id) + "\u2026");
        }

        private void ResetCallProgress()
        {
            _callPassword = null;
            _callRoomSent = false;
            _callHosting = false;
            _callVoiceJoined = false;
            _callHadCompany = false;
            _callRingingOut = false;
            _callRoomStageUtc = DateTime.MinValue;
        }

        private void ShowTrayOrFlash(string text)
        {
            if (_friendsView != null)
                _friendsView.ShowCallStatus(text);
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
                        if (accept && !AppOnScreen)
                            RestoreFromTray();
                        if (!accept || !OkToLeaveRoomForCall())
                        {
                            _friends.EndCall();
                            return;
                        }
                        // They may have hung up while you were answering that question.
                        if (!_friends.CanAcceptCall)
                        {
                            ShowTrayOrFlash("The call ended.");
                            return;
                        }
                        _callFriendId = id;
                        ResetCallProgress();
                        _friends.AcceptCall(_friendMapping != null);
                    };
                    _incomingCall.Ring += delegate { PlaySound(SoundCue.Ring); };
                    if (!AppOnScreen)
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
            if (_callRingingOut && _callRoomStageUtc == DateTime.MinValue && DateTime.UtcNow >= _nextRingbackUtc)
            {
                _nextRingbackUtc = DateTime.UtcNow.AddSeconds(4);
                PlaySound(SoundCue.Ringback);
            }
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

        // ----- Room name and art (host only) -----

        private bool IsHostingNow(SavedRoom room)
        {
            VoipEngine engine = _engine;
            return engine != null && engine.IsRunning && engine.IsHost && (Object.ReferenceEquals(room, _activeSavedRoom) || Object.ReferenceEquals(room, _selectedRoom));
        }

        private void RenameRoom(SavedRoom room)
        {
            string value = TextPrompt.Show(this, "Rename room", "Room name", room.Name);
            if (value == null)
                return;
            string name = VoipEngine.CleanRoomName(value);
            if (name.Length == 0)
                return;
            room.Name = name;
            SavedRoomStore.Save(_savedRooms);
            if (IsHostingNow(room))
            {
                _activeRoomName = name;
                _engine.SetRoomName(name);
            }
            RebuildRoomList(room);
            UpdateSelectedRoomUi();
        }

        private void ChooseRoomArt(SavedRoom room, int kind)
        {
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Title = kind == RoomArt.Icon ? "Choose a room picture" : "Choose a room banner (wide pictures work best)";
                open.Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*";
                if (open.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    byte[] pixels = kind == RoomArt.Icon
                        ? ProfilePicture.FromFile(open.FileName, RoomArt.IconSize, RoomArt.IconSize)
                        : ProfilePicture.FromFile(open.FileName, RoomArt.BannerWidth, RoomArt.BannerHeight);
                    byte[] blob = RoomArt.Pack(kind, pixels);
                    if (blob == null)
                        throw new InvalidOperationException("That picture is too detailed to share. Try a simpler one.");
                    if (kind == RoomArt.Icon)
                        SetOwnRoomArt(room, blob, room.BannerBlob);
                    else
                        SetOwnRoomArt(room, room.IconBlob, blob);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex is InvalidOperationException ? ex.Message : "That file couldn't be opened as a picture.", "Room picture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void SetOwnRoomArt(SavedRoom room, byte[] icon, byte[] banner)
        {
            room.SetArt(icon, banner);
            SavedRoomStore.Save(_savedRooms);
            if (IsHostingNow(room))
                _engine.SetRoomArt(icon, banner);
            RebuildRoomList(room);
            RefreshShell(_engine);
        }

        private VoipEngine _artEngineSeen;
        private int _artVersionSeen = -1;

        /// <summary>Members: keeps the host's art with the saved room, so its tile shows it offline too.</summary>
        private void UseHostRoomArt()
        {
            VoipEngine engine = _engine;
            if (engine == null || engine.IsHost || !engine.IsConnected || _callFriendId != null)
                return;
            int version = engine.RoomArtVersion;
            if (Object.ReferenceEquals(engine, _artEngineSeen) && version == _artVersionSeen)
                return;
            _artEngineSeen = engine;
            _artVersionSeen = version;
            SavedRoom room = _activeSavedRoom ?? _selectedRoom;
            if (room == null)
                return;
            byte[] icon = engine.GetRoomArt(RoomArt.Icon), banner = engine.GetRoomArt(RoomArt.Banner);
            if (RoomArt.VersionOf(icon) == RoomArt.VersionOf(room.IconBlob) && RoomArt.VersionOf(banner) == RoomArt.VersionOf(room.BannerBlob))
                return;
            room.SetArt(icon, banner);
            if (_savedRooms.Contains(room))
            {
                SavedRoomStore.Save(_savedRooms);
                RebuildRoomList(room);
            }
            _rail.Invalidate();
        }

        /// <summary>Rooms carry the name their host gave them, for everyone in them.</summary>
        private void UseHostRoomName()
        {
            VoipEngine engine = _engine;
            if (engine == null || engine.IsHost || !engine.IsConnected || _callFriendId != null)
                return;
            string name = engine.RoomName;
            if (name.Length == 0 || name == _activeRoomName)
                return;
            _activeRoomName = name;
            SavedRoom room = _activeSavedRoom ?? _selectedRoom;
            if (room != null && room.Name != name)
            {
                room.Name = name;
                if (_savedRooms.Contains(room))
                {
                    SavedRoomStore.Save(_savedRooms);
                    RebuildRoomList(room);
                }
            }
            UpdateSelectedRoomUi();
        }
    }

    /// <summary>Your friends on the left; on the right, the one chat you clicked.</summary>
    internal sealed class FriendsView : UserControl
    {
        private readonly FriendService _friends;
        private readonly Panel _left = new Panel();
        private readonly Panel _right = new Panel();
        private readonly FlatButton _codeButton = new FlatButton();
        private readonly FlatButton _addButton = new FlatButton();
        private readonly Label _title = new Label();
        private readonly Label _subtitle = new Label();
        private readonly Label _notice = new Label();
        private readonly FlatButton _acceptButton = new FlatButton();
        private readonly FlatButton _blockButton = new FlatButton();
        private readonly FlatButton _updateCodeButton = new FlatButton();
        private readonly RichTextBox _chat = new RichTextBox();
        private readonly FriendChatView _chatView = new FriendChatView();
        private readonly Panel _chatHost = new Panel();
        private readonly RoundField _search = new RoundField(IconKind.Search);
        private readonly FlatButton _emojiButton = new FlatButton();
        private string _viewShownFor;
        private readonly Panel _composer = new Panel();
        private readonly RoundField _input = new RoundField(IconKind.None);
        private readonly FlatButton _sendButton = new FlatButton();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private string _selectedId;
        private int _versionShown = -1;
        private bool _addressChanged;
        private string _callStatus;
        private readonly bool _firewallBlocks = !WindowsSecurity.FirewallAllows(Application.ExecutablePath);
        private bool _firewallDismissed;
        private readonly FlatButton _backupButton = new FlatButton();

        /// <summary>Goes through the app, which also opens and closes the router port.</summary>
        public Action<FriendStatus> ChangeStatus;
        public Action<string> Call;
        public Action ReturnToCall;
        private bool _callInProgress;

        /// <summary>Everyone, in order, whenever anything about them changes. The rail shows them.</summary>
        public event Action<List<FriendInfo>> PeopleChanged;

        public string SelectedId { get { return _selectedId; } }

        /// <summary>Your friend code and backup buttons, which the app shows in its sidebar.</summary>
        public Control ListPanel { get { return _left; } }
        private readonly FlatButton _callButton = new FlatButton();

        public FriendsView(FriendService friends)
        {
            _friends = friends;
            Font = Ui.Body;

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
            _search.Placeholder = "Search this chat";
            _search.Box.TextChanged += delegate
            {
                _chatView.SetFilter(_search.Box.Text);
                RefreshAll(true);
            };
            _search.Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    _search.Box.Clear();
                    e.SuppressKeyPress = true;
                }
            };
            head.Controls.Add(_search);
            head.Resize += delegate
            {
                _blockButton.Location = new Point(head.Width - 150, 14);
                _acceptButton.Location = new Point(head.Width - 244, 14);
                _updateCodeButton.Location = new Point(head.Width - 144, 14);
                _callButton.Location = new Point(head.Width - 98, 14);
                _search.SetBounds(head.Width - 256, 15, 150, 32);
                // Stop short of the buttons, so long names can't cover them.
                _title.Width = Math.Max(80, head.Width - 290);
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
                    bool goBack = _callInProgress;
                    _callStatus = null;
                    _callInProgress = false;
                    RefreshNotice(null);
                    if (goBack && ReturnToCall != null)
                        ReturnToCall();
                }
                else if (_addressChanged)
                {
                    _addressChanged = false;
                    CopyCode();
                    RefreshNotice(null);
                }
                else if (_firewallBlocks && !_firewallDismissed && _friends.Status != FriendStatus.Offline)
                {
                    _firewallDismissed = true;
                    WindowsSecurity.AllowThroughFirewall(Application.ExecutablePath);
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
            _emojiButton.Icon = IconKind.Smile;
            _emojiButton.Look = ButtonLook.Ghost;
            _emojiButton.IconSize = 20;
            _emojiButton.Click += delegate { PickEmoji(); };
            _composer.Controls.Add(_input);
            _composer.Controls.Add(_emojiButton);
            _composer.Controls.Add(_sendButton);
            _composer.Resize += delegate
            {
                _input.SetBounds(16, 10, _composer.Width - 126, 42);
                _emojiButton.SetBounds(_composer.Width - 102, 13, 36, 36);
                _sendButton.SetBounds(_composer.Width - 58, 10, 42, 42);
            };

            _chat.Dock = DockStyle.Fill;
            _chat.ReadOnly = true;
            _chat.BorderStyle = BorderStyle.None;
            _chat.DetectUrls = false; // Text from other people is only ever shown.
            _chat.TabStop = false;
            _chat.Font = Ui.Body;
            _chatHost.Dock = DockStyle.Fill;
            _chatHost.Controls.Add(_chat);
            _chatView.Dock = DockStyle.Fill;
            _chatView.ReactRequested += delegate(ulong messageId, string emoji)
            {
                if (_selectedId != null)
                    _friends.ToggleReaction(_selectedId, messageId, emoji);
            };
            _chatHost.Controls.Add(_chatView);
            _right.Controls.Add(_chatHost);
            _right.Controls.Add(_composer);
            _right.Controls.Add(_notice);
            _right.Controls.Add(head);

            Theme.Changed += ThemeChanged;
            Disposed += delegate
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
            _chatView.BackColor = t.Main;
            _chatView.ForeColor = t.Text;
            _input.Fill = t.Surface;
            _input.ApplyTheme();
            _search.Fill = t.Surface;
            _search.ApplyTheme();
            _viewShownFor = null;
            Invalidate(true);
        }

        public bool IsReading(string id)
        {
            return Visible && ContainsFocus && id == _selectedId;
        }

        /// <summary>Shows how a call is going, under the friend's name.</summary>
        public void ShowCallStatus(string text)
        {
            _callStatus = text;
            _callInProgress = false;
            RefreshAll(true);
        }

        /// <summary>A call has no tile in the rail, so this is the way back to it.</summary>
        public void ShowCallInProgress()
        {
            _callStatus = "You're in a call. Click here to go back to it.";
            _callInProgress = true;
            RefreshAll(true);
        }

        public void ShowAddressChanged()
        {
            _addressChanged = true;
            RefreshAll(true);
        }

        internal void CopyCode()
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

        internal void BackUp()
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

        internal void Restore()
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

        public void AddFriend()
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

        private void PickEmoji()
        {
            EmojiPicker picker = new EmojiPicker();
            picker.BackColor = Theme.Current.Surface;
            Point screen = _emojiButton.PointToScreen(new Point(_emojiButton.Width - picker.Width, -picker.Height - 8));
            Rectangle area = Screen.FromControl(this).WorkingArea;
            picker.Location = new Point(Math.Max(area.Left + 4, Math.Min(screen.X, area.Right - picker.Width - 4)), Math.Max(area.Top + 4, screen.Y));
            picker.Picked += delegate(EmojiEntry entry)
            {
                int caret = _input.Box.SelectionStart;
                _input.Box.Text = _input.Box.Text.Insert(caret, entry.Text);
                _input.Box.SelectionStart = caret + entry.Text.Length;
                _input.Box.Focus();
            };
            picker.Show(this);
        }

        private void SendCurrent()
        {
            if (_selectedId == null)
                return;
            if (_friends.SendMessage(_selectedId, _input.Box.Text))
                _input.Box.Clear();
        }

        public void ShowPersonMenu(string id, Control anchor, Point at)
        {
            FriendInfo person = _friends.GetPeople().Find(delegate(FriendInfo p) { return p.Id == id; });
            if (person == null)
                return;
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
            if (anchor != null)
                menu.Show(anchor, at);
            else
                menu.Show(at);
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

        public void RefreshNow()
        {
            RefreshAll(true);
        }

        public void SelectPerson(string id)
        {
            _selectedId = id;
            _friends.MarkRead(id);
            RefreshAll(true);
            if (_input.Visible)
                _input.Box.Focus(); // So messages count as read while you're here.
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
            FriendInfo selected = people.Find(delegate(FriendInfo p) { return p.Id == _selectedId; });
            _selectedId = selected == null ? null : selected.Id;
            if (PeopleChanged != null)
                PeopleChanged(people);
            ShowPerson(selected);
        }

        private void ShowPerson(FriendInfo person)
        {
            bool isFriend = person != null && person.State == FriendState.Friend;
            bool isRequest = person != null && person.State == FriendState.RequestReceived;
            bool longUnreachable = isFriend && person.Status == FriendStatus.Offline && (DateTime.UtcNow - person.LastSeenUtc).TotalDays >= 2.0;
            _composer.Visible = isFriend;
            _chat.Visible = person != null && !isFriend;
            _chatView.Visible = isFriend;
            _chatHost.Padding = isFriend ? new Padding(0) : new Padding(20, 6, 12, 6);
            _search.Visible = isFriend;
            _acceptButton.Visible = isRequest;
            _blockButton.Visible = isRequest;
            _updateCodeButton.Visible = longUnreachable;
            _callButton.Visible = isFriend && !longUnreachable && person.Status != FriendStatus.Offline;
            _title.Text = person == null ? "Friends" : person.Name;
            _subtitle.Text = person == null ? "Click a friend on the far left to open your chat." : Describe(person) + "  ·  ID " + person.ShortId;
            RefreshNotice(person);

            if (!isFriend)
            {
                _chat.Clear();
                _viewShownFor = null;
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
            else if (_firewallBlocks && !_firewallDismissed && _friends.Status != FriendStatus.Offline)
                text = "Windows Firewall may block friend requests from new people. Click to allow m\u00E6la through.";
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
            bool switched = _viewShownFor != person.Id;
            if (switched && _search.Box.Text.Length > 0)
                _search.Box.Clear();
            _viewShownFor = person.Id;
            _chatView.Show(_friends.GetChat(person.Id), person.Name, "You", switched);
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
            // In the corner, so it can never sit on top of a question in the middle of the screen.
            StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
            AddHeading(name, "is calling you");
            FlatButton decline = Button("Decline", DialogResult.None);
            FlatButton accept = Button("Accept", DialogResult.None);
            decline.Click += delegate { Finish(false); };
            accept.Click += delegate { Finish(true); };
            AddButtons(decline, accept);
            CancelButton = null;
            AcceptButton = null; // A stray Enter mustn't answer a call.
            FormClosing += delegate { Finish(false); };
            _ring.Interval = 3000;
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
            Hide(); // First: answering can open a question, which would lock this window while it covers it.
            if (Answered != null)
                Answered(accept);
            BeginInvoke((MethodInvoker)delegate { base.Close(); });
        }
    }

    /// <summary>Someone wants to be your friend. Closing it leaves the request in your friends list.</summary>
    internal sealed class FriendRequestForm : ThemedDialog
    {
        public event Action<bool> Answered;

        public FriendRequestForm(string name, string shortId) : base("Friend request", 400, 200)
        {
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
            AddHeading(name, "wants to be your friend  \u00B7  ID " + shortId);
            FlatButton decline = Button(String.Empty, DialogResult.None);
            FlatButton accept = Button(String.Empty, DialogResult.None);
            decline.Icon = IconKind.Close;
            accept.Icon = IconKind.Check;
            decline.Click += delegate { Finish(false); };
            accept.Click += delegate { Finish(true); };
            AddButtons(decline, accept);
            CancelButton = null;
            AcceptButton = null; // A stray Enter mustn't accept anyone.
            ToolTip tips = new ToolTip();
            tips.SetToolTip(accept, "Accept");
            tips.SetToolTip(decline, "Decline and block");
        }

        private void Finish(bool accept)
        {
            if (Answered != null)
                Answered(accept);
            Close();
        }
    }

    /// <summary>The one-time offer. Windows asks you to approve each one you choose.</summary>
    internal sealed class WindowsSetupDialog : ThemedDialog
    {
        private readonly ToggleSwitch _firewall;
        private readonly ToggleSwitch _defender;

        public WindowsSetupDialog(bool firewallAlreadyAllows) : base("Set up Windows", 560, firewallAlreadyAllows ? 300 : 360)
        {
            AddHeading("Set up Windows for m\u00E6la?", "Both are optional. Windows asks you to approve each one you choose.");
            if (!firewallAlreadyAllows)
                _firewall = AddToggle("Allow through Windows Firewall", "Lets friends and room members reach you. Without it, requests may not arrive.", true);
            _defender = AddToggle("Exclude m\u00E6la from Defender scans", "For if Defender wrongly flags it. Excluded files aren't scanned.", false);
            Cursor_Y += 4;
            AddText("You can change either one any time in Settings > Network.", Theme.Current.Text, ClientSize.Width - Margin_X * 2);
            AddButtons(Button("Not now", DialogResult.Cancel), Button("Continue", DialogResult.OK));
        }

        public bool AllowFirewall { get { return _firewall != null && _firewall.Checked; } }
        public bool ExcludeFromDefender { get { return _defender.Checked; } }
    }
}
