using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>Your profile (picture + status), everyone's avatars, and the profile card.</summary>
    internal sealed partial class MainForm
    {
        private sealed class CachedAvatar
        {
            public uint Version;
            public Bitmap Picture;
        }

        private readonly Dictionary<uint, CachedAvatar> _avatarCache = new Dictionary<uint, CachedAvatar>();
        private VoipEngine _avatarCacheEngine;
        private int _lastProfilesVersion = -1;
        private VoipEngine _profileSentTo;
        private byte[] _myPicture = ProfileStore.LoadPicture();
        private byte[] _myBanner = ProfileStore.LoadBanner();
        private Bitmap _myPictureBitmap;
        private Bitmap _myBannerBitmap;
        private readonly Dictionary<uint, CachedAvatar> _bannerCache = new Dictionary<uint, CachedAvatar>();
        private ProfileCard _openCard;

        /// <summary>A member's picture, or null to draw their initial.</summary>
        private Image AvatarImage(uint memberId)
        {
            VoipEngine engine = _engine;
            if (engine == null || memberId == 0)
                return null;
            if (!Object.ReferenceEquals(engine, _avatarCacheEngine))
                ClearAvatarCache(engine);
            MemberProfile profile = engine.GetProfile(memberId);
            if (profile == null || profile.Pixels == null)
                return null;
            CachedAvatar cached;
            if (_avatarCache.TryGetValue(memberId, out cached) && cached.Version == profile.Version)
                return cached.Picture;
            if (cached != null && cached.Picture != null)
                cached.Picture.Dispose();
            cached = new CachedAvatar();
            cached.Version = profile.Version;
            cached.Picture = ProfilePicture.ToBitmap(profile.Pixels);
            _avatarCache[memberId] = cached;
            return cached.Picture;
        }

        private void ClearAvatarCache(VoipEngine engine)
        {
            foreach (CachedAvatar c in _avatarCache.Values)
            {
                if (c.Picture != null)
                    c.Picture.Dispose();
            }
            _avatarCache.Clear();
            foreach (CachedAvatar c in _bannerCache.Values)
            {
                if (c.Picture != null)
                    c.Picture.Dispose();
            }
            _bannerCache.Clear();
            _avatarCacheEngine = engine;
        }

        /// <summary>Someone's banner and card effect: yours from your settings, anyone else's as shared. Null for none.</summary>
        private MemberExtras ExtrasFor(RoomMemberInfo member)
        {
            if (member.IsLocal)
            {
                MemberExtras mine = new MemberExtras();
                mine.Banner = _preferences.Banner == BannerKind.Picture && _myBanner == null ? BannerKind.Auto : _preferences.Banner;
                mine.ColourA = _preferences.BannerColourA;
                mine.ColourB = _preferences.BannerColourB;
                mine.Pixels = mine.Banner == BannerKind.Picture ? _myBanner : null;
                mine.Effect = _preferences.CardEffect;
                mine.Deco = _preferences.PictureDeco;
                return mine;
            }
            VoipEngine engine = _engine;
            return engine == null ? null : engine.GetExtras(member.Id);
        }

        /// <summary>The banner picture from someone's extras, or null.</summary>
        private Image BannerImage(RoomMemberInfo member, MemberExtras extras)
        {
            if (extras == null || extras.Banner != BannerKind.Picture || extras.Pixels == null)
                return null;
            if (member.IsLocal)
            {
                if (_myBannerBitmap == null)
                    _myBannerBitmap = ProfilePicture.ToBitmap(extras.Pixels, VoipEngine.BannerWidth, VoipEngine.BannerHeight);
                return _myBannerBitmap;
            }
            if (!Object.ReferenceEquals(_engine, _avatarCacheEngine))
                ClearAvatarCache(_engine);
            CachedAvatar cached;
            if (_bannerCache.TryGetValue(member.Id, out cached) && cached.Version == extras.Version)
                return cached.Picture;
            if (cached != null && cached.Picture != null)
                cached.Picture.Dispose();
            cached = new CachedAvatar();
            cached.Version = extras.Version;
            cached.Picture = ProfilePicture.ToBitmap(extras.Pixels, VoipEngine.BannerWidth, VoipEngine.BannerHeight);
            _bannerCache[member.Id] = cached;
            return cached.Picture;
        }

        private Image MyPicture()
        {
            if (_myPicture == null)
                return null;
            if (_myPictureBitmap == null)
                _myPictureBitmap = ProfilePicture.ToBitmap(_myPicture);
            return _myPictureBitmap;
        }

        /// <summary>Gives the engine your profile once per room (and again after you edit it).</summary>
        private void SendProfileToEngine(bool changed)
        {
            VoipEngine engine = _engine;
            if (engine == null || !engine.IsRunning)
                return;
            if (!changed && Object.ReferenceEquals(engine, _profileSentTo))
                return;
            _profileSentTo = engine;
            SendExtrasToEngine(engine);
            if (_myPicture == null && String.IsNullOrEmpty(_preferences.Status))
                return; // Nothing to share.
            engine.SetLocalProfile(_preferences.Status, _myPicture);
        }

        /// <summary>Your banner and card effect, if you've set either.</summary>
        private void SendExtrasToEngine(VoipEngine engine)
        {
            BannerKind banner = _preferences.Banner;
            if (banner == BannerKind.Picture && _myBanner == null)
                banner = BannerKind.Auto;
            if (banner == BannerKind.Auto && _preferences.CardEffect == CardEffect.None && _preferences.PictureDeco == PictureDeco.None)
                return; // Nothing to share.
            engine.SetLocalExtras(banner, _preferences.BannerColourA, _preferences.BannerColourB, banner == BannerKind.Picture ? _myBanner : null, _preferences.CardEffect, _preferences.PictureDeco);
        }

        /// <summary>UI timer: redraw avatars when profiles arrive or change.</summary>
        private void RefreshProfiles(VoipEngine engine)
        {
            SendProfileToEngine(false);
            int version = engine == null ? -1 : engine.ProfilesVersion;
            if (version == _lastProfilesVersion)
                return;
            _lastProfilesVersion = version;
            _channels.Invalidate();
            _chatList.Invalidate();
            if (_openCard != null && !_openCard.IsDisposed)
                _openCard.Invalidate(true);
        }

        // ----- Profile card -----

        private void ShowProfileCard(RoomMemberInfo member, Point screenLocation)
        {
            VoipEngine engine = _engine;
            if (member == null || (engine == null && !member.IsLocal))
                return;
            if (_openCard != null && !_openCard.IsDisposed)
                _openCard.Close();

            MemberProfile profile = engine == null || member.IsLocal ? null : engine.GetProfile(member.Id);
            string status = member.IsLocal ? _preferences.Status : (profile == null ? String.Empty : profile.Status);
            string state;
            if (engine == null || !engine.IsConnected)
                state = "Not in a room";
            else if (member.ChannelId > 0)
            {
                string channel = engine.GetChannelName(member.ChannelId);
                state = "In voice · " + channel + (member.Deafened ? " · deafened" : member.Muted ? " · muted" : String.Empty);
            }
            else
                state = "In the room";

            FriendStatus? myStatus = member.IsLocal && _friends != null ? _friends.Status : (FriendStatus?)null;
            MemberExtras extras = ExtrasFor(member);
            ProfileCard card = new ProfileCard(member, member.IsLocal ? MyPicture() : AvatarImage(member.Id), status, state, engine, member.IsLocal, myStatus,
                extras, BannerImage(member, extras), _preferences.ShowDecos);
            card.StatusChosen += delegate(FriendStatus chosen)
            {
                card.Close();
                SetMyStatus(chosen);
                RefreshShell(_engine);
            };
            card.EditProfileClicked += delegate { card.Close(); ShowEditProfile(); };
            card.MoveClicked += delegate(int channelId) { if (_engine != null) _engine.MoveMemberToChannel(member.Id, channelId); };
            card.KickClicked += delegate(bool ban)
            {
                if (_engine == null)
                    return;
                string question = (ban ? "Ban " : "Kick ") + member.Name + (ban ? " for this session?" : "?");
                if (MessageBox.Show(this, question, ban ? "Ban" : "Kick", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    _engine.KickMember(member.Id, ban);
                    card.Close();
                }
            };
            card.PeerSettingsChanged += delegate { _channels.Invalidate(); };
            card.WatchClicked += delegate { card.Close(); WatchStream(member); };
            card.FormClosed += delegate { if (Object.ReferenceEquals(_openCard, card)) _openCard = null; };

            Rectangle area = Screen.FromPoint(screenLocation).WorkingArea;
            int x = Math.Max(area.Left + 4, Math.Min(screenLocation.X + 12, area.Right - card.Width - 4));
            int y = Math.Max(area.Top + 4, Math.Min(screenLocation.Y - 40, area.Bottom - card.Height - 4));
            card.Location = new Point(x, y);
            _openCard = card;
            card.Show(this);
        }

        // ----- Edit profile -----

        private void ShowEditProfile()
        {
            using (EditProfileDialog dialog = new EditProfileDialog(_preferences.DisplayName, _preferences.Status, _myPicture, _preferences, _myBanner))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                string name = dialog.DisplayName.Trim();
                if (name.Length > 0 && name != _preferences.DisplayName)
                    _preferences.DisplayName = name.Length > 32 ? name.Substring(0, 32) : name;
                _preferences.Status = VoipEngine.CleanStatus(dialog.Status);
                _myPicture = dialog.Picture;
                if (_myPictureBitmap != null)
                {
                    _myPictureBitmap.Dispose();
                    _myPictureBitmap = null;
                }
                ProfileStore.SavePicture(_myPicture);
                bool extrasChanged = dialog.Banner != _preferences.Banner || dialog.BannerColourA != _preferences.BannerColourA || dialog.BannerColourB != _preferences.BannerColourB
                    || dialog.Effect != _preferences.CardEffect || dialog.Deco != _preferences.PictureDeco || !Object.ReferenceEquals(dialog.BannerPicture, _myBanner);
                _preferences.Banner = dialog.Banner;
                _preferences.BannerColourA = dialog.BannerColourA;
                _preferences.BannerColourB = dialog.BannerColourB;
                _preferences.CardEffect = dialog.Effect;
                _preferences.PictureDeco = dialog.Deco;
                if (!Object.ReferenceEquals(_myBanner, dialog.BannerPicture) && _myBannerBitmap != null)
                {
                    _myBannerBitmap.Dispose();
                    _myBannerBitmap = null;
                }
                _myBanner = dialog.BannerPicture;
                ProfileStore.SaveBanner(_myBanner);
                PreferencesStore.Save(_preferences);
                if (_engine != null && _engine.IsRunning)
                {
                    _engine.SetLocalProfile(_preferences.Status, _myPicture);
                    if (extrasChanged)
                    {
                        BannerKind banner = _preferences.Banner == BannerKind.Picture && _myBanner == null ? BannerKind.Auto : _preferences.Banner;
                        _engine.SetLocalExtras(banner, _preferences.BannerColourA, _preferences.BannerColourB, banner == BannerKind.Picture ? _myBanner : null, _preferences.CardEffect, _preferences.PictureDeco);
                    }
                }
                _userBar.Picture = MyPicture();
                RefreshShell(_engine);
                Invalidate(true);
            }
        }
    }

    /// <summary>Your picture, stored next to your settings (raw pixels only, never the original file).</summary>
    internal static class ProfileStore
    {
        private static string PicturePath
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maela"), "picture.bin"); }
        }

        private static string BannerPath
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maela"), "banner.bin"); }
        }

        public static byte[] LoadPicture() { return Load(PicturePath, VoipEngine.AvatarRawBytes); }
        public static void SavePicture(byte[] pixels) { Save(PicturePath, pixels); }
        public static byte[] LoadBanner() { return Load(BannerPath, VoipEngine.BannerRawBytes); }
        public static void SaveBanner(byte[] pixels) { Save(BannerPath, pixels); }

        private static byte[] Load(string path, int size)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length != size)
                    return null;
                return File.ReadAllBytes(path);
            }
            catch
            {
                return null;
            }
        }

        private static void Save(string path, byte[] pixels)
        {
            try
            {
                if (pixels == null)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, pixels);
            }
            catch { }
        }
    }

    /// <summary>Small card shown when you click someone: picture, name, status, and actions.</summary>
    internal sealed class ProfileCard : Form
    {
        public event EventHandler EditProfileClicked;
        public event Action<FriendStatus> StatusChosen;
        public event Action<int> MoveClicked;
        public event Action<bool> KickClicked;
        public event EventHandler WatchClicked;
        public event EventHandler PeerSettingsChanged;

        private readonly RoomMemberInfo _member;
        private readonly Image _picture;
        private readonly string _status;
        private readonly string _state;
        private readonly TrackBar _volume = new TrackBar();
        private readonly ToggleSwitch _mute = new ToggleSwitch();
        private readonly Label _volumeValue = new Label();
        private readonly MemberExtras _extras;
        private readonly Image _banner;
        private readonly int _bannerHeight;
        private readonly CardEffect _effect;
        private readonly PictureDeco _deco;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private bool _closing;

        public ProfileCard(RoomMemberInfo member, Image picture, string status, string state, VoipEngine engine, bool isLocal, FriendStatus? myStatus,
            MemberExtras extras, Image banner, bool showEffect)
        {
            _member = member;
            _picture = picture;
            _status = status ?? String.Empty;
            _state = state;
            _extras = extras;
            _banner = banner;
            // A chosen banner gets more room, so a picture isn't just a strip.
            _bannerHeight = extras != null && extras.Banner != BannerKind.Auto ? 84 : 56;
            _effect = showEffect && extras != null ? extras.Effect : CardEffect.None;
            _deco = showEffect && extras != null ? extras.Deco : PictureDeco.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            BackColor = Theme.Current.Surface;
            ForeColor = Theme.Current.Text;
            Font = Ui.Body;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            int width = 300;
            int y = 158 + (_bannerHeight - 56) + (_status.Length > 0 ? 22 : 0);

            if (isLocal && myStatus.HasValue)
            {
                Label caption = new Label();
                caption.AutoSize = true;
                caption.Font = Ui.SmallBold;
                caption.ForeColor = Theme.Current.Text2;
                caption.Text = "Appear as";
                caption.Location = new Point(16, y);
                Controls.Add(caption);
                y += 22;
                FriendStatus[] choices = { FriendStatus.Online, FriendStatus.Away, FriendStatus.Busy, FriendStatus.Offline };
                int i;
                for (i = 0; i < choices.Length; i++)
                {
                    FriendStatus choice = choices[i];
                    FlatButton option = SmallButton(MainForm.StatusLabel(choice), choice == myStatus.Value ? ButtonLook.Soft : ButtonLook.Secondary, 16 + i * 68, y, 64);
                    option.Click += delegate
                    {
                        if (choice != myStatus.Value && StatusChosen != null)
                            StatusChosen(choice);
                    };
                }
                y += 44;
            }

            if (isLocal)
            {
                FlatButton edit = new FlatButton();
                edit.Look = ButtonLook.Primary;
                edit.Text = "Edit profile";
                edit.Underlay = Theme.Current.Surface;
                edit.SetBounds(16, y, width - 32, 36);
                edit.Click += delegate { if (EditProfileClicked != null) EditProfileClicked(this, EventArgs.Empty); };
                Controls.Add(edit);
                y += 50;
            }
            else
            {
                LiveStreamInfo live = engine.GetLiveStream(member.Id);
                if (live != null)
                {
                    FlatButton watch = new FlatButton();
                    watch.Look = ButtonLook.Danger;
                    watch.Icon = IconKind.Screen;
                    watch.Text = "Watch stream · " + live.Height + "p" + live.Fps;
                    watch.Underlay = Theme.Current.Surface;
                    watch.Enabled = member.ChannelId > 0 && member.ChannelId == engine.LocalChannelId;
                    watch.SetBounds(16, y, width - 32, 36);
                    watch.Click += delegate { if (WatchClicked != null) WatchClicked(this, EventArgs.Empty); };
                    Controls.Add(watch);
                    y += 46;
                }
                Label caption = new Label();
                caption.AutoSize = true;
                caption.Font = Ui.SmallBold;
                caption.ForeColor = Theme.Current.Text2;
                caption.Text = "Volume (just for you)";
                caption.Location = new Point(16, y);
                Controls.Add(caption);
                _volumeValue.AutoSize = false;
                _volumeValue.TextAlign = ContentAlignment.TopRight;
                _volumeValue.Font = Ui.Small;
                _volumeValue.ForeColor = Theme.Current.Muted;
                _volumeValue.SetBounds(width - 80, y, 64, 18);
                Controls.Add(_volumeValue);
                y += 20;
                _volume.Minimum = 0;
                _volume.Maximum = 200;
                _volume.TickStyle = TickStyle.None;
                _volume.Value = Math.Max(0, Math.Min(200, engine.GetPeerVolumePercent(member.Id)));
                _volume.BackColor = Theme.Current.Surface;
                _volume.SetBounds(10, y, width - 20, 32);
                _volumeValue.Text = _volume.Value + "%";
                _volume.ValueChanged += delegate
                {
                    engine.SetPeerVolumePercent(member.Id, _volume.Value);
                    _volumeValue.Text = _volume.Value + "%";
                    if (PeerSettingsChanged != null) PeerSettingsChanged(this, EventArgs.Empty);
                };
                Controls.Add(_volume);
                y += 38;
                _mute.Text = "Mute for me";
                _mute.Checked = engine.GetPeerMuted(member.Id);
                _mute.SetBounds(16, y, width - 32, 30);
                _mute.CheckedChanged += delegate
                {
                    engine.SetPeerMuted(member.Id, _mute.Checked);
                    if (PeerSettingsChanged != null) PeerSettingsChanged(this, EventArgs.Empty);
                };
                Controls.Add(_mute);
                y += 40;

                if (engine.IsHost)
                {
                    FlatButton move = SmallButton("Move", ButtonLook.Secondary, 16, y, 84);
                    move.Click += delegate
                    {
                        ContextMenuStrip menu = new ContextMenuStrip();
                        menu.Renderer = new ThemedMenuRenderer();
                        menu.ShowImageMargin = false;
                        foreach (VoiceChannelInfo channel in engine.GetChannels())
                        {
                            if (channel.IsText)
                                continue;
                            VoiceChannelInfo target = channel;
                            ToolStripItem item = menu.Items.Add(channel.Name, null, delegate { if (MoveClicked != null) MoveClicked(target.Id); });
                            item.Enabled = target.Id != member.ChannelId;
                        }
                        ToolStripItem outOfVoice = menu.Items.Add("Remove from voice", null, delegate { if (MoveClicked != null) MoveClicked(0); });
                        outOfVoice.Enabled = member.ChannelId > 0;
                        menu.Closed += delegate { BeginInvoke((MethodInvoker)menu.Dispose); };
                        menu.Show(move, new Point(0, move.Height));
                    };
                    FlatButton kick = SmallButton("Kick", ButtonLook.Secondary, 108, y, 84);
                    kick.Click += delegate { if (KickClicked != null) KickClicked(false); };
                    FlatButton ban = SmallButton("Ban", ButtonLook.Danger, 200, y, 84);
                    ban.Click += delegate { if (KickClicked != null) KickClicked(true); };
                    y += 48;
                }
            }

            ClientSize = new Size(width, y + 6);
            if (_effect != CardEffect.None || _deco != PictureDeco.None)
            {
                Timer frames = new Timer();
                frames.Interval = 33;
                frames.Tick += delegate { Invalidate(false); };
                frames.Start();
                FormClosed += delegate { frames.Dispose(); };
            }
            Deactivate += delegate { if (!_closing) { _closing = true; BeginInvoke((MethodInvoker)Close); } };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
        }

        private FlatButton SmallButton(string text, ButtonLook look, int x, int y, int width)
        {
            FlatButton b = new FlatButton();
            b.Look = look;
            b.Text = text;
            b.Underlay = Theme.Current.Surface;
            b.SetBounds(x, y, width, 34);
            Controls.Add(b);
            return b;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            // Their banner (or their name's colour), avatar overlapping it.
            int shift = _bannerHeight - 56;
            Rectangle banner = new Rectangle(0, 0, Width, _bannerHeight);
            if (_extras == null)
                CardEffects.PaintBanner(g, banner, BannerKind.Auto, 0, 0, null, Ui.NameColor(_member.Name));
            else
                CardEffects.PaintBanner(g, banner, _extras.Banner, _extras.ColourA, _extras.ColourB, _banner, Ui.NameColor(_member.Name));
            // The card deco goes behind the picture, the words and the buttons.
            if (_effect != CardEffect.None)
                CardEffects.PaintEffect(g, _effect, ClientSize, banner, new PointF(54, 60 + shift), 40, _extras.ColourA, _extras.ColourB, (int)_member.Id, _clock.Elapsed.TotalSeconds);
            using (SolidBrush ring = new SolidBrush(t.Surface))
                g.FillEllipse(ring, 12, 18 + shift, 84, 84);
            Ui.DrawAvatar(g, new Rectangle(16, 22 + shift, 76, 76), _member.Name, false, t.Surface, _picture);

            int nameY = 108 + shift;
            Size nameSize = Ui.Measure(g, _member.Name, Ui.Heading);
            Ui.DrawText(g, _member.Name, Ui.Heading, new Rectangle(16, nameY, Width - 60, 30), t.Text, TextFormatFlags.Left);
            if (_member.IsHost)
                Icons.Draw(g, IconKind.Crown, new RectangleF(Math.Min(16 + nameSize.Width + 6, Width - 40), nameY + 6, 18, 18), Color.FromArgb(240, 178, 50));
            int y = nameY + 32;
            if (_status.Length > 0)
            {
                Ui.DrawText(g, _status, Ui.Body, new Rectangle(16, y, Width - 32, 20), t.Text2, TextFormatFlags.Left);
                y += 22;
            }
            Ui.DrawText(g, _state, Ui.Small, new Rectangle(16, y, Width - 32, 16), t.Muted, TextFormatFlags.Left);
            if (_deco != PictureDeco.None)
                CardEffects.PaintDeco(g, _deco, new PointF(54, 60 + shift), 40, _extras.ColourA, _extras.ColourB, _clock.Elapsed.TotalSeconds);

            using (Pen border = new Pen(t.Line))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Edit your name, status, picture, banner and card effect.</summary>
    internal sealed class EditProfileDialog : ThemedDialog
    {
        private readonly RoundField _name;
        private readonly RoundField _status;
        private readonly PictureBoxRound _preview = new PictureBoxRound();
        private readonly CardPreview _card = new CardPreview();
        private readonly List<FlatButton> _kindButtons = new List<FlatButton>();
        private readonly List<FlatButton> _effectButtons = new List<FlatButton>();
        private readonly List<FlatButton> _decoButtons = new List<FlatButton>();
        private readonly FlatButton _colourA = new FlatButton();
        private readonly FlatButton _colourB = new FlatButton();
        private byte[] _picture;
        private byte[] _bannerPicture;

        public string DisplayName { get { return _name.Box.Text; } }
        public string Status { get { return _status.Box.Text; } }
        public byte[] Picture { get { return _picture; } }
        public BannerKind Banner { get { return _card.Banner; } }
        public int BannerColourA { get { return _card.ColourA; } }
        public int BannerColourB { get { return _card.ColourB; } }
        public byte[] BannerPicture { get { return _bannerPicture; } }
        public CardEffect Effect { get { return _card.Effect; } }
        public PictureDeco Deco { get { return _card.Deco; } }

        public EditProfileDialog(string name, string status, byte[] picture, AppPreferences preferences, byte[] bannerPicture) : base("Edit profile", 520, 826)
        {
            _picture = picture;
            _bannerPicture = bannerPicture;
            AddHeading("Your profile", "Your picture, status, banner and decos are shared with people in rooms you join.");

            _preview.SetBounds(Margin_X, Cursor_Y, 96, 96);
            _preview.NameForInitial = name;
            _preview.Picture = picture == null ? null : ProfilePicture.ToBitmap(picture);
            Controls.Add(_preview);

            FlatButton choose = new FlatButton();
            choose.Look = ButtonLook.Secondary;
            choose.Text = "Choose picture…";
            choose.SetBounds(Margin_X + 116, Cursor_Y + 14, 170, 36);
            choose.Click += delegate { ChoosePicture(); };
            Controls.Add(choose);

            FlatButton remove = new FlatButton();
            remove.Look = ButtonLook.Ghost;
            remove.Text = "Remove picture";
            remove.SetBounds(Margin_X + 110, Cursor_Y + 54, 170, 30);
            remove.Click += delegate
            {
                _picture = null;
                _preview.Picture = null;
                _preview.Invalidate();
                _card.Picture = null;
                _card.Invalidate();
            };
            Controls.Add(remove);
            Cursor_Y += 112;

            _name = AddField("Display name", name, false);
            _status = AddField("Status (optional, up to " + VoipEngine.MaxStatusChars + " characters)", status, false);
            _status.Box.MaxLength = VoipEngine.MaxStatusChars;
            _name.Box.MaxLength = 32;
            _name.Box.TextChanged += delegate { _card.PersonName = _name.Box.Text; _card.Invalidate(); };
            _status.Box.TextChanged += delegate { _card.Status = _status.Box.Text; _card.Invalidate(); };

            // The card as others will see it, with the banner choices beside it.
            AddFieldLabel("Profile card", Margin_X, Cursor_Y);
            Cursor_Y += 22;
            _card.PersonName = name ?? String.Empty;
            _card.Status = status ?? String.Empty;
            _card.Picture = _preview.Picture;
            _card.Banner = preferences.Banner == BannerKind.Picture && bannerPicture == null ? BannerKind.Auto : preferences.Banner;
            _card.ColourA = preferences.BannerColourA;
            _card.ColourB = preferences.BannerColourB;
            _card.BannerPicture = bannerPicture == null ? null : ProfilePicture.ToBitmap(bannerPicture, VoipEngine.BannerWidth, VoipEngine.BannerHeight);
            _card.Effect = preferences.CardEffect;
            _card.Deco = preferences.PictureDeco;
            _card.SetBounds(Margin_X, Cursor_Y, 300, 150);
            Controls.Add(_card);

            string[] kinds = { "Name colour", "Colour", "Gradient", "Picture" };
            for (int i = 0; i < kinds.Length; i++)
            {
                BannerKind kind = (BannerKind)i;
                FlatButton b = new FlatButton();
                b.Text = kinds[i];
                b.SetBounds(Margin_X + 316, Cursor_Y + i * 39, ClientSize.Width - Margin_X * 2 - 316, 34);
                b.Click += delegate { PickBanner(kind); };
                Controls.Add(b);
                _kindButtons.Add(b);
            }
            Cursor_Y += 160;

            _colourA.Text = "First colour";
            _colourA.SetBounds(Margin_X, Cursor_Y, 146, 34);
            _colourA.Click += delegate { int c; if (PickColour(_card.ColourA, out c)) { _card.ColourA = c; _card.Invalidate(); } };
            Controls.Add(_colourA);
            _colourB.Text = "Second colour";
            _colourB.SetBounds(Margin_X + 154, Cursor_Y, 146, 34);
            _colourB.Click += delegate { int c; if (PickColour(_card.ColourB, out c)) { _card.ColourB = c; _card.Invalidate(); } };
            Controls.Add(_colourB);
            FlatButton bannerFile = new FlatButton();
            bannerFile.Text = "Banner picture…";
            bannerFile.SetBounds(Margin_X + 316, Cursor_Y, ClientSize.Width - Margin_X * 2 - 316, 34);
            bannerFile.Click += delegate { ChooseBanner(); };
            Controls.Add(bannerFile);
            Cursor_Y += 46;

            // Two decos: one on the card, one around your picture wherever you appear.
            AddFieldLabel("Card deco", Margin_X, Cursor_Y);
            Cursor_Y += 22;
            int count = CardEffects.Cards.Length, gap = 6;
            int width = (ClientSize.Width - Margin_X * 2 - gap * (count - 1)) / count;
            for (int i = 0; i < count; i++)
            {
                CardEffect effect = CardEffects.Cards[i];
                FlatButton b = new FlatButton();
                b.Text = CardEffects.Name(effect);
                b.SetBounds(Margin_X + i * (width + gap), Cursor_Y, width, 34);
                b.Click += delegate { _card.Effect = effect; ShowChoices(); };
                Controls.Add(b);
                _effectButtons.Add(b);
            }
            Cursor_Y += 46;
            AddFieldLabel("Picture deco", Margin_X, Cursor_Y);
            Cursor_Y += 22;
            for (int i = 0; i < CardEffects.Decos.Length; i++)
            {
                PictureDeco deco = CardEffects.Decos[i];
                FlatButton b = new FlatButton();
                b.Text = CardEffects.Name(deco);
                b.SetBounds(Margin_X + i * (width + gap), Cursor_Y, width, 34);
                b.Click += delegate { _card.Deco = deco; ShowChoices(); };
                Controls.Add(b);
                _decoButtons.Add(b);
            }
            Cursor_Y += 46;

            Label note = AddText("Only small copies of your pictures are shared, never the original files or their details (like location). People see your decos if they have Dynamic effects on.", Theme.Current.Muted, ClientSize.Width - Margin_X * 2);
            note.Font = Ui.Small;
            note.Height = TextRenderer.MeasureText(note.Text, note.Font, new Size(note.Width, 0), TextFormatFlags.WordBreak).Height;

            ShowChoices();
            // Fit the window to what's in it, then put the buttons underneath.
            ClientSize = new Size(ClientSize.Width, note.Bottom + 82);
            AddButtons(Button("Cancel", DialogResult.Cancel), Button("Save", DialogResult.OK));
        }

        private static new FlatButton Button(string text, DialogResult result)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.DialogResult = result;
            return b;
        }

        private void PickBanner(BannerKind kind)
        {
            if (kind == BannerKind.Picture && _bannerPicture == null)
            {
                ChooseBanner(); // Picks the kind too, once a picture is chosen.
                return;
            }
            _card.Banner = kind;
            ShowChoices();
        }

        /// <summary>The chosen banner and effect stand out; the colours only when something uses them.</summary>
        private void ShowChoices()
        {
            for (int i = 0; i < _kindButtons.Count; i++)
                _kindButtons[i].Look = (BannerKind)i == _card.Banner ? ButtonLook.Soft : ButtonLook.Secondary;
            for (int i = 0; i < _effectButtons.Count; i++)
                _effectButtons[i].Look = CardEffects.Cards[i] == _card.Effect ? ButtonLook.Soft : ButtonLook.Secondary;
            for (int i = 0; i < _decoButtons.Count; i++)
                _decoButtons[i].Look = CardEffects.Decos[i] == _card.Deco ? ButtonLook.Soft : ButtonLook.Secondary;
            bool effect = _card.Effect != CardEffect.None || _card.Deco != PictureDeco.None;
            _colourA.Enabled = _card.Banner == BannerKind.Colour || _card.Banner == BannerKind.Gradient || effect;
            _colourB.Enabled = _card.Banner == BannerKind.Gradient || effect;
            _colourA.Invalidate();
            _colourB.Invalidate();
            _card.Animate(effect);
            _card.Invalidate();
        }

        private bool PickColour(int current, out int picked)
        {
            picked = current;
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.Color = CardEffects.ToColor(current);
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return false;
                picked = (dialog.Color.R << 16) | (dialog.Color.G << 8) | dialog.Color.B;
                return true;
            }
        }

        private void ChooseBanner()
        {
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Title = "Choose a banner picture";
                open.Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*";
                if (open.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    _bannerPicture = ProfilePicture.FromFile(open.FileName, VoipEngine.BannerWidth, VoipEngine.BannerHeight);
                    if (_card.BannerPicture != null)
                        _card.BannerPicture.Dispose();
                    _card.BannerPicture = ProfilePicture.ToBitmap(_bannerPicture, VoipEngine.BannerWidth, VoipEngine.BannerHeight);
                    _card.Banner = BannerKind.Picture;
                    ShowChoices();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex is InvalidOperationException ? ex.Message : "That file couldn't be opened as a picture.", "Banner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ChoosePicture()
        {
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Title = "Choose a profile picture";
                open.Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*";
                if (open.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    _picture = ProfilePicture.FromFile(open.FileName);
                    _preview.Picture = ProfilePicture.ToBitmap(_picture);
                    _preview.Invalidate();
                    _card.Picture = _preview.Picture;
                    _card.Invalidate();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex is InvalidOperationException ? ex.Message : "That file couldn't be opened as a picture.", "Profile picture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _card.Animate(false);
            base.Dispose(disposing);
        }
    }

    /// <summary>The top of a profile card as others see it: banner, picture, name and effect.</summary>
    internal sealed class CardPreview : Control
    {
        public string PersonName = String.Empty, Status = String.Empty;
        public Image Picture, BannerPicture;
        public BannerKind Banner;
        public int ColourA, ColourB;
        public CardEffect Effect;
        public PictureDeco Deco;
        private Timer _timer;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        public CardPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Animate(bool on)
        {
            if (on && _timer == null)
            {
                _timer = new Timer();
                _timer.Interval = 33;
                _timer.Tick += delegate { Invalidate(); };
                _timer.Start();
            }
            else if (!on && _timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            Rectangle banner = new Rectangle(0, 0, Width, 84);
            CardEffects.PaintBanner(g, banner, Banner, ColourA, ColourB, BannerPicture, Ui.NameColor(PersonName));
            CardEffects.PaintEffect(g, Effect, Size, banner, new PointF(50, 84), 34, ColourA, ColourB, 1, _clock.Elapsed.TotalSeconds);
            using (SolidBrush ring = new SolidBrush(t.Surface))
                g.FillEllipse(ring, 12, 46, 76, 76);
            Ui.DrawAvatar(g, new Rectangle(16, 50, 68, 68), PersonName, false, t.Surface, Picture);
            Ui.DrawText(g, PersonName, Ui.Heading, new Rectangle(98, 88, Width - 108, 30), t.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (!String.IsNullOrEmpty(Status))
                Ui.DrawText(g, Status, Ui.Small, new Rectangle(98, 118, Width - 108, 18), t.Text2, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            CardEffects.PaintDeco(g, Deco, new PointF(50, 84), 34, ColourA, ColourB, _clock.Elapsed.TotalSeconds);
            using (Pen border = new Pen(t.Line))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Round picture preview (falls back to the initial).</summary>
    internal sealed class PictureBoxRound : Control
    {
        public Image Picture;
        public string NameForInitial = String.Empty;

        public PictureBoxRound()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Current.Main);
            Ui.DrawAvatar(e.Graphics, new Rectangle(2, 2, Width - 4, Height - 4), NameForInitial, false, Theme.Current.Main, Picture);
        }
    }
}
