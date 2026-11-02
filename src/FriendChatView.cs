using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>
    /// One friend's chat, drawn the same way as room chat: colour emoji, day dividers, reaction
    /// chips and search highlights. Text from other people is only ever drawn.
    /// </summary>
    internal sealed class FriendChatView : ListBox
    {
        private const int TextLeft = 72;
        private const int RightPadding = 24;
        private const int InlineEmoji = 21;
        private const int JumboEmoji = 44;
        private const int ChipHeight = 26;
        private const TextFormatFlags RunFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        private static readonly string[] QuickReactions = { "👍", "❤️", "😂", "🔥", "🎉", "😮" };

        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private string _theirName = String.Empty;
        private string _myName = String.Empty;
        private string _filter = String.Empty;
        private string _lastKey;
        private int _hover = -1;
        private Row _menuRow;

        /// <summary>The person clicked a reaction: the message's id, then the emoji.</summary>
        public event Action<ulong, string> ReactRequested;

        /// <summary>A message with a picture or a YouTube link was clicked: its text.</summary>
        public event Action<string> OpenRequested;

        private sealed class Row
        {
            public FriendChatEntry Entry;
            public string DayLabel;
            public bool ShowHeader;
            public List<FriendReaction> Reactions;
            public int LayoutWidth = -1;
            public List<Run> Runs;
            public int TextHeight;
            public readonly List<Rectangle> Chips = new List<Rectangle>();
        }

        private sealed class Run
        {
            public string Text;
            public EmojiEntry Emoji;
            public Rectangle Bounds;
        }

        public FriendChatView()
        {
            BorderStyle = BorderStyle.None;
            DrawMode = DrawMode.OwnerDrawVariable;
            IntegralHeight = false;
            HorizontalScrollbar = false;
            _menu.Opening += MenuOpening;
            Resize += delegate { _lastKey = null; };
        }

        public void SetFilter(string filter)
        {
            _filter = filter == null ? String.Empty : filter.Trim();
            _lastKey = null;
        }

        /// <summary>Rebuilds only when something visible changed, and stays at the bottom if you were there.</summary>
        public void Show(List<FriendChatEntry> entries, string theirName, string myName, bool switchedChat)
        {
            int delivered = 0;
            foreach (FriendChatEntry entry in entries)
            {
                if (entry.Delivered)
                    delivered++;
            }
            string key = entries.Count + "|" + delivered + "|" + _filter + "|" + ClientSize.Width + "|" + theirName + "|" + myName + "|" + Theme.Current.Id + "|" + switchedChat;
            if (key == _lastKey && !switchedChat)
                return;
            _lastKey = key;
            _theirName = theirName;
            _myName = myName;

            bool atBottom = switchedChat || Items.Count == 0 || IsAtBottom();
            int top = TopIndex;
            BeginUpdate();
            Items.Clear();
            foreach (Row row in BuildRows(entries))
                Items.Add(row);
            EndUpdate();
            if (Items.Count > 0)
                TopIndex = atBottom ? Items.Count - 1 : Math.Min(top, Items.Count - 1);
            _hover = -1;
        }

        private bool IsAtBottom()
        {
            if (Items.Count == 0)
                return true;
            int last = IndexFromPoint(4, Math.Max(0, ClientSize.Height - 2));
            return last < 0 || last >= Items.Count - 1;
        }

        private List<Row> BuildRows(List<FriendChatEntry> entries)
        {
            Dictionary<ulong, List<FriendReaction>> reactions = FriendChat.Summarize(entries);
            List<Row> rows = new List<Row>();
            FriendChatEntry previous = null;
            DateTime previousDay = DateTime.MinValue;
            foreach (FriendChatEntry entry in entries)
            {
                if (entry.ReactionTo != 0)
                    continue;
                if (_filter.Length > 0 && entry.Text.IndexOf(_filter, StringComparison.CurrentCultureIgnoreCase) < 0)
                    continue;
                DateTime local = entry.TimeUtc.ToLocalTime();
                if (local.Date != previousDay)
                {
                    Row divider = new Row();
                    divider.DayLabel = local.Date == DateTime.Today ? "Today" : (local.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : local.ToLongDateString());
                    rows.Add(divider);
                    previousDay = local.Date;
                    previous = null;
                }
                Row row = new Row();
                row.Entry = entry;
                row.ShowHeader = previous == null || previous.Mine != entry.Mine || (entry.TimeUtc - previous.TimeUtc).TotalMinutes > 5.0;
                List<FriendReaction> list;
                if (entry.MessageId != 0 && reactions.TryGetValue(entry.MessageId, out list))
                    row.Reactions = list;
                rows.Add(row);
                previous = entry;
            }
            return rows;
        }

        private int TextWidth { get { return Math.Max(80, ClientSize.Width - TextLeft - RightPadding); } }

        private void EnsureLayout(Graphics g, Row row, int width)
        {
            if (row.LayoutWidth == width && row.Runs != null)
                return;
            row.LayoutWidth = width;
            row.Runs = new List<Run>();
            List<object> tokens = EmojiPack.Tokenize(MainForm.ChatRowText(row.Entry.Text));
            int emojiCount = 0;
            bool onlyEmoji = true;
            foreach (object token in tokens)
            {
                if (token is EmojiEntry) emojiCount++;
                else if (((string)token).Trim().Length > 0) onlyEmoji = false;
            }
            bool jumbo = onlyEmoji && emojiCount > 0 && emojiCount <= 3;
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
                    Run run = new Run();
                    run.Emoji = emoji;
                    run.Bounds = new Rectangle(x, y + (lineHeight - emojiSize) / 2, emojiSize, emojiSize);
                    row.Runs.Add(run);
                    x += emojiSize + 2;
                    continue;
                }
                foreach (Match word in Regex.Matches((string)token, @"\n|[^\S\n]+|\S+"))
                {
                    if (word.Value == "\n")
                    {
                        x = 0;
                        y += lineHeight;
                        continue;
                    }
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
                        int fit = piece.Length - 1;
                        while (fit > 1 && Ui.Measure(g, piece.Substring(0, fit), Ui.Body).Width > width - x) fit--;
                        Run part = new Run();
                        part.Text = piece.Substring(0, fit);
                        part.Bounds = new Rectangle(x, y + (lineHeight - fontHeight) / 2, width - x, fontHeight);
                        row.Runs.Add(part);
                        piece = piece.Substring(fit);
                        x = 0;
                        y += lineHeight;
                        w = Ui.Measure(g, piece, Ui.Body).Width;
                    }
                    Run text = new Run();
                    text.Text = piece;
                    text.Bounds = new Rectangle(x, y + (lineHeight - fontHeight) / 2, w + 2, fontHeight);
                    row.Runs.Add(text);
                    x += w;
                }
            }
            row.TextHeight = y + lineHeight;
        }

        protected override void OnMeasureItem(MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count)
                return;
            Row row = (Row)Items[e.Index];
            if (row.Entry == null)
            {
                e.ItemHeight = 38;
                return;
            }
            EnsureLayout(e.Graphics, row, TextWidth);
            int height = row.TextHeight + 4;
            if (row.ShowHeader)
                height += 28;
            if (row.Reactions != null && row.Reactions.Count > 0)
                height += ChipHeight + 8;
            e.ItemHeight = Math.Min(255, Math.Max(row.ShowHeader ? 50 : 24, height));
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count)
                return;
            Row row = (Row)Items[e.Index];
            Theme t = Theme.Current;
            Graphics g = e.Graphics;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool hover = e.Index == _hover && row.Entry != null;
            Color back = selected && row.Entry != null ? Ui.Mix(t.Main, t.Text, 0.06f) : (hover ? Ui.Mix(t.Main, t.Text, 0.035f) : t.Main);
            using (SolidBrush b = new SolidBrush(back))
                g.FillRectangle(b, e.Bounds);

            if (row.DayLabel != null)
            {
                int mid = e.Bounds.Top + e.Bounds.Height / 2;
                Size size = Ui.Measure(g, row.DayLabel, Ui.SmallBold);
                int center = e.Bounds.Left + e.Bounds.Width / 2;
                using (Pen pen = new Pen(t.Line))
                {
                    g.DrawLine(pen, e.Bounds.Left + 20, mid, center - size.Width / 2 - 10, mid);
                    g.DrawLine(pen, center + size.Width / 2 + 10, mid, e.Bounds.Right - 20, mid);
                }
                Ui.DrawText(g, row.DayLabel, Ui.SmallBold, new Point(center - size.Width / 2, mid - size.Height / 2), t.Muted);
                return;
            }

            FriendChatEntry entry = row.Entry;
            int y = e.Bounds.Top + 3;
            if (row.ShowHeader)
            {
                y = e.Bounds.Top + 10;
                string name = entry.Mine ? _myName : _theirName;
                Ui.DrawAvatar(g, new Rectangle(e.Bounds.Left + 20, y, 38, 38), name, false, back);
                Size nameSize = Ui.Measure(g, name, Ui.BodyBold);
                Ui.DrawText(g, name, Ui.BodyBold, new Point(e.Bounds.Left + TextLeft, y), Ui.NameColor(name));
                string when = entry.TimeUtc.ToLocalTime().ToShortTimeString();
                Ui.DrawText(g, when, Ui.Small, new Point(e.Bounds.Left + TextLeft + nameSize.Width + 8, y + 2), t.Muted);
                y += 24;
            }

            EnsureLayout(g, row, TextWidth);
            bool waiting = entry.Mine && !entry.Delivered;
            Color textColor = waiting ? t.Muted : t.Text2;
            Point origin = new Point(e.Bounds.Left + TextLeft, y);
            foreach (Run run in row.Runs)
            {
                Rectangle r = run.Bounds;
                r.Offset(origin);
                if (run.Emoji != null)
                    EmojiPicker.DrawEmoji(g, run.Emoji, r);
                else
                {
                    if (_filter.Length > 0 && run.Text.IndexOf(_filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        Ui.FillRound(g, Ui.Mix(back, t.Accent, 0.30f), new RectangleF(r.X - 2, r.Y - 1, r.Width + 2, r.Height + 2), 4);
                    TextRenderer.DrawText(g, run.Text, Ui.Body, r.Location, textColor, RunFlags);
                }
            }
            y += row.TextHeight + 4;

            row.Chips.Clear();
            if (row.Reactions != null && row.Reactions.Count > 0)
            {
                int x = e.Bounds.Left + TextLeft;
                foreach (FriendReaction reaction in row.Reactions)
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
                    row.Chips.Add(chip);
                    x += w + 6;
                }
                y += ChipHeight + 8;
            }

            if (waiting)
                Ui.DrawText(g, "Waiting to send", Ui.Small, new Point(e.Bounds.Right - 130, e.Bounds.Top + 6), t.Muted);
            else if (hover && entry.MessageId != 0)
            {
                Rectangle react = ReactRect(e.Bounds);
                Ui.FillRound(g, t.Surface, react, 8);
                Ui.StrokeRound(g, t.Line, new RectangleF(react.X + 0.5f, react.Y + 0.5f, react.Width - 1, react.Height - 1), 8, 1f);
                Icons.Draw(g, IconKind.Smile, new RectangleF(react.X + 7, react.Y + 6, 18, 18), t.Text2);
            }
        }

        private static Rectangle ReactRect(Rectangle bounds)
        {
            return new Rectangle(bounds.Right - 54, bounds.Top + 4, 32, 30);
        }

        private Row RowAt(Point point, out int index)
        {
            index = IndexFromPoint(point);
            return index >= 0 && index < Items.Count ? (Row)Items[index] : null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index;
            Row row = RowAt(e.Location, out index);
            if (index != _hover)
            {
                int old = _hover;
                _hover = index;
                if (old >= 0 && old < Items.Count)
                    Invalidate(GetItemRectangle(old));
                if (index >= 0 && index < Items.Count)
                    Invalidate(GetItemRectangle(index));
            }
            bool target = false;
            if (row != null && row.Entry != null && row.Entry.MessageId != 0 && row.Entry.Delivered)
            {
                target = ReactRect(GetItemRectangle(index)).Contains(e.Location);
                foreach (Rectangle chip in row.Chips)
                    target |= chip.Contains(e.Location);
            }
            Cursor = target ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover >= 0 && _hover < Items.Count)
                Invalidate(GetItemRectangle(_hover));
            _hover = -1;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int index;
            Row row = RowAt(e.Location, out index);
            if (row == null || row.Entry == null)
                return;
            if (e.Button == MouseButtons.Right)
            {
                SelectedIndex = index;
                _menuRow = row;
                _menu.Renderer = new ThemedMenuRenderer();
                _menu.Show(this, e.Location);
                return;
            }
            if (e.Button == MouseButtons.Left && (row.Entry.MessageId == 0 || !row.Entry.Delivered))
            {
                if (OpenRequested != null)
                    OpenRequested(row.Entry.Text); // Such as your own picture, before it's delivered.
                return;
            }
            if (e.Button != MouseButtons.Left || row.Entry.MessageId == 0 || !row.Entry.Delivered)
                return;
            if (ReactRect(GetItemRectangle(index)).Contains(e.Location))
            {
                PickReaction(row.Entry);
                return;
            }
            int i;
            for (i = 0; i < row.Chips.Count && row.Reactions != null && i < row.Reactions.Count; i++)
            {
                if (row.Chips[i].Contains(e.Location))
                {
                    Raise(row.Entry.MessageId, row.Reactions[i].Emoji);
                    return;
                }
            }
            if (OpenRequested != null)
                OpenRequested(row.Entry.Text); // A picture or a YouTube link.
        }

        private void Raise(ulong messageId, string emoji)
        {
            if (ReactRequested != null)
                ReactRequested(messageId, emoji);
        }

        private void PickReaction(FriendChatEntry entry)
        {
            EmojiPicker picker = new EmojiPicker();
            picker.BackColor = Theme.Current.Surface;
            Point at = Cursor.Position;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            picker.Location = new Point(Math.Max(area.Left + 4, Math.Min(at.X - picker.Width, area.Right - picker.Width - 4)), Math.Max(area.Top + 4, Math.Min(at.Y, area.Bottom - picker.Height - 4)));
            picker.Picked += delegate(EmojiEntry picked) { Raise(entry.MessageId, picked.Text); };
            picker.Show(FindForm());
        }

        private void MenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _menu.Items.Clear();
            Row row = _menuRow;
            if (row == null || row.Entry == null)
            {
                e.Cancel = true;
                return;
            }
            FriendChatEntry entry = row.Entry;
            if (entry.MessageId != 0 && entry.Delivered)
            {
                foreach (string quick in QuickReactions)
                {
                    EmojiEntry emoji = EmojiPack.Find(quick);
                    if (emoji == null)
                        continue;
                    ToolStripMenuItem item = new ToolStripMenuItem("React " + emoji.Text, EmojiPack.GetImage(emoji));
                    item.Click += delegate { Raise(entry.MessageId, emoji.Text); };
                    _menu.Items.Add(item);
                }
                ToolStripMenuItem more = new ToolStripMenuItem("More reactions…");
                more.Click += delegate { PickReaction(entry); };
                _menu.Items.Add(more);
                _menu.Items.Add(new ToolStripSeparator());
            }
            _menu.Items.Add("Copy text", null, delegate
            {
                try { Clipboard.SetText(entry.Text); }
                catch { }
            });
        }
    }
}
