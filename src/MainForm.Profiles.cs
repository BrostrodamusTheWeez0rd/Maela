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
        private Bitmap _myPictureBitmap;
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
            _avatarCacheEngine = engine;
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
            if (_myPicture == null && String.IsNullOrEmpty(_preferences.Status))
                return; // Nothing to share.
            engine.SetLocalProfile(_preferences.Status, _myPicture);
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
            ProfileCard card = new ProfileCard(member, member.IsLocal ? MyPicture() : AvatarImage(member.Id), status, state, engine, member.IsLocal, myStatus);
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
            using (EditProfileDialog dialog = new EditProfileDialog(_preferences.DisplayName, _preferences.Status, _myPicture))
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
                PreferencesStore.Save(_preferences);
                if (_engine != null && _engine.IsRunning)
                    _engine.SetLocalProfile(_preferences.Status, _myPicture);
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

        public static byte[] LoadPicture()
        {
            try
            {
                string path = PicturePath;
                if (!File.Exists(path) || new FileInfo(path).Length != VoipEngine.AvatarRawBytes)
                    return null;
                return File.ReadAllBytes(path);
            }
            catch
            {
                return null;
            }
        }

        public static void SavePicture(byte[] pixels)
        {
            try
            {
                string path = PicturePath;
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
        private bool _closing;

        public ProfileCard(RoomMemberInfo member, Image picture, string status, string state, VoipEngine engine, bool isLocal, FriendStatus? myStatus)
        {
            _member = member;
            _picture = picture;
            _status = status ?? String.Empty;
            _state = state;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            BackColor = Theme.Current.Surface;
            ForeColor = Theme.Current.Text;
            Font = Ui.Body;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            int width = 300;
            int y = 158 + (_status.Length > 0 ? 22 : 0);

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
            // Banner in the person's colour, avatar overlapping it.
            using (SolidBrush banner = new SolidBrush(Color.FromArgb(150, Ui.NameColor(_member.Name))))
                g.FillRectangle(banner, 0, 0, Width, 56);
            using (SolidBrush ring = new SolidBrush(t.Surface))
                g.FillEllipse(ring, 12, 18, 84, 84);
            Ui.DrawAvatar(g, new Rectangle(16, 22, 76, 76), _member.Name, false, t.Surface, _picture);

            int nameY = 108;
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

            using (Pen border = new Pen(t.Line))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Edit your name, status and picture.</summary>
    internal sealed class EditProfileDialog : ThemedDialog
    {
        private readonly RoundField _name;
        private readonly RoundField _status;
        private readonly PictureBoxRound _preview = new PictureBoxRound();
        private byte[] _picture;

        public string DisplayName { get { return _name.Box.Text; } }
        public string Status { get { return _status.Box.Text; } }
        public byte[] Picture { get { return _picture; } }

        public EditProfileDialog(string name, string status, byte[] picture) : base("Edit profile", 460, 470)
        {
            _picture = picture;
            AddHeading("Your profile", "Your picture and status are shared with people in rooms you join.");

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
            };
            Controls.Add(remove);
            Cursor_Y += 112;

            _name = AddField("Display name", name, false);
            _status = AddField("Status (optional, up to " + VoipEngine.MaxStatusChars + " characters)", status, false);
            _status.Box.MaxLength = VoipEngine.MaxStatusChars;
            _name.Box.MaxLength = 32;

            Label note = AddText("Only a small copy of the picture is shared, never the original file or its details (like location).", Theme.Current.Muted, ClientSize.Width - Margin_X * 2);
            note.Font = Ui.Small;

            AddButtons(Button("Cancel", DialogResult.Cancel), Button("Save", DialogResult.OK));
        }

        private static new FlatButton Button(string text, DialogResult result)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.DialogResult = result;
            return b;
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
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex is InvalidOperationException ? ex.Message : "That file couldn't be opened as a picture.", "Profile picture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
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
