using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        private bool _graphicsChecked;
        private string _graphicsCard; // The graphics card's name, or null if there isn't one.

        private string GraphicsCard
        {
            get
            {
                if (!_graphicsChecked)
                {
                    _graphicsChecked = true;
                    string name;
                    _graphicsCard = GraphicsCheck.HasGraphicsCard(out name) ? (name ?? String.Empty) : null;
                }
                return _graphicsCard;
            }
        }

        /// <summary>
        /// Until someone picks a look themselves, Spatial goes to PCs with a graphics card and
        /// Lightweight to the rest. Checked on every start until then, in case a card is added.
        /// </summary>
        private void ChooseLayoutForGraphics()
        {
            if (_preferences.LayoutChosen)
                return;
            _preferences.ClassicLayout = GraphicsCard == null;
        }

        private const string WhatsNewId = "0.13"; // Change it to show a new announcement after a later update.

        /// <summary>
        /// The announcement for this update, once. New installs have just seen setup, so they skip
        /// it. Whatever look is picked in it counts as their own choice from then on.
        /// </summary>
        private void ShowWhatsNewOnce(bool newInstall)
        {
            if (_preferences.WhatsNewSeen == WhatsNewId)
                return;
            _preferences.WhatsNewSeen = WhatsNewId;
            PreferencesStore.Save(_preferences);
            if (newInstall)
                return;
            // Give the Spatial window a moment to open, so this appears over it rather than behind.
            System.Windows.Forms.Timer wait = new System.Windows.Forms.Timer();
            wait.Interval = 1200;
            wait.Tick += delegate
            {
                wait.Stop();
                wait.Dispose();
                bool spatial;
                using (WhatsNewDialog announcement = new WhatsNewDialog(!_preferences.ClassicLayout, GraphicsCard))
                {
                    announcement.ShowDialog(this);
                    spatial = announcement.SpatialChosen;
                }
                _preferences.ClassicLayout = !spatial;
                _preferences.LayoutChosen = true;
                PreferencesStore.Save(_preferences);
                ApplyLayoutChoice();
            };
            wait.Start();
        }

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
            s.FrameRate = _preferences.FrameRate;
            s.ReadingFont = _preferences.ReadingFont;
            s.TextScale = _preferences.TextScale;
            s.ReduceMotion = _preferences.ReduceMotion;
            s.MyPicture = SpatialPicture(MyPicture());
            if (_preferences.ShowDecos)
            {
                s.MyDeco = _preferences.PictureDeco;
                s.MyDecoA = _preferences.BannerColourA;
                s.MyDecoB = _preferences.BannerColourB;
            }
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
            s.Circles = CopyCircles(_circles);
            structure.Append(FormatCircles(_circles));
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
                r.Placed = room.Placed;
                r.PlaceX = room.PlaceX;
                r.PlaceY = room.PlaceY;
                r.Size = room.Size;
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
                structure.Append(r.Name).Append(r.Circle).Append(r.Active).Append(r.Online).Append(r.Checking).Append(r.Members).Append('/').Append(r.Max).Append(r.Picture != null).Append(SpatialPlace.Format(r.Placed, r.PlaceX, r.PlaceY, r.Size)).Append(';');

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
                    string place;
                    if (_friendPlaces.TryGetValue(person.Id, out place))
                        SpatialPlace.Parse(place, out f.Placed, out f.PlaceX, out f.PlaceY, out f.Size);
                    s.Friends.Add(f);
                    structure.Append(f.Name).Append(f.Circle).Append(f.Status).Append(f.Unread).Append(place).Append(';');
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
                    ChannelLayout layout = engine.GetChannelLayout(channel.Id);
                    if (layout != null)
                    {
                        c.HasLayout = true;
                        c.Angle = layout.Angle;
                        c.Reach = layout.Reach;
                        c.Size = layout.Size;
                    }
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
                        MemberExtras extras = _preferences.ShowDecos ? ExtrasFor(member) : null;
                        if (extras != null)
                        {
                            p.Deco = extras.Deco;
                            p.DecoA = extras.ColourA;
                            p.DecoB = extras.ColourB;
                        }
                        c.People.Add(p);
                        if (p.Speaking)
                            talk.Append(p.Id).Append(',');
                        structure.Append(p.Id).Append(p.Name).Append(p.Muted).Append(p.Deafened).Append(p.Live).Append(p.Picture != null).Append(p.Deco).Append(p.DecoA).Append(p.DecoB);
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
                        FillAttachments(line, null);
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
                    FillAttachments(line, _spatialFriendId);
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
            s.LayoutKey = engine == null ? 0 : engine.ChannelLayoutsVersion;
            s.TalkKey = talk.ToString();
            s.DockKey = s.MyName + s.MyStatus + s.Muted + s.Deafened + s.Noise + s.Running + s.Connected + s.InVoice + s.Sharing + s.Ping + s.LocalChannel + s.RoomName + s.Message + (s.MyPicture != null) + s.MyDeco + s.MyDecoA + s.MyDecoB;
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
                ShowSpatialMenu(menu);
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
                CancelEventArgs opening = new CancelEventArgs();
                RoomMenuOpening(_roomMenu, opening);
                if (opening.Cancel)
                    return;
                if (Math.Abs(room.Size - 1) > 0.005)
                {
                    _roomMenu.Items.Add(new ToolStripSeparator());
                    _roomMenu.Items.Add("Reset size", null, delegate
                    {
                        room.Size = 1;
                        SavedRoomStore.Save(_savedRooms);
                    });
                }
                ShowSpatialMenu(_roomMenu);
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
                        CancelEventArgs opening = new CancelEventArgs();
                        ChannelMenuOpening(_channelMenu, opening);
                        if (!opening.Cancel)
                            ShowSpatialMenu(_channelMenu);
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
                CancelEventArgs opening = new CancelEventArgs();
                MemberMenuOpening(_memberMenu, opening);
                if (_contextMember != null && !opening.Cancel)
                    ShowSpatialMenu(_memberMenu);
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
                ShowSpatialMenu(menu);
            });
        }

        // ----- Circles. Saved as "id|name|hasPosition|x|y;...", with the name escaped. -----

        // One list for rooms and friends, saved in RoomCircles. Friends' circles used to have their
        // own list (FriendCircles); its circles are moved into this one the first time it loads.
        private List<SpatialCircle> _circles;
        private Dictionary<string, string> _friendCircleOf;
        private Dictionary<string, string> _friendPlaces; // id -> "x,y,size", see SpatialPlace.

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
                if (!(Math.Abs(circle.X) <= 1000000 && Math.Abs(circle.Y) <= 1000000))
                {
                    circle.HasPosition = false; // "NaN" or "Infinity": placed afresh.
                    circle.X = circle.Y = 0;
                }
                circles.Add(circle);
            }
            if (mainName == null)
                return circles;
            if (!circles.Exists(delegate(SpatialCircle c) { return c.Id == SpatialCircle.FriendsId; }))
                circles.Insert(0, new SpatialCircle { Id = SpatialCircle.FriendsId, Name = "Friends" });
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
            if (_circles != null)
                return;
            _circles = ParseCircles(_preferences.RoomCircles, "Rooms");
            _friendCircleOf = new Dictionary<string, string>();
            foreach (string pair in (_preferences.FriendCircleOf ?? String.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = pair.IndexOf(':');
                if (colon > 0)
                    _friendCircleOf[pair.Substring(0, colon)] = pair.Substring(colon + 1);
            }
            _friendPlaces = new Dictionary<string, string>();
            foreach (string pair in (_preferences.FriendPlaces ?? String.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = pair.IndexOf(':');
                if (colon > 0)
                    _friendPlaces[pair.Substring(0, colon)] = pair.Substring(colon + 1);
            }
            if (!String.IsNullOrEmpty(_preferences.FriendCircles))
            {
                // Friends' own circles join the one list. Their old places were in the friends tab's
                // space, so they're placed afresh. Friends in its main circle go to Friends.
                foreach (SpatialCircle old in ParseCircles(_preferences.FriendCircles, null))
                {
                    if (old.Id == SpatialCircle.FavouritesId || old.Id == SpatialCircle.MainId || _circles.Exists(delegate(SpatialCircle c) { return c.Id == old.Id; }))
                        continue;
                    old.HasPosition = false;
                    _circles.Add(old);
                }
                foreach (string friend in new List<string>(_friendCircleOf.Keys))
                    if (_friendCircleOf[friend] == SpatialCircle.MainId) _friendCircleOf.Remove(friend);
                _preferences.FriendCircles = String.Empty;
                SaveCircles();
            }
        }

        private void SaveCircles()
        {
            _preferences.RoomCircles = FormatCircles(_circles);
            List<string> pairs = new List<string>();
            foreach (KeyValuePair<string, string> pair in _friendCircleOf)
                pairs.Add(pair.Key + ":" + pair.Value);
            _preferences.FriendCircleOf = String.Join(",", pairs.ToArray());
            List<string> places = new List<string>();
            foreach (KeyValuePair<string, string> pair in _friendPlaces)
                if (pair.Value.Length > 0) places.Add(pair.Key + ":" + pair.Value);
            _preferences.FriendPlaces = String.Join(";", places.ToArray());
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
                room.Placed = false;
                SavedRoomStore.Save(_savedRooms);
            });
        }

        internal void SpatialMoveFriendToCircle(string id, string circle)
        {
            SpatialDo(delegate
            {
                LoadCircles();
                if (circle == SpatialCircle.FriendsId)
                    _friendCircleOf.Remove(id);
                else
                    _friendCircleOf[id] = circle;
                SetFriendPlace(id, false, 0, 0, null);
                SaveCircles();
            });
        }

        /// <summary>A room dropped somewhere in the space: its circle (or none) and its place and size there.</summary>
        internal void SpatialPlaceRoom(object key, string circle, double x, double y, double size)
        {
            SavedRoom room = key as SavedRoom;
            if (room == null)
                return;
            SpatialDo(delegate
            {
                room.Circle = circle == SpatialCircle.MainId ? String.Empty : circle;
                room.Placed = true;
                room.PlaceX = x;
                room.PlaceY = y;
                room.Size = Math.Max(0.6, Math.Min(1.8, size));
                SavedRoomStore.Save(_savedRooms);
            });
        }

        internal void SpatialPlaceFriend(string id, string circle, double x, double y, double size)
        {
            SpatialDo(delegate
            {
                LoadCircles();
                if (circle == SpatialCircle.FriendsId)
                    _friendCircleOf.Remove(id);
                else
                    _friendCircleOf[id] = circle;
                SetFriendPlace(id, true, x, y, size);
                SaveCircles();
            });
        }

        /// <summary>A friend's place and size. A null size keeps the one they have.</summary>
        private void SetFriendPlace(string id, bool placed, double x, double y, double? size)
        {
            string old;
            bool wasPlaced;
            double ox, oy, oldSize;
            _friendPlaces.TryGetValue(id, out old);
            SpatialPlace.Parse(old, out wasPlaced, out ox, out oy, out oldSize);
            string text = SpatialPlace.Format(placed, x, y, size ?? oldSize);
            if (text.Length == 0)
                _friendPlaces.Remove(id);
            else
                _friendPlaces[id] = text;
        }

        internal void SpatialPlaceCircle(string id, double x, double y)
        {
            SpatialDo(delegate
            {
                LoadCircles();
                SpatialCircle circle = _circles.Find(delegate(SpatialCircle c) { return c.Id == id; });
                if (circle == null)
                    return;
                circle.HasPosition = true;
                circle.X = x;
                circle.Y = y;
                SaveCircles();
            });
        }

        /// <summary>A new circle: where you right-clicked in the space, or in the next free spot.</summary>
        internal void SpatialNewCircle(double? x, double? y)
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
                if (x.HasValue && y.HasValue)
                {
                    circle.HasPosition = true;
                    circle.X = x.Value;
                    circle.Y = y.Value;
                }
                _circles.Add(circle);
                SaveCircles();
            });
        }

        internal void SpatialCircleMenu(string id)
        {
            SpatialDialog(delegate
            {
                LoadCircles();
                List<SpatialCircle> circles = _circles;
                SpatialCircle circle = circles.Find(delegate(SpatialCircle c) { return c.Id == id; });
                if (circle == null)
                    return;
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new ThemedMenuRenderer();
                menu.ShowImageMargin = false;
                menu.Items.Add("Tidy this circle", null, delegate
                {
                    // Everything in it back to the automatic layout, at its usual size.
                    foreach (SavedRoom room in _savedRooms)
                    {
                        string own = String.IsNullOrEmpty(room.Circle) ? SpatialCircle.MainId : room.Circle;
                        if (own != id)
                            continue;
                        room.Placed = false;
                        room.Size = 1;
                    }
                    SavedRoomStore.Save(_savedRooms);
                    foreach (string friend in new List<string>(_friendPlaces.Keys))
                    {
                        string own;
                        if (!_friendCircleOf.TryGetValue(friend, out own))
                            own = SpatialCircle.FriendsId;
                        if (own == id)
                            _friendPlaces.Remove(friend);
                    }
                    SaveCircles();
                });
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
                        // Whatever was in it goes back to Rooms or Friends.
                        circles.Remove(circle);
                        foreach (SavedRoom room in _savedRooms)
                        {
                            if (room.Circle != id)
                                continue;
                            room.Circle = String.Empty;
                            room.Placed = false; // Its place was in the circle that's gone.
                        }
                        SavedRoomStore.Save(_savedRooms);
                        foreach (string friend in new List<string>(_friendCircleOf.Keys))
                        {
                            if (_friendCircleOf[friend] != id)
                                continue;
                            _friendCircleOf.Remove(friend);
                            SetFriendPlace(friend, false, 0, 0, null);
                        }
                        SaveCircles();
                    });
                }
                ShowSpatialMenu(menu);
            });
        }

        internal void SpatialCopyRoomCode() { SpatialDo(delegate { CopyInviteClicked(null, EventArgs.Empty); }); }
        internal void SpatialPreviewShare() { SpatialDialog(delegate { ShowSharePreview(); }); }

        internal void SpatialAddFriend() { SpatialDialog(delegate { if (_friendsView != null) _friendsView.AddFriend(); }); }
        internal void SpatialCopyFriendCode() { SpatialDialog(delegate { if (_friendsView != null) _friendsView.CopyCode(); }); }
        internal void SpatialFriendMenu(string id)
        {
            SpatialDialog(delegate
            {
                if (_friendsView == null)
                    return;
                ContextMenuStrip menu = _friendsView.BuildPersonMenu(id);
                if (menu == null)
                    return;
                LoadCircles();
                string place;
                bool placed;
                double x, y, size;
                _friendPlaces.TryGetValue(id, out place);
                SpatialPlace.Parse(place, out placed, out x, out y, out size);
                if (Math.Abs(size - 1) > 0.005)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("Reset size", null, delegate
                    {
                        SetFriendPlace(id, placed, x, y, 1);
                        SaveCircles();
                    });
                }
                ShowSpatialMenu(menu);
            });
        }

        // ----- Menus. Classic builds them, so the items and what they do are the same in both
        // layouts; Spatial draws them, and a click runs the Classic item here. -----

        private readonly List<ToolStripItem> _spatialMenuItems = new List<ToolStripItem>();
        private ContextMenuStrip _spatialMenuOwned; // A one-off menu built for Spatial, freed with the next.

        private void ShowSpatialMenu(ContextMenuStrip menu)
        {
            bool shared = menu == _roomMenu || menu == _channelMenu || menu == _memberMenu;
            if (_spatialMenuOwned != null && _spatialMenuOwned != menu)
                _spatialMenuOwned.Dispose();
            _spatialMenuOwned = shared ? null : menu;
            _spatialMenuItems.Clear();
            List<SpatialMenuEntry> entries = MenuEntries(menu.Items);
            if (entries.Count > 0)
                SpatialTest.ShowMenu(entries);
        }

        private List<SpatialMenuEntry> MenuEntries(ToolStripItemCollection items)
        {
            List<SpatialMenuEntry> entries = new List<SpatialMenuEntry>();
            foreach (ToolStripItem item in items)
            {
                if (!item.Available)
                    continue;
                SpatialMenuEntry entry = new SpatialMenuEntry();
                if (item is ToolStripSeparator)
                {
                    entry.Separator = true;
                    entries.Add(entry);
                    continue;
                }
                entry.Text = item.Text.Replace("&&", "&");
                entry.Enabled = item.Enabled;
                entry.Picture = SpatialPicture(item.Image);
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null)
                {
                    entry.Checked = menuItem.Checked;
                    if (menuItem.HasDropDownItems)
                        entry.Children = MenuEntries(menuItem.DropDownItems);
                }
                entry.Id = _spatialMenuItems.Count;
                _spatialMenuItems.Add(item);
                entries.Add(entry);
            }
            return entries;
        }

        internal void SpatialMenuClicked(int id)
        {
            SpatialDo(delegate
            {
                if (id >= 0 && id < _spatialMenuItems.Count)
                    _spatialMenuItems[id].PerformClick();
            });
        }
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
                ShowSpatialMenu(menu);
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

        // ----- Profile cards. Spatial draws them; these give it the details and do what it asks,
        // through the same calls the Lightweight card uses. -----

        internal void SpatialProfile(uint id)
        {
            SpatialDo(delegate
            {
                VoipEngine engine = _engine;
                RoomMemberInfo member = id == Maela.SpatialProfile.Mine ? MyMember() : FindMember(id);
                if (member == null || (engine == null && !member.IsLocal))
                    return;
                SpatialProfile p = new SpatialProfile();
                p.Id = id == Maela.SpatialProfile.Mine ? id : member.Id;
                p.Name = member.Name;
                p.IsLocal = member.IsLocal;
                p.IsHost = member.IsHost;
                p.ChannelId = member.ChannelId;
                p.Picture = SpatialPicture(member.IsLocal ? MyPicture() : AvatarImage(member.Id));
                MemberExtras extras = ExtrasFor(member);
                if (extras != null)
                {
                    p.Banner = extras.Banner;
                    p.BannerA = extras.ColourA;
                    p.BannerB = extras.ColourB;
                    p.BannerPicture = SpatialPicture(BannerImage(member, extras));
                    if (p.Banner == BannerKind.Picture && p.BannerPicture == null)
                        p.Banner = BannerKind.Auto;
                    p.Effect = _preferences.ShowDecos ? extras.Effect : CardEffect.None;
                    p.Deco = _preferences.ShowDecos ? extras.Deco : PictureDeco.None;
                }
                MemberProfile profile = engine == null || member.IsLocal ? null : engine.GetProfile(member.Id);
                p.Status = member.IsLocal ? _preferences.Status : (profile == null ? String.Empty : profile.Status);
                if (engine == null || !engine.IsConnected)
                    p.State = "Not in a room";
                else if (member.ChannelId > 0)
                    p.State = "In voice \u00B7 " + engine.GetChannelName(member.ChannelId) + (member.Deafened ? " \u00B7 deafened" : member.Muted ? " \u00B7 muted" : String.Empty);
                else
                    p.State = "In the room";
                if (member.IsLocal)
                    p.MyStatus = _friends == null ? null : StatusLabel(_friends.Status);
                else
                {
                    p.Volume = engine.GetPeerVolumePercent(member.Id);
                    p.MutedForMe = engine.GetPeerMuted(member.Id);
                    p.ViewerIsHost = engine.IsHost;
                    LiveStreamInfo live = engine.GetLiveStream(member.Id);
                    if (live != null)
                    {
                        p.LiveText = "Watch stream \u00B7 " + live.Height + "p" + live.Fps;
                        p.CanWatch = member.ChannelId > 0 && member.ChannelId == engine.LocalChannelId;
                    }
                    foreach (VoiceChannelInfo channel in engine.GetChannels())
                    {
                        if (channel.Id <= 0 || channel.IsText)
                            continue;
                        SpatialRoomChannel c = new SpatialRoomChannel();
                        c.Id = channel.Id;
                        c.Name = channel.Name;
                        p.VoiceChannels.Add(c);
                    }
                }
                SpatialTest.ShowProfile(p);
            });
        }

        internal void SpatialSetPeerVolume(uint id, int percent)
        {
            SpatialDo(delegate
            {
                if (_engine == null)
                    return;
                _engine.SetPeerVolumePercent(id, percent);
                _channels.Invalidate();
            });
        }

        internal void SpatialSetPeerMuted(uint id, bool muted)
        {
            SpatialDo(delegate
            {
                if (_engine == null)
                    return;
                _engine.SetPeerMuted(id, muted);
                _channels.Invalidate();
            });
        }

        internal void SpatialSetMyStatus(string label)
        {
            SpatialDo(delegate
            {
                FriendStatus chosen;
                try { chosen = (FriendStatus)Enum.Parse(typeof(FriendStatus), label); }
                catch (ArgumentException) { return; }
                SetMyStatus(chosen);
                RefreshShell(_engine);
            });
        }

        /// <summary>Hosts only: where a channel sits round the room, and how big it is. Everyone sees it live.</summary>
        internal void SpatialChannelLayout(int channel, double angle, double reach, double size)
        {
            SpatialDo(delegate
            {
                if (_engine == null || !_engine.IsHost)
                    return;
                _engine.SetChannelLayout(channel, angle, reach, size);
                // Saved with the room once it's been still for a moment, not on every step of a drag.
                if (_layoutSaveTimer == null)
                {
                    _layoutSaveTimer = new Timer();
                    _layoutSaveTimer.Interval = 800;
                    _layoutSaveTimer.Tick += delegate
                    {
                        _layoutSaveTimer.Stop();
                        SaveHostedChannelLayout();
                    };
                }
                _layoutSaveTimer.Stop();
                _layoutSaveTimer.Start();
            });
        }

        private Timer _layoutSaveTimer;

        /// <summary>
        /// A hosted room's channel layout for the rooms file: one "angle,reach,size" per channel, in
        /// the same order as its channel names (which is the order they get their ids when it
        /// starts again), separated by ';', with an empty entry for a channel in its usual place.
        /// </summary>
        private static string FormatChannelLayout(VoipEngine engine, VoiceChannelInfo[] channels)
        {
            StringBuilder text = new StringBuilder();
            bool any = false;
            for (int i = 0; i < channels.Length; i++)
            {
                if (i > 0)
                    text.Append(';');
                ChannelLayout layout = engine.GetChannelLayout(channels[i].Id);
                if (layout == null)
                    continue;
                any = true;
                text.Append(layout.Angle.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(layout.Reach.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(layout.Size.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            }
            return any ? text.ToString() : String.Empty;
        }

        /// <summary>Puts a saved layout back on a room that has just started. Anything unreadable is skipped.</summary>
        private static void RestoreChannelLayout(VoipEngine engine, string saved)
        {
            if (String.IsNullOrEmpty(saved))
                return;
            VoiceChannelInfo[] channels = engine.GetChannels();
            string[] entries = saved.Split(';');
            for (int i = 0; i < entries.Length && i < channels.Length; i++)
            {
                string[] values = entries[i].Split(',');
                double angle, reach, size;
                System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
                if (values.Length != 3
                    || !Double.TryParse(values[0], System.Globalization.NumberStyles.Float, invariant, out angle)
                    || !Double.TryParse(values[1], System.Globalization.NumberStyles.Float, invariant, out reach)
                    || !Double.TryParse(values[2], System.Globalization.NumberStyles.Float, invariant, out size))
                    continue;
                engine.SetChannelLayout(channels[i].Id, angle, reach, size);
            }
        }

        /// <summary>Called from the Dynamic window's own thread every frame, so it only reads the engine.</summary>
        internal float SpatialVoiceLevel(uint id)
        {
            VoipEngine engine = _engine;
            return engine == null ? 0f : engine.GetVoiceLevel(id);
        }

        internal void SpatialEditProfile() { SpatialDialog(delegate { ShowEditProfile(); }); }

        internal void SpatialWatch(uint id)
        {
            SpatialDialog(delegate
            {
                RoomMemberInfo member = FindMember(id);
                if (member != null)
                    WatchStream(member);
            });
        }

        /// <summary>Hosts only: into a voice channel, or out of voice with 0.</summary>
        internal void SpatialMovePerson(uint id, int channel)
        {
            SpatialDo(delegate
            {
                if (_engine != null && _engine.IsHost)
                    _engine.MoveMemberToChannel(id, channel);
            });
        }

        /// <summary>Hosts only. The card has already asked for a second click to confirm.</summary>
        internal void SpatialKick(uint id, bool ban)
        {
            SpatialDo(delegate
            {
                if (_engine != null && _engine.IsHost)
                    _engine.KickMember(id, ban);
            });
        }
    }
}
