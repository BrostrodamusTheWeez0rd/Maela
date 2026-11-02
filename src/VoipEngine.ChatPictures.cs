using System;
using System.Collections.Generic;
using System.Net;

namespace Maela
{
    /// <summary>
    /// Pictures in room chat (see ChatPicture for what a picture is and why it's safe).
    ///
    /// Posting: the sender offers a picture to the host; the host pulls it in, a window of chunks
    /// at a time, checks it (its name is its hash, and it must unpack exactly), keeps it, and only
    /// then posts the chat message that shows it. Viewing: anyone who sees that message pulls the
    /// picture from the host the same way. Everything is inside the encrypted session; chunks only
    /// ever answer a request, so nobody can push data at anyone.
    ///
    /// Protocol:
    ///   PictureOffer client -> host : I have picture P (size, channel, my message id), please take it
    ///   PictureGet   either way     : send me chunks first..first+count of P
    ///   PictureData  either way     : one chunk of P
    /// Older versions ignore all three, and see the message's plain words instead of the picture.
    /// </summary>
    internal sealed partial class VoipEngine
    {
        private const byte PacketPictureOffer = 33;
        private const byte PacketPictureGet = 34;
        private const byte PacketPictureData = 35;
        private const int PictureChunkBytes = 1000;
        private const int PictureWindow = 64;              // Chunks asked for at once.
        private const double PictureAskMs = 150.0;
        private const double PictureStallSeconds = 30.0;
        private const double PictureMinIntervalSeconds = 4.0; // One picture every few seconds per member.
        private const long HostPictureBudget = 96L * 1024 * 1024;
        private const long ClientPictureBudget = 128L * 1024 * 1024;

        private sealed class PictureTransfer
        {
            public string Id;
            public int Total, Count, Have;
            public int AskedEnd;  // The end of the window asked for last.
            private int _gap;     // No missing chunk before this one.
            public byte[][] Chunks;
            public DateTime AskedUtc = DateTime.MinValue, ProgressUtc = DateTime.UtcNow;
            public bool Whole;    // Every chunk is in and it's being checked: stays listed until kept, so it isn't asked for again.
            // Uploads only (host side): where it goes once it's here.
            public uint MemberId;
            public int ChannelId;
            public uint ClientId;
            public IPEndPoint From;

            public PictureTransfer(string id, int total)
            {
                Id = id;
                Total = total;
                Count = (total + PictureChunkBytes - 1) / PictureChunkBytes;
                Chunks = new byte[Count][];
            }

            /// <summary>The first chunk still missing, or Count when there are none.</summary>
            public int FirstMissing()
            {
                while (_gap < Count && Chunks[_gap] != null)
                    _gap++;
                return _gap;
            }

            public byte[] Join()
            {
                byte[] all = new byte[Total];
                for (int i = 0; i < Count; i++)
                    Buffer.BlockCopy(Chunks[i], 0, all, i * PictureChunkBytes, Chunks[i].Length);
                return all;
            }
        }

        // All under _stateLock.
        private readonly Dictionary<string, byte[]> _pictures = new Dictionary<string, byte[]>(); // Checked pictures we hold.
        private readonly List<string> _pictureOrder = new List<string>();                         // Oldest first, for the budget.
        private long _pictureBytes;
        private readonly Dictionary<string, PictureTransfer> _pictureDownloads = new Dictionary<string, PictureTransfer>();
        private readonly Dictionary<uint, PictureTransfer> _pictureUploads = new Dictionary<uint, PictureTransfer>(); // Host: member -> incoming.
        private readonly Dictionary<uint, DateTime> _lastPictureUtc = new Dictionary<uint, DateTime>();
        private readonly Dictionary<string, byte[]> _myPictures = new Dictionary<string, byte[]>();  // Client: offered, waiting to be taken.
        private readonly Dictionary<string, DateTime> _pictureGaveUp = new Dictionary<string, DateTime>();

        /// <summary>
        /// Posts a picture (made by ChatPicture.FromFile on this PC) to the channel whose chat is
        /// open, or the voice channel we're in. It shows as "sending" until the host has it.
        /// </summary>
        public bool SendPicture(byte[] blob)
        {
            return blob != null && SendPictureAs(blob, ChatPicture.IdOf(blob));
        }

        /// <summary>Tests: offers a picture under a name that isn't its hash, as a tampered client would.</summary>
        internal bool SendPictureAsForTests(byte[] blob, string id)
        {
            return SendPictureAs(blob, id);
        }

        internal const byte PictureOfferTypeForTests = PacketPictureOffer;
        internal const byte PictureGetTypeForTests = PacketPictureGet;
        internal const byte PictureDataTypeForTests = PacketPictureData;

        private bool SendPictureAs(byte[] blob, string id)
        {
            int width, height;
            if (!ChatPicture.TryReadSize(blob, out width, out height) || !_running || !IsConnected)
                return false;
            int channelId = ChatChannelId;
            if (channelId <= 0 || _localId == 0)
                return false;
            string marker = ChatPicture.Marker(id, width, height, blob.Length);
            lock (_stateLock)
                KeepPictureLocked(id, blob);
            if (_isHost)
            {
                PostHostChat(channelId, _localId, _localName, 0, marker, ChatMessage.ChatKindPicture, 0);
                return true;
            }
            ChatMessage pending = new ChatMessage();
            pending.Kind = ChatMessage.ChatKindPicture;
            pending.ChannelId = channelId;
            pending.SenderId = _localId;
            pending.SenderName = _localName;
            pending.Text = marker;
            pending.TimeUtc = DateTime.UtcNow;
            pending.Pending = true;
            pending.IsLocal = true;
            lock (_stateLock)
            {
                pending.ClientId = ++_nextChatClientId;
                _myPictures[id] = blob;
                _pendingChat.Add(pending);
                _chatVersion++;
            }
            SendPictureOffer(pending, id, blob.Length);
            return true;
        }

        /// <summary>
        /// A picture a message shows, once it's here and checked; until then null, and it's fetched
        /// in the background (ChatVersion changes when it arrives).
        /// </summary>
        public byte[] GetPicture(string id, int bytes)
        {
            if (!ChatPicture.IsId(id) || bytes <= 0 || bytes > ChatPicture.MaxBytes)
                return null;
            lock (_stateLock)
            {
                byte[] blob;
                if (_pictures.TryGetValue(id, out blob))
                    return blob;
                DateTime gaveUp;
                if (_isHost || _pictureDownloads.ContainsKey(id) || (_pictureGaveUp.TryGetValue(id, out gaveUp) && (DateTime.UtcNow - gaveUp).TotalSeconds < 20))
                    return null;
                _pictureDownloads[id] = new PictureTransfer(id, bytes);
            }
            return null;
        }

        /// <summary>True while a picture is on its way, false once it's here or couldn't be had.</summary>
        public bool PictureComing(string id)
        {
            lock (_stateLock)
                return _pictureDownloads.ContainsKey(id);
        }

        /// <summary>Keeps a checked picture, letting the oldest go past the budget. _stateLock held.</summary>
        private void KeepPictureLocked(string id, byte[] blob)
        {
            if (_pictures.ContainsKey(id))
                return;
            _pictures[id] = blob;
            _pictureOrder.Add(id);
            _pictureBytes += blob.Length;
            long budget = _isHost ? HostPictureBudget : ClientPictureBudget;
            while (_pictureBytes > budget && _pictureOrder.Count > 1)
            {
                string oldest = _pictureOrder[0];
                _pictureOrder.RemoveAt(0);
                byte[] gone;
                if (_pictures.TryGetValue(oldest, out gone))
                {
                    _pictureBytes -= gone.Length;
                    _pictures.Remove(oldest);
                }
            }
        }

        /// <summary>A whole picture, checked: its name is its hash and it unpacks exactly.</summary>
        private static bool PictureChecks(string id, byte[] blob)
        {
            int width, height;
            byte[] rgb;
            return ChatPicture.IdOf(blob) == id && ChatPicture.TryDecode(blob, out width, out height, out rgb);
        }

        private void ResetPictures()
        {
            lock (_stateLock)
            {
                _pictures.Clear();
                _pictureOrder.Clear();
                _pictureBytes = 0;
                _pictureDownloads.Clear();
                _pictureUploads.Clear();
                _lastPictureUtc.Clear();
                _myPictures.Clear();
                _pictureGaveUp.Clear();
            }
        }

        // ----- Wire -----

        private static byte[] IdBytes(string id)
        {
            byte[] bytes = new byte[16];
            for (int i = 0; i < 16; i++)
                bytes[i] = Convert.ToByte(id.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string IdText(byte[] payload, int offset)
        {
            return BitConverter.ToString(payload, offset, 16).Replace("-", String.Empty).ToLowerInvariant();
        }

        private void SendPictureOffer(ChatMessage pending, string id, int total)
        {
            IPEndPoint host = _hostEndpoint;
            if (host == null || !_connected)
                return;
            lock (_stateLock)
            {
                pending.Attempts++;
                pending.LastSentUtc = DateTime.UtcNow;
            }
            byte[] payload = new byte[28];
            Buffer.BlockCopy(IdBytes(id), 0, payload, 0, 16);
            Buffer.BlockCopy(BitConverter.GetBytes(total), 0, payload, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(pending.ChannelId), 0, payload, 20, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(pending.ClientId), 0, payload, 24, 4);
            SendPacket(host, PacketPictureOffer, _localId, 0, payload);
        }

        private void AskForChunks(IPEndPoint to, PictureTransfer transfer)
        {
            // The first gap, a window's worth.
            int first = transfer.FirstMissing();
            if (first >= transfer.Count)
                return;
            int count = 0;
            while (first + count < transfer.Count && count < PictureWindow)
                count++;
            byte[] payload = new byte[20];
            Buffer.BlockCopy(IdBytes(transfer.Id), 0, payload, 0, 16);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)first), 0, payload, 16, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)count), 0, payload, 18, 2);
            transfer.AskedUtc = DateTime.UtcNow;
            transfer.AskedEnd = first + count;
            SendPacket(to, PacketPictureGet, _localId, 0, payload);
        }

        /// <summary>Answers a request for chunks of a picture we have. Only what was asked, and never too much at once.</summary>
        private void SendChunks(IPEndPoint to, byte[] payload, byte[] blob)
        {
            int first = BitConverter.ToUInt16(payload, 16), count = Math.Min(PictureWindow, (int)BitConverter.ToUInt16(payload, 18));
            int chunks = (blob.Length + PictureChunkBytes - 1) / PictureChunkBytes;
            for (int i = first; i < first + count && i < chunks; i++)
            {
                int length = Math.Min(PictureChunkBytes, blob.Length - i * PictureChunkBytes);
                byte[] data = new byte[22 + length];
                Buffer.BlockCopy(payload, 0, data, 0, 16);
                Buffer.BlockCopy(BitConverter.GetBytes(blob.Length), 0, data, 16, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)i), 0, data, 20, 2);
                Buffer.BlockCopy(blob, i * PictureChunkBytes, data, 22, length);
                SendPacket(to, PacketPictureData, _localId, 0, data);
            }
        }

        /// <summary>
        /// Adds one chunk to a transfer, if it fits exactly. True once the transfer is whole. Asks
        /// for the next window as soon as the last one is in.
        /// </summary>
        private bool AddPictureChunk(PictureTransfer transfer, byte[] data, IPEndPoint from)
        {
            if (data.Length < 23 || BitConverter.ToInt32(data, 16) != transfer.Total)
                return false;
            int index = BitConverter.ToUInt16(data, 20), length = data.Length - 22;
            if (index >= transfer.Count || length != Math.Min(PictureChunkBytes, transfer.Total - index * PictureChunkBytes))
                return false;
            if (transfer.Chunks[index] == null)
            {
                byte[] chunk = new byte[length];
                Buffer.BlockCopy(data, 22, chunk, 0, length);
                transfer.Chunks[index] = chunk;
                transfer.Have++;
                transfer.ProgressUtc = DateTime.UtcNow;
            }
            if (transfer.Have == transfer.Count)
                return true;
            if (from != null && transfer.FirstMissing() >= transfer.AskedEnd)
                AskForChunks(from, transfer);
            return false;
        }

        // ----- Host -----

        private bool HandleHostPicturePacket(Participant known, Packet packet)
        {
            byte[] p = packet.Payload;
            if (packet.Type == PacketPictureOffer)
            {
                if (p == null || p.Length != 28)
                    return true;
                string id = IdText(p, 0);
                int total = BitConverter.ToInt32(p, 16), channelId = BitConverter.ToInt32(p, 20);
                uint clientId = BitConverter.ToUInt32(p, 24);
                if (total <= 7 || total > ChatPicture.MaxBytes)
                    return true;
                byte[] have = null;
                string name;
                lock (_stateLock)
                {
                    if (channelId <= 0 || !_channels.ContainsKey(channelId) || (known.ChannelId != channelId && known.ViewChannelId != channelId))
                        return true;
                    if (known.RecentChatIds.Contains(clientId))
                        return true; // Already posted; the echo is on its way or the sync will bring it.
                    PictureTransfer upload;
                    if (_pictureUploads.TryGetValue(known.Id, out upload))
                    {
                        if (upload.Id == id)
                            return true; // Already taking it.
                        if ((DateTime.UtcNow - upload.ProgressUtc).TotalSeconds < PictureStallSeconds)
                            return true; // One at a time; the client offers again.
                    }
                    DateTime last;
                    if (_lastPictureUtc.TryGetValue(known.Id, out last) && (DateTime.UtcNow - last).TotalSeconds < PictureMinIntervalSeconds)
                        return true;
                    name = known.Name;
                    if (!_pictures.TryGetValue(id, out have))
                    {
                        upload = new PictureTransfer(id, total);
                        upload.MemberId = known.Id;
                        upload.ChannelId = channelId;
                        upload.ClientId = clientId;
                        upload.From = new IPEndPoint(known.Endpoint.Address, known.Endpoint.Port);
                        _pictureUploads[known.Id] = upload;
                        AskForChunks(upload.From, upload);
                        return true;
                    }
                    // The same picture is already here: post it straight away.
                    _lastPictureUtc[known.Id] = DateTime.UtcNow;
                    known.RecentChatIds.Enqueue(clientId);
                }
                int width, height;
                ChatPicture.TryReadSize(have, out width, out height);
                PostHostChat(channelId, known.Id, name, clientId, ChatPicture.Marker(id, width, height, have.Length), ChatMessage.ChatKindPicture, 0);
                return true;
            }

            if (packet.Type == PacketPictureData)
            {
                if (p == null || p.Length < 23)
                    return true;
                string id = IdText(p, 0);
                PictureTransfer upload;
                lock (_stateLock)
                {
                    if (!_pictureUploads.TryGetValue(known.Id, out upload) || upload.Id != id || upload.Whole)
                        return true; // Only what we asked this member for, once.
                    if (!AddPictureChunk(upload, p, upload.From))
                        return true;
                    upload.Whole = true;
                }
                byte[] blob = upload.Join();
                bool checks = PictureChecks(id, blob);
                lock (_stateLock)
                {
                    PictureTransfer listed;
                    if (_pictureUploads.TryGetValue(known.Id, out listed) && Object.ReferenceEquals(listed, upload))
                        _pictureUploads.Remove(known.Id);
                }
                if (!checks)
                    return true; // Not what it claimed to be: dropped, nothing posted.
                int width, height;
                ChatPicture.TryReadSize(blob, out width, out height);
                string name;
                lock (_stateLock)
                {
                    if (known.RecentChatIds.Contains(upload.ClientId))
                        return true; // Already posted.
                    KeepPictureLocked(id, blob);
                    _lastPictureUtc[known.Id] = DateTime.UtcNow;
                    known.RecentChatIds.Enqueue(upload.ClientId);
                    while (known.RecentChatIds.Count > 64)
                        known.RecentChatIds.Dequeue();
                    name = known.Name;
                }
                PostHostChat(upload.ChannelId, known.Id, name, upload.ClientId, ChatPicture.Marker(id, width, height, blob.Length), ChatMessage.ChatKindPicture, 0);
                return true;
            }

            if (packet.Type == PacketPictureGet)
            {
                if (p == null || p.Length != 20)
                    return true;
                byte[] blob;
                lock (_stateLock)
                {
                    if (!_pictures.TryGetValue(IdText(p, 0), out blob))
                        return true;
                }
                SendChunks(known.Endpoint, p, blob);
                return true;
            }
            return false;
        }

        /// <summary>Host maintenance: keep pulling pictures in, and drop any that stall.</summary>
        private void HostPictureMaintenance()
        {
            List<PictureTransfer> ask = new List<PictureTransfer>();
            lock (_stateLock)
            {
                List<uint> stalled = new List<uint>();
                foreach (KeyValuePair<uint, PictureTransfer> pair in _pictureUploads)
                {
                    if ((DateTime.UtcNow - pair.Value.ProgressUtc).TotalSeconds > PictureStallSeconds)
                        stalled.Add(pair.Key);
                    else if ((DateTime.UtcNow - pair.Value.AskedUtc).TotalMilliseconds > PictureAskMs)
                        ask.Add(pair.Value);
                }
                foreach (uint member in stalled)
                    _pictureUploads.Remove(member);
            }
            foreach (PictureTransfer upload in ask)
                AskForChunks(upload.From, upload);
        }

        /// <summary>Host: forget a member's upload when they leave. _stateLock held.</summary>
        private void ForgetPicturesLocked(uint memberId)
        {
            _pictureUploads.Remove(memberId);
            _lastPictureUtc.Remove(memberId);
        }

        // ----- Client -----

        private bool HandleClientPicturePacket(Packet packet)
        {
            byte[] p = packet.Payload;
            if (packet.Type == PacketPictureGet)
            {
                // The host taking a picture we offered.
                if (p == null || p.Length != 20)
                    return true;
                string id = IdText(p, 0);
                byte[] blob;
                lock (_stateLock)
                {
                    if (!_myPictures.TryGetValue(id, out blob))
                        return true;
                    foreach (ChatMessage pending in _pendingChat)
                    {
                        if (pending.Kind == ChatMessage.ChatKindPicture && pending.Text.Contains(id))
                        {
                            pending.LastSentUtc = DateTime.UtcNow; // It's being taken: no need to offer again yet.
                            pending.Attempts = 0;
                        }
                    }
                }
                SendChunks(_hostEndpoint, p, blob);
                return true;
            }

            if (packet.Type == PacketPictureData)
            {
                if (p == null || p.Length < 23)
                    return true;
                string id = IdText(p, 0);
                PictureTransfer download;
                lock (_stateLock)
                {
                    if (!_pictureDownloads.TryGetValue(id, out download) || download.Whole)
                        return true; // Only what we asked for, once.
                    if (!AddPictureChunk(download, p, _hostEndpoint))
                        return true;
                    download.Whole = true;
                }
                byte[] blob = download.Join();
                bool checks = PictureChecks(id, blob);
                lock (_stateLock)
                {
                    _pictureDownloads.Remove(id);
                    if (checks)
                        KeepPictureLocked(id, blob);
                    else
                        _pictureGaveUp[id] = DateTime.UtcNow;
                    _chatVersion++;
                }
                return true;
            }
            return false;
        }

        /// <summary>Client maintenance: offer our pictures until taken, and keep fetching the ones being shown.</summary>
        private void ClientPictureMaintenance(DateTime now)
        {
            List<PictureTransfer> ask = new List<PictureTransfer>();
            List<ChatMessage> offers = new List<ChatMessage>();
            lock (_stateLock)
            {
                List<string> stalled = new List<string>();
                foreach (KeyValuePair<string, PictureTransfer> pair in _pictureDownloads)
                {
                    if ((now - pair.Value.ProgressUtc).TotalSeconds > PictureStallSeconds)
                        stalled.Add(pair.Key);
                    else if ((now - pair.Value.AskedUtc).TotalMilliseconds > PictureAskMs * 2)
                        ask.Add(pair.Value);
                }
                foreach (string id in stalled)
                {
                    _pictureDownloads.Remove(id);
                    _pictureGaveUp[id] = now;
                    _chatVersion++;
                }
                foreach (ChatMessage pending in _pendingChat)
                {
                    if (pending.Kind != ChatMessage.ChatKindPicture || pending.Failed)
                        continue;
                    if ((now - pending.LastSentUtc).TotalMilliseconds < 1500)
                        continue;
                    if (pending.Attempts >= ChatMaxAttempts)
                    {
                        pending.Failed = true;
                        pending.Pending = false;
                        _chatVersion++;
                        continue;
                    }
                    offers.Add(pending);
                }
                // Offered pictures that have been posted, or given up on, needn't be kept for the host.
                List<string> done = new List<string>();
                foreach (string id in _myPictures.Keys)
                    if (!_pendingChat.Exists(delegate(ChatMessage m) { return m.Kind == ChatMessage.ChatKindPicture && !m.Failed && m.Text.Contains(id); }))
                        done.Add(id);
                foreach (string id in done)
                    _myPictures.Remove(id);
            }
            foreach (PictureTransfer download in ask)
                AskForChunks(_hostEndpoint, download);
            foreach (ChatMessage pending in offers)
            {
                string id;
                int width, height, bytes;
                if (ChatPicture.TryParseMarker(pending.Text, out id, out width, out height, out bytes))
                    SendPictureOffer(pending, id, bytes);
            }
        }
    }
}
