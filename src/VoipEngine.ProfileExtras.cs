using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Maela
{
    /// <summary>How a profile card's banner is filled.</summary>
    internal enum BannerKind : byte
    {
        Auto = 0,     // The name's colour, as before banners existed.
        Colour = 1,
        Gradient = 2,
        Picture = 3,
    }

    /// <summary>A card deco: a moving effect on someone's profile card, for people who have Dynamic effects on.</summary>
    internal enum CardEffect : byte
    {
        None = 0,
        Glow = 1,
        Sparkles = 2,
        Aurora = 3,
    }

    /// <summary>A picture deco: moves around someone's picture, on their card and wherever they appear.</summary>
    internal enum PictureDeco : byte
    {
        None = 0,
        Orbit = 1,
        Halo = 2,
        Ripple = 3,
    }

    /// <summary>The extras on a member's profile: a banner, a card deco and a picture deco.</summary>
    internal sealed class MemberExtras
    {
        public uint Version;
        public BannerKind Banner;
        public int ColourA, ColourB; // 0xRRGGBB, for the banner and both decos.
        /// <summary>BannerWidth x BannerHeight pixels, RGB565 little-endian, only for a picture banner.</summary>
        public byte[] Pixels;
        public CardEffect Effect;
        public PictureDeco Deco;
    }

    /// <summary>
    /// Profile extras (banner + card effect), shared through the host exactly like profiles,
    /// with the same safety rules: a banner picture is raw pixels made on the sender's PC,
    /// everyone else only decompresses (output capped to the exact size) and copies them, and
    /// the host checks every blob before storing or forwarding it.
    ///
    /// A family of its own (packets 29-32, peer list section 6 written after everything older
    /// versions know) so older versions never see any of it, and their strict profile check is
    /// left alone.
    /// </summary>
    internal sealed partial class VoipEngine
    {
        public const int BannerWidth = 240;
        public const int BannerHeight = 80;
        public const int BannerRawBytes = BannerWidth * BannerHeight * 2;
        public const int MaxExtrasBytes = 32000; // 32 chunks, so a request still fits one bitmask.
        private const int ExtrasHeaderBytes = 10;
        private const byte ExtrasFormat = 1;
        private const byte PeerListSectionExtras = 6;

        private const byte PacketExtrasPut = 29;
        private const byte PacketExtrasAck = 30;
        private const byte PacketExtrasGet = 31;
        private const byte PacketExtrasData = 32;

        private readonly Dictionary<uint, MemberExtras> _extras = new Dictionary<uint, MemberExtras>();
        private readonly Dictionary<uint, byte[]> _extrasBlobs = new Dictionary<uint, byte[]>(); // Host: what we forward.
        private readonly Dictionary<uint, uint> _advertisedExtras = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, ProfileAssembly> _extrasDownloads = new Dictionary<uint, ProfileAssembly>();
        private readonly Dictionary<uint, ProfileAssembly> _extrasUploads = new Dictionary<uint, ProfileAssembly>();
        private readonly Dictionary<uint, DateTime> _lastExtrasChangeUtc = new Dictionary<uint, DateTime>();
        private byte[] _localExtrasBlob;
        private uint _localExtrasVersion;
        private bool _localExtrasAcked = true;
        private DateTime _localExtrasSentUtc = DateTime.MinValue;

        /// <summary>A member's extras, or null if they haven't set any (or they haven't arrived yet).</summary>
        public MemberExtras GetExtras(uint memberId)
        {
            lock (_stateLock)
            {
                MemberExtras e;
                return _extras.TryGetValue(memberId, out e) ? e : null;
            }
        }

        /// <summary>Sets your own extras. Picture pixels must be exactly BannerRawBytes, made on this PC.</summary>
        public void SetLocalExtras(BannerKind banner, int colourA, int colourB, byte[] pixels, CardEffect effect, PictureDeco deco)
        {
            byte[] blob = EncodeExtras(banner, colourA, colourB, pixels, effect, deco);
            if (blob == null)
                return;
            lock (_stateLock)
            {
                _localExtrasBlob = blob;
                _localExtrasVersion = NewProfileVersion(_localExtrasVersion);
                _localExtrasAcked = false;
                _localExtrasSentUtc = DateTime.MinValue;
                if (_isHost && _localId != 0)
                    StoreExtrasLocked(_localId, _localExtrasVersion, blob);
                else
                    ApplyLocalExtrasLocked();
            }
            if (_isHost)
                BroadcastPeerList();
            else
                SendLocalExtras();
        }

        // ----- Encoding -----

        /// <summary>
        /// Extras blob: [u8 format][u8 banner kind][u8 card deco][u8 picture deco][3 bytes colour A][3 bytes colour B]
        /// then, for a picture banner only, the compressed pixels. A photo that won't fit is made
        /// to fit by dropping the lowest colour bits, which compresses better.
        /// </summary>
        internal static byte[] EncodeExtras(BannerKind banner, int colourA, int colourB, byte[] pixels, CardEffect effect, PictureDeco deco)
        {
            if ((byte)banner > (byte)BannerKind.Picture || (byte)effect > (byte)CardEffect.Aurora || (byte)deco > (byte)PictureDeco.Ripple)
                return null;
            if (banner == BannerKind.Picture && (pixels == null || pixels.Length != BannerRawBytes))
                return null;
            for (int drop = 0; drop <= 3; drop++)
            {
                MemoryStream stream = new MemoryStream();
                stream.WriteByte(ExtrasFormat);
                stream.WriteByte((byte)banner);
                stream.WriteByte((byte)effect);
                stream.WriteByte((byte)deco);
                WriteColour(stream, colourA);
                WriteColour(stream, colourB);
                if (banner == BannerKind.Picture)
                {
                    byte[] coarse = drop == 0 ? pixels : Coarser(pixels, drop);
                    using (DeflateStream deflate = new DeflateStream(stream, CompressionMode.Compress, true))
                        deflate.Write(coarse, 0, coarse.Length);
                }
                byte[] blob = stream.ToArray();
                if (blob.Length <= MaxExtrasBytes)
                    return blob;
                if (banner != BannerKind.Picture)
                    return null;
            }
            return null;
        }

        private static void WriteColour(Stream stream, int rgb)
        {
            stream.WriteByte((byte)(rgb >> 16));
            stream.WriteByte((byte)(rgb >> 8));
            stream.WriteByte((byte)rgb);
        }

        /// <summary>RGB565 pixels with the lowest <paramref name="drop"/> bits of each colour cleared.</summary>
        private static byte[] Coarser(byte[] pixels, int drop)
        {
            int keep = (1 << drop) - 1;
            int mask = 0xFFFF & ~((keep << 11) | (keep << 5) | keep);
            byte[] copy = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i += 2)
            {
                int v = (pixels[i] | (pixels[i + 1] << 8)) & mask;
                copy[i] = (byte)v;
                copy[i + 1] = (byte)(v >> 8);
            }
            return copy;
        }

        /// <summary>
        /// Checks and unpacks an extras blob from anyone. Returns null if anything is off: an
        /// unknown format, kind or deco, stray bytes, or pixels that don't decompress to
        /// exactly BannerRawBytes.
        /// </summary>
        internal static MemberExtras DecodeExtras(byte[] blob)
        {
            if (blob == null || blob.Length < ExtrasHeaderBytes || blob.Length > MaxExtrasBytes)
                return null;
            if (blob[0] != ExtrasFormat || blob[1] > (byte)BannerKind.Picture || blob[2] > (byte)CardEffect.Aurora || blob[3] > (byte)PictureDeco.Ripple)
                return null;
            MemberExtras extras = new MemberExtras();
            extras.Banner = (BannerKind)blob[1];
            extras.Effect = (CardEffect)blob[2];
            extras.Deco = (PictureDeco)blob[3];
            extras.ColourA = (blob[4] << 16) | (blob[5] << 8) | blob[6];
            extras.ColourB = (blob[7] << 16) | (blob[8] << 8) | blob[9];
            if (extras.Banner != BannerKind.Picture)
                return blob.Length == ExtrasHeaderBytes ? extras : null;
            if (blob.Length == ExtrasHeaderBytes)
                return null;

            byte[] pixels = new byte[BannerRawBytes];
            try
            {
                using (MemoryStream input = new MemoryStream(blob, ExtrasHeaderBytes, blob.Length - ExtrasHeaderBytes, false))
                using (DeflateStream inflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    int read = 0;
                    while (read < pixels.Length)
                    {
                        int n = inflate.Read(pixels, read, pixels.Length - read);
                        if (n <= 0)
                            return null; // Too short.
                        read += n;
                    }
                    if (inflate.Read(new byte[1], 0, 1) != 0)
                        return null; // Too long: never decompress beyond the expected size.
                }
            }
            catch
            {
                return null;
            }
            extras.Pixels = pixels;
            return extras;
        }

        // ----- Sending -----

        /// <summary>Client: (re)send our extras to the host until it confirms.</summary>
        private void SendLocalExtras()
        {
            byte[] blob;
            uint version;
            lock (_stateLock)
            {
                if (_localExtrasAcked || _localExtrasBlob == null || !_connected || _hostEndpoint == null)
                    return;
                blob = _localExtrasBlob;
                version = _localExtrasVersion;
                _localExtrasSentUtc = DateTime.UtcNow;
            }
            int count = ChunkCount(blob.Length);
            for (int i = 0; i < count; i++)
                SendPacket(_hostEndpoint, PacketExtrasPut, _localId, 0, BuildChunk(0, false, version, i, count, blob));
        }

        // ----- Host -----

        /// <summary>Host: stores checked extras. Call with _stateLock held.</summary>
        private bool StoreExtrasLocked(uint memberId, uint version, byte[] blob)
        {
            MemberExtras extras = DecodeExtras(blob);
            if (extras == null)
                return false;
            extras.Version = version;
            _extras[memberId] = extras;
            _extrasBlobs[memberId] = blob;
            _profilesVersion++;
            return true;
        }

        /// <summary>Host: extras packets from a known member. Returns true if handled.</summary>
        private bool HandleHostExtrasPacket(Participant known, Packet packet)
        {
            if (packet.Type == PacketExtrasPut)
            {
                byte[] p = packet.Payload;
                if (p == null || p.Length < 9)
                    return true;
                uint version = BitConverter.ToUInt32(p, 0);
                int index = p[4], count = p[5], total = BitConverter.ToUInt16(p, 6);
                bool stored = false;
                bool ack = false;
                lock (_stateLock)
                {
                    MemberExtras current;
                    if (_extras.TryGetValue(known.Id, out current) && current.Version == version)
                        ack = true; // Already have it: the ack was lost.
                    else
                    {
                        DateTime last;
                        if (_lastExtrasChangeUtc.TryGetValue(known.Id, out last) && (DateTime.UtcNow - last).TotalSeconds < ProfileMinIntervalSeconds)
                            return true; // Too soon; the client keeps retrying.
                        if (!AddChunk(_extrasUploads, known.Id, version, index, count, total, p, 8, MaxExtrasBytes))
                            return true;
                        ProfileAssembly assembly = _extrasUploads[known.Id];
                        if (assembly.Complete)
                        {
                            _extrasUploads.Remove(known.Id);
                            if (StoreExtrasLocked(known.Id, version, assembly.Join()))
                            {
                                _lastExtrasChangeUtc[known.Id] = DateTime.UtcNow;
                                stored = true;
                                ack = true;
                            }
                        }
                    }
                }
                if (ack)
                    SendPacket(known.Endpoint, PacketExtrasAck, _localId, 0, BitConverter.GetBytes(version));
                if (stored)
                    BroadcastPeerList();
                return true;
            }

            if (packet.Type == PacketExtrasGet)
            {
                byte[] p = packet.Payload;
                if (p == null || p.Length != 12)
                    return true;
                uint memberId = BitConverter.ToUInt32(p, 0);
                uint version = BitConverter.ToUInt32(p, 4);
                uint wanted = BitConverter.ToUInt32(p, 8);
                byte[] blob;
                lock (_stateLock)
                {
                    MemberExtras extras;
                    if (!_extras.TryGetValue(memberId, out extras) || extras.Version != version || !_extrasBlobs.TryGetValue(memberId, out blob))
                        return true;
                }
                int count = ChunkCount(blob.Length);
                for (int i = 0; i < count && i < 32; i++)
                {
                    if ((wanted & (1u << i)) != 0)
                        SendPacket(known.Endpoint, PacketExtrasData, _localId, 0, BuildChunk(memberId, true, version, i, count, blob));
                }
                return true;
            }
            return false;
        }

        // ----- Client -----

        private bool HandleClientExtrasPacket(Packet packet)
        {
            if (packet.Type == PacketExtrasAck)
            {
                if (packet.Payload != null && packet.Payload.Length == 4)
                {
                    lock (_stateLock)
                    {
                        if (BitConverter.ToUInt32(packet.Payload, 0) == _localExtrasVersion)
                            _localExtrasAcked = true;
                    }
                }
                return true;
            }

            if (packet.Type == PacketExtrasData)
            {
                byte[] p = packet.Payload;
                if (p == null || p.Length < 13)
                    return true;
                uint memberId = BitConverter.ToUInt32(p, 0);
                uint version = BitConverter.ToUInt32(p, 4);
                int index = p[8], count = p[9], total = BitConverter.ToUInt16(p, 10);
                lock (_stateLock)
                {
                    uint advertised;
                    if (!_advertisedExtras.TryGetValue(memberId, out advertised) || advertised != version)
                        return true; // Only what the host told us about.
                    if (!AddChunk(_extrasDownloads, memberId, version, index, count, total, p, 12, MaxExtrasBytes))
                        return true;
                    ProfileAssembly assembly = _extrasDownloads[memberId];
                    if (!assembly.Complete)
                        return true;
                    _extrasDownloads.Remove(memberId);
                    MemberExtras extras = DecodeExtras(assembly.Join());
                    if (extras == null)
                        return true;
                    extras.Version = version;
                    _extras[memberId] = extras;
                    _profilesVersion++;
                }
                return true;
            }
            return false;
        }

        /// <summary>Client maintenance: send our extras until acked, fetch extras that changed.</summary>
        private void ClientExtrasMaintenance(DateTime now)
        {
            bool resend;
            lock (_stateLock)
                resend = !_localExtrasAcked && _localExtrasBlob != null && (now - _localExtrasSentUtc).TotalMilliseconds >= 1500.0;
            if (resend)
                SendLocalExtras();

            List<byte[]> requests = new List<byte[]>();
            lock (_stateLock)
            {
                foreach (KeyValuePair<uint, uint> pair in _advertisedExtras)
                {
                    MemberExtras have;
                    if (_extras.TryGetValue(pair.Key, out have) && have.Version == pair.Value)
                        continue;
                    ProfileAssembly assembly;
                    if (!_extrasDownloads.TryGetValue(pair.Key, out assembly) || assembly.Version != pair.Value)
                    {
                        assembly = null;
                        _extrasDownloads.Remove(pair.Key);
                    }
                    else if ((now - assembly.RequestedUtc).TotalMilliseconds < 1200.0)
                        continue;
                    else if ((now - assembly.StartedUtc).TotalSeconds > 30.0)
                    {
                        _extrasDownloads.Remove(pair.Key); // Start over.
                        assembly = null;
                    }

                    uint wanted = 0xFFFFFFFF; // All chunks.
                    if (assembly != null && assembly.Chunks.Length > 0)
                    {
                        wanted = 0;
                        for (int i = 0; i < assembly.Chunks.Length; i++)
                            if (assembly.Chunks[i] == null) wanted |= 1u << i;
                        assembly.RequestedUtc = now;
                    }
                    else if (assembly != null)
                        assembly.RequestedUtc = now;
                    else
                    {
                        ProfileAssembly placeholder = new ProfileAssembly();
                        placeholder.Version = pair.Value;
                        placeholder.Total = -1;
                        placeholder.Chunks = new byte[0][];
                        placeholder.RequestedUtc = now;
                        _extrasDownloads[pair.Key] = placeholder;
                    }
                    byte[] payload = new byte[12];
                    Buffer.BlockCopy(BitConverter.GetBytes(pair.Key), 0, payload, 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(pair.Value), 0, payload, 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(wanted), 0, payload, 8, 4);
                    requests.Add(payload);
                    if (requests.Count >= 2)
                        break; // A few at a time: these can be bigger than profiles.
                }
            }
            foreach (byte[] request in requests)
                SendPacket(_hostEndpoint, PacketExtrasGet, _localId, 0, request);
        }

        /// <summary>Called after a welcome: our extras must be sent again (the host is new to us).</summary>
        private void ExtrasReconnected()
        {
            lock (_stateLock)
            {
                _localExtrasAcked = _localExtrasBlob == null;
                _localExtrasSentUtc = DateTime.MinValue;
                _advertisedExtras.Clear();
                _extrasDownloads.Clear();
                ApplyLocalExtrasLocked();
            }
        }

        // ----- Peer list section -----

        /// <summary>Host: writes (member, extras version) for everyone with extras. _stateLock held.</summary>
        private void WriteExtrasSection(BinaryWriter writer)
        {
            List<KeyValuePair<uint, uint>> list = new List<KeyValuePair<uint, uint>>();
            foreach (KeyValuePair<uint, MemberExtras> pair in _extras)
            {
                if (_memberNames.ContainsKey(pair.Key))
                    list.Add(new KeyValuePair<uint, uint>(pair.Key, pair.Value.Version));
            }
            if (list.Count == 0)
                return;
            writer.Write(PeerListSectionExtras);
            writer.Write((byte)Math.Min(255, list.Count));
            for (int i = 0; i < list.Count && i < 255; i++)
            {
                writer.Write(list[i].Key);
                writer.Write(list[i].Value);
            }
        }

        /// <summary>Client: apply the advertised versions and forget extras of people who left. _stateLock held.</summary>
        private void ApplyAdvertisedExtrasLocked(Dictionary<uint, uint> advertised)
        {
            _advertisedExtras.Clear();
            foreach (KeyValuePair<uint, uint> pair in advertised)
            {
                if (pair.Key != _localId)
                    _advertisedExtras[pair.Key] = pair.Value;
            }
            List<uint> gone = new List<uint>();
            foreach (uint id in _extras.Keys)
            {
                if (id != _localId && !advertised.ContainsKey(id))
                    gone.Add(id);
            }
            foreach (uint id in gone)
            {
                _extras.Remove(id);
                _extrasDownloads.Remove(id);
            }
            if (gone.Count > 0)
                _profilesVersion++;
        }

        /// <summary>Host: forget a member's extras when they leave. _stateLock held.</summary>
        private void ForgetExtrasLocked(uint memberId)
        {
            if (_extras.Remove(memberId))
                _profilesVersion++;
            _extrasBlobs.Remove(memberId);
            _extrasUploads.Remove(memberId);
            _lastExtrasChangeUtc.Remove(memberId);
        }

        /// <summary>Everything about extras, when the engine stops. _stateLock held.</summary>
        private void ClearExtrasLocked()
        {
            _extras.Clear();
            _extrasBlobs.Clear();
            _extrasUploads.Clear();
            _extrasDownloads.Clear();
            _advertisedExtras.Clear();
            _lastExtrasChangeUtc.Clear();
        }

        internal const byte ExtrasPutTypeForTests = PacketExtrasPut;
        internal const byte ExtrasGetTypeForTests = PacketExtrasGet;

        /// <summary>Our own extras (shown immediately, before the host confirms).</summary>
        private void ApplyLocalExtrasLocked()
        {
            if (_localExtrasBlob == null || _localId == 0)
                return;
            MemberExtras mine = DecodeExtras(_localExtrasBlob);
            if (mine == null)
                return;
            mine.Version = _localExtrasVersion;
            _extras[_localId] = mine;
            _profilesVersion++;
        }
    }
}
