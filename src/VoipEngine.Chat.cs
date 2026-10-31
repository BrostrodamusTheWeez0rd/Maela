using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Maela
{
    /// <summary>Total for one emoji on one message.</summary>
    internal sealed class ReactionSummary
    {
        public string Emoji;
        public int Count;
        public bool Mine;
    }

    /// <summary>One text message in a voice channel's chat.</summary>
    internal sealed class ChatMessage
    {
        public int ChannelId;
        public uint Sequence;       // Host-assigned, increasing per channel. 0 while pending.
        public uint SenderId;
        public uint ClientId;       // Sender's own id for the message, used to match the host's echo.
        public string SenderName;
        public string Text;
        public DateTime TimeUtc;
        public bool Pending;        // Sent by us, not yet confirmed by the host.
        public bool Failed;         // Gave up waiting for the host.
        public bool IsLocal;

        /// <summary>0 = text message, 1 = reaction toggle (Text is the emoji, TargetSequence the message).</summary>
        public byte Kind;
        public uint TargetSequence;

        public bool IsReaction { get { return Kind == ChatKindReaction; } }

        public const byte ChatKindText = 0;
        public const byte ChatKindReaction = 1;

        // Client-side retry state.
        internal DateTime LastSentUtc;
        internal int Attempts;
    }

    /// <summary>
    /// Text chat inside each voice channel.
    ///
    /// The host is the source of truth: it numbers each channel's messages and keeps recent
    /// history. Delivery over UDP is made reliable from both ends:
    ///   - A client re-sends its own message until the host's broadcast echoes it back.
    ///   - On every keepalive (and immediately after changing channel) a client reports the
    ///     last message number it has without gaps; the host re-sends anything newer. The same
    ///     mechanism delivers recent history when someone joins a channel.
    /// Hosts and clients that predate chat simply ignore these packet types.
    /// </summary>
    internal sealed partial class VoipEngine
    {
        public const int MaxChatChars = 400;

        private const byte PacketChat = 12;        // host -> client: one channel message
        private const byte PacketChatSend = 13;    // client -> host: please post this
        private const byte PacketChatSync = 14;    // client -> host: I have everything up to N
        private const int MaxChatTextBytes = 1100; // Keeps packets under a typical 1400-byte MTU.
        private const int ChatHistoryPerChannel = 200;
        private const int ChatInitialHistory = 60;
        private const int ChatResendBatch = 60;
        private const int ChatMaxAttempts = 8;
        private const double ChatRetryMs = 800.0;
        private const double ChatMinIntervalMs = 120.0;

        private readonly Dictionary<int, List<ChatMessage>> _chat = new Dictionary<int, List<ChatMessage>>();
        private readonly Dictionary<int, uint> _chatNextSequence = new Dictionary<int, uint>();
        private readonly List<ChatMessage> _pendingChat = new List<ChatMessage>();
        private uint _nextChatClientId;
        private int _chatVersion;
        private int _chatSyncedChannel = -1;

        /// <summary>Increments whenever any chat content changes. Cheap to poll from the UI.</summary>
        public int ChatVersion { get { lock (_stateLock) return _chatVersion; } }

        /// <summary>Messages for a channel, oldest first, including our own unconfirmed ones.</summary>
        public ChatMessage[] GetChat(int channelId)
        {
            lock (_stateLock)
            {
                List<ChatMessage> result = new List<ChatMessage>();
                List<ChatMessage> list;
                if (_chat.TryGetValue(channelId, out list))
                    result.AddRange(list);
                int i;
                for (i = 0; i < _pendingChat.Count; i++)
                {
                    if (_pendingChat[i].ChannelId == channelId)
                        result.Add(_pendingChat[i]);
                }
                return result.ToArray();
            }
        }

        /// <summary>Posts a message to the voice channel we are in. Returns false if we are not in one.</summary>
        public bool SendChat(string text)
        {
            return Post(ChatMessage.ChatKindText, 0, CleanChatText(text));
        }

        /// <summary>Adds your reaction to a message, or removes it if you already reacted with that emoji.</summary>
        public bool ToggleReaction(uint targetSequence, string emoji)
        {
            if (targetSequence == 0 || EmojiPack.Find(emoji) == null)
                return false;
            return Post(ChatMessage.ChatKindReaction, targetSequence, EmojiPack.Find(emoji).Text);
        }

        private bool Post(byte kind, uint target, string clean)
        {
            if (clean.Length == 0 || !_running || !IsConnected)
                return false;
            int channelId = _localChannelId;
            if (channelId <= 0 || _localId == 0)
                return false;

            if (_isHost)
            {
                PostHostChat(channelId, _localId, _localName, 0, clean, kind, target);
                return true;
            }

            ChatMessage pending = new ChatMessage();
            pending.Kind = kind;
            pending.TargetSequence = target;
            pending.ChannelId = channelId;
            pending.SenderId = _localId;
            pending.SenderName = _localName;
            pending.Text = clean;
            pending.TimeUtc = DateTime.UtcNow;
            pending.Pending = true;
            pending.IsLocal = true;
            lock (_stateLock)
            {
                pending.ClientId = ++_nextChatClientId;
                _pendingChat.Add(pending);
                _chatVersion++;
            }
            SendPendingChat(pending);
            return true;
        }

        /// <summary>Drops our own messages that failed to send.</summary>
        public void DismissFailedChat()
        {
            lock (_stateLock)
            {
                if (_pendingChat.RemoveAll(delegate(ChatMessage m) { return m.Failed; }) > 0)
                    _chatVersion++;
            }
        }

        private void ResetChat()
        {
            lock (_stateLock)
            {
                _chat.Clear();
                _chatNextSequence.Clear();
                _pendingChat.Clear();
                _chatSyncedChannel = -1;
                _chatVersion++;
            }
        }

        private static string CleanChatText(string text)
        {
            if (text == null)
                return String.Empty;
            StringBuilder builder = new StringBuilder(text.Length);
            int i;
            for (i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n' || c == '\t')
                    builder.Append(' ');
                else if (!Char.IsControl(c))
                    builder.Append(c);
            }
            string clean = builder.ToString().Trim();
            if (clean.Length > MaxChatChars)
                clean = clean.Substring(0, MaxChatChars);
            // Stay within the byte budget without splitting a surrogate pair.
            while (clean.Length > 0 && Encoding.UTF8.GetByteCount(clean) > MaxChatTextBytes)
            {
                int cut = clean.Length - 1;
                if (cut > 0 && Char.IsLowSurrogate(clean[cut]))
                    cut--;
                clean = clean.Substring(0, cut);
            }
            return clean;
        }

        // ----- Host -----

        private void PostHostChat(int channelId, uint senderId, string senderName, uint clientId, string text, byte kind, uint target)
        {
            ChatMessage message = new ChatMessage();
            message.Kind = kind;
            message.TargetSequence = target;
            message.ChannelId = channelId;
            message.SenderId = senderId;
            message.SenderName = String.IsNullOrEmpty(senderName) ? "Guest" : senderName;
            message.ClientId = clientId;
            message.Text = text;
            message.TimeUtc = DateTime.UtcNow;
            message.IsLocal = senderId == _localId;

            List<IPEndPoint> targets = new List<IPEndPoint>();
            lock (_stateLock)
            {
                uint next;
                if (!_chatNextSequence.TryGetValue(channelId, out next))
                    next = 1;
                message.Sequence = next;
                _chatNextSequence[channelId] = next + 1;

                List<ChatMessage> list;
                if (!_chat.TryGetValue(channelId, out list))
                {
                    list = new List<ChatMessage>();
                    _chat[channelId] = list;
                }
                list.Add(message);
                if (list.Count > ChatHistoryPerChannel)
                    list.RemoveRange(0, list.Count - ChatHistoryPerChannel);
                _chatVersion++;

                foreach (KeyValuePair<string, Participant> pair in _hostParticipants)
                {
                    if (pair.Value.ChannelId == channelId)
                        targets.Add(new IPEndPoint(pair.Value.Endpoint.Address, pair.Value.Endpoint.Port));
                }
            }

            byte[] payload = BuildChatPayload(message);
            int i;
            for (i = 0; i < targets.Count; i++)
                SendPacket(targets[i], PacketChat, _localId, 0, payload);
        }

        /// <summary>Handles chat packets from a known participant. Returns true if the packet was a chat packet.</summary>
        private bool HandleHostChatPacket(Participant known, Packet packet)
        {
            if (packet.Type == PacketChatSend)
            {
                uint clientId;
                int channelId;
                string text;
                byte kind;
                uint target;
                if (!TryParseChatSend(packet.Payload, out clientId, out channelId, out kind, out target, out text))
                    return true;
                text = CleanChatText(text);
                if (text.Length == 0)
                    return true;
                if (kind == ChatMessage.ChatKindReaction)
                {
                    EmojiEntry emoji = EmojiPack.Find(text);
                    if (emoji == null || target == 0)
                        return true; // Reactions must be a known emoji on a real message.
                    text = emoji.Text;
                }
                else if (kind != ChatMessage.ChatKindText)
                {
                    return true;
                }

                ChatMessage duplicate = null;
                string name;
                lock (_stateLock)
                {
                    if (known.ChannelId != channelId || channelId <= 0)
                        return true; // Only members of a channel may post in it.

                    if (known.RecentChatIds.Contains(clientId))
                    {
                        List<ChatMessage> list;
                        if (_chat.TryGetValue(channelId, out list))
                        {
                            int i;
                            for (i = list.Count - 1; i >= 0; i--)
                            {
                                if (list[i].SenderId == known.Id && list[i].ClientId == clientId)
                                {
                                    duplicate = list[i];
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        DateTime now = DateTime.UtcNow;
                        if ((now - known.LastChatUtc).TotalMilliseconds < ChatMinIntervalMs)
                            return true; // Flood protection; the client will retry.
                        known.LastChatUtc = now;
                        known.RecentChatIds.Enqueue(clientId);
                        while (known.RecentChatIds.Count > 64)
                            known.RecentChatIds.Dequeue();
                    }
                    name = known.Name;
                }

                if (duplicate != null)
                {
                    // Our echo was lost: resend it to the sender only.
                    SendPacket(known.Endpoint, PacketChat, _localId, 0, BuildChatPayload(duplicate));
                    return true;
                }

                PostHostChat(channelId, known.Id, name, clientId, text, kind, target);
                return true;
            }

            if (packet.Type == PacketChatSync)
            {
                if (packet.Payload == null || packet.Payload.Length != 8)
                    return true;
                int channelId = BitConverter.ToInt32(packet.Payload, 0);
                uint haveUpTo = BitConverter.ToUInt32(packet.Payload, 4);

                List<ChatMessage> missing = new List<ChatMessage>();
                IPEndPoint target;
                lock (_stateLock)
                {
                    if (known.ChannelId != channelId || channelId <= 0)
                        return true;
                    target = new IPEndPoint(known.Endpoint.Address, known.Endpoint.Port);
                    List<ChatMessage> list;
                    if (_chat.TryGetValue(channelId, out list) && list.Count > 0)
                    {
                        uint newest = list[list.Count - 1].Sequence;
                        if (haveUpTo >= newest)
                            return true;
                        uint from = haveUpTo == 0
                            ? (newest > ChatInitialHistory ? newest - ChatInitialHistory + 1 : 1)
                            : haveUpTo + 1;
                        int i;
                        for (i = 0; i < list.Count && missing.Count < ChatResendBatch; i++)
                        {
                            if (list[i].Sequence >= from)
                                missing.Add(list[i]);
                        }
                    }
                }

                int m;
                for (m = 0; m < missing.Count; m++)
                    SendPacket(target, PacketChat, _localId, 0, BuildChatPayload(missing[m]));
                return true;
            }

            return false;
        }

        // ----- Client -----

        private bool HandleClientChatPacket(Packet packet)
        {
            if (packet.Type != PacketChat)
                return false;
            if (!_connected)
                return true;

            ChatMessage message;
            if (!TryParseChat(packet.Payload, out message))
                return true;
            message.IsLocal = message.SenderId == _localId;

            lock (_stateLock)
            {
                List<ChatMessage> list;
                if (!_chat.TryGetValue(message.ChannelId, out list))
                {
                    list = new List<ChatMessage>();
                    _chat[message.ChannelId] = list;
                }

                // Insert in sequence order, ignoring duplicates.
                int index = list.Count;
                while (index > 0 && list[index - 1].Sequence > message.Sequence)
                    index--;
                if (index > 0 && list[index - 1].Sequence == message.Sequence)
                    return true;
                list.Insert(index, message);
                if (list.Count > ChatHistoryPerChannel)
                    list.RemoveRange(0, list.Count - ChatHistoryPerChannel);

                if (message.IsLocal)
                {
                    int i;
                    for (i = _pendingChat.Count - 1; i >= 0; i--)
                    {
                        if (_pendingChat[i].ClientId == message.ClientId)
                            _pendingChat.RemoveAt(i);
                    }
                }
                _chatVersion++;
            }
            return true;
        }

        /// <summary>Called from the client maintenance loop while connected.</summary>
        private void ClientChatMaintenance(DateTime now, bool keepAliveDue)
        {
            List<ChatMessage> resend = new List<ChatMessage>();
            lock (_stateLock)
            {
                int i;
                for (i = 0; i < _pendingChat.Count; i++)
                {
                    ChatMessage pending = _pendingChat[i];
                    if (pending.Failed || (now - pending.LastSentUtc).TotalMilliseconds < ChatRetryMs)
                        continue;
                    if (pending.Attempts >= ChatMaxAttempts)
                    {
                        pending.Failed = true;
                        pending.Pending = false;
                        _chatVersion++;
                        continue;
                    }
                    resend.Add(pending);
                }
            }

            int r;
            for (r = 0; r < resend.Count; r++)
                SendPendingChat(resend[r]);

            int channelId = _localChannelId;
            if (channelId <= 0)
            {
                _chatSyncedChannel = -1;
                return;
            }
            if (keepAliveDue || channelId != _chatSyncedChannel)
            {
                _chatSyncedChannel = channelId;
                byte[] payload = new byte[8];
                Buffer.BlockCopy(BitConverter.GetBytes(channelId), 0, payload, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(ContiguousChatSequence(channelId)), 0, payload, 4, 4);
                SendPacket(_hostEndpoint, PacketChatSync, _localId, 0, payload);
            }
        }

        private uint ContiguousChatSequence(int channelId)
        {
            lock (_stateLock)
            {
                List<ChatMessage> list;
                if (!_chat.TryGetValue(channelId, out list) || list.Count == 0)
                    return 0;
                uint contiguous = list[0].Sequence;
                int i;
                for (i = 1; i < list.Count; i++)
                {
                    if (list[i].Sequence != contiguous + 1)
                        break;
                    contiguous = list[i].Sequence;
                }
                return contiguous;
            }
        }

        private void SendPendingChat(ChatMessage pending)
        {
            IPEndPoint host = _hostEndpoint;
            if (host == null || !_connected)
                return;
            lock (_stateLock)
            {
                pending.Attempts++;
                pending.LastSentUtc = DateTime.UtcNow;
            }

            byte[] text = Encoding.UTF8.GetBytes(pending.Text);
            MemoryStream stream = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(pending.ClientId);
            writer.Write(pending.ChannelId);
            writer.Write(pending.Kind);
            writer.Write(pending.TargetSequence);
            writer.Write((ushort)text.Length);
            writer.Write(text);
            writer.Flush();
            SendPacket(host, PacketChatSend, _localId, 0, stream.ToArray());
        }

        // ----- Wire format -----

        private static byte[] BuildChatPayload(ChatMessage message)
        {
            byte[] name = Encoding.UTF8.GetBytes(message.SenderName ?? String.Empty);
            if (name.Length > 80)
            {
                byte[] trimmed = new byte[80];
                Buffer.BlockCopy(name, 0, trimmed, 0, 80);
                name = trimmed;
            }
            byte[] text = Encoding.UTF8.GetBytes(message.Text ?? String.Empty);

            MemoryStream stream = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(message.ChannelId);
            writer.Write(message.Sequence);
            writer.Write(ToUnixMilliseconds(message.TimeUtc));
            writer.Write(message.SenderId);
            writer.Write(message.ClientId);
            writer.Write((byte)name.Length);
            writer.Write(name);
            writer.Write((ushort)text.Length);
            writer.Write(text);
            writer.Write(message.Kind);
            writer.Write(message.TargetSequence);
            writer.Flush();
            return stream.ToArray();
        }

        private static bool TryParseChat(byte[] payload, out ChatMessage message)
        {
            message = null;
            if (payload == null || payload.Length < 32)
                return false;
            try
            {
                int offset = 0;
                ChatMessage m = new ChatMessage();
                m.ChannelId = BitConverter.ToInt32(payload, offset); offset += 4;
                m.Sequence = BitConverter.ToUInt32(payload, offset); offset += 4;
                m.TimeUtc = FromUnixMilliseconds(BitConverter.ToInt64(payload, offset)); offset += 8;
                m.SenderId = BitConverter.ToUInt32(payload, offset); offset += 4;
                m.ClientId = BitConverter.ToUInt32(payload, offset); offset += 4;
                int nameLength = payload[offset++];
                if (offset + nameLength + 2 > payload.Length)
                    return false;
                m.SenderName = Encoding.UTF8.GetString(payload, offset, nameLength); offset += nameLength;
                int textLength = BitConverter.ToUInt16(payload, offset); offset += 2;
                if (offset + textLength + 5 != payload.Length)
                    return false;
                m.Text = CleanChatText(Encoding.UTF8.GetString(payload, offset, textLength));
                offset += textLength;
                m.Kind = payload[offset++];
                m.TargetSequence = BitConverter.ToUInt32(payload, offset);
                if (m.ChannelId <= 0 || m.Sequence == 0)
                    return false;
                message = m;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseChatSend(byte[] payload, out uint clientId, out int channelId, out byte kind, out uint target, out string text)
        {
            clientId = 0;
            channelId = 0;
            kind = 0;
            target = 0;
            text = null;
            if (payload == null || payload.Length < 15)
                return false;
            clientId = BitConverter.ToUInt32(payload, 0);
            channelId = BitConverter.ToInt32(payload, 4);
            kind = payload[8];
            target = BitConverter.ToUInt32(payload, 9);
            int length = BitConverter.ToUInt16(payload, 13);
            if (15 + length != payload.Length || length > MaxChatTextBytes + 16)
                return false;
            try { text = Encoding.UTF8.GetString(payload, 15, length); }
            catch { return false; }
            return true;
        }

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static long ToUnixMilliseconds(DateTime utc)
        {
            return (long)(utc - UnixEpoch).TotalMilliseconds;
        }

        private static DateTime FromUnixMilliseconds(long value)
        {
            try { return UnixEpoch.AddMilliseconds(value); }
            catch { return DateTime.UtcNow; }
        }

        /// <summary>
        /// Folds reaction toggles into per-message totals. Pending (unconfirmed) toggles of our
        /// own are included so a click shows immediately.
        /// </summary>
        public static Dictionary<uint, List<ReactionSummary>> SummarizeReactions(ChatMessage[] messages)
        {
            Dictionary<string, HashSet<uint>> who = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
            Dictionary<string, bool> mine = new Dictionary<string, bool>(StringComparer.Ordinal);
            List<string> order = new List<string>();
            int i;
            for (i = 0; i < messages.Length; i++)
            {
                ChatMessage m = messages[i];
                if (!m.IsReaction || m.Failed)
                    continue;
                string key = m.TargetSequence + "|" + m.Text;
                HashSet<uint> set;
                if (!who.TryGetValue(key, out set))
                {
                    set = new HashSet<uint>();
                    who[key] = set;
                    order.Add(key);
                }
                if (!set.Remove(m.SenderId))
                    set.Add(m.SenderId);
                if (m.IsLocal)
                    mine[key] = set.Contains(m.SenderId);
            }

            Dictionary<uint, List<ReactionSummary>> result = new Dictionary<uint, List<ReactionSummary>>();
            for (i = 0; i < order.Count; i++)
            {
                HashSet<uint> set = who[order[i]];
                if (set.Count == 0)
                    continue;
                int bar = order[i].IndexOf('|');
                uint target = UInt32.Parse(order[i].Substring(0, bar));
                ReactionSummary summary = new ReactionSummary();
                summary.Emoji = order[i].Substring(bar + 1);
                summary.Count = set.Count;
                bool m;
                summary.Mine = mine.TryGetValue(order[i], out m) && m;
                List<ReactionSummary> list;
                if (!result.TryGetValue(target, out list))
                {
                    list = new List<ReactionSummary>();
                    result[target] = list;
                }
                list.Add(summary);
            }
            return result;
        }
    }
}
