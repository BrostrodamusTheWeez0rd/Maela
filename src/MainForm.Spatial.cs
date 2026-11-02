using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>
    /// The bridge to the Spatial window, which runs on its own thread. The UI timer sends it copies
    /// of the real state, and everything it asks for is carried out here on the main UI thread,
    /// through the same code the normal window uses.
    /// </summary>
    internal sealed partial class MainForm
    {
        private string _spatialFriendId;
        private bool _spatialBroken; // The Spatial window failed: Classic takes over until the next start.
        private readonly Dictionary<Image, System.Windows.Media.ImageSource> _spatialPictures = new Dictionary<Image, System.Windows.Media.ImageSource>();

        /// <summary>Spatial is the app's only window; this form stays hidden and keeps running it.</summary>
        private bool SpatialMode { get { return _preferences != null && !_preferences.ClassicLayout && !_spatialBroken; } }

        private bool AppOnScreen { get { return SpatialMode ? SpatialTest.WindowOnScreen : Visible; } }

        private bool AppFocused { get { return SpatialMode ? SpatialTest.WindowActive : ContainsFocus; } }

        protected override void SetVisibleCore(bool value)
        {
            if (value && SpatialMode)
            {
                if (!IsHandleCreated)
                    CreateHandle();
                base.SetVisibleCore(false);
                SpatialTest.Open(this);
                BeginInvoke((MethodInvoker)FirstShown);
                return;
            }
            base.SetVisibleCore(value);
        }

        /// <summary>Settings changed the layout: swap windows, staying in any room or call.</summary>
        private void ApplyLayoutChoice()
        {
            if (SpatialMode)
            {
                Hide();
                SpatialTest.Open(this);
            }
            else if (SpatialTest.IsOpen)
            {
                ReleaseSpatialOwner();
                SpatialTest.CloseWindow();
                _spatialFriendId = null;
                Show();
                Activate();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        private void SetNativeOwner(IntPtr owner)
        {
            const int GWLP_HWNDPARENT = -8;
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(Handle, GWLP_HWNDPARENT, owner);
            else
                SetWindowLong32(Handle, GWLP_HWNDPARENT, owner.ToInt32());
        }

        private void ReleaseSpatialOwner()
        {
            SetNativeOwner(IntPtr.Zero);
        }

        /// <summary>This hidden form owns every dialog, so owning it keeps them above the Spatial window.</summary>
        internal void SpatialWindowReady(IntPtr window)
        {
            SpatialDo(delegate { if (SpatialMode) SetNativeOwner(window); });
        }

        /// <summary>Dialogs centre on this form, so it shadows the Spatial window's position.</summary>
        internal void SpatialWindowMoved(int x, int y, int width, int height)
        {
            SpatialDo(delegate
            {
                if (SpatialMode)
                    SetBounds(x, y, width, height);
            });
        }

        /// <summary>The X on the Spatial window does what the X on Classic does: tray or quit.</summary>
        internal void SpatialCloseRequested()
        {
            SpatialDo(delegate { Close(); });
        }

        internal void SpatialWindowFailed()
        {
            SpatialDo(delegate
            {
                if (!SpatialMode)
                    return;
                _spatialBroken = true;
                ReleaseSpatialOwner();
                Show();
                Activate();
            });
        }

        private void PushSpatialSnapshot()
        {
            if (!SpatialTest.IsOpen)
                return;
            SpatialTest.Send(BuildSpatialSnapshot());
        }

        private System.Windows.Media.ImageSource SpatialPicture(Image image)
        {
            if (image == null)
                return null;
            System.Windows.Media.ImageSource source;
            if (_spatialPictures.TryGetValue(image, out source))
                return source;
            if (_spatialPictures.Count > 128)
                _spatialPictures.Clear();
            try
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    lock (image)
                        image.Save(stream, ImageFormat.Png);
                    stream.Position = 0;
                    System.Windows.Media.Imaging.BitmapImage bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    source = bitmap;
                }
            }
            catch
            {
                source = null;
            }
            _spatialPictures[image] = source;
            return source;
        }

        private SpatialSnapshot BuildSpatialSnapshot()
        {
            VoipEngine engine = _engine;
            bool running = engine != null && engine.IsRunning;
            bool connected = running && engine.IsConnected;
            SpatialSnapshot s = new SpatialSnapshot();
            s.MyName = _preferences.DisplayName;
            s.ThemeId = Theme.Current.Id;
            s.MyPicture = SpatialPicture(MyPicture());
            s.MyStatus = _friends == null ? "Offline" : StatusLabel(_friends.Status);
            s.Muted = _muted;
            s.Deafened = _deafened;
            s.Noise = _preferences.NoiseSuppression;
            s.Running = running;
            s.Connected = connected;
            s.IsHost = running && engine.IsHost;
            s.InVoice = connected && engine.IsInVoice;
            s.Sharing = connected && engine.IsSharingScreen;
            s.Ping = connected ? engine.PingMs : 0;
            s.RoomName = running ? _activeRoomName : String.Empty;
            s.Message = running && !connected ? engine.LastMessage : String.Empty;
            s.LocalChannel = connected ? engine.LocalChannelId : 0;
            s.ChatChannel = connected ? engine.ChatChannelId : 0;

            StringBuilder structure = new StringBuilder();
            StringBuilder talk = new StringBuilder();
            LoadCircles();
            s.RoomCircles = CopyCircles(_roomCircles);
            s.FriendCircles = CopyCircles(_friendCircles);
            structure.Append(FormatCircles(_roomCircles)).Append(FormatCircles(_friendCircles));
            bool sawActive = false;
            foreach (SavedRoom room in _savedRooms)
            {
                SpatialRoom r = new SpatialRoom();
                r.Key = room;
                r.Name = room.Name;
                r.Owned = room.Owned;
                r.Circle = room.Circle;
                r.Active = running && Object.ReferenceEquals(room, _activeSavedRoom);
                r.Online = r.Active || room.Presence == RoomPresence.Online;
                r.Checking = room.Presence == RoomPresence.Checking;
                r.Members = room.MemberCount;
                r.Max = room.MaxMembers;
                r.Picture = SpatialPicture(room.IconImage);
                sawActive |= r.Active;
                s.Rooms.Add(r);
            }
            if (running && !sawActive)
            {
                // A room that isn't saved, such as the one behind a friend call.
                SpatialRoom r = new SpatialRoom();
                r.Key = "live";
                r.Name = String.IsNullOrEmpty(_activeRoomName) ? "Room" : _activeRoomName;
                r.Active = true;
                r.Online = true;
                r.Members = connected ? engine.MemberCount : 0;
                r.Max = VoipEngine.MaxRoomMembers;
                s.Rooms.Add(r);
            }
            foreach (SpatialRoom r in s.Rooms)
                structure.Append(r.Name).Append(r.Circle).Append(r.Active).Append(r.Online).Append(r.Checking).Append(r.Members).Append('/').Append(r.Max).Append(r.Picture != null).Append(';');

            if (_friends != null)
            {
                foreach (FriendInfo person in _friends.GetPeople())
                {
                    if (person.State == FriendState.Blocked)
                        continue;
                    SpatialFriend f = new SpatialFriend();
                    f.Id = person.Id;
                    f.Name = person.Name;
                    f.Status = person.State == FriendState.RequestSent ? "Request sent" : person.State == FriendState.RequestReceived ? "Wants to be friends" : StatusLabel(person.Status);
                    f.IsFriend = person.State == FriendState.Friend;
                    f.WantsToBeFriends = person.State == FriendState.RequestReceived;
                    f.Unread = person.Id == _spatialFriendId ? 0 : person.Unread;
                    _friendCircleOf.TryGetValue(person.Id, out f.Circle);
                    s.Friends.Add(f);
                    structure.Append(f.Name).Append(f.Circle).Append(f.Status).Append(f.Unread).Append(';');
                }
                s.Friends.Sort(delegate(SpatialFriend a, SpatialFriend b) { return String.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            }

            if (connected)
            {
                RoomMemberInfo[] members = engine.GetMembers();
                foreach (VoiceChannelInfo channel in engine.GetChannels())
                {
                    if (channel.Id <= 0)
                        continue;
                    SpatialRoomChannel c = new SpatialRoomChannel();
                    c.Id = channel.Id;
                    c.Name = channel.Name;
                    c.IsText = channel.IsText;
                    foreach (RoomMemberInfo member in members)
                    {
                        if (member.ChannelId != channel.Id)
                            continue;
                        SpatialPerson p = new SpatialPerson();
                        p.Id = member.Id;
                        p.Name = member.Name;
                        p.IsLocal = member.IsLocal;
                        p.Speaking = member.IsSpeaking;
                        p.Muted = member.Muted;
                        p.Deafened = member.Deafened;
                        p.ChannelId = member.ChannelId;
                        p.Live = member.IsLocal ? engine.IsSharingScreen : engine.GetLiveStream(member.Id) != null;
                        p.Picture = SpatialPicture(member.IsLocal ? MyPicture() : AvatarImage(member.Id));
                        c.People.Add(p);
                        if (p.Speaking)
                            talk.Append(p.Id).Append(',');
                        structure.Append(p.Id).Append(p.Name).Append(p.Muted).Append(p.Deafened).Append(p.Live).Append(p.Picture != null);
                    }
                    s.Channels.Add(c);
                    structure.Append('#').Append(c.Id).Append(c.Name).Append(c.IsText).Append(';');
                }
                structure.Append("voice").Append(s.LocalChannel);
                if (s.ChatChannel > 0)
                {
                    s.ChatKey = engine.GetHashCode() + "|" + s.ChatChannel + "|" + engine.ChatVersion;
                    ChatMessage[] messages = engine.GetChat(s.ChatChannel);
                    Dictionary<uint, List<ReactionSummary>> reactions = VoipEngine.SummarizeReactions(messages);
                    foreach (ChatMessage message in messages)
                    {
                        if (message.IsReaction)
                            continue;
                        SpatialLine line = new SpatialLine();
                        line.Who = message.SenderName;
                        line.Picture = SpatialPicture(message.IsLocal ? MyPicture() : AvatarImage(message.SenderId));
                        line.Text = message.Text;
                        line.TimeUtc = message.TimeUtc;
                        line.Note = message.Failed ? "Not sent" : message.Pending ? "Sending…" : null;
                        if (!message.Pending && !message.Failed && message.Sequence != 0)
                        {
                            line.ReactKey = message.Sequence;
                            List<ReactionSummary> summary;
                            if (reactions.TryGetValue(message.Sequence, out summary))
                                foreach (ReactionSummary r in summary)
                                    line.Reactions.Add(SpatialReactionFor(r.Emoji, r.Count, r.Mine));
                        }
                        s.Chat.Add(line);
                    }
                }
            }

            if (_friends != null && _spatialFriendId != null)
            {
                s.FriendChatId = _spatialFriendId;
                s.FriendChatKey = _spatialFriendId + "|" + _friends.Version;
                FriendInfo person = _friends.GetPeople().Find(delegate(FriendInfo p) { return p.Id == _spatialFriendId; });
                string theirName = person == null ? String.Empty : person.Name;
                List<FriendChatEntry> entries = _friends.GetChat(_spatialFriendId);
                Dictionary<ulong, List<FriendReaction>> reactions = FriendChat.Summarize(entries);
                foreach (FriendChatEntry entry in entries)
                {
                    if (entry.ReactionTo != 0)
                        continue;
                    SpatialLine line = new SpatialLine();
                    line.Who = entry.Mine ? _preferences.DisplayName : theirName;
                    line.Picture = entry.Mine ? s.MyPicture : null;
                    line.Text = entry.Text;
                    line.TimeUtc = entry.TimeUtc;
                    line.Note = entry.Mine && !entry.Delivered ? "Not delivered yet" : null;
                    line.ReactKey = entry.MessageId;
                    List<FriendReaction> summary;
                    if (entry.MessageId != 0 && reactions.TryGetValue(entry.MessageId, out summary))
                        foreach (FriendReaction r in summary)
                            line.Reactions.Add(SpatialReactionFor(r.Emoji, r.Count, r.Mine));
                    s.FriendChat.Add(line);
                }
                if (person != null && person.Unread != 0)
                    _friends.MarkRead(_spatialFriendId);
            }

            s.StructureKey = structure.ToString();
            s.TalkKey = talk.ToString();
            s.DockKey = s.MyName + s.MyStatus + s.Muted + s.Deafened + s.Noise + s.Running + s.Connected + s.InVoice + s.Sharing + s.Ping + s.LocalChannel + s.RoomName + s.Message + (s.MyPicture != null);
            return s;
        }

        /// <summary>WPF here can't draw colour emoji, so reactions carry the emoji pack's picture.</summary>
        private SpatialReaction SpatialReactionFor(string emoji, int count, bool mine)
        {
            SpatialReaction reaction = new SpatialReaction();
            reaction.Emoji = emoji;
            reaction.Count = count;
            reaction.Mine = mine;
            EmojiEntry entry = EmojiPack.Find(emoji);
            if (entry != null)
                reaction.Picture = SpatialPicture(EmojiPack.GetImage(entry));
            return reaction;
        }

        private void ToggleReaction(string friendId, ulong key, string emoji)
        {
            if (friendId != null)
            {
                if (_friends != null)
                    _friends.ToggleReaction(friendId, key, emoji);
            }
            else if (_engine != null)
                _engine.ToggleReaction((uint)key, emoji);
        }

        /// <summary>A chip under a message: the same emoji again turns your reaction off.</summary>
        internal void SpatialToggleReaction(string friendId, ulong key, string emoji)
        {
            SpatialDo(delegate { ToggleReaction(friendId, key, emoji); });
        }

        /// <summary>Right-click a message: the quick reactions, or any emoji.</summary>
        internal void SpatialReactMenu(string friendId, ulong key)
        {
            SpatialDialog(delegate
            {
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new ThemedMenuRenderer();
                foreach (string quick in QuickReactions)
                {
                    EmojiEntry entry = EmojiPack.Find(quick);
                    if (entry == null)
                        continue;
                    ToolStripMenuItem item = new ToolStripMenuItem("React " + entry.Text, EmojiPack.GetImage(entry));
                    item.Click += delegate { ToggleReaction(friendId, key, entry.Text); };
                    menu.Items.Add(item);
                }
                ToolStripMenuItem more = new ToolStripMenuItem("More reactions…");
                more.Click += delegate { SpatialPickEmoji(delegate(string text) { SpatialToggleReaction(friendId, key, text); }); };
                menu.Items.Add(more);
                menu.Closed += delegate { BeginInvoke((MethodInvoker)menu.Dispose); };
                menu.Show(Cursor.Position);
            });
        }

        private void SpatialDo(MethodInvoker action)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Closing down.
            }
        }

        /// <summary>For anything that opens a dialog. If one is already open, it comes forward instead.</summary>
        private void SpatialDialog(MethodInvoker action)
        {
            SpatialDo(delegate
            {
                foreach (Form form in Application.OpenForms)
                {
                    if (form.Modal)
                    {
                        form.Activate();
                        return;
                    }
                }
                action();
            });
        }

        internal void SpatialRoomMenu(object key)
        {
            SavedRoom room = key as SavedRoom;
            if (room == null)
                return;
            SpatialDialog(delegate
            {
                SelectRoomInList(room);
                UpdateSelectedRoomUi();
                _roomMenu.Show(Cursor.Position);
            });
        }

        internal void SpatialChannelMenu(int id)
        {
            SpatialDialog(delegate
            {
                VoipEngine engine = _engine;
                if (engine == null)
                    return;
                foreach (VoiceChannelInfo channel in engine.GetChannels())
                {
                    if (channel.Id == id)
                    {
                        _contextChannel = channel;
                        _channelMenu.Show(Cursor.Position);
                        return;
                    }
                }
            });
        }

        internal void SpatialMemberMenu(uint id)
        {
            SpatialDialog(delegate
            {
                _contextMember = FindMember(id);
                if (_contextMember != null)
                    _memberMenu.Show(Cursor.Position);
            });
        }

        internal void SpatialAddChannel()
        {
            SpatialDialog(delegate
            {
                if (_engine == null || !_engine.IsHost)
                    return;
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new ThemedMenuRenderer();
                menu.ShowImageMargin = false;
                menu.Items.Add("Voice channel", null, delegate { CreateChannelPrompt(false); });
                menu.Items.Add("Chat-only channel", null, delegate { CreateChannelPrompt(true); });
                menu.Closed += delegate { BeginInvoke((MethodInvoker)menu.Dispose); };
                menu.Show(Cursor.Position);
            });
        }

        // ----- Circles. Saved as "id|name|hasPosition|x|y;...", with the name escaped. -----

        private List<SpatialCircle> _roomCircles, _friendCircles;
        private Dictionary<string, string> _friendCircleOf;

        private static List<SpatialCircle> ParseCircles(string saved, string mainName)
        {
            List<SpatialCircle> circles = new List<SpatialCircle>();
            foreach (string entry in (saved ?? String.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split('|');
                if (parts.Length < 5)
                    continue;
                SpatialCircle circle = new SpatialCircle();
                circle.Id = parts[0];
                try { circle.Name = Uri.UnescapeDataString(parts[1]); }
                catch { circle.Name = parts[1]; }
                circle.HasPosition = parts[2] == "1";
                Double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out circle.X);
                Double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out circle.Y);
                circles.Add(circle);
            }
            if (!circles.Exists(delegate(SpatialCircle c) { return c.Id == SpatialCircle.MainId; }))
                circles.Insert(0, new SpatialCircle { Id = SpatialCircle.MainId, Name = mainName });
            if (!circles.Exists(delegate(SpatialCircle c) { return c.Id == SpatialCircle.FavouritesId; }))
                circles.Insert(0, new SpatialCircle { Id = SpatialCircle.FavouritesId, Name = "Favourites" });
            return circles;
        }

        private static string FormatCircles(List<SpatialCircle> circles)
        {
            List<string> entries = new List<string>();
            foreach (SpatialCircle c in circles)
                entries.Add(c.Id + "|" + Uri.EscapeDataString(c.Name) + "|" + (c.HasPosition ? "1" : "0") + "|" + c.X.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "|" + c.Y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            return String.Join(";", entries.ToArray());
        }

        private void LoadCircles()
        {
            if (_roomCircles != null)
                return;
            _roomCircles = ParseCircles(_preferences.RoomCircles, "Rooms");
            _friendCircles = ParseCircles(_preferences.FriendCircles, "Friends");
            _friendCircleOf = new Dictionary<string, string>();
            foreach (string pair in (_preferences.FriendCircleOf ?? String.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = pair.IndexOf(':');
                if (colon > 0)
                    _friendCircleOf[pair.Substring(0, colon)] = pair.Substring(colon + 1);
            }
        }

        private void SaveCircles()
        {
            _preferences.RoomCircles = FormatCircles(_roomCircles);
            _preferences.FriendCircles = FormatCircles(_friendCircles);
            List<string> pairs = new List<string>();
            foreach (KeyValuePair<string, string> pair in _friendCircleOf)
                pairs.Add(pair.Key + ":" + pair.Value);
            _preferences.FriendCircleOf = String.Join(",", pairs.ToArray());
            PreferencesStore.Save(_preferences);
        }

        private static List<SpatialCircle> CopyCircles(List<SpatialCircle> circles)
        {
            List<SpatialCircle> copy = new List<SpatialCircle>();
            foreach (SpatialCircle c in circles)
                copy.Add(new SpatialCircle { Id = c.Id, Name = c.Name, HasPosition = c.HasPosition, X = c.X, Y = c.Y });
            return copy;
        }

        internal void SpatialMoveRoomToCircle(object key, string circle)
        {
            SavedRoom room = key as SavedRoom;
            if (room == null)
                return;
            SpatialDo(delegate
            {
                room.Circle = circle == SpatialCircle.MainId ? String.Empty : circle;
                SavedRoomStore.Save(_savedRooms);
            });
        }

        internal void SpatialMoveFriendToCircle(string id, string circle)
        {
            SpatialDo(delegate
            {
                LoadCircles();
                if (circle == SpatialCircle.MainId)
                    _friendCircleOf.Remove(id);
                else
                    _friendCircleOf[id] = circle;
                SaveCircles();
            });
        }

        internal void SpatialPlaceCircle(bool rooms, string id, double x, double y)
        {
            SpatialDo(delegate
            {
                LoadCircles();
                SpatialCircle circle = (rooms ? _roomCircles : _friendCircles).Find(delegate(SpatialCircle c) { return c.Id == id; });
                if (circle == null)
                    return;
                circle.HasPosition = true;
                circle.X = x;
                circle.Y = y;
                SaveCircles();
            });
        }

        internal void SpatialNewCircle(bool rooms)
        {
            SpatialDialog(delegate
            {
                string name = TextPrompt.Show(this, "New circle", "Circle name", String.Empty);
                if (String.IsNullOrWhiteSpace(name))
                    return;
                LoadCircles();
                SpatialCircle circle = new SpatialCircle();
                circle.Id = "c" + DateTime.UtcNow.Ticks.ToString("x");
                circle.Name = name.Trim();
                (rooms ? _roomCircles : _friendCircles).Add(circle);
                SaveCircles();
            });
        }

        internal void SpatialCircleMenu(bool rooms, string id)
        {
            SpatialDialog(delegate
            {
                LoadCircles();
                List<SpatialCircle> circles = rooms ? _roomCircles : _friendCircles;
                SpatialCircle circle = circles.Find(delegate(SpatialCircle c) { return c.Id == id; });
                if (circle == null)
                    return;
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new ThemedMenuRenderer();
                menu.ShowImageMargin = false;
                menu.Items.Add("Rename circle", null, delegate
                {
                    string name = TextPrompt.Show(this, "Rename circle", "Circle name", circle.Name);
                    if (String.IsNullOrWhiteSpace(name))
                        return;
                    circle.Name = name.Trim();
                    SaveCircles();
                });
                if (!circle.Protected)
                {
                    menu.Items.Add("Delete circle", null, delegate
                    {
                        circles.Remove(circle);
                        if (rooms)
                        {
                            foreach (SavedRoom room in _savedRooms)
                                if (room.Circle == id) room.Circle = String.Empty;
                            SavedRoomStore.Save(_savedRooms);
                        }
                        else
                        {
                            foreach (string friend in new List<string>(_friendCircleOf.Keys))
                                if (_friendCircleOf[friend] == id) _friendCircleOf.Remove(friend);
                        }
                        SaveCircles();
                    });
                }
                menu.Closed += delegate { BeginInvoke((MethodInvoker)menu.Dispose); };
                menu.Show(Cursor.Position);
            });
        }

        internal void SpatialCopyRoomCode() { SpatialDo(delegate { CopyInviteClicked(null, EventArgs.Empty); }); }
        internal void SpatialPreviewShare() { SpatialDialog(delegate { ShowSharePreview(); }); }

        internal void SpatialAddFriend() { SpatialDialog(delegate { if (_friendsView != null) _friendsView.AddFriend(); }); }
        internal void SpatialCopyFriendCode() { SpatialDialog(delegate { if (_friendsView != null) _friendsView.CopyCode(); }); }
        internal void SpatialFriendMenu(string id) { SpatialDialog(delegate { if (_friendsView != null) _friendsView.ShowPersonMenu(id, null, Cursor.Position); }); }
        internal void SpatialAcceptFriend(string id) { SpatialDo(delegate { if (_friends != null) _friends.AcceptRequest(id); }); }
        internal void SpatialBlockFriend(string id) { SpatialDo(delegate { if (_friends != null) _friends.Block(id); }); }

        internal void SpatialBackupMenu()
        {
            SpatialDialog(delegate
            {
                if (_friendsView == null)
                    return;
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new ThemedMenuRenderer();
                menu.Items.Add("Back up my account…", null, delegate { _friendsView.BackUp(); });
                menu.Items.Add("Restore from a backup…", null, delegate { _friendsView.Restore(); });
                menu.Closed += delegate { BeginInvoke((MethodInvoker)menu.Dispose); };
                menu.Show(Cursor.Position);
            });
        }

        /// <summary>The emoji chooser, handing the pick back to the Spatial window's thread.</summary>
        internal void SpatialPickEmoji(Action<string> picked)
        {
            SpatialDialog(delegate
            {
                EmojiPicker picker = new EmojiPicker();
                picker.BackColor = Theme.Current.Surface;
                Point at = Cursor.Position;
                Rectangle area = Screen.FromPoint(at).WorkingArea;
                picker.Location = new Point(Math.Max(area.Left + 4, Math.Min(at.X - picker.Width, area.Right - picker.Width - 4)), Math.Max(area.Top + 4, at.Y - picker.Height - 8));
                picker.Picked += delegate(EmojiEntry entry) { picked(entry.Text); };
                picker.Show(this);
            });
        }

        internal void SpatialEnterRoom(object key)
        {
            SavedRoom room = key as SavedRoom;
            if (room == null)
                return;
            SpatialDialog(delegate
            {
                RoomClickedByUser(room);
                if (_engine == null || !_engine.IsRunning)
                    StartSelectedRoom();
            });
        }

        internal void SpatialAddRoom(bool host)
        {
            SpatialDialog(delegate
            {
                if (_engine != null && _engine.IsRunning)
                {
                    MessageBox.Show(this, "Leave your current room first.", "mæla", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (host)
                    ShowHostRoomDialog(null);
                else
                    ShowJoinRoomDialog();
            });
        }

        internal void SpatialViewChannel(int id)
        {
            SpatialDo(delegate { if (_engine != null && _engine.IsConnected) _engine.ViewChannel(id); });
        }

        internal void SpatialJoinVoice(int id)
        {
            SpatialDo(delegate
            {
                if (_engine != null && _engine.IsConnected && id != _engine.LocalChannelId && !_engine.IsTextChannel(id))
                    _engine.ChangeChannel(id);
            });
        }

        internal void SpatialLeaveVoice() { SpatialDo(delegate { LeaveClicked(null, EventArgs.Empty); }); }
        internal void SpatialDisconnect() { SpatialDialog(delegate { DisconnectClicked(null, EventArgs.Empty); }); }
        internal void SpatialMute() { SpatialDo(delegate { MuteButtonClicked(null, EventArgs.Empty); }); }
        internal void SpatialDeafen() { SpatialDo(delegate { DeafenButtonClicked(null, EventArgs.Empty); }); }
        internal void SpatialNoise() { SpatialDo(delegate { ToggleNoiseSuppression(); }); }
        internal void SpatialShare() { SpatialDialog(delegate { ShareScreenClicked(null, EventArgs.Empty); }); }
        internal void SpatialChangeShare() { SpatialDialog(delegate { ChangeSharingClicked(); }); }
        internal void SpatialStopShare() { SpatialDo(delegate { StopSharing(); RefreshShell(_engine); }); }
        internal void SpatialSettings() { SpatialDialog(delegate { SettingsClicked(null, EventArgs.Empty); }); }
        internal void SpatialMyProfile() { SpatialDialog(delegate { ShowMyProfileCard(); }); }

        internal void SpatialSendRoomChat(int channel, string text)
        {
            SpatialDo(delegate
            {
                if (_engine == null || !_engine.IsConnected)
                    return;
                _engine.ViewChannel(channel);
                _engine.SendChat(text);
            });
        }

        internal void SpatialOpenFriend(string id)
        {
            SpatialDo(delegate
            {
                _spatialFriendId = id;
                if (id != null && _friends != null)
                    _friends.MarkRead(id);
            });
        }

        internal void SpatialSendFriend(string id, string text)
        {
            SpatialDo(delegate { if (_friends != null) _friends.SendMessage(id, text); });
        }

        internal void SpatialCallFriend(string id)
        {
            SpatialDialog(delegate { StartFriendCall(id); });
        }

        /// <summary>Someone live in your channel opens their stream; anyone else opens their card.</summary>
        internal void SpatialPersonClicked(uint id)
        {
            SpatialDialog(delegate
            {
                VoipEngine engine = _engine;
                if (engine == null)
                    return;
                RoomMemberInfo member = FindMember(id);
                if (member == null)
                    return;
                if (!member.IsLocal && member.ChannelId == engine.LocalChannelId && engine.GetLiveStream(member.Id) != null)
                    WatchStream(member);
                else
                    ShowProfileCard(member, Cursor.Position);
            });
        }
    }
}
