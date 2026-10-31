using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>One line in the chat view: a day divider or a message (with or without the name header).</summary>
    internal sealed class ChatRow
    {
        public ChatMessage Message;
        public bool ShowHeader;
        public string DayLabel;
    }

    /// <summary>Text chat panel for the voice channel you are in.</summary>
    internal sealed partial class MainForm
    {
        private const int ChatTextLeft = 72;
        private const int ChatRightPadding = 20;

        private static readonly Color ChatComposerBg = Color.FromArgb(56, 58, 64);
        private static readonly Color ChatSelected = Color.FromArgb(44, 46, 51);
        private static readonly Color[] NameColors = new Color[]
        {
            Color.FromArgb(88, 101, 242), Color.FromArgb(35, 165, 90), Color.FromArgb(240, 178, 50),
            Color.FromArgb(235, 69, 158), Color.FromArgb(0, 168, 252), Color.FromArgb(242, 63, 67),
            Color.FromArgb(155, 89, 182), Color.FromArgb(26, 188, 156)
        };

        private readonly ListBox _chatList = new ListBox();
        private readonly TextBox _chatInput = new TextBox();
        private readonly Label _chatEmpty = new Label();
        private readonly ContextMenuStrip _chatMenu = new ContextMenuStrip();
        private readonly Font _chatTextFont = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _chatNameFont = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _chatTimeFont = new Font("Segoe UI", 7.5F, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _chatAvatarFont = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point);
        private string _chatViewKey = String.Empty;
        private string _chatCue = String.Empty;
        private VoipEngine _chatEngine;
        private int _chatChannel = -1;
        private uint _chatNewestSeen;
        private ChatRow _chatContextRow;

        private void BuildChatArea(Panel main, int top)
        {
            Panel composer = new Panel();
            composer.Dock = DockStyle.Bottom;
            composer.Height = 72;
            composer.BackColor = MainBg;
            composer.Padding = new Padding(16, 4, 16, 20);
            main.Controls.Add(composer);

            Panel box = new Panel();
            box.Dock = DockStyle.Fill;
            box.BackColor = ChatComposerBg;
            box.Padding = new Padding(14, 13, 14, 8);
            box.Cursor = Cursors.IBeam;
            box.Click += delegate { if (_chatInput.Enabled) _chatInput.Focus(); };
            composer.Controls.Add(box);

            _chatInput.Dock = DockStyle.Fill;
            _chatInput.BorderStyle = BorderStyle.None;
            _chatInput.BackColor = ChatComposerBg;
            _chatInput.ForeColor = TextMain;
            _chatInput.Font = _chatTextFont;
            _chatInput.MaxLength = VoipEngine.MaxChatChars;
            _chatInput.Enabled = false;
            _chatInput.KeyDown += ChatInputKeyDown;
            _chatInput.HandleCreated += delegate { ApplyChatCue(); };
            box.Controls.Add(_chatInput);

            _chatList.Location = new Point(0, top);
            _chatList.Size = new Size(main.Width, main.Height - top - composer.Height);
            _chatList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _chatList.BackColor = MainBg;
            _chatList.ForeColor = TextMain;
            _chatList.BorderStyle = BorderStyle.None;
            _chatList.DrawMode = DrawMode.OwnerDrawVariable;
            _chatList.IntegralHeight = false;
            _chatList.HorizontalScrollbar = false;
            _chatList.MeasureItem += ChatMeasureItem;
            _chatList.DrawItem += ChatDrawItem;
            _chatList.MouseDown += ChatMouseDown;
            _chatList.KeyDown += ChatListKeyDown;
            _chatList.Resize += delegate { _chatViewKey = String.Empty; };
            main.Controls.Add(_chatList);

            _chatEmpty.AutoSize = false;
            _chatEmpty.Location = _chatList.Location;
            _chatEmpty.Size = _chatList.Size;
            _chatEmpty.Anchor = _chatList.Anchor;
            _chatEmpty.BackColor = MainBg;
            _chatEmpty.ForeColor = TextMuted;
            _chatEmpty.Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            _chatEmpty.TextAlign = ContentAlignment.MiddleCenter;
            main.Controls.Add(_chatEmpty);
            _chatEmpty.BringToFront();

            _chatMenu.BackColor = Sidebar;
            _chatMenu.ForeColor = TextMain;
            _chatMenu.ShowImageMargin = false;
            _chatMenu.Opening += ChatMenuOpening;

            ShowChatEmpty("Join a room and a voice channel to start chatting.");
        }

        /// <summary>Called from the UI timer. Rebuilds the chat view only when something changed.</summary>
        private void RefreshChat(VoipEngine engine)
        {
            bool inChannel = engine != null && engine.IsRunning && engine.IsConnected && engine.LocalChannelId > 0;
            int channel = inChannel ? engine.LocalChannelId : 0;
            string channelName = inChannel ? engine.GetChannelName(channel) : String.Empty;
            if (String.IsNullOrWhiteSpace(channelName))
                channelName = "channel";

            _chatInput.Enabled = inChannel;
            SetChatCue(inChannel ? "Message #" + channelName : "Join a voice channel to chat");

            string key = (engine == null ? "none" : engine.GetHashCode().ToString()) + "|" + channel + "|" +
                (inChannel ? engine.ChatVersion : 0) + "|" + _chatList.ClientSize.Width + "|" + channelName;
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
                ShowChatEmpty(engine != null && engine.IsRunning && engine.IsConnected
                    ? "You're in the room lobby.\r\nJoin a voice channel to talk and chat with the people in it."
                    : "Join a room and a voice channel to start chatting.");
                return;
            }

            ChatMessage[] messages = engine.GetChat(channel);
            if (messages.Length == 0)
            {
                _chatList.Items.Clear();
                _chatNewestSeen = 0;
                ShowChatEmpty("Welcome to #" + channelName + "!\r\nMessages here are shared with everyone in this voice channel.");
                return;
            }
            _chatEmpty.Visible = false;

            // Stay pinned to the bottom unless the user has scrolled up to read history.
            bool atBottom = channelChanged || _chatList.Items.Count == 0 || IsChatScrolledToBottom();
            int previousTop = _chatList.TopIndex;

            List<ChatRow> rows = BuildChatRows(messages);
            _chatList.BeginUpdate();
            _chatList.Items.Clear();
            int i;
            for (i = 0; i < rows.Count; i++)
                _chatList.Items.Add(rows[i]);
            _chatList.EndUpdate();

            ChatMessage newest = messages[messages.Length - 1];
            bool ownNewest = newest.IsLocal && (newest.Pending || newest.Sequence > _chatNewestSeen);
            if (atBottom || ownNewest)
                _chatList.TopIndex = Math.Max(0, _chatList.Items.Count - 1);
            else
                _chatList.TopIndex = Math.Min(previousTop, Math.Max(0, _chatList.Items.Count - 1));

            uint newestConfirmed = 0;
            bool fromOthers = false;
            for (i = 0; i < messages.Length; i++)
            {
                if (messages[i].Pending || messages[i].Failed)
                    continue;
                if (!channelChanged && messages[i].Sequence > _chatNewestSeen && !messages[i].IsLocal)
                    fromOthers = true;
                if (messages[i].Sequence > newestConfirmed)
                    newestConfirmed = messages[i].Sequence;
            }
            if (fromOthers && _chatNewestSeen != 0)
                FlashForAttention();
            _chatNewestSeen = Math.Max(_chatNewestSeen, newestConfirmed);
        }

        private List<ChatRow> BuildChatRows(ChatMessage[] messages)
        {
            List<ChatRow> rows = new List<ChatRow>();
            ChatMessage previous = null;
            DateTime previousDay = DateTime.MinValue;
            int i;
            for (i = 0; i < messages.Length; i++)
            {
                ChatMessage m = messages[i];
                DateTime local = m.TimeUtc.ToLocalTime();
                if (local.Date != previousDay)
                {
                    ChatRow divider = new ChatRow();
                    divider.DayLabel = FormatDay(local);
                    rows.Add(divider);
                    previousDay = local.Date;
                    previous = null;
                }

                ChatRow row = new ChatRow();
                row.Message = m;
                row.ShowHeader = previous == null || previous.SenderId != m.SenderId ||
                    !String.Equals(previous.SenderName, m.SenderName, StringComparison.Ordinal) ||
                    (m.TimeUtc - previous.TimeUtc).TotalMinutes > 5.0;
                rows.Add(row);
                previous = m;
            }
            return rows;
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
        }

        private void ChatInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter || e.Shift)
                return;
            e.Handled = true;
            e.SuppressKeyPress = true;

            VoipEngine engine = _engine;
            string text = _chatInput.Text;
            if (engine == null || String.IsNullOrWhiteSpace(text))
                return;
            if (engine.SendChat(text))
            {
                _chatInput.Clear();
                RefreshChat(engine);
            }
        }

        private void ChatMeasureItem(object sender, MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _chatList.Items.Count)
                return;
            ChatRow row = _chatList.Items[e.Index] as ChatRow;
            if (row == null)
                return;
            if (row.Message == null)
            {
                e.ItemHeight = 36;
                return;
            }

            int width = Math.Max(80, _chatList.ClientSize.Width - ChatTextLeft - ChatRightPadding);
            Size text = TextRenderer.MeasureText(e.Graphics, row.Message.Text, _chatTextFont, new Size(width, 0), ChatTextFlags);
            int height = text.Height + 4;
            if (row.ShowHeader)
                height += 30;
            if (row.Message.Failed)
                height += 16;
            e.ItemHeight = Math.Min(255, Math.Max(row.ShowHeader ? 48 : 22, height));
        }

        private const TextFormatFlags ChatTextFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        private void ChatDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _chatList.Items.Count)
                return;
            ChatRow row = _chatList.Items[e.Index] as ChatRow;
            if (row == null)
                return;

            Graphics g = e.Graphics;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush back = new SolidBrush(selected && row.Message != null ? ChatSelected : MainBg))
                g.FillRectangle(back, e.Bounds);

            if (row.Message == null)
            {
                DrawDayDivider(g, e.Bounds, row.DayLabel);
                return;
            }

            ChatMessage m = row.Message;
            int y = e.Bounds.Top + 2;
            if (row.ShowHeader)
            {
                y = e.Bounds.Top + 8;
                Color nameColor = ColorForName(m.SenderName);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle avatar = new Rectangle(e.Bounds.Left + 16, y, 40, 40);
                using (SolidBrush b = new SolidBrush(nameColor))
                    g.FillEllipse(b, avatar);
                string initial = String.IsNullOrEmpty(m.SenderName) ? "?" : m.SenderName.Substring(0, 1).ToUpperInvariant();
                TextRenderer.DrawText(g, initial, _chatAvatarFont, avatar, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                g.SmoothingMode = SmoothingMode.Default;

                string name = String.IsNullOrEmpty(m.SenderName) ? "Guest" : m.SenderName;
                Size nameSize = TextRenderer.MeasureText(g, name, _chatNameFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, name, _chatNameFont, new Point(e.Bounds.Left + ChatTextLeft, y), nameColor, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, FormatTime(m.TimeUtc), _chatTimeFont, new Point(e.Bounds.Left + ChatTextLeft + nameSize.Width + 8, y + 4), TextMuted, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                y += 22;
            }

            int width = Math.Max(80, e.Bounds.Width - ChatTextLeft - ChatRightPadding);
            Rectangle textBounds = new Rectangle(e.Bounds.Left + ChatTextLeft, y, width, e.Bounds.Bottom - y);
            Color textColor = m.Pending || m.Failed ? TextMuted : Color.FromArgb(219, 222, 225);
            TextRenderer.DrawText(g, m.Text, _chatTextFont, textBounds, textColor, ChatTextFlags);

            if (m.Failed)
            {
                Size text = TextRenderer.MeasureText(g, m.Text, _chatTextFont, new Size(width, 0), ChatTextFlags);
                TextRenderer.DrawText(g, "Not delivered - right-click to dismiss", _chatTimeFont, new Point(e.Bounds.Left + ChatTextLeft, y + text.Height + 2), Danger, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        private void DrawDayDivider(Graphics g, Rectangle bounds, string label)
        {
            int mid = bounds.Top + bounds.Height / 2;
            Size size = TextRenderer.MeasureText(g, label, _chatTimeFont, Size.Empty, TextFormatFlags.NoPadding);
            int center = bounds.Left + bounds.Width / 2;
            using (Pen pen = new Pen(Border))
            {
                g.DrawLine(pen, bounds.Left + 16, mid, center - size.Width / 2 - 8, mid);
                g.DrawLine(pen, center + size.Width / 2 + 8, mid, bounds.Right - 16, mid);
            }
            TextRenderer.DrawText(g, label, _chatTimeFont, new Point(center - size.Width / 2, mid - size.Height / 2), TextMuted, TextFormatFlags.NoPadding);
        }

        private void ChatMouseDown(object sender, MouseEventArgs e)
        {
            int index = _chatList.IndexFromPoint(e.Location);
            _chatContextRow = index >= 0 && index < _chatList.Items.Count ? _chatList.Items[index] as ChatRow : null;
            if (e.Button == MouseButtons.Right && _chatContextRow != null && _chatContextRow.Message != null)
            {
                _chatList.SelectedIndex = index;
                _chatMenu.Show(_chatList, e.Location);
            }
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
            _chatMenu.Items.Add("Copy text", null, delegate { CopyChatText(message.Text); });
            if (message.Failed)
            {
                ToolStripMenuItem dismiss = new ToolStripMenuItem("Dismiss undelivered messages");
                dismiss.ForeColor = Danger;
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

        private static Color ColorForName(string name)
        {
            int hash = 17;
            string value = name ?? String.Empty;
            int i;
            for (i = 0; i < value.Length; i++)
                hash = unchecked(hash * 31 + Char.ToLowerInvariant(value[i]));
            return NameColors[(hash & 0x7FFFFFFF) % NameColors.Length];
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
