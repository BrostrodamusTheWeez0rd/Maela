using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Maela
{
    /// <summary>A live screen share someone in the room has started.</summary>
    internal sealed class LiveStreamInfo
    {
        public uint MemberId;
        public uint StreamId;
        public int Width;
        public int Height;
        public int Fps;
    }

    internal enum StreamWatchState { None, Connecting, Direct, Relayed }

    /// <summary>
    /// Screen-share transport.
    ///
    /// Who gets video: only people who click "Watch stream". Going live just tells the room
    /// (a badge next to your name); nothing is sent until someone watches.
    ///
    /// Path: straight from the sharer to each viewer. The host introduces the two (it already
    /// knows everyone's address) and gives both a fresh random key for that pair, over their
    /// encrypted connections. Both sides then send small "punch" packets to each other at once,
    /// which opens a direct path through most home routers. Until that works (or if it never
    /// does, or if either side turned direct connections off) the video goes through the host
    /// instead. A direct connection means the sharer and that viewer learn each other's IP
    /// address; nobody else does.
    ///
    /// Every media packet is encrypted and authenticated with that pair's keys (AES-256-CTR +
    /// HMAC-SHA256 with replay protection). When relayed, the host only forwards the sealed
    /// packet between people it introduced, within a bandwidth cap.
    ///
    /// Video: each encoded frame is split into numbered fragments. Viewers rebuild frames, ask
    /// for a fresh keyframe when something was lost (until it arrives, damaged frames are
    /// dropped so the picture never smears), and report what they receive twice a second so
    /// the sharer can pick a bitrate that every viewer can take.
    /// </summary>
    internal sealed partial class VoipEngine
    {
        public const int MaxStreamViewers = 8;
        public const int MaxStreamPixels = 5120 * 2160;   // Up to 4K (and ultrawide 1440p).
        private const int MediaFragmentBytes = 1100;
        private const int MaxFrameBytes = 4 * 1024 * 1024;
        private const int MaxPendingFrames = 48;
        private const long RelayBytesPerSecond = 16L * 1024 * 1024; // Per sharer through the host (~130 Mbps).
        private const byte OuterMedia = 0x45;
        private const int MediaAdBytes = 6 + 4 + 4;         // Outer header, from, to.

        private const byte PacketStreamAnnounce = 21;     // client -> host: I'm live (or stopped)
        private const byte PacketStreamWatch = 22;        // viewer -> host: I want to watch M
        private const byte PacketStreamWatchStop = 23;    // viewer -> host: stop
        private const byte PacketStreamPeer = 24;         // host -> both: introduce sharer and viewer
        private const byte PacketStreamPeerEnd = 25;      // host -> both: that pair is over
        private const byte PacketRelayMedia = 26;         // either way through the host: a sealed media packet
        private const byte PeerListSectionStreams = 3;

        private const byte MediaPunch = 0;
        private const byte MediaVideo = 1;
        private const byte MediaKeyframeRequest = 2;
        private const byte MediaReport = 3;

        private sealed class StreamLink
        {
            public uint PeerId;
            public uint StreamId;
            public bool IsSender;
            public bool AllowDirect;
            public PacketCipher Send;
            public PacketCipher Receive;
            public IPEndPoint PublicEndpoint;
            public IPEndPoint LocalEndpoint;
            public IPEndPoint DirectEndpoint;
            public DateTime CreatedUtc = DateTime.UtcNow;
            public DateTime LastPunchUtc = DateTime.MinValue;
            public DateTime LastHeardUtc = DateTime.UtcNow;
            public DateTime LastReportUtc = DateTime.UtcNow;
            public DateTime LastKeyframeRequestUtc = DateTime.MinValue;
            // Receiving side.
            public readonly Dictionary<uint, FrameAssembly> Frames = new Dictionary<uint, FrameAssembly>();
            public uint LastDelivered;
            public bool AnyDelivered;
            public bool WaitingForKeyframe = true;
            public long ReportBytes;
            public int ReportFragments;
            public int ReportLostFragments;
            // Sending side: what the viewer said last.
            public int ViewerKbps = -1;
            public int ViewerLossPermille;

            public void Dispose()
            {
                if (Send != null) Send.Dispose();
                if (Receive != null) Receive.Dispose();
            }
        }

        private sealed class FrameAssembly
        {
            public byte[][] Fragments;
            public int Received;
            public bool Keyframe;
            public DateTime FirstUtc = DateTime.UtcNow;
        }

        private sealed class HostWatch
        {
            public uint SharerId;
            public uint ViewerId;
            public uint StreamId;
        }

        // Shared.
        private readonly Dictionary<ulong, StreamLink> _streamLinks = new Dictionary<ulong, StreamLink>();
        private readonly Dictionary<uint, LiveStreamInfo> _liveStreams = new Dictionary<uint, LiveStreamInfo>();
        private uint _localStreamId;
        private int _localStreamWidth, _localStreamHeight, _localStreamFps;
        private uint _nextFrameNumber;
        private volatile bool _keyframeRequested;
        private bool _allowDirectStreams = true;
        // Host.
        private readonly Dictionary<ulong, HostWatch> _hostWatches = new Dictionary<ulong, HostWatch>();
        private readonly Dictionary<uint, IPEndPoint> _memberLocalEndpoints = new Dictionary<uint, IPEndPoint>();
        private readonly Dictionary<uint, long> _relayBudget = new Dictionary<uint, long>();
        private readonly Dictionary<uint, DateTime> _lastWatchRequestUtc = new Dictionary<uint, DateTime>();
        private DateTime _relayBudgetResetUtc = DateTime.UtcNow;

        /// <summary>Viewer callback: (sharer id, one complete encoded frame, is keyframe). Receive thread.</summary>
        public Action<uint, byte[], bool> VideoFrameReceived { get; set; }

        /// <summary>
        /// Off: never connect directly to other people for screen sharing; video always goes
        /// through the host, so nobody but the host learns your IP address. Takes effect for
        /// new streams.
        /// </summary>
        public bool AllowDirectStreams
        {
            get { return _allowDirectStreams; }
            set { _allowDirectStreams = value; }
        }

        private static ulong LinkKey(uint peerId, uint streamId, bool sender)
        {
            return ((ulong)peerId << 32) ^ ((ulong)streamId << 1) ^ (sender ? 1UL : 0UL);
        }

        private static ulong WatchKey(uint sharerId, uint viewerId)
        {
            return ((ulong)sharerId << 32) | viewerId;
        }

        // ===== Going live =====

        public bool IsSharingScreen { get { return _localStreamId != 0; } }

        /// <summary>Goes live. Returns false if not in a voice channel or the size/rate isn't supported.</summary>
        public bool StartScreenShare(int width, int height, int fps)
        {
            if (!_running || !IsConnected || _localChannelId <= 0)
                return false;
            if (!ValidStreamFormat(width, height, fps))
                return false;
            uint id = (uint)RandomNumber() | 1u;
            _localStreamWidth = width;
            _localStreamHeight = height;
            _localStreamFps = fps;
            _localStreamId = id;
            _keyframeRequested = true;
            AnnounceStream();
            return true;
        }

        public void StopScreenShare()
        {
            if (_localStreamId == 0)
                return;
            _localStreamId = 0;
            AnnounceStream();
            List<StreamLink> ended = new List<StreamLink>();
            lock (_stateLock)
            {
                List<ulong> keys = new List<ulong>();
                foreach (KeyValuePair<ulong, StreamLink> pair in _streamLinks)
                    if (pair.Value.IsSender) keys.Add(pair.Key);
                foreach (ulong key in keys)
                {
                    ended.Add(_streamLinks[key]);
                    _streamLinks.Remove(key);
                }
            }
            foreach (StreamLink link in ended)
                link.Dispose();
        }

        private static bool ValidStreamFormat(int width, int height, int fps)
        {
            return width >= 160 && height >= 120 && width <= 5120 && height <= 4096 && (long)width * height <= MaxStreamPixels &&
                   (fps == 30 || fps == 60 || fps == 120);
        }

        private void AnnounceStream()
        {
            if (_isHost)
            {
                SetHostStream(_localId, _localStreamId, _localStreamWidth, _localStreamHeight, _localStreamFps);
                return;
            }
            if (!_connected || _hostEndpoint == null)
                return;
            byte[] payload = new byte[4 + 2 + 2 + 1 + 6];
            Buffer.BlockCopy(BitConverter.GetBytes(_localStreamId), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)_localStreamWidth), 0, payload, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)_localStreamHeight), 0, payload, 6, 2);
            payload[8] = (byte)_localStreamFps;
            WriteEndpoint(payload, 9, LocalEndpointTowardsHost());
            // Sent three times: losing a "stopped" would leave a dead badge for a while.
            for (int i = 0; i < 3; i++)
                SendPacket(_hostEndpoint, PacketStreamAnnounce, _localId, 0, payload);
        }

        /// <summary>Viewers (or a host relay) asked: the encoder should make the next frame a keyframe.</summary>
        public bool ConsumeKeyframeRequest()
        {
            bool requested = _keyframeRequested;
            _keyframeRequested = false;
            return requested;
        }

        public int StreamViewerCount
        {
            get
            {
                lock (_stateLock)
                {
                    int n = 0;
                    foreach (StreamLink link in _streamLinks.Values)
                        if (link.IsSender) n++;
                    return n;
                }
            }
        }

        /// <summary>
        /// A bitrate every viewer can take, from their reports: the slowest viewer's measured rate,
        /// trimmed when they lose packets, else allowed to grow. 0 = no viewers yet.
        /// </summary>
        public int SuggestedBitrateKbps(int maximumKbps)
        {
            lock (_stateLock)
            {
                int best = maximumKbps;
                bool any = false;
                foreach (StreamLink link in _streamLinks.Values)
                {
                    if (!link.IsSender || link.ViewerKbps < 0)
                        continue;
                    any = true;
                    int allowed = link.ViewerLossPermille > 30 ? (int)(link.ViewerKbps * 0.8) : (int)(link.ViewerKbps * 1.25) + 500;
                    best = Math.Min(best, allowed);
                }
                return any ? Math.Max(500, best) : maximumKbps;
            }
        }

        /// <summary>Sends one encoded frame to everyone watching. Call from the encoder thread.</summary>
        public void SendVideoFrame(byte[] frame, bool keyframe)
        {
            if (_localStreamId == 0 || frame == null || frame.Length == 0 || frame.Length > MaxFrameBytes)
                return;
            List<StreamLink> viewers = new List<StreamLink>();
            lock (_stateLock)
            {
                foreach (StreamLink link in _streamLinks.Values)
                    if (link.IsSender && link.StreamId == _localStreamId) viewers.Add(link);
            }
            if (viewers.Count == 0)
                return;

            uint number = ++_nextFrameNumber;
            int count = (frame.Length + MediaFragmentBytes - 1) / MediaFragmentBytes;
            for (int i = 0; i < count; i++)
            {
                int offset = i * MediaFragmentBytes;
                int length = Math.Min(MediaFragmentBytes, frame.Length - offset);
                byte[] media = new byte[1 + 4 + 2 + 2 + 1 + length];
                media[0] = MediaVideo;
                Buffer.BlockCopy(BitConverter.GetBytes(number), 0, media, 1, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)i), 0, media, 5, 2);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)count), 0, media, 7, 2);
                media[9] = keyframe ? (byte)1 : (byte)0;
                Buffer.BlockCopy(frame, offset, media, 10, length);
                foreach (StreamLink link in viewers)
                    SendMedia(link, media);
            }
        }

        // ===== Watching =====

        public LiveStreamInfo[] GetLiveStreams()
        {
            lock (_stateLock)
                return new List<LiveStreamInfo>(_liveStreams.Values).ToArray();
        }

        public LiveStreamInfo GetLiveStream(uint memberId)
        {
            lock (_stateLock)
            {
                LiveStreamInfo info;
                return _liveStreams.TryGetValue(memberId, out info) ? info : null;
            }
        }

        public bool WatchStream(uint sharerId)
        {
            LiveStreamInfo info = GetLiveStream(sharerId);
            if (info == null || sharerId == _localId || !IsConnected)
                return false;
            if (_isHost)
                return HostStartWatch(_localId, sharerId, info.StreamId, _allowDirectStreams);
            byte[] payload = new byte[4 + 4 + 1 + 6];
            Buffer.BlockCopy(BitConverter.GetBytes(sharerId), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(info.StreamId), 0, payload, 4, 4);
            payload[8] = _allowDirectStreams ? (byte)1 : (byte)0;
            WriteEndpoint(payload, 9, LocalEndpointTowardsHost());
            SendPacket(_hostEndpoint, PacketStreamWatch, _localId, 0, payload);
            return true;
        }

        public void StopWatching(uint sharerId)
        {
            StreamLink link = RemoveReceivingLink(sharerId);
            if (link != null)
                link.Dispose();
            if (_isHost)
                HostEndWatch(sharerId, _localId);
            else if (_connected && _hostEndpoint != null)
                SendPacket(_hostEndpoint, PacketStreamWatchStop, _localId, 0, BitConverter.GetBytes(sharerId));
        }

        /// <summary>Viewer: ask the sharer for a fresh keyframe (e.g. after the viewer fell behind).</summary>
        public void RequestStreamKeyframe(uint sharerId)
        {
            StreamLink link = null;
            lock (_stateLock)
            {
                foreach (StreamLink l in _streamLinks.Values)
                    if (!l.IsSender && l.PeerId == sharerId) { link = l; break; }
            }
            if (link != null)
            {
                link.LastKeyframeRequestUtc = DateTime.MinValue;
                RequestKeyframe(link);
            }
        }

        public StreamWatchState GetWatchState(uint sharerId)
        {
            lock (_stateLock)
            {
                foreach (StreamLink link in _streamLinks.Values)
                {
                    if (!link.IsSender && link.PeerId == sharerId)
                        return link.DirectEndpoint != null ? StreamWatchState.Direct : (link.AnyDelivered ? StreamWatchState.Relayed : StreamWatchState.Connecting);
                }
            }
            return StreamWatchState.None;
        }

        private StreamLink RemoveReceivingLink(uint sharerId)
        {
            lock (_stateLock)
            {
                foreach (KeyValuePair<ulong, StreamLink> pair in _streamLinks)
                {
                    if (!pair.Value.IsSender && pair.Value.PeerId == sharerId)
                    {
                        _streamLinks.Remove(pair.Key);
                        return pair.Value;
                    }
                }
            }
            return null;
        }

        // ===== Host: streams and introductions =====

        private void SetHostStream(uint memberId, uint streamId, int width, int height, int fps)
        {
            List<HostWatch> ended = new List<HostWatch>();
            lock (_stateLock)
            {
                LiveStreamInfo existing;
                _liveStreams.TryGetValue(memberId, out existing);
                if (streamId == 0)
                {
                    if (existing == null)
                        return;
                    _liveStreams.Remove(memberId);
                }
                else
                {
                    if (existing != null && existing.StreamId == streamId)
                        return; // Repeat.
                    LiveStreamInfo info = new LiveStreamInfo();
                    info.MemberId = memberId;
                    info.StreamId = streamId;
                    info.Width = width;
                    info.Height = height;
                    info.Fps = fps;
                    _liveStreams[memberId] = info;
                }
                foreach (HostWatch w in _hostWatches.Values)
                    if (w.SharerId == memberId && (existing == null || w.StreamId == existing.StreamId)) ended.Add(w);
            }
            foreach (HostWatch w in ended)
                HostEndWatch(w.SharerId, w.ViewerId);
            BroadcastPeerList();
        }

        private bool HandleHostStreamPacket(Participant known, Packet packet)
        {
            byte[] p = packet.Payload;
            if (packet.Type == PacketStreamAnnounce)
            {
                if (p == null || p.Length != 15)
                    return true;
                uint streamId = BitConverter.ToUInt32(p, 0);
                int width = BitConverter.ToUInt16(p, 4), height = BitConverter.ToUInt16(p, 6), fps = p[8];
                IPEndPoint local = ReadEndpoint(p, 9);
                lock (_stateLock)
                {
                    if (local != null)
                        _memberLocalEndpoints[known.Id] = local;
                    if (streamId != 0 && (known.ChannelId <= 0 || !ValidStreamFormat(width, height, fps)))
                        return true; // Must be in voice, with a supported format.
                }
                SetHostStream(known.Id, streamId, width, height, fps);
                return true;
            }

            if (packet.Type == PacketStreamWatch)
            {
                if (p == null || p.Length != 15)
                    return true;
                uint sharerId = BitConverter.ToUInt32(p, 0);
                uint streamId = BitConverter.ToUInt32(p, 4);
                bool allowDirect = p[8] == 1;
                IPEndPoint local = ReadEndpoint(p, 9);
                lock (_stateLock)
                {
                    DateTime last;
                    if (_lastWatchRequestUtc.TryGetValue(known.Id, out last) && (DateTime.UtcNow - last).TotalMilliseconds < 250)
                        return true; // Flood protection.
                    _lastWatchRequestUtc[known.Id] = DateTime.UtcNow;
                    if (local != null)
                        _memberLocalEndpoints[known.Id] = local;
                }
                HostStartWatch(known.Id, sharerId, streamId, allowDirect);
                return true;
            }

            if (packet.Type == PacketStreamWatchStop)
            {
                if (p != null && p.Length == 4)
                    HostEndWatch(BitConverter.ToUInt32(p, 0), known.Id);
                return true;
            }

            if (packet.Type == PacketRelayMedia)
            {
                HostRelay(known.Id, p);
                return true;
            }
            return false;
        }

        /// <summary>Host: introduce a viewer to a sharer. Checks the stream exists and both are in the same voice channel.</summary>
        private bool HostStartWatch(uint viewerId, uint sharerId, uint streamId, bool viewerAllowsDirect)
        {
            Participant sharer = null, viewer = null;
            IPEndPoint sharerLocal, viewerLocal;
            lock (_stateLock)
            {
                LiveStreamInfo info;
                if (viewerId == sharerId || !_liveStreams.TryGetValue(sharerId, out info) || info.StreamId != streamId)
                    return false;
                int sharerChannel, viewerChannel;
                if (!_memberChannels.TryGetValue(sharerId, out sharerChannel) || !_memberChannels.TryGetValue(viewerId, out viewerChannel) ||
                    sharerChannel <= 0 || sharerChannel != viewerChannel)
                    return false; // Watch streams in your own voice channel.
                int viewers = 0;
                foreach (HostWatch w in _hostWatches.Values)
                    if (w.SharerId == sharerId) viewers++;
                if (viewers >= MaxStreamViewers && !_hostWatches.ContainsKey(WatchKey(sharerId, viewerId)))
                    return false;
                sharer = FindParticipantLocked(sharerId);
                viewer = FindParticipantLocked(viewerId);
                if ((sharer == null && sharerId != _localId) || (viewer == null && viewerId != _localId))
                    return false;
                HostWatch watch = new HostWatch();
                watch.SharerId = sharerId;
                watch.ViewerId = viewerId;
                watch.StreamId = streamId;
                _hostWatches[WatchKey(sharerId, viewerId)] = watch;
                _memberLocalEndpoints.TryGetValue(sharerId, out sharerLocal);
                _memberLocalEndpoints.TryGetValue(viewerId, out viewerLocal);
            }

            byte[] key = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(key);
            // Direct only between two members who both allow it; the host is reached directly anyway.
            bool direct = viewerAllowsDirect && sharer != null && viewer != null;
            IPEndPoint sharerPublic = sharer == null ? null : sharer.Endpoint;
            IPEndPoint viewerPublic = viewer == null ? null : viewer.Endpoint;

            if (sharerId == _localId)
                CreateLink(viewerId, streamId, true, false, key, sharerId, viewerId, null, null);
            else
                SendPacket(sharer.Endpoint, PacketStreamPeer, _localId, 0, BuildStreamPeer(viewerId, streamId, true, direct, key, viewerPublic, viewerLocal));
            if (viewerId == _localId)
                CreateLink(sharerId, streamId, false, false, key, sharerId, viewerId, null, null);
            else
                SendPacket(viewer.Endpoint, PacketStreamPeer, _localId, 0, BuildStreamPeer(sharerId, streamId, false, direct, key, sharerPublic, sharerLocal));
            Array.Clear(key, 0, key.Length);
            return true;
        }

        private void HostEndWatch(uint sharerId, uint viewerId)
        {
            HostWatch watch;
            Participant sharer, viewer;
            lock (_stateLock)
            {
                ulong key = WatchKey(sharerId, viewerId);
                if (!_hostWatches.TryGetValue(key, out watch))
                    return;
                _hostWatches.Remove(key);
                sharer = FindParticipantLocked(sharerId);
                viewer = FindParticipantLocked(viewerId);
            }
            byte[] toSharer = new byte[8], toViewer = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(viewerId), 0, toSharer, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(watch.StreamId), 0, toSharer, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(sharerId), 0, toViewer, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(watch.StreamId), 0, toViewer, 4, 4);
            if (sharerId == _localId) EndLink(viewerId, watch.StreamId, true);
            else if (sharer != null) SendPacket(sharer.Endpoint, PacketStreamPeerEnd, _localId, 0, toSharer);
            if (viewerId == _localId) EndLink(sharerId, watch.StreamId, false);
            else if (viewer != null) SendPacket(viewer.Endpoint, PacketStreamPeerEnd, _localId, 0, toViewer);
        }

        /// <summary>Host maintenance: end streams and watches that are no longer valid.</summary>
        private void HostStreamMaintenance(DateTime now)
        {
            List<uint> stopStreams = new List<uint>();
            List<HostWatch> endWatches = new List<HostWatch>();
            lock (_stateLock)
            {
                foreach (LiveStreamInfo info in _liveStreams.Values)
                {
                    int channel;
                    if (!_memberNames.ContainsKey(info.MemberId) || !_memberChannels.TryGetValue(info.MemberId, out channel) || channel <= 0)
                        stopStreams.Add(info.MemberId); // Left the room or voice: the stream ends.
                }
                foreach (HostWatch w in _hostWatches.Values)
                {
                    int sc, vc;
                    LiveStreamInfo info;
                    if (!_liveStreams.TryGetValue(w.SharerId, out info) || info.StreamId != w.StreamId ||
                        !_memberChannels.TryGetValue(w.SharerId, out sc) || !_memberChannels.TryGetValue(w.ViewerId, out vc) || sc != vc)
                        endWatches.Add(w);
                }
                if ((now - _relayBudgetResetUtc).TotalSeconds >= 1.0)
                {
                    _relayBudget.Clear();
                    _relayBudgetResetUtc = now;
                }
            }
            foreach (uint id in stopStreams)
            {
                if (id == _localId)
                    _localStreamId = 0;
                SetHostStream(id, 0, 0, 0, 0);
            }
            foreach (HostWatch w in endWatches)
                HostEndWatch(w.SharerId, w.ViewerId);
        }

        /// <summary>Host: forward a sealed media packet between two people it introduced.</summary>
        private void HostRelay(uint fromId, byte[] p)
        {
            if (p == null || p.Length < 4 + MediaAdBytes + PacketCipher.Overhead || p.Length > 4 + MediaAdBytes + PacketCipher.Overhead + 16 + MediaFragmentBytes)
                return;
            uint toId = BitConverter.ToUInt32(p, 0);
            Participant target;
            lock (_stateLock)
            {
                if (!_hostWatches.ContainsKey(WatchKey(fromId, toId)) && !_hostWatches.ContainsKey(WatchKey(toId, fromId)))
                    return; // Only between a sharer and a viewer the host introduced.
                long used;
                _relayBudget.TryGetValue(fromId, out used);
                if (used + p.Length > RelayBytesPerSecond)
                    return; // Bandwidth cap per member.
                _relayBudget[fromId] = used + p.Length;
                target = FindParticipantLocked(toId);
            }
            if (toId == _localId)
            {
                HandleSealedMedia(fromId, p, 4, p.Length - 4, null);
                return;
            }
            if (target == null)
                return;
            byte[] forward = (byte[])p.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes(fromId), 0, forward, 0, 4); // Receivers learn who it's from.
            SendPacket(target.Endpoint, PacketRelayMedia, _localId, 0, forward);
        }

        private Participant FindParticipantLocked(uint id)
        {
            foreach (Participant p in _hostParticipants.Values)
                if (p.Id == id) return p;
            return null;
        }

        private static byte[] BuildStreamPeer(uint otherId, uint streamId, bool youSend, bool direct, byte[] key, IPEndPoint publicEndpoint, IPEndPoint localEndpoint)
        {
            byte[] p = new byte[4 + 4 + 1 + 1 + 32 + 6 + 6];
            Buffer.BlockCopy(BitConverter.GetBytes(otherId), 0, p, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(streamId), 0, p, 4, 4);
            p[8] = youSend ? (byte)1 : (byte)0;
            p[9] = direct ? (byte)1 : (byte)0;
            Buffer.BlockCopy(key, 0, p, 10, 32);
            WriteEndpoint(p, 42, publicEndpoint);
            WriteEndpoint(p, 48, localEndpoint);
            return p;
        }

        // ===== Client: introductions from the host =====

        private bool HandleClientStreamPacket(Packet packet)
        {
            byte[] p = packet.Payload;
            if (packet.Type == PacketStreamPeer)
            {
                if (p == null || p.Length != 54)
                    return true;
                uint otherId = BitConverter.ToUInt32(p, 0);
                uint streamId = BitConverter.ToUInt32(p, 4);
                bool youSend = p[8] == 1;
                bool direct = p[9] == 1 && _allowDirectStreams;
                byte[] key = new byte[32];
                Buffer.BlockCopy(p, 10, key, 0, 32);
                if (youSend && streamId != _localStreamId)
                    return true; // Not our current stream.
                uint sharerId = youSend ? _localId : otherId, viewerId = youSend ? otherId : _localId;
                CreateLink(otherId, streamId, youSend, direct, key, sharerId, viewerId, ReadEndpoint(p, 42), ReadEndpoint(p, 48));
                Array.Clear(key, 0, key.Length);
                return true;
            }
            if (packet.Type == PacketStreamPeerEnd)
            {
                if (p != null && p.Length == 8)
                {
                    uint otherId = BitConverter.ToUInt32(p, 0), streamId = BitConverter.ToUInt32(p, 4);
                    EndLink(otherId, streamId, true);
                    EndLink(otherId, streamId, false);
                }
                return true;
            }
            if (packet.Type == PacketRelayMedia)
            {
                if (p != null && p.Length > 4)
                    HandleSealedMedia(BitConverter.ToUInt32(p, 0), p, 4, p.Length - 4, null);
                return true;
            }
            return false;
        }

        private void CreateLink(uint peerId, uint streamId, bool isSender, bool allowDirect, byte[] key, uint sharerId, uint viewerId, IPEndPoint publicEndpoint, IPEndPoint localEndpoint)
        {
            byte[] info = Encoding.ASCII.GetBytes("maela stream " + sharerId + ">" + viewerId + "#" + streamId);
            byte[] keys = Hkdf.DeriveKey(null, key, info, 128);
            byte[] sharerToViewer = new byte[64], viewerToSharer = new byte[64];
            Buffer.BlockCopy(keys, 0, sharerToViewer, 0, 64);
            Buffer.BlockCopy(keys, 64, viewerToSharer, 0, 64);
            Array.Clear(keys, 0, keys.Length);

            StreamLink link = new StreamLink();
            link.PeerId = peerId;
            link.StreamId = streamId;
            link.IsSender = isSender;
            link.AllowDirect = allowDirect && publicEndpoint != null;
            link.Send = new PacketCipher(isSender ? sharerToViewer : viewerToSharer);
            link.Receive = new PacketCipher(isSender ? viewerToSharer : sharerToViewer);
            link.PublicEndpoint = publicEndpoint;
            link.LocalEndpoint = localEndpoint;
            Array.Clear(sharerToViewer, 0, 64);
            Array.Clear(viewerToSharer, 0, 64);

            StreamLink old;
            lock (_stateLock)
            {
                ulong k = LinkKey(peerId, streamId, isSender);
                _streamLinks.TryGetValue(k, out old);
                _streamLinks[k] = link;
            }
            if (old != null)
                old.Dispose();
            if (isSender)
                _keyframeRequested = true; // A new viewer needs a keyframe to start.
            if (link.AllowDirect)
                Punch(link);
        }

        private void EndLink(uint peerId, uint streamId, bool isSender)
        {
            StreamLink link;
            lock (_stateLock)
            {
                ulong k = LinkKey(peerId, streamId, isSender);
                if (!_streamLinks.TryGetValue(k, out link))
                    return;
                _streamLinks.Remove(k);
            }
            link.Dispose();
        }

        // ===== Media =====

        private void SendMedia(StreamLink link, byte[] media)
        {
            byte[] ad = new byte[MediaAdBytes];
            Buffer.BlockCopy(OuterHeader(OuterMedia), 0, ad, 0, OuterHeaderBytes);
            Buffer.BlockCopy(BitConverter.GetBytes(_localId), 0, ad, 6, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(link.PeerId), 0, ad, 10, 4);
            byte[] sealedPacket;
            try { sealedPacket = link.Send.Seal(ad, media); }
            catch (ObjectDisposedException) { return; }

            IPEndPoint direct = link.DirectEndpoint;
            if (direct != null && media[0] != MediaPunch)
            {
                SendDatagram(direct, sealedPacket);
                return;
            }
            if (media[0] == MediaPunch)
            {
                if (link.PublicEndpoint != null) SendDatagram(link.PublicEndpoint, sealedPacket);
                if (link.LocalEndpoint != null && !link.LocalEndpoint.Equals(link.PublicEndpoint)) SendDatagram(link.LocalEndpoint, sealedPacket);
                return;
            }
            // Through the host (or to/from the host itself).
            byte[] relay = new byte[4 + sealedPacket.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(link.PeerId), 0, relay, 0, 4);
            Buffer.BlockCopy(sealedPacket, 0, relay, 4, sealedPacket.Length);
            if (_isHost)
            {
                Participant target;
                lock (_stateLock)
                    target = FindParticipantLocked(link.PeerId);
                if (target != null)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(_localId), 0, relay, 0, 4);
                    SendPacket(target.Endpoint, PacketRelayMedia, _localId, 0, relay);
                }
            }
            else if (_hostEndpoint != null)
                SendPacket(_hostEndpoint, PacketRelayMedia, _localId, 0, relay);
        }

        private void Punch(StreamLink link)
        {
            link.LastPunchUtc = DateTime.UtcNow;
            SendMedia(link, new byte[] { MediaPunch });
        }

        /// <summary>A sealed media packet straight from another member (not through the host).</summary>
        private bool TryHandleDirectMedia(IPEndPoint sender, byte[] data, int length)
        {
            if (length < MediaAdBytes || data[5] != OuterMedia)
                return false;
            uint fromId = BitConverter.ToUInt32(data, 6);
            HandleSealedMedia(fromId, data, 0, length, sender);
            return true;
        }

        /// <summary>Checks, decrypts and handles one sealed media packet from <paramref name="fromId"/>.</summary>
        private void HandleSealedMedia(uint fromId, byte[] buffer, int offset, int length, IPEndPoint directSender)
        {
            if (length < MediaAdBytes + PacketCipher.Overhead + 1 || length > 1400)
                return;
            byte[] sealedPacket = new byte[length];
            Buffer.BlockCopy(buffer, offset, sealedPacket, 0, length);
            if (BitConverter.ToUInt32(sealedPacket, 0) != Magic || sealedPacket[4] != WireVersion || sealedPacket[5] != OuterMedia)
                return;
            if (BitConverter.ToUInt32(sealedPacket, 6) != fromId || BitConverter.ToUInt32(sealedPacket, 10) != _localId)
                return;

            List<StreamLink> candidates = new List<StreamLink>();
            lock (_stateLock)
            {
                foreach (StreamLink l in _streamLinks.Values)
                    if (l.PeerId == fromId) candidates.Add(l);
            }
            foreach (StreamLink link in candidates)
            {
                byte[] media;
                try { media = link.Receive.Open(sealedPacket, sealedPacket.Length, MediaAdBytes); }
                catch (ObjectDisposedException) { continue; }
                if (media == null || media.Length == 0)
                    continue;
                link.LastHeardUtc = DateTime.UtcNow;
                if (directSender != null && link.AllowDirect && link.DirectEndpoint == null)
                {
                    link.DirectEndpoint = new IPEndPoint(directSender.Address, directSender.Port);
                    Punch(link); // Let the other side know this path works too.
                }
                HandleMedia(link, media);
                return;
            }
        }

        private void HandleMedia(StreamLink link, byte[] media)
        {
            switch (media[0])
            {
                case MediaPunch:
                    return;
                case MediaKeyframeRequest:
                    if (link.IsSender)
                        _keyframeRequested = true;
                    return;
                case MediaReport:
                    if (link.IsSender && media.Length == 1 + 4 + 2)
                    {
                        link.ViewerKbps = (int)Math.Min(1000000, BitConverter.ToUInt32(media, 1));
                        link.ViewerLossPermille = Math.Min(1000, (int)BitConverter.ToUInt16(media, 5));
                    }
                    return;
                case MediaVideo:
                    if (!link.IsSender && media.Length > 10)
                        ReceiveFragment(link, media);
                    return;
            }
        }

        private void ReceiveFragment(StreamLink link, byte[] media)
        {
            uint number = BitConverter.ToUInt32(media, 1);
            int index = BitConverter.ToUInt16(media, 5), count = BitConverter.ToUInt16(media, 7);
            bool keyframe = media[9] == 1;
            int length = media.Length - 10;
            if (count == 0 || index >= count || (long)count * MediaFragmentBytes > MaxFrameBytes + MediaFragmentBytes ||
                (index < count - 1 && length != MediaFragmentBytes) || length > MediaFragmentBytes)
                return;

            byte[] complete = null;
            bool completeKey = false;
            bool requestKeyframe = false;
            uint sharer = link.PeerId;
            lock (_stateLock)
            {
                link.ReportBytes += media.Length;
                link.ReportFragments++;
                if (link.AnyDelivered && (int)(number - link.LastDelivered) <= 0)
                    return; // Old.
                FrameAssembly frame;
                if (!link.Frames.TryGetValue(number, out frame))
                {
                    if (link.Frames.Count >= MaxPendingFrames)
                        DropOldestFrame(link);
                    frame = new FrameAssembly();
                    frame.Fragments = new byte[count][];
                    frame.Keyframe = keyframe;
                    link.Frames[number] = frame;
                }
                if (frame.Fragments.Length != count || frame.Fragments[index] != null)
                    return;
                byte[] data = new byte[length];
                Buffer.BlockCopy(media, 10, data, 0, length);
                frame.Fragments[index] = data;
                frame.Received++;
                if (frame.Received < count)
                    return;

                // Complete. Anything older still pending was lost.
                link.Frames.Remove(number);
                List<uint> older = new List<uint>();
                foreach (KeyValuePair<uint, FrameAssembly> pair in link.Frames)
                    if ((int)(pair.Key - number) < 0) older.Add(pair.Key);
                foreach (uint o in older)
                {
                    FrameAssembly lost = link.Frames[o];
                    link.ReportLostFragments += lost.Fragments.Length - lost.Received;
                    link.Frames.Remove(o);
                }
                bool gap = link.AnyDelivered && number != link.LastDelivered + 1;
                if (gap || older.Count > 0)
                    link.WaitingForKeyframe = true;
                link.LastDelivered = number;
                link.AnyDelivered = true;
                if (link.WaitingForKeyframe && !frame.Keyframe)
                {
                    requestKeyframe = true; // Can't decode this cleanly; wait for a keyframe.
                }
                else
                {
                    link.WaitingForKeyframe = false;
                    complete = new byte[(count - 1) * MediaFragmentBytes + frame.Fragments[count - 1].Length];
                    int at = 0;
                    for (int i = 0; i < count; i++)
                    {
                        Buffer.BlockCopy(frame.Fragments[i], 0, complete, at, frame.Fragments[i].Length);
                        at += frame.Fragments[i].Length;
                    }
                    completeKey = frame.Keyframe;
                }
            }
            if (requestKeyframe)
                RequestKeyframe(link);
            if (complete != null)
            {
                Action<uint, byte[], bool> sink = VideoFrameReceived;
                if (sink != null)
                    sink(sharer, complete, completeKey);
            }
        }

        private static void DropOldestFrame(StreamLink link)
        {
            bool first = true;
            uint oldest = 0;
            foreach (uint n in link.Frames.Keys)
            {
                if (first || (int)(n - oldest) < 0) { oldest = n; first = false; }
            }
            if (!first)
            {
                FrameAssembly lost = link.Frames[oldest];
                link.ReportLostFragments += lost.Fragments.Length - lost.Received;
                link.Frames.Remove(oldest);
                link.WaitingForKeyframe = true;
            }
        }

        private void RequestKeyframe(StreamLink link)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - link.LastKeyframeRequestUtc).TotalMilliseconds < 300)
                return;
            link.LastKeyframeRequestUtc = now;
            SendMedia(link, new byte[] { MediaKeyframeRequest });
        }

        /// <summary>Both roles, from the maintenance loop: punching, keyframe requests and reports.</summary>
        private void StreamLinkMaintenance(DateTime now)
        {
            List<StreamLink> links;
            lock (_stateLock)
                links = new List<StreamLink>(_streamLinks.Values);
            foreach (StreamLink link in links)
            {
                // Keep trying a direct path for 6 s; also keeps the router's mapping open.
                if (link.AllowDirect && link.DirectEndpoint == null && (now - link.CreatedUtc).TotalSeconds < 6 && (now - link.LastPunchUtc).TotalMilliseconds >= 200)
                    Punch(link);
                if (link.DirectEndpoint != null && (now - link.LastPunchUtc).TotalSeconds >= 5)
                    Punch(link);

                if (link.IsSender)
                    continue;
                if (link.WaitingForKeyframe && (now - link.CreatedUtc).TotalMilliseconds > 200)
                    RequestKeyframe(link);
                double seconds = (now - link.LastReportUtc).TotalSeconds;
                if (seconds >= 0.5)
                {
                    long bytes;
                    int fragments, lost;
                    lock (_stateLock)
                    {
                        bytes = link.ReportBytes; fragments = link.ReportFragments; lost = link.ReportLostFragments;
                        link.ReportBytes = 0; link.ReportFragments = 0; link.ReportLostFragments = 0;
                    }
                    link.LastReportUtc = now;
                    int kbps = (int)Math.Min(1000000, bytes * 8 / 1000 / seconds);
                    int lossPermille = fragments + lost == 0 ? 0 : (int)(1000L * lost / (fragments + lost));
                    byte[] report = new byte[7];
                    report[0] = MediaReport;
                    Buffer.BlockCopy(BitConverter.GetBytes((uint)kbps), 0, report, 1, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes((ushort)lossPermille), 0, report, 5, 2);
                    SendMedia(link, report);
                }
            }
        }

        // ===== Peer list section and helpers =====

        private void WriteStreamSection(System.IO.BinaryWriter writer)
        {
            if (_liveStreams.Count == 0)
                return;
            writer.Write(PeerListSectionStreams);
            writer.Write((byte)Math.Min(255, _liveStreams.Count));
            int n = 0;
            foreach (LiveStreamInfo info in _liveStreams.Values)
            {
                if (n++ >= 255) break;
                writer.Write(info.MemberId);
                writer.Write(info.StreamId);
                writer.Write((ushort)info.Width);
                writer.Write((ushort)info.Height);
                writer.Write((byte)info.Fps);
            }
        }

        private static int ReadStreamSection(byte[] payload, int offset, Dictionary<uint, LiveStreamInfo> into)
        {
            if (offset >= payload.Length)
                return -1;
            int count = payload[offset++];
            if (offset + count * 13 > payload.Length)
                return -1;
            for (int i = 0; i < count; i++)
            {
                LiveStreamInfo info = new LiveStreamInfo();
                info.MemberId = BitConverter.ToUInt32(payload, offset);
                info.StreamId = BitConverter.ToUInt32(payload, offset + 4);
                info.Width = BitConverter.ToUInt16(payload, offset + 8);
                info.Height = BitConverter.ToUInt16(payload, offset + 10);
                info.Fps = payload[offset + 12];
                offset += 13;
                if (info.StreamId != 0 && ValidStreamFormat(info.Width, info.Height, info.Fps))
                    into[info.MemberId] = info;
            }
            return offset;
        }

        /// <summary>Client: apply the room's live streams; stop watching any that ended. _stateLock held.</summary>
        private List<StreamLink> ApplyLiveStreamsLocked(Dictionary<uint, LiveStreamInfo> streams)
        {
            _liveStreams.Clear();
            foreach (KeyValuePair<uint, LiveStreamInfo> pair in streams)
                _liveStreams[pair.Key] = pair.Value;
            List<StreamLink> ended = new List<StreamLink>();
            List<ulong> keys = new List<ulong>();
            foreach (KeyValuePair<ulong, StreamLink> pair in _streamLinks)
            {
                if (pair.Value.IsSender)
                    continue;
                LiveStreamInfo info;
                if (!_liveStreams.TryGetValue(pair.Value.PeerId, out info) || info.StreamId != pair.Value.StreamId)
                    keys.Add(pair.Key);
            }
            foreach (ulong k in keys)
            {
                ended.Add(_streamLinks[k]);
                _streamLinks.Remove(k);
            }
            return ended;
        }

        private void ResetStreams()
        {
            List<StreamLink> links;
            lock (_stateLock)
            {
                links = new List<StreamLink>(_streamLinks.Values);
                _streamLinks.Clear();
                _liveStreams.Clear();
                _hostWatches.Clear();
                _memberLocalEndpoints.Clear();
                _relayBudget.Clear();
                _lastWatchRequestUtc.Clear();
            }
            _localStreamId = 0;
            foreach (StreamLink link in links)
                link.Dispose();
        }

        private static void WriteEndpoint(byte[] buffer, int offset, IPEndPoint endpoint)
        {
            if (endpoint == null || endpoint.AddressFamily != AddressFamily.InterNetwork)
                return; // Zeros = none.
            Buffer.BlockCopy(endpoint.Address.GetAddressBytes(), 0, buffer, offset, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)endpoint.Port), 0, buffer, offset + 4, 2);
        }

        private static IPEndPoint ReadEndpoint(byte[] buffer, int offset)
        {
            int port = BitConverter.ToUInt16(buffer, offset + 4);
            if (port == 0)
                return null;
            byte[] address = new byte[4];
            Buffer.BlockCopy(buffer, offset, address, 0, 4);
            if (address[0] == 0 || address[0] == 255 || address[0] >= 224)
                return null; // Not a usable unicast address.
            return new IPEndPoint(new IPAddress(address), port);
        }

        /// <summary>Our LAN address and port, for people on the same network as us (no traffic is sent).</summary>
        private IPEndPoint LocalEndpointTowardsHost()
        {
            if (_hostEndpoint == null || _boundPort == 0)
                return null;
            try
            {
                using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(_hostEndpoint); // UDP connect only picks a route.
                    IPEndPoint local = probe.LocalEndPoint as IPEndPoint;
                    return local == null ? null : new IPEndPoint(local.Address, _boundPort);
                }
            }
            catch
            {
                return null;
            }
        }

        private static int RandomNumber()
        {
            byte[] b = new byte[4];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(b);
            return BitConverter.ToInt32(b, 0) & 0x7FFFFFFF;
        }
    }
}
