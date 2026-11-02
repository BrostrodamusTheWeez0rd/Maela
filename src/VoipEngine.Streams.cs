using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

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

    /// <summary>How sending your stream is going, for the stats readout.</summary>
    internal sealed class ShareNetworkStats
    {
        public long QueuedBytes, DroppedFrames, ResentFragments;
        public int LossPermille, RttMs, ParityGroup;
    }

    /// <summary>How a stream you're watching is arriving, for the stats readout.</summary>
    internal sealed class WatchNetworkStats
    {
        public int Kbps, LossPermille;
        public long Repaired, Lost, KeyframeRequests, AudioFrames, AudioConcealed;
    }

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
        // Older copies ignore these three, and still get and send plain video.
        private const byte MediaParity = 4;
        private const byte MediaResend = 5;
        private const byte MediaProbe = 6;
        private const byte MediaProbeReply = 7;
        private const byte MediaAudio = 8;               // Sound of the shared screen: sequence, Opus.

        private const int RecoveryWaitMs = 120;          // How long a later frame waits for a damaged one to be repaired.
        private const int ResendAfterMs = 15;            // Give parity and stragglers this long first.
        private const int ResendRetryMs = 40;
        private const int ResendTries = 2;
        private const int ResendMaxPerRequest = 64;
        private const int ResendMaxPerSecond = 2000;     // Per viewer; a request can't make us send without limit.
        private const int KeepSentFrames = 128;
        private const long KeepSentBytes = 16 * 1024 * 1024;
        private const int KeyframeRequestGapMs = 500;    // Bursts of requests shouldn't become bursts of keyframes.
        private const int ParityHoldSeconds = 10;
        private const int CongestionRiseMs = 30;

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
            public double RttMs = -1, BaseRttMs = -1;
            public int ResendBudget = ResendMaxPerSecond;
            public DateTime ResendBudgetUtc = DateTime.UtcNow;
            // Receiving side: for the readout, and for repairing frames.
            public DateTime GapSinceUtc = DateTime.MinValue;
            public int LastKbps, LastLossPermille;
            public long Repaired, Lost, KeyframeRequests;
            // Stream sound (only the receive thread touches these, until Dispose).
            public StreamAudioDecoder AudioDecoder;
            public PlaybackStream AudioPlayback;
            public bool AudioPlaybackFailed;
            public long AudioFrames;

            public void Dispose()
            {
                if (Send != null) Send.Dispose();
                if (Receive != null) Receive.Dispose();
                PlaybackStream playback = AudioPlayback;
                AudioPlayback = null;
                if (playback != null) playback.Dispose();
            }
        }

        private sealed class FrameAssembly
        {
            public byte[][] Fragments;
            public int Received;
            public bool Keyframe;
            public DateTime FirstUtc = DateTime.UtcNow;
            public DateTime LastResendUtc = DateTime.MinValue;
            public int ResendTries;
            public int ResendAsked;
            public List<byte[]> Parity;
            public bool Complete { get { return Received == Fragments.Length; } }
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
        // Pacing.
        private SendScheduler _scheduler;
        private DateTime _lastPacerKeyframeUtc = DateTime.MinValue;
        private DateTime _lastVideoCapUtc = DateTime.MinValue;
        private long _videoCapBytes;
        private BitrateController _bitrate;
        // What was sent lately, so lost fragments can go again.
        private readonly Dictionary<uint, byte[][]> _sentFrames = new Dictionary<uint, byte[][]>();
        private readonly Queue<uint> _sentOrder = new Queue<uint>();
        private long _sentFrameBytes;
        private int _parityGroup;
        private DateTime _parityUntilUtc = DateTime.MinValue;
        private DateTime _lastViewerKeyframeUtc = DateTime.MinValue;
        private long _resentFragments;

        private readonly Dictionary<uint, int> _streamVolume = new Dictionary<uint, int>();
        private readonly List<byte[]> _audioFrames = new List<byte[]>();

        /// <summary>Tests only: each decoded frame of stream sound that would be played.</summary>
        internal Action<uint, byte[]> StreamAudioForTests { get; set; }
        /// <summary>Tests only: really play stream sound though this engine is otherwise headless.</summary>
        internal bool PlayStreamAudioForTests { get; set; }

        /// <summary>Tests only: this share of incoming direct video is lost on the way in.</summary>
        internal double IncomingMediaLossForTests { get; set; }

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

        /// <summary>Changes what the stream is while live. Same stream, so viewers stay connected.</summary>
        public bool UpdateScreenShare(int width, int height, int fps)
        {
            if (_localStreamId == 0 || !ValidStreamFormat(width, height, fps))
                return false;
            _localStreamWidth = width;
            _localStreamHeight = height;
            _localStreamFps = fps;
            _keyframeRequested = true;
            _videoCapBytes = 0;
            lock (_stateLock)
                _bitrate = null; // Starts again from what the new settings allow.
            AnnounceStream();
            return true;
        }

        public void StopScreenShare()
        {
            if (_localStreamId == 0)
                return;
            _localStreamId = 0;
            AnnounceStream();
            SendScheduler scheduler = _scheduler;
            if (scheduler != null)
                scheduler.ClearVideo();
            _videoCapBytes = 0;
            lock (_stateLock)
            {
                _bitrate = null;
                _sentFrames.Clear();
                _sentOrder.Clear();
                _sentFrameBytes = 0;
                _parityGroup = 0;
            }
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
        /// A bitrate every viewer can take. Falls on signs of congestion only, so a still screen
        /// that happens to send little doesn't hold quality down when it starts moving.
        /// </summary>
        public int SuggestedBitrateKbps(int maximumKbps)
        {
            lock (_stateLock)
            {
                if (_bitrate == null)
                    _bitrate = new BitrateController(maximumKbps);
                else
                    _bitrate.SetMax(maximumKbps);
                return _bitrate.CurrentKbps;
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
            SendScheduler scheduler = _scheduler;
            if (viewers.Count == 0 || scheduler == null)
                return;

            uint number = ++_nextFrameNumber;
            int count = (frame.Length + MediaFragmentBytes - 1) / MediaFragmentBytes;
            int group = _parityGroup;
            int parityCount = group > 0 ? (count + group - 1) / group : 0;
            byte[][] all = new byte[count][];
            PacedPacket[] packets = new PacedPacket[(count + parityCount) * viewers.Count];
            int next = 0;
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
                all[i] = media;
                foreach (StreamLink link in viewers)
                    packets[next++] = new PacedPacket(link, media);
            }
            for (int p = 0; p < parityCount; p++)
            {
                byte[] parity = BuildParity(all, number, count, keyframe, p * group, Math.Min(group, count - p * group));
                foreach (StreamLink link in viewers)
                    packets[next++] = new PacedPacket(link, parity);
            }
            RememberSent(number, all, frame.Length);
            scheduler.EnqueueVideo(packets, keyframe);
        }

        private const int ParityHeaderBytes = 14;

        /// <summary>type, frame, first fragment, fragments covered, frame's fragments, keyframe, XOR of lengths, XOR of data.</summary>
        private static byte[] BuildParity(byte[][] fragments, uint number, int count, bool keyframe, int first, int covered)
        {
            int longest = 0;
            for (int i = first; i < first + covered; i++)
                longest = Math.Max(longest, fragments[i].Length - 10);
            byte[] parity = new byte[ParityHeaderBytes + longest];
            parity[0] = MediaParity;
            Buffer.BlockCopy(BitConverter.GetBytes(number), 0, parity, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)first), 0, parity, 5, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)covered), 0, parity, 7, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)count), 0, parity, 9, 2);
            parity[11] = keyframe ? (byte)1 : (byte)0;
            ushort lengths = 0;
            for (int i = first; i < first + covered; i++)
            {
                int length = fragments[i].Length - 10;
                lengths ^= (ushort)length;
                FecXor.Mix(parity, ParityHeaderBytes, fragments[i], 10, length);
            }
            Buffer.BlockCopy(BitConverter.GetBytes(lengths), 0, parity, 12, 2);
            return parity;
        }

        private void RememberSent(uint number, byte[][] fragments, int bytes)
        {
            lock (_stateLock)
            {
                _sentFrames[number] = fragments;
                _sentOrder.Enqueue(number);
                _sentFrameBytes += bytes;
                while (_sentOrder.Count > KeepSentFrames || (_sentFrameBytes > KeepSentBytes && _sentOrder.Count > 1))
                {
                    uint oldest = _sentOrder.Dequeue();
                    byte[][] old;
                    if (_sentFrames.TryGetValue(oldest, out old))
                    {
                        foreach (byte[] fragment in old)
                            _sentFrameBytes -= fragment.Length - 10;
                        _sentFrames.Remove(oldest);
                    }
                }
            }
        }

        /// <summary>Sharer: a viewer asks for lost fragments again.</summary>
        private void HandleResendRequest(StreamLink link, byte[] media)
        {
            if (media.Length < 6 || media[5] == 0 || media.Length != 6 + media[5] * 2 || media[5] > ResendMaxPerRequest)
                return;
            SendScheduler scheduler = _scheduler;
            if (scheduler == null)
                return;
            uint number = BitConverter.ToUInt32(media, 1);
            List<PacedPacket> packets = new List<PacedPacket>();
            lock (_stateLock)
            {
                byte[][] fragments;
                if (!_sentFrames.TryGetValue(number, out fragments))
                    return;
                DateTime now = DateTime.UtcNow;
                if ((now - link.ResendBudgetUtc).TotalSeconds >= 1)
                {
                    link.ResendBudgetUtc = now;
                    link.ResendBudget = ResendMaxPerSecond;
                }
                for (int i = 0; i < media[5] && link.ResendBudget > 0; i++)
                {
                    int index = BitConverter.ToUInt16(media, 6 + i * 2);
                    if (index >= fragments.Length)
                        continue;
                    packets.Add(new PacedPacket(link, fragments[index]));
                    link.ResendBudget--;
                }
            }
            Interlocked.Add(ref _resentFragments, packets.Count);
            scheduler.EnqueueResend(packets.ToArray());
        }

        // ===== Stream sound =====

        /// <summary>Sends one Opus frame of the shared screen's sound to everyone watching. Goes ahead of video, behind voice.</summary>
        public void SendStreamAudio(uint sequence, byte[] opus)
        {
            if (_localStreamId == 0 || opus == null || opus.Length == 0)
                return;
            List<StreamLink> viewers = new List<StreamLink>();
            lock (_stateLock)
            {
                foreach (StreamLink link in _streamLinks.Values)
                    if (link.IsSender && link.StreamId == _localStreamId) viewers.Add(link);
            }
            if (viewers.Count == 0)
                return;
            byte[] media = new byte[5 + opus.Length];
            media[0] = MediaAudio;
            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, media, 1, 4);
            Buffer.BlockCopy(opus, 0, media, 5, opus.Length);
            foreach (StreamLink link in viewers)
                SendMedia(link, media);
        }

        /// <summary>How loud a stream you're watching plays: 0 to 100, 0 being muted.</summary>
        public void SetStreamVolume(uint sharerId, int volume)
        {
            volume = Math.Max(0, Math.Min(100, volume));
            List<PlaybackStream> playing = new List<PlaybackStream>();
            lock (_stateLock)
            {
                _streamVolume[sharerId] = volume;
                foreach (StreamLink link in _streamLinks.Values)
                {
                    if (!link.IsSender && link.PeerId == sharerId && link.AudioPlayback != null)
                        playing.Add(link.AudioPlayback);
                }
            }
            foreach (PlaybackStream stream in playing)
                stream.SetGain(volume / 100f);
        }

        private void ReceiveStreamAudio(StreamLink link, byte[] media)
        {
            if (media.Length < 6 || media.Length > 5 + StreamAudioEncoder.MaxPacketBytes)
                return;
            if (link.AudioDecoder == null)
                link.AudioDecoder = new StreamAudioDecoder();
            _audioFrames.Clear();
            link.AudioDecoder.Decode(BitConverter.ToUInt32(media, 1), media, 5, media.Length - 5, _audioFrames);
            if (_deafened)
                return;
            foreach (byte[] frame in _audioFrames)
            {
                link.AudioFrames++;
                Action<uint, byte[]> tap = StreamAudioForTests;
                if (tap != null)
                    tap(link.PeerId, frame);
                PlaybackStream playback = PlaybackFor(link);
                if (playback != null)
                    playback.Enqueue(frame);
            }
        }

        private PlaybackStream PlaybackFor(StreamLink link)
        {
            if (link.AudioPlayback != null || link.AudioPlaybackFailed || (HeadlessForTests && !PlayStreamAudioForTests))
                return link.AudioPlayback;
            WAVEFORMATEX format = new WAVEFORMATEX();
            format.wFormatTag = 1;
            format.nChannels = StreamAudioEncoder.Channels;
            format.nSamplesPerSec = StreamAudioEncoder.SampleRate;
            format.wBitsPerSample = 16;
            format.nBlockAlign = (ushort)(StreamAudioEncoder.Channels * 2);
            format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;
            try
            {
                // Through the echo canceller's reference, so speakers don't put the stream into the mic.
                PlaybackStream created = new PlaybackStream(format, _outputDeviceId, _farEnd);
                int volume;
                lock (_stateLock)
                {
                    if (!_streamVolume.TryGetValue(link.PeerId, out volume))
                        volume = 100;
                }
                created.SetGain(volume / 100f);
                link.AudioPlayback = created;
            }
            catch
            {
                link.AudioPlaybackFailed = true; // No speakers: the picture carries on.
            }
            return link.AudioPlayback;
        }

        // ===== Pacing =====

        private void StartScheduler()
        {
            _scheduler = new SendScheduler(
                delegate(object link, byte[] media) { SendMedia((StreamLink)link, media); },
                delegate(object target, byte[] forward) { SendPacket((IPEndPoint)target, PacketRelayMedia, _localId, 0, forward); },
                OnVideoDropped);
        }

        private void StopScheduler()
        {
            SendScheduler scheduler = _scheduler;
            _scheduler = null;
            if (scheduler != null)
                scheduler.Dispose();
        }

        /// <summary>Frames were dropped before sending, so viewers can't decode what follows until a keyframe.</summary>
        private void OnVideoDropped()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastPacerKeyframeUtc).TotalMilliseconds < 1000)
                return; // A keyframe costs more than the frames lost; don't add to the congestion.
            _lastPacerKeyframeUtc = now;
            _keyframeRequested = true;
        }

        /// <summary>
        /// Twice a second, from viewers' reports. Sets the encoder's bitrate and how much parity to
        /// send, and keeps congestion in our own queue, where it can't delay voice, instead of the
        /// router's: when a viewer loses packets, video is held near what actually gets through;
        /// while the loss stays away the limit lifts again.
        /// </summary>
        private void UpdateVideoCap(DateTime now)
        {
            SendScheduler scheduler = _scheduler;
            if (scheduler == null || _localStreamId == 0 || (now - _lastVideoCapUtc).TotalMilliseconds < 500)
                return;
            _lastVideoCapUtc = now;
            long receivedKbps = 0;
            int worstLoss = 0, worstRise = 0, viewers = 0;
            bool anyProbes = false;
            lock (_stateLock)
            {
                foreach (StreamLink link in _streamLinks.Values)
                {
                    if (!link.IsSender || link.ViewerKbps < 0)
                        continue;
                    viewers++;
                    receivedKbps += link.ViewerKbps;
                    worstLoss = Math.Max(worstLoss, link.ViewerLossPermille);
                    if (link.RttMs >= 0)
                    {
                        anyProbes = true;
                        worstRise = Math.Max(worstRise, (int)(link.RttMs - link.BaseRttMs));
                    }
                }
                if (viewers > 0 && _bitrate != null)
                    _bitrate.Update(_clock.ElapsedMilliseconds, worstLoss, worstRise);
                int wanted = FecXor.GroupSizeForLoss(worstLoss);
                if (wanted > 0)
                {
                    _parityGroup = _parityGroup == 0 ? wanted : Math.Min(_parityGroup, wanted);
                    _parityUntilUtc = now.AddSeconds(ParityHoldSeconds);
                }
                else if (now > _parityUntilUtc)
                    _parityGroup = 0;
            }
            if (viewers == 0)
                return;
            // Loss alone can just be bad Wi-Fi, and holding video back then only loses more of it.
            // Loss with rising ping means a queue is filling. No probe replies (an older viewer): go by loss.
            if (worstLoss > 30 && (worstRise > CongestionRiseMs || !anyProbes))
            {
                long arriving = receivedKbps * 125 * 1000 / Math.Max(1, 1000 - worstLoss); // Bytes per second sent, allowing for what was lost.
                long limit = Math.Max(SendScheduler.MinRateBytes * viewers, arriving * 3 / 2);
                _videoCapBytes = _videoCapBytes == 0 ? limit : Math.Min(_videoCapBytes, limit);
            }
            else if (_videoCapBytes > 0)
            {
                _videoCapBytes = _videoCapBytes * 5 / 4;
                if (_videoCapBytes > SendScheduler.MaxRateBytes)
                    _videoCapBytes = 0;
            }
            scheduler.SetVideoCap(_videoCapBytes);
        }

        public ShareNetworkStats GetShareNetworkStats()
        {
            ShareNetworkStats stats = new ShareNetworkStats();
            SendScheduler scheduler = _scheduler;
            if (scheduler != null)
            {
                stats.QueuedBytes = scheduler.QueuedVideoBytes;
                stats.DroppedFrames = scheduler.DroppedFrames;
            }
            stats.ResentFragments = Interlocked.Read(ref _resentFragments);
            lock (_stateLock)
            {
                stats.ParityGroup = _parityGroup;
                foreach (StreamLink link in _streamLinks.Values)
                {
                    if (!link.IsSender)
                        continue;
                    stats.LossPermille = Math.Max(stats.LossPermille, link.ViewerLossPermille);
                    stats.RttMs = Math.Max(stats.RttMs, (int)link.RttMs);
                }
            }
            return stats;
        }

        public WatchNetworkStats GetWatchNetworkStats(uint sharerId)
        {
            lock (_stateLock)
            {
                foreach (StreamLink link in _streamLinks.Values)
                {
                    if (link.IsSender || link.PeerId != sharerId)
                        continue;
                    WatchNetworkStats stats = new WatchNetworkStats();
                    stats.Kbps = link.LastKbps;
                    stats.LossPermille = link.LastLossPermille;
                    stats.Repaired = link.Repaired;
                    stats.Lost = link.Lost;
                    stats.KeyframeRequests = link.KeyframeRequests;
                    stats.AudioFrames = link.AudioFrames;
                    stats.AudioConcealed = link.AudioDecoder == null ? 0 : link.AudioDecoder.Concealed;
                    return stats;
                }
            }
            return null;
        }

        /// <summary>Tests only.</summary>
        internal SendScheduler SchedulerForTests { get { return _scheduler; } }

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
            bool changedOnly = false;
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
                    {
                        if (existing.Width == width && existing.Height == height && existing.Fps == fps)
                            return; // Repeat.
                        changedOnly = true; // Same stream with new settings: viewers carry on.
                    }
                    LiveStreamInfo info = new LiveStreamInfo();
                    info.MemberId = memberId;
                    info.StreamId = streamId;
                    info.Width = width;
                    info.Height = height;
                    info.Fps = fps;
                    _liveStreams[memberId] = info;
                }
                if (!changedOnly)
                {
                    foreach (HostWatch w in _hostWatches.Values)
                        if (w.SharerId == memberId && (existing == null || w.StreamId == existing.StreamId)) ended.Add(w);
                }
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
            // Whole-size ones are video. Keyframe requests, reports, punches and sound don't wait behind it.
            SendScheduler scheduler = _scheduler;
            if (scheduler != null && p.Length > 4 + MediaAdBytes + PacketCipher.Overhead + 5 + StreamAudioEncoder.MaxPacketBytes)
                scheduler.EnqueueRelay(target.Endpoint, forward);
            else
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
            if (IncomingMediaLossForTests > 0.0)
            {
                lock (_testLossRandom)
                {
                    if (_testLossRandom.NextDouble() < IncomingMediaLossForTests)
                        return true;
                }
            }
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
                    {
                        DateTime now = DateTime.UtcNow;
                        if ((now - _lastViewerKeyframeUtc).TotalMilliseconds >= KeyframeRequestGapMs)
                        {
                            _lastViewerKeyframeUtc = now;
                            _keyframeRequested = true;
                        }
                    }
                    return;
                case MediaResend:
                    if (link.IsSender)
                        HandleResendRequest(link, media);
                    return;
                case MediaParity:
                    if (!link.IsSender)
                        ReceiveParity(link, media);
                    return;
                case MediaAudio:
                    if (!link.IsSender)
                        ReceiveStreamAudio(link, media);
                    return;
                case MediaProbe:
                    if (!link.IsSender && media.Length == 9)
                    {
                        media[0] = MediaProbeReply;
                        SendMedia(link, media);
                    }
                    return;
                case MediaProbeReply:
                    if (link.IsSender && media.Length == 9)
                        NoteProbeReply(link, BitConverter.ToInt64(media, 1));
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

            List<KeyValuePair<byte[], bool>> deliver = new List<KeyValuePair<byte[], bool>>();
            bool requestKeyframe = false;
            byte[] resend = null;
            lock (_stateLock)
            {
                link.ReportBytes += media.Length;
                link.ReportFragments++;
                if (link.AnyDelivered && (int)(number - link.LastDelivered) <= 0)
                    return; // Old.
                FrameAssembly frame = FrameFor(link, number, count, keyframe);
                if (frame.Fragments.Length != count || frame.Fragments[index] != null)
                    return;
                byte[] data = new byte[length];
                Buffer.BlockCopy(media, 10, data, 0, length);
                frame.Fragments[index] = data;
                frame.Received++;
                TryParity(link, frame);
                AdvanceFrames(link, DateTime.UtcNow, deliver, ref requestKeyframe);
                resend = NextResendRequest(link, number, DateTime.UtcNow);
            }
            FinishReceive(link, deliver, requestKeyframe, resend);
        }

        private void ReceiveParity(StreamLink link, byte[] media)
        {
            if (media.Length < ParityHeaderBytes + 1 || media.Length > ParityHeaderBytes + MediaFragmentBytes)
                return;
            uint number = BitConverter.ToUInt32(media, 1);
            int first = BitConverter.ToUInt16(media, 5), covered = BitConverter.ToUInt16(media, 7), count = BitConverter.ToUInt16(media, 9);
            if (count == 0 || covered == 0 || first + covered > count || (long)count * MediaFragmentBytes > MaxFrameBytes + MediaFragmentBytes)
                return;

            List<KeyValuePair<byte[], bool>> deliver = new List<KeyValuePair<byte[], bool>>();
            bool requestKeyframe = false;
            lock (_stateLock)
            {
                link.ReportBytes += media.Length;
                if (link.AnyDelivered && (int)(number - link.LastDelivered) <= 0)
                    return;
                FrameAssembly frame = FrameFor(link, number, count, media[11] == 1);
                if (frame.Fragments.Length != count)
                    return;
                if (frame.Parity == null)
                    frame.Parity = new List<byte[]>();
                if (frame.Parity.Count >= count)
                    return;
                frame.Parity.Add(media);
                TryParity(link, frame);
                AdvanceFrames(link, DateTime.UtcNow, deliver, ref requestKeyframe);
            }
            FinishReceive(link, deliver, requestKeyframe, null);
        }

        private void FinishReceive(StreamLink link, List<KeyValuePair<byte[], bool>> deliver, bool requestKeyframe, byte[] resend)
        {
            if (resend != null)
                SendMedia(link, resend);
            if (requestKeyframe)
                RequestKeyframe(link);
            Action<uint, byte[], bool> sink = VideoFrameReceived;
            if (sink != null)
            {
                foreach (KeyValuePair<byte[], bool> item in deliver)
                    sink(link.PeerId, item.Key, item.Value);
            }
        }

        private static FrameAssembly FrameFor(StreamLink link, uint number, int count, bool keyframe)
        {
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
            return frame;
        }

        /// <summary>Rebuilds any fragment that is the only one missing from a parity group.</summary>
        private static void TryParity(StreamLink link, FrameAssembly frame)
        {
            if (frame.Parity == null || frame.Complete)
                return;
            foreach (byte[] parity in frame.Parity)
            {
                int first = BitConverter.ToUInt16(parity, 5), covered = BitConverter.ToUInt16(parity, 7);
                int missing = -1, missingCount = 0;
                for (int i = first; i < first + covered && i < frame.Fragments.Length; i++)
                {
                    if (frame.Fragments[i] == null)
                    {
                        missing = i;
                        missingCount++;
                    }
                }
                if (missingCount != 1)
                    continue;
                int length = BitConverter.ToUInt16(parity, 12);
                for (int i = first; i < first + covered; i++)
                {
                    if (i != missing)
                        length ^= frame.Fragments[i].Length;
                }
                // Parity comes through the same authenticated link, but sizes still get checked.
                bool last = missing == frame.Fragments.Length - 1;
                if (length <= 0 || length > MediaFragmentBytes || (!last && length != MediaFragmentBytes) || length > parity.Length - ParityHeaderBytes)
                    continue;
                byte[] rebuilt = new byte[length];
                Buffer.BlockCopy(parity, ParityHeaderBytes, rebuilt, 0, length);
                for (int i = first; i < first + covered; i++)
                {
                    if (i != missing)
                        FecXor.Mix(rebuilt, 0, frame.Fragments[i], 0, Math.Min(length, frame.Fragments[i].Length));
                }
                frame.Fragments[missing] = rebuilt;
                frame.Received++;
                link.Repaired++;
                link.ReportLostFragments++; // It was still lost: the bitrate should know.
            }
        }

        /// <summary>
        /// Hands over finished frames strictly in order. When one is damaged, later ones wait
        /// briefly for the repair; if none comes, or a newer keyframe makes it pointless, it's
        /// skipped and the viewer waits for a keyframe as it must.
        /// </summary>
        private void AdvanceFrames(StreamLink link, DateTime now, List<KeyValuePair<byte[], bool>> deliver, ref bool requestKeyframe)
        {
            while (true)
            {
                bool anyComplete = false, anyKey = false;
                uint oldestComplete = 0, newestKey = 0;
                foreach (KeyValuePair<uint, FrameAssembly> pair in link.Frames)
                {
                    if (!pair.Value.Complete)
                        continue;
                    if (!anyComplete || (int)(pair.Key - oldestComplete) < 0)
                        oldestComplete = pair.Key;
                    anyComplete = true;
                    if (pair.Value.Keyframe && (!anyKey || (int)(pair.Key - newestKey) > 0))
                    {
                        newestKey = pair.Key;
                        anyKey = true;
                    }
                }
                if (!anyComplete)
                {
                    link.GapSinceUtc = DateTime.MinValue;
                    return;
                }

                uint target;
                if (!link.AnyDelivered)
                    target = anyKey ? newestKey : oldestComplete;
                else if (oldestComplete == link.LastDelivered + 1)
                    target = oldestComplete;
                else if (anyKey)
                    target = newestKey;
                else
                {
                    if (link.GapSinceUtc == DateTime.MinValue)
                        link.GapSinceUtc = now;
                    if ((now - link.GapSinceUtc).TotalMilliseconds < RecoveryWaitMs)
                        return;
                    target = oldestComplete;
                }

                bool skipped = link.AnyDelivered && target != link.LastDelivered + 1;
                List<uint> older = new List<uint>();
                foreach (uint n in link.Frames.Keys)
                {
                    if ((int)(n - target) < 0)
                        older.Add(n);
                }
                foreach (uint o in older)
                {
                    FrameAssembly lost = link.Frames[o];
                    int missing = lost.Fragments.Length - lost.Received;
                    link.Lost += missing;
                    link.ReportLostFragments += Math.Max(0, missing - lost.ResendAsked); // Those it asked for are already counted.
                    link.Frames.Remove(o);
                }
                if (skipped || older.Count > 0)
                    link.WaitingForKeyframe = true;

                FrameAssembly frame = link.Frames[target];
                link.Frames.Remove(target);
                link.GapSinceUtc = DateTime.MinValue;
                link.LastDelivered = target;
                link.AnyDelivered = true;
                if (link.WaitingForKeyframe && !frame.Keyframe)
                {
                    requestKeyframe = true; // Can't decode this cleanly; wait for a keyframe.
                    continue;
                }
                link.WaitingForKeyframe = false;
                int total = 0;
                foreach (byte[] fragment in frame.Fragments)
                    total += fragment.Length;
                byte[] complete = new byte[total];
                int at = 0;
                foreach (byte[] fragment in frame.Fragments)
                {
                    Buffer.BlockCopy(fragment, 0, complete, at, fragment.Length);
                    at += fragment.Length;
                }
                deliver.Add(new KeyValuePair<byte[], bool>(complete, frame.Keyframe));
            }
        }

        /// <summary>The oldest damaged frame behind the newest one seen, if it's time to ask for what it lacks.</summary>
        private static byte[] NextResendRequest(StreamLink link, uint newest, DateTime now)
        {
            bool found = false;
            uint chosen = 0;
            foreach (KeyValuePair<uint, FrameAssembly> pair in link.Frames)
            {
                if ((int)(pair.Key - newest) >= 0 || pair.Value.Complete || pair.Value.ResendTries >= ResendTries)
                    continue;
                if ((now - pair.Value.FirstUtc).TotalMilliseconds < ResendAfterMs || (now - pair.Value.LastResendUtc).TotalMilliseconds < ResendRetryMs)
                    continue;
                if (!found || (int)(pair.Key - chosen) < 0)
                {
                    chosen = pair.Key;
                    found = true;
                }
            }
            if (!found)
                return null;
            FrameAssembly frame = link.Frames[chosen];
            frame.LastResendUtc = now;
            frame.ResendTries++;
            int missing = frame.Fragments.Length - frame.Received;
            if (missing > ResendMaxPerRequest)
            {
                frame.ResendTries = ResendTries; // Too much lost: a keyframe is the better fix.
                return null;
            }
            byte[] request = new byte[6 + missing * 2];
            request[0] = MediaResend;
            Buffer.BlockCopy(BitConverter.GetBytes(chosen), 0, request, 1, 4);
            request[5] = (byte)missing;
            int next = 6;
            for (int i = 0; i < frame.Fragments.Length; i++)
            {
                if (frame.Fragments[i] != null)
                    continue;
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)i), 0, request, next, 2);
                next += 2;
            }
            if (frame.ResendTries == 1)
            {
                frame.ResendAsked = missing;
                link.ReportLostFragments += missing;
            }
            return request;
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
                int missing = lost.Fragments.Length - lost.Received;
                link.Lost += missing;
                link.ReportLostFragments += Math.Max(0, missing - lost.ResendAsked);
                link.Frames.Remove(oldest);
                link.WaitingForKeyframe = true;
            }
        }

        /// <summary>Sharer: how long a probe took there and back. It waits in the same router queue as video, so a rise means congestion.</summary>
        private void NoteProbeReply(StreamLink link, long sentTicks)
        {
            double ms = (_clock.ElapsedTicks - sentTicks) * 1000.0 / Stopwatch.Frequency;
            if (ms < 0 || ms > 5000)
                return;
            lock (_stateLock)
            {
                link.RttMs = link.RttMs < 0 ? ms : link.RttMs + (ms - link.RttMs) * 0.3;
                // The lowest seen is the empty-queue baseline; it drifts up in case the route changes.
                link.BaseRttMs = link.BaseRttMs < 0 || link.RttMs < link.BaseRttMs ? link.RttMs : link.BaseRttMs + 0.05;
            }
        }

        private void RequestKeyframe(StreamLink link)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - link.LastKeyframeRequestUtc).TotalMilliseconds < 300)
                return;
            link.LastKeyframeRequestUtc = now;
            lock (_stateLock)
                link.KeyframeRequests++;
            SendMedia(link, new byte[] { MediaKeyframeRequest });
        }

        /// <summary>Both roles, from the maintenance loop: punching, keyframe requests and reports.</summary>
        private void StreamLinkMaintenance(DateTime now)
        {
            UpdateVideoCap(now);
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
                {
                    byte[] probe = new byte[9];
                    probe[0] = MediaProbe;
                    Buffer.BlockCopy(BitConverter.GetBytes(_clock.ElapsedTicks), 0, probe, 1, 8);
                    SendMedia(link, probe);
                    continue;
                }

                // Frames may be waiting on a repair that won't come, with nothing arriving to notice.
                List<KeyValuePair<byte[], bool>> deliver = new List<KeyValuePair<byte[], bool>>();
                bool needKeyframe = false;
                lock (_stateLock)
                    AdvanceFrames(link, now, deliver, ref needKeyframe);
                FinishReceive(link, deliver, needKeyframe, null);

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
                    link.LastKbps = kbps;
                    link.LastLossPermille = lossPermille;
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
