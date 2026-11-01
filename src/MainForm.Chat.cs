using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>One line in the chat view: a day divider, a system line, or a message.</summary>
    internal sealed class ChatRow
    {
        public ChatMessage Message;
        public bool ShowHeader;
        public string DayLabel;
        public string SystemText;
        public DateTime SystemTimeUtc;
        public List<ReactionSummary> Reactions;

        // Layout cache.
        public int LayoutWidth = -1;
        public List<TextRun> Runs;
        public int TextHeight;
        public List<Rectangle> ChipRects = new List<Rectangle>();
    }

    /// <summary>A positioned piece of message text: either a string or a colour emoji.</summary>
    internal sealed class TextRun
    {
        public string Text;
        public EmojiEntry Emoji;
        public Rectangle Bounds;
    }

    internal sealed class SystemLine
    {
        public int ChannelId;
        public DateTime TimeUtc;
        public string Text;
        public bool Joined;
    }

    /// <summary>Text chat for the voice channel you are in.</summary>
    internal sealed partial class MainForm
    {
        private const int ChatTextLeft = 72;
        private const int ChatRightPadding = 24;
        private const int InlineEmoji = 21;
        private const int JumboEmoji = 44;
        private const int ChipHeight = 26;

        private static readonly string[] QuickReactions = { "👍", "❤️", "😂", "🔥", "🎉", "😮" };

        private readonly ListBox _chatList = new ListBox();
        private readonly TextBox _chatInput = new TextBox();
        private readonly Label _chatEmpty = new Label();
        private readonly Control _composerBox = new ComposerSurface();
        private readonly Panel _composer = new Panel();
        private readonly FlatButton _emojiButton = new FlatButton();
        private readonly FlatButton _sendButton = new FlatButton();
        private readonly ContextMenuStrip _chatMenu = new ContextMenuStrip();
        private readonly List<SystemLine> _systemLines = new List<SystemLine>();
        private string _chatViewKey = String.Empty;
        private string _chatCue = String.Empty;
        private string _chatFilter = String.Empty;
        private VoipEngine _chatEngine;
        private int _chatChannel = -1;
        private uint _chatNewestSeen;
        private ChatRow _chatContextRow;
        private int _chatHover = -1;
        private Dictionary<uint, string> _channelMemberSnapshot = new Dictionary<uint, string>();

        private void BuildChatArea(Control main)
        {
            _composer.Dock = DockStyle.Bottom;
            _composer.Height = 76;
            _composer.Padding = new Padding(20, 6, 20, 20);
            main.Controls.Add(_composer);

            _composerBox.Dock = DockStyle.Fill;
            _composerBox.Padding = new Padding(16, 0, 8, 0);
            _composerBox.Cursor = Cursors.IBeam;
            _composerBox.Click += delegate { if (_chatInput.Enabled) _chatInput.Focus(); };
            _composer.Controls.Add(_composerBox);

            _sendButton.Look = ButtonLook.Primary;
            _sendButton.Icon = IconKind.Send;
            _sendButton.Size = new Size(38, 38);
            _sendButton.Radius = 10;
            _sendButton.Click += delegate { SendChatFromInput(); };
            _composerBox.Controls.Add(_sendButton);

            _emojiButton.Look = ButtonLook.Ghost;
            _emojiButton.Icon = IconKind.Smile;
            _emojiButton.IconSize = 20;
            _emojiButton.Size = new Size(36, 36);
            _emojiButton.Click += delegate { ShowEmojiPicker(_emojiButton, null); };
            _composerBox.Controls.Add(_emojiButton);

            _chatInput.BorderStyle = BorderStyle.None;
            _chatInput.Font = Ui.Body;
            _chatInput.MaxLength = VoipEngine.MaxChatChars;
            _chatInput.Enabled = false;
            _chatInput.KeyDown += ChatInputKeyDown;
            _chatInput.HandleCreated += delegate { ApplyChatCue(); };
            _composerBox.Controls.Add(_chatInput);
            _composerBox.Resize += delegate { LayoutComposer(); };

            _chatList.Dock = DockStyle.Fill;
            _chatList.BorderStyle = BorderStyle.None;
            _chatList.DrawMode = DrawMode.OwnerDrawVariable;
            _chatList.IntegralHeight = false;
            _chatList.HorizontalScrollbar = false;
            _chatList.MeasureItem += ChatMeasureItem;
            _chatList.DrawItem += ChatDrawItem;
            _chatList.MouseDown += ChatMouseDown;
            _chatList.MouseUp += ChatMouseUp;
            _chatList.MouseMove += ChatMouseMove;
            _chatList.MouseLeave += delegate { SetChatHover(-1); };
            _chatList.KeyDown += ChatListKeyDown;
            _chatList.Resize += delegate { _chatViewKey = String.Empty; };
            main.Controls.Add(_chatList);
            _chatList.BringToFront();

            _chatEmpty.AutoSize = false;
            _chatEmpty.Dock = DockStyle.Fill;
            _chatEmpty.TextAlign = ContentAlignment.MiddleCenter;
            _chatEmpty.Font = Ui.Body;
            main.Controls.Add(_chatEmpty);
            _chatEmpty.BringToFront();

            _chatMenu.ShowImageMargin = true;
            _chatMenu.Opening += ChatMenuOpening;

            ShowChatEmpty("Join a room and a voice channel to start chatting.");
        }

        private void LayoutComposer()
        {
            int h = _composerBox.Height;
            _sendButton.Location = new Point(_composerBox.Width - _sendButton.Width - 7, (h - _sendButton.Height) / 2);
            _emojiButton.Location = new Point(_sendButton.Left - _emojiButton.Width - 4, (h - _emojiButton.Height) / 2);
            int boxHeight = _chatInput.PreferredHeight;
            _chatInput.SetBounds(16, (h - boxHeight) / 2 + 1, Math.Max(20, _emojiButton.Left - 24), boxHeight);
        }

        private void ApplyChatTheme()
        {
            Theme t = Theme.Current;
            _composer.BackColor = t.Main;
            _composerBox.BackColor = t.Main;
            _chatInput.BackColor = t.Surface;
            _chatInput.ForeColor = t.Text;
            _chatList.BackColor = t.Main;
            _chatList.ForeColor = t.Text;
            _chatEmpty.BackColor = t.Main;
            _chatEmpty.ForeColor = t.Muted;
            _emojiButton.Underlay = t.Surface;
            _sendButton.Underlay = t.Surface;
            _chatMenu.Renderer = new ThemedMenuRenderer();
            _composerBox.Invalidate();
            _chatViewKey = String.Empty;
        }

        /// <summary>Called from the UI timer. Rebuilds the chat view only when something changed.</summary>
        private void RefreshChat(VoipEngine engine)
        {
            // Chat shows the highlighted channel (one click), whether or not you're in its voice.
            bool inChannel = engine != null && engine.IsRunning && engine.IsConnected && engine.ChatChannelId > 0;
            int channel = inChannel ? engine.ChatChannelId : 0;
            string channelName = inChannel ? engine.GetChannelName(channel) : String.Empty;
            if (String.IsNullOrWhiteSpace(channelName))
                channelName = "channel";

            _chatInput.Enabled = inChannel;
            _sendButton.Enabled = inChannel;
            _emojiButton.Enabled = inChannel;
            SetChatCue(inChannel ? "Message #" + channelName : "Pick a channel to chat");

            string key = (engine == null ? "none" : engine.GetHashCode().ToString()) + "|" + channel + "|" +
                (inChannel ? engine.ChatVersion : 0) + "|" + _chatList.ClientSize.Width + "|" + channelName + "|" + _chatFilter + "|" + _systemLines.Count + "|" + Theme.Current.Id;
            if (key == _chatViewKey)
                return;
            _chatViewKey = key;

            bool channelChanged = !Object.ReferenceEquals(engine, _chatEngine) || channel != _chatChannel;
            _chatEngine = engine;
            _chatChannel = channel;

            if (!inChannel)
            {
                _chatList.Items.Clear();
                _chatNewestSeen = 0;
                if (engine != null && engine.IsRunning)
                    ShowChatEmpty(engine.IsConnected
                        ? "Click a channel on the left to read its chat.\r\nDouble-click a voice channel to talk."
                        : (String.IsNullOrEmpty(engine.LastMessage) ? "Connecting…" : engine.LastMessage));
                else
                    _chatEmpty.Visible = false;
                return;
            }

            ChatMessage[] messages = engine.GetChat(channel);
            List<ChatRow> rows = BuildChatRows(messages, channel);
            if (rows.Count == 0)
            {
                _chatList.Items.Clear();
                _chatNewestSeen = 0;
                ShowChatEmpty(_chatFilter.Length > 0
                    ? "No messages in #" + channelName + " match “" + _chatFilter + "”."
                    : "Welcome to #" + channelName + "!\r\nTalk and chat with everyone in this channel. Messages are encrypted between you and the host.");
                return;
            }
            _chatEmpty.Visible = false;

            // Stay pinned to the bottom unless the user has scrolled up to read history.
            bool atBottom = channelChanged || _chatList.Items.Count == 0 || IsChatScrolledToBottom();
            int previousTop = _chatList.TopIndex;

            _chatList.BeginUpdate();
            _chatList.Items.Clear();
            int i;
            for (i = 0; i < rows.Count; i++)
                _chatList.Items.Add(rows[i]);
            _chatList.EndUpdate();

            ChatMessage newest = null;
            for (i = messages.Length - 1; i >= 0 && newest == null; i--)
            {
                if (!messages[i].IsReaction)
                    newest = messages[i];
            }
            bool ownNewest = newest != null && newest.IsLocal && (newest.Pending || newest.Sequence > _chatNewestSeen);
            if (atBottom || ownNewest)
                _chatList.TopIndex = Math.Max(0, _chatList.Items.Count - 1);
            else
                _chatList.TopIndex = Math.Min(previousTop, Math.Max(0, _chatList.Items.Count - 1));

            uint newestConfirmed = 0;
            bool fromOthers = false;
            for (i = 0; i < messages.Length; i++)
            {
                if (messages[i].Pending || messages[i].Failed || messages[i].IsReaction)
                    continue;
                if (!channelChanged && messages[i].Sequence > _chatNewestSeen && !messages[i].IsLocal)
                    fromOthers = true;
                if (messages[i].Sequence > newestConfirmed)
                    newestConfirmed = messages[i].Sequence;
            }
            if (fromOthers && _chatNewestSeen != 0)
            {
                FlashForAttention();
                if (!ContainsFocus || WindowState == FormWindowState.Minimized)
                    PlaySound(SoundCue.Message);
            }
            _chatNewestSeen = Math.Max(_chatNewestSeen, newestConfirmed);
        }

        private List<ChatRow> BuildChatRows(ChatMessage[] messages, int channel)
        {
            Dictionary<uint, List<ReactionSummary>> reactions = VoipEngine.SummarizeReactions(messages);

            // Merge messages and this channel's join/leave lines in time order.
            List<object> items = new List<object>();
            int i;
            for (i = 0; i < messages.Length; i++)
            {
                if (messages[i].IsReaction)
                    continue;
                if (_chatFilter.Length > 0 && messages[i].Text.IndexOf(_chatFilter, StringComparison.CurrentCultureIgnoreCase) < 0
                    && (messages[i].SenderName ?? String.Empty).IndexOf(_chatFilter, StringComparison.CurrentCultureIgnoreCase) < 0)
                    continue;
                items.Add(messages[i]);
            }
            if (_chatFilter.Length == 0)
            {
                foreach (SystemLine line in _systemLines)
                {
                    if (line.ChannelId == channel)
                        items.Add(line);
                }
            }
            items.Sort(delegate(object a, object b) { return TimeOf(a).CompareTo(TimeOf(b)); });

            List<ChatRow> rows = new List<ChatRow>();
            ChatMessage previous = null;
            DateTime previousDay = DateTime.MinValue;
            for (i = 0; i < items.Count; i++)
            {
                DateTime local = TimeOf(items[i]).ToLocalTime();
                if (local.Date != previousDay)
                {
                    ChatRow divider = new ChatRow();
                    divider.DayLabel = FormatDay(local);
                    rows.Add(divider);
                    previousDay = local.Date;
                    previous = null;
                }

                SystemLine system = items[i] as SystemLine;
                if (system != null)
                {
                    ChatRow row = new ChatRow();
                    row.SystemText = system.Text;
                    row.SystemTimeUtc = system.TimeUtc;
                    rows.Add(row);
                    previous = null;
                    continue;
                }

                ChatMessage m = (ChatMessage)items[i];
                ChatRow messageRow = new ChatRow();
                messageRow.Message = m;
                messageRow.ShowHeader = previous == null || previous.SenderId != m.SenderId ||
                    !String.Equals(previous.SenderName, m.SenderName, StringComparison.Ordinal) ||
                    (m.TimeUtc - previous.TimeUtc).TotalMinutes > 5.0;
                List<ReactionSummary> list;
                if (m.Sequence != 0 && reactions.TryGetValue(m.Sequence, out list))
                    messageRow.Reactions = list;
                rows.Add(messageRow);
                previous = m;
            }
            return rows;
        }

        private static DateTime TimeOf(object item)
        {
            ChatMessage m = item as ChatMessage;
            return m != null ? m.TimeUtc : ((SystemLine)item).TimeUtc;
        }

        /// <summary>Notes people joining/leaving the channel you're in (shown as chat lines).</summary>
        private void TrackChannelMembership(VoipEngine engine, RoomMemberInfo[] members)
        {
            int channel = engine.LocalChannelId;
            Dictionary<uint, string> now = new Dictionary<uint, string>();
            int i;
            for (i = 0; i < members.Length; i++)
            {
                if (members[i].ChannelId == channel && channel > 0)
                    now[members[i].Id] = members[i].Name;
            }

            bool sameChannel = _channelMemberSnapshot.ContainsKey(UInt32.MaxValue) && _channelMemberSnapshot[UInt32.MaxValue] == channel.ToString();
            if (sameChannel && channel > 0)
            {
                foreach (KeyValuePair<uint, string> pair in now)
                {
                    if (!_channelMemberSnapshot.ContainsKey(pair.Key) && pair.Key != engine.LocalMemberId)
                    {
                        AddSystemLine(channel, pair.Value + " joined", true);
                        PlaySound(SoundCue.PeerJoined);
                    }
                }
                foreach (KeyValuePair<uint, string> pair in _channelMemberSnapshot)
                {
                    if (pair.Key != UInt32.MaxValue && !now.ContainsKey(pair.Key))
                    {
                        AddSystemLine(channel, pair.Value + " left", false);
                        PlaySound(SoundCue.PeerLeft);
                    }
                }
            }
            now[UInt32.MaxValue] = channel.ToString();
            _channelMemberSnapshot = now;
        }

        private void AddSystemLine(int channel, string text, bool joined)
        {
            SystemLine line = new SystemLine();
            line.ChannelId = channel;
            line.TimeUtc = DateTime.UtcNow;
            line.Text = text;
            line.Joined = joined;
            _systemLines.Add(line);
            if (_systemLines.Count > 200)
                _systemLines.RemoveAt(0);
        }

        private bool IsChatScrolledToBottom()
        {
            int count = _chatList.Items.Count;
            if (count == 0)
                return true;
            int last = _chatList.IndexFromPoint(4, Math.Max(0, _chatList.ClientSize.Height - 2));
            return last < 0 || last >= count - 1;
        }

        private void ShowChatEmpty(string text)
        {
            _chatEmpty.Text = text;
            _chatEmpty.Visible = true;
            _chatEmpty.BringToFront();
        }

        private void SetChatFilter(string filter)
        {
            string clean = filter == null ? String.Empty : filter.Trim();
            if (clean == _chatFilter)
                return;
            _chatFilter = clean;
            _chatViewKey = String.Empty;
            RefreshChat(_engine);
        }

        private void ChatInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter || e.Shift)
                return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            SendChatFromInput();
        }

        private void SendChatFromInput()
        {
            VoipEngine engine = _engine;
            string text = _chatInput.Text;
            if (engine == null || String.IsNullOrWhiteSpace(text))
                return;
            if (engine.SendChat(text))
            {
                _chatInput.Clear();
                RefreshChat(engine);
            }
            _chatInput.Focus();
        }

        private void ShowEmojiPicker(Control anchor, ChatMessage reactTo)
        {
            EmojiPicker picker = new EmojiPicker();
            picker.BackColor = Theme.Current.Surface;
            Point screen = anchor.PointToScreen(new Point(anchor.Width - picker.Width, -picker.Height - 8));
            Rectangle area = Screen.FromControl(this).WorkingArea;
            screen.X = Math.Max(area.Left + 4, Math.Min(screen.X, area.Right - picker.Width - 4));
            screen.Y = Math.Max(area.Top + 4, screen.Y);
            picker.Location = screen;
            picker.Picked += delegate(EmojiEntry entry)
            {
                if (reactTo != null)
                {
                    if (_engine != null)
                        _engine.ToggleReaction(reactTo.Sequence, entry.Text);
                    return;
                }
                int caret = _chatInput.SelectionStart;
                _chatInput.Text = _chatInput.Text.Insert(caret, entry.Text);
                _chatInput.SelectionStart = caret + entry.Text.Length;
                _chatInput.Focus();
            };
            picker.Show(this);
        }

        // ----- Layout and drawing -----

        private static bool IsJumbo(List<object> tokens)
        {
            int emoji = 0;
            foreach (object t in tokens)
            {
                if (t is EmojiEntry) emoji++;
                else if (((string)t).Trim().Length > 0) return false;
            }
            return emoji > 0 && emoji <= 3;
        }

        private void EnsureLayout(Graphics g, ChatRow row, int width)
        {
            if (row.LayoutWidth == width && row.Runs != null)
                return;
            row.LayoutWidth = width;
            row.Runs = new List<TextRun>();
            List<object> tokens = EmojiPack.Tokenize(row.Message.Text);
            bool jumbo = IsJumbo(tokens);
            int emojiSize = jumbo ? JumboEmoji : InlineEmoji;
            int fontHeight = Ui.Body.Height;
            int lineHeight = Math.Max(fontHeight + 3, jumbo ? JumboEmoji + 4 : InlineEmoji + 1);
            int space = Math.Max(3, Ui.Measure(g, "a a", Ui.Body).Width - Ui.Measure(g, "aa", Ui.Body).Width);
            int x = 0;
            int y = 0;

            foreach (object token in tokens)
            {
                EmojiEntry emoji = token as EmojiEntry;
                if (emoji != null)
                {
                    if (x + emojiSize > width && x > 0) { x = 0; y += lineHeight; }
                    TextRun run = new TextRun();
                    run.Emoji = emoji;
                    run.Bounds = new Rectangle(x, y + (lineHeight - emojiSize) / 2, emojiSize, emojiSize);
                    row.Runs.Add(run);
                    x += emojiSize + 2;
                    continue;
                }

                foreach (Match word in Regex.Matches((string)token, @"\S+|\s+"))
                {
                    if (word.Value.Trim().Length == 0)
                    {
                        if (x > 0) x += space * word.Value.Length;
                        continue;
                    }
                    string piece = word.Value;
                    int w = Ui.Measure(g, piece, Ui.Body).Width;
                    if (x + w > width && x > 0) { x = 0; y += lineHeight; }
                    while (w > width && piece.Length > 1)
                    {
                        // A single very long word: break it by characters.
                        int fit = piece.Length - 1;
                        while (fit > 1 && Ui.Measure(g, piece.Substring(0, fit), Ui.Body).Width > width - x) fit--;
                        TextRun part = new TextRun();
                        part.Text = piece.Substring(0, fit);
                        part.Bounds = new Rectangle(x, y + (lineHeight - fontHeight) / 2, width - x, fontHeight);
                        row.Runs.Add(part);
                        piece = piece.Substring(fit);
                        x = 0;
                        y += lineHeight;
                        w = Ui.Measure(g, piece, Ui.Body).Width;
                    }
                    TextRun text = new TextRun();
                    text.Text = piece;
                    text.Bounds = new Rectangle(x, y + (lineHeight - fontHeight) / 2, w + 2, fontHeight);
                    row.Runs.Add(text);
                    x += w;
                }
            }
            row.TextHeight = y + lineHeight;
        }

        private int ChatTextWidth { get { return Math.Max(80, _chatList.ClientSize.Width - ChatTextLeft - ChatRightPadding); } }

        private void ChatMeasureItem(object sender, MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _chatList.Items.Count)
                return;
            ChatRow row = _chatList.Items[e.Index] as ChatRow;
            if (row == null)
                return;
            if (row.Message == null)
            {
                e.ItemHeight = row.DayLabel != null ? 38 : 28;
                return;
            }

            EnsureLayout(e.Graphics, row, ChatTextWidth);
            int height = row.TextHeight + 4;
            if (row.ShowHeader)
                height += 28;
            if (row.Reactions != null && row.Reactions.Count > 0)
                height += ChipHeight + 8;
            if (row.Message.Failed)
                height += 16;
            e.ItemHeight = Math.Min(255, Math.Max(row.ShowHeader ? 50 : 24, height));
        }

        private const TextFormatFlags RunFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        private void ChatDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _chatList.Items.Count)
                return;
            ChatRow row = _chatList.Items[e.Index] as ChatRow;
            if (row == null)
                return;

            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool hover = e.Index == _chatHover && row.Message != null;
            Color back = selected && row.Message != null ? Ui.Mix(t.Main, t.Text, 0.06f) : (hover ? Ui.Mix(t.Main, t.Text, 0.035f) : t.Main);
            using (SolidBrush b = new SolidBrush(back))
                g.FillRectangle(b, e.Bounds);

            if (row.DayLabel != null)
            {
                DrawDayDivider(g, e.Bounds, row.DayLabel);
                return;
            }
            if (row.SystemText != null)
            {
                Icons.Draw(g, IconKind.Speaker, new RectangleF(e.Bounds.Left + 34, e.Bounds.Top + 7, 14, 14), t.Good);
                Ui.DrawText(g, row.SystemText + "  ·  " + row.SystemTimeUtc.ToLocalTime().ToShortTimeString(), Ui.Small,
                    new Rectangle(e.Bounds.Left + ChatTextLeft, e.Bounds.Top, e.Bounds.Width - ChatTextLeft, e.Bounds.Height), t.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                return;
            }

            ChatMessage m = row.Message;
            int y = e.Bounds.Top + 3;
            if (row.ShowHeader)
            {
                y = e.Bounds.Top + 10;
                Ui.DrawAvatar(g, new Rectangle(e.Bounds.Left + 20, y, 38, 38), m.SenderName, false, back, m.IsLocal ? MyPicture() : AvatarImage(m.SenderId));
                string name = String.IsNullOrEmpty(m.SenderName) ? "Guest" : m.SenderName;
                Size nameSize = Ui.Measure(g, name, Ui.BodyBold);
                Ui.DrawText(g, name, Ui.BodyBold, new Point(e.Bounds.Left + ChatTextLeft, y), Ui.NameColor(name));
                Ui.DrawText(g, FormatTime(m.TimeUtc), Ui.Small, new Point(e.Bounds.Left + ChatTextLeft + nameSize.Width + 8, y + 2), t.Muted);
                y += 24;
            }

            EnsureLayout(g, row, ChatTextWidth);
            Color textColor = m.Pending || m.Failed ? t.Muted : t.Text2;
            Point origin = new Point(e.Bounds.Left + ChatTextLeft, y);
            foreach (TextRun run in row.Runs)
            {
                Rectangle r = run.Bounds;
                r.Offset(origin);
                if (run.Emoji != null)
                    EmojiPicker.DrawEmoji(g, run.Emoji, r);
                else
                {
                    if (_chatFilter.Length > 0 && run.Text.IndexOf(_chatFilter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        Ui.FillRound(g, Ui.Mix(back, t.Accent, 0.30f), new RectangleF(r.X - 2, r.Y - 1, r.Width + 2, r.Height + 2), 4);
                    TextRenderer.DrawText(g, run.Text, Ui.Body, r.Location, textColor, RunFlags);
                }
            }
            y += row.TextHeight + 4;

            row.ChipRects.Clear();
            if (row.Reactions != null && row.Reactions.Count > 0)
            {
                int x = e.Bounds.Left + ChatTextLeft;
                foreach (ReactionSummary reaction in row.Reactions)
                {
                    string count = reaction.Count.ToString();
                    int w = 16 + 8 + Ui.Measure(g, count, Ui.SmallBold).Width + 20;
                    Rectangle chip = new Rectangle(x, y + 2, w, ChipHeight);
                    Color fill = reaction.Mine ? Ui.Mix(back, t.Accent, t.Dark ? 0.22f : 0.14f) : Ui.Mix(back, t.Text, 0.07f);
                    Ui.FillRound(g, fill, chip, ChipHeight / 2);
                    if (reaction.Mine)
                        Ui.StrokeRound(g, Ui.Mix(back, t.Accent, 0.7f), new RectangleF(chip.X + 0.5f, chip.Y + 0.5f, chip.Width - 1, chip.Height - 1), ChipHeight / 2, 1.2f);
                    EmojiPicker.DrawEmoji(g, EmojiPack.Find(reaction.Emoji), new Rectangle(chip.X + 9, chip.Y + 5, 16, 16));
                    Ui.DrawText(g, count, Ui.SmallBold, new Point(chip.X + 30, chip.Y + 5), reaction.Mine ? t.Accent2 : t.Text2);
                    row.ChipRects.Add(chip);
                    x += w + 6;
                }
                y += ChipHeight + 8;
            }

            if (m.Failed)
                Ui.DrawText(g, "Not delivered — right-click to dismiss", Ui.Small, new Point(e.Bounds.Left + ChatTextLeft, y), t.Danger);

            if (hover && !m.Pending && !m.Failed && m.Sequence != 0)
            {
                Rectangle react = ReactButtonRect(e.Bounds);
                Ui.FillRound(g, t.Surface, react, 8);
                Ui.StrokeRound(g, t.Line, new RectangleF(react.X + 0.5f, react.Y + 0.5f, react.Width - 1, react.Height - 1), 8, 1f);
                Icons.Draw(g, IconKind.Smile, new RectangleF(react.X + 7, react.Y + 6, 18, 18), t.Text2);
            }
        }

        private static Rectangle ReactButtonRect(Rectangle rowBounds)
        {
            return new Rectangle(rowBounds.Right - 54, rowBounds.Top + 4, 32, 30);
        }

        private void DrawDayDivider(Graphics g, Rectangle bounds, string label)
        {
            Theme t = Theme.Current;
            int mid = bounds.Top + bounds.Height / 2;
            Size size = Ui.Measure(g, label, Ui.SmallBold);
            int center = bounds.Left + bounds.Width / 2;
            using (Pen pen = new Pen(t.Line))
            {
                g.DrawLine(pen, bounds.Left + 20, mid, center - size.Width / 2 - 10, mid);
                g.DrawLine(pen, center + size.Width / 2 + 10, mid, bounds.Right - 20, mid);
            }
            Ui.DrawText(g, label, Ui.SmallBold, new Point(center - size.Width / 2, mid - size.Height / 2), t.Muted);
        }

        private void SetChatHover(int index)
        {
            if (index == _chatHover)
                return;
            int old = _chatHover;
            _chatHover = index;
            if (old >= 0 && old < _chatList.Items.Count)
                _chatList.Invalidate(_chatList.GetItemRectangle(old));
            if (index >= 0 && index < _chatList.Items.Count)
                _chatList.Invalidate(_chatList.GetItemRectangle(index));
        }

        private void ChatMouseMove(object sender, MouseEventArgs e)
        {
            int index = _chatList.IndexFromPoint(e.Location);
            SetChatHover(index);
            ChatRow row = index >= 0 && index < _chatList.Items.Count ? _chatList.Items[index] as ChatRow : null;
            bool overTarget = false;
            if (row != null && row.Message != null)
            {
                Rectangle bounds = _chatList.GetItemRectangle(index);
                overTarget = ReactButtonRect(bounds).Contains(e.Location);
                foreach (Rectangle chip in row.ChipRects)
                    overTarget |= chip.Contains(e.Location);
            }
            _chatList.Cursor = overTarget ? Cursors.Hand : Cursors.Default;
        }

        private void ChatMouseDown(object sender, MouseEventArgs e)
        {
            int index = _chatList.IndexFromPoint(e.Location);
            _chatContextRow = index >= 0 && index < _chatList.Items.Count ? _chatList.Items[index] as ChatRow : null;
            if (e.Button == MouseButtons.Left && _chatContextRow != null && _chatContextRow.Message != null && _chatContextRow.ShowHeader)
            {
                // Click a picture or name to open that person's card.
                Rectangle bounds = _chatList.GetItemRectangle(index);
                if (e.X < ChatTextLeft + 220 && e.Y < bounds.Top + 50 && (e.X < ChatTextLeft || e.Y < bounds.Top + 30))
                {
                    RoomMemberInfo who = FindMember(_chatContextRow.Message.SenderId);
                    if (who != null)
                        ShowProfileCard(who, _chatList.PointToScreen(e.Location));
                    return;
                }
            }
            if (e.Button == MouseButtons.Right && _chatContextRow != null && _chatContextRow.Message != null)
            {
                _chatList.SelectedIndex = index;
                _chatMenu.Show(_chatList, e.Location);
            }
        }

        private void ChatMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _engine == null)
                return;
            int index = _chatList.IndexFromPoint(e.Location);
            ChatRow row = index >= 0 && index < _chatList.Items.Count ? _chatList.Items[index] as ChatRow : null;
            if (row == null || row.Message == null || row.Message.Sequence == 0)
                return;

            Rectangle bounds = _chatList.GetItemRectangle(index);
            if (ReactButtonRect(bounds).Contains(e.Location))
            {
                ShowEmojiPickerAt(e.Location, row.Message);
                return;
            }
            int i;
            for (i = 0; i < row.ChipRects.Count && row.Reactions != null && i < row.Reactions.Count; i++)
            {
                if (row.ChipRects[i].Contains(e.Location))
                {
                    _engine.ToggleReaction(row.Message.Sequence, row.Reactions[i].Emoji);
                    return;
                }
            }
        }

        private void ShowEmojiPickerAt(Point listPoint, ChatMessage reactTo)
        {
            Control anchor = new Control();
            anchor.Bounds = new Rectangle(listPoint.X - 340, listPoint.Y, 340, 1);
            _chatList.Controls.Add(anchor);
            ShowEmojiPicker(anchor, reactTo);
            _chatList.Controls.Remove(anchor);
            anchor.Dispose();
        }

        private void ChatMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _chatMenu.Items.Clear();
            ChatRow row = _chatContextRow;
            if (row == null || row.Message == null)
            {
                e.Cancel = true;
                return;
            }

            ChatMessage message = row.Message;
            if (!message.Pending && !message.Failed && message.Sequence != 0)
            {
                foreach (string quick in QuickReactions)
                {
                    EmojiEntry entry = EmojiPack.Find(quick);
                    if (entry == null)
                        continue;
                    ToolStripMenuItem item = new ToolStripMenuItem("React " + entry.Text, EmojiPack.GetImage(entry));
                    item.Click += delegate { if (_engine != null) _engine.ToggleReaction(message.Sequence, entry.Text); };
                    _chatMenu.Items.Add(item);
                }
                ToolStripMenuItem more = new ToolStripMenuItem("More reactions…");
                more.Click += delegate { ShowEmojiPickerAt(_chatList.PointToClient(Cursor.Position), message); };
                _chatMenu.Items.Add(more);
                _chatMenu.Items.Add(new ToolStripSeparator());
            }
            _chatMenu.Items.Add("Copy text", null, delegate { CopyChatText(message.Text); });
            if (message.Failed)
            {
                ToolStripMenuItem dismiss = new ToolStripMenuItem("Dismiss undelivered messages");
                dismiss.ForeColor = Theme.Current.Danger;
                dismiss.Click += delegate
                {
                    if (_engine != null)
                        _engine.DismissFailedChat();
                };
                _chatMenu.Items.Add(dismiss);
            }
        }

        private void ChatListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                ChatRow row = _chatList.SelectedItem as ChatRow;
                if (row != null && row.Message != null)
                    CopyChatText(row.Message.Text);
                e.Handled = true;
            }
        }

        private void CopyChatText(string text)
        {
            if (String.IsNullOrEmpty(text))
                return;
            try { Clipboard.SetText(text); } catch { }
        }

        private void SetChatCue(string cue)
        {
            if (cue == _chatCue)
                return;
            _chatCue = cue;
            ApplyChatCue();
        }

        private void ApplyChatCue()
        {
            if (!_chatInput.IsHandleCreated)
                return;
            try { ChatNativeMethods.SendMessage(_chatInput.Handle, ChatNativeMethods.EM_SETCUEBANNER, (IntPtr)1, _chatCue); }
            catch { } // Placeholder text is cosmetic.
        }

        private void FlashForAttention()
        {
            if (ContainsFocus && WindowState != FormWindowState.Minimized)
                return;
            try
            {
                ChatNativeMethods.FLASHWINFO info = new ChatNativeMethods.FLASHWINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(ChatNativeMethods.FLASHWINFO));
                info.hwnd = Handle;
                info.dwFlags = ChatNativeMethods.FLASHW_TRAY | ChatNativeMethods.FLASHW_TIMERNOFG;
                info.uCount = 3;
                ChatNativeMethods.FlashWindowEx(ref info);
            }
            catch { }
        }

        private static string FormatTime(DateTime utc)
        {
            DateTime local = utc.ToLocalTime();
            string time = local.ToShortTimeString();
            if (local.Date == DateTime.Today)
                return "Today at " + time;
            if (local.Date == DateTime.Today.AddDays(-1))
                return "Yesterday at " + time;
            return local.ToShortDateString() + " " + time;
        }

        private static string FormatDay(DateTime local)
        {
            if (local.Date == DateTime.Today)
                return "Today";
            if (local.Date == DateTime.Today.AddDays(-1))
                return "Yesterday";
            return local.ToLongDateString();
        }
    }

    /// <summary>The rounded message box behind the chat input.</summary>
    internal sealed class ComposerSurface : Panel
    {
        public ComposerSurface()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme.Current;
            e.Graphics.Clear(t.Main);
            Ui.FillRound(e.Graphics, t.Surface, new RectangleF(0, 0, Width, Height), 14);
            Ui.StrokeRound(e.Graphics, t.Line, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 14, 1f);
        }
    }

    /// <summary>Context menus in the current theme.</summary>
    internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new ThemedColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.ForeColor != Theme.Current.Danger)
                e.TextColor = e.Item.Enabled ? Theme.Current.Text : Theme.Current.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Current.Text2;
            base.OnRenderArrow(e);
        }

        private sealed class ThemedColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Current.Surface; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Current.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Current.Surface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Current.Surface; } }
            public override Color MenuBorder { get { return Theme.Current.Line; } }
            public override Color MenuItemBorder { get { return Theme.Current.Surface2; } }
            public override Color MenuItemSelected { get { return Theme.Current.Surface2; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Current.Surface2; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Current.Surface2; } }
            public override Color SeparatorDark { get { return Theme.Current.Line; } }
            public override Color SeparatorLight { get { return Theme.Current.Line; } }
            public override Color CheckBackground { get { return Theme.Current.AccentSoft; } }
            public override Color CheckSelectedBackground { get { return Theme.Current.AccentSoft; } }
        }
    }

    internal static class ChatNativeMethods
    {
        public const int EM_SETCUEBANNER = 0x1501;
        public const uint FLASHW_TRAY = 0x00000002;
        public const uint FLASHW_TIMERNOFG = 0x0000000C;

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlashWindowEx(ref FLASHWINFO info);
    }
}
