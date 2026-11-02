using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Maela
{
    /// <summary>
    /// A member's profile: a short status line and a small profile picture.
    /// </summary>
    internal sealed class MemberProfile
    {
        public uint Version;
        public string Status = String.Empty;
        /// <summary>AvatarSize x AvatarSize pixels, RGB565 little-endian, or null for no picture.</summary>
        public byte[] Pixels;
    }

    /// <summary>
    /// Profiles (status line + picture), shared through the host.
    ///
    /// Safety is the point of the design:
    ///   - A picture is never sent as an image file. The sender's own PC turns their chosen
    ///     image into AvatarSize x AvatarSize raw pixels (RGB565) and compresses them. Everyone
    ///     else only decompresses (built-in Deflate, output capped to exactly the expected size)
    ///     and copies pixels; no image decoder ever runs on data from someone else. File names,
    ///     paths and picture metadata (location, camera) never leave the sender's PC.
    ///   - Hard caps: 16 KB per profile, 16 chunks, 60-character status, one change every 5 s
    ///     per member. The host checks every profile before storing or forwarding it.
    ///
    /// Protocol (all inside the encrypted session):
    ///   ProfilePut  client -> host : one chunk of the sender's profile (resent until acked)
    ///   ProfileAck  host -> client : profile version stored
    ///   ProfileGet  client -> host : please send member M's profile version V (chunks I need)
    ///   ProfileData host -> client : one chunk of member M's profile
    /// The peer list carries (member, version) so clients fetch only what changed. Older
    /// versions ignore all of this.
    /// </summary>
    internal sealed partial class VoipEngine
    {
        public const int AvatarSize = 80;
        public const int AvatarRawBytes = AvatarSize * AvatarSize * 2;
        public const int MaxStatusChars = 60;
        private const int MaxProfileBytes = 16 * 1024;
        private const int ProfileChunkBytes = 1000;
        private const int MaxProfileChunks = (MaxProfileBytes + ProfileChunkBytes - 1) / ProfileChunkBytes;
        private const double ProfileMinIntervalSeconds = 5.0;
        private const byte PeerListSectionProfiles = 2;

        private const byte PacketProfilePut = 17;
        private const byte PacketProfileAck = 18;
        private const byte PacketProfileGet = 19;
        private const byte PacketProfileData = 20;

        private readonly Dictionary<uint, MemberProfile> _profiles = new Dictionary<uint, MemberProfile>();
        private readonly Dictionary<uint, uint> _advertisedProfiles = new Dictionary<uint, uint>(); // Client: member -> version the host has.
        private readonly Dictionary<uint, ProfileAssembly> _profileDownloads = new Dictionary<uint, ProfileAssembly>();
        private readonly Dictionary<uint, ProfileAssembly> _profileUploads = new Dictionary<uint, ProfileAssembly>(); // Host: member -> incoming.
        private readonly Dictionary<uint, DateTime> _lastProfileChangeUtc = new Dictionary<uint, DateTime>();
        private byte[] _localProfileBlob;
        private uint _localProfileVersion;
        private bool _localProfileAcked = true;
        private DateTime _localProfileSentUtc = DateTime.MinValue;
        private int _profilesVersion;

        private sealed class ProfileAssembly
        {
            public uint Version;
            public int Total;
            public byte[][] Chunks;
            public DateTime RequestedUtc = DateTime.MinValue;
            public DateTime StartedUtc = DateTime.UtcNow;

            public bool Complete
            {
                get
                {
                    for (int i = 0; i < Chunks.Length; i++)
                        if (Chunks[i] == null) return false;
                    return true;
                }
            }

            public byte[] Join()
            {
                byte[] all = new byte[Total];
                int offset = 0;
                for (int i = 0; i < Chunks.Length; i++)
                {
                    Buffer.BlockCopy(Chunks[i], 0, all, offset, Chunks[i].Length);
                    offset += Chunks[i].Length;
                }
                return all;
            }
        }

        /// <summary>Increments whenever any profile changes. Cheap to poll from the UI.</summary>
        public int ProfilesVersion { get { lock (_stateLock) return _profilesVersion; } }

        /// <summary>A member's profile, or null if they haven't set one (or it hasn't arrived yet).</summary>
        public MemberProfile GetProfile(uint memberId)
        {
            lock (_stateLock)
            {
                MemberProfile p;
                return _profiles.TryGetValue(memberId, out p) ? p : null;
            }
        }

        /// <summary>
        /// Sets your own profile. <paramref name="pixels"/> must be exactly AvatarRawBytes of
        /// RGB565 (made on this PC by ProfilePicture.FromImage), or null for no picture.
        /// </summary>
        public void SetLocalProfile(string status, byte[] pixels)
        {
            byte[] blob = EncodeProfile(CleanStatus(status), pixels);
            if (blob == null)
                return;
            lock (_stateLock)
            {
                _localProfileBlob = blob;
                _localProfileVersion = NewProfileVersion(_localProfileVersion);
                _localProfileAcked = false;
                _localProfileSentUtc = DateTime.MinValue;
                if (_isHost && _localId != 0)
                    StoreProfileLocked(_localId, _localProfileVersion, blob);
                else
                    ApplyLocalProfileLocked(); // Show it straight away; the host confirms later.
            }
            if (_isHost)
                BroadcastPeerList();
            else
                SendLocalProfile();
        }

        private static uint NewProfileVersion(uint previous)
        {
            uint v = (uint)Environment.TickCount ^ (uint)Guid.NewGuid().GetHashCode();
            return v == 0 || v == previous ? previous + 1 : v;
        }

        public static string CleanStatus(string value)
        {
            if (String.IsNullOrEmpty(value))
                return String.Empty;
            StringBuilder b = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                // No control characters, line breaks or invisible formatting tricks.
                if (Char.IsControl(ch) || ch == '​' || ch == '‎' || ch == '‏' || (ch >= '‪' && ch <= '‮') || (ch >= '⁦' && ch <= '⁩'))
                    continue;
                b.Append(ch);
            }
            string clean = b.ToString().Trim();
            if (clean.Length > MaxStatusChars)
                clean = clean.Substring(0, MaxStatusChars).Trim();
            return clean;
        }

        // ----- Encoding -----

        /// <summary>Profile blob: [u8 status bytes][status UTF-8][u8 has picture][compressed pixels].</summary>
        private static byte[] EncodeProfile(string status, byte[] pixels)
        {
            if (pixels != null && pixels.Length != AvatarRawBytes)
                return null;
            byte[] statusBytes = Encoding.UTF8.GetBytes(status ?? String.Empty);
            if (statusBytes.Length > 240)
                return null;
            MemoryStream stream = new MemoryStream();
            stream.WriteByte((byte)statusBytes.Length);
            stream.Write(statusBytes, 0, statusBytes.Length);
            stream.WriteByte(pixels == null ? (byte)0 : (byte)1);
            if (pixels != null)
            {
                using (DeflateStream deflate = new DeflateStream(stream, CompressionMode.Compress, true))
                    deflate.Write(pixels, 0, pixels.Length);
            }
            byte[] blob = stream.ToArray();
            return blob.Length <= MaxProfileBytes ? blob : null;
        }

        /// <summary>
        /// Checks and unpacks a profile blob from anyone. Returns null if anything is off: wrong
        /// sizes, bad text, or pixels that don't decompress to exactly AvatarRawBytes.
        /// </summary>
        internal static MemberProfile DecodeProfile(byte[] blob)
        {
            if (blob == null || blob.Length < 2 || blob.Length > MaxProfileBytes)
                return null;
            int statusLength = blob[0];
            if (1 + statusLength + 1 > blob.Length)
                return null;
            string status;
            try { status = new UTF8Encoding(false, true).GetString(blob, 1, statusLength); }
            catch { return null; }
            if (status != CleanStatus(status))
                return null;
            int flag = blob[1 + statusLength];
            int dataStart = 2 + statusLength;
            MemberProfile profile = new MemberProfile();
            profile.Status = status;
            if (flag == 0)
                return dataStart == blob.Length ? profile : null;
            if (flag != 1 || dataStart >= blob.Length)
                return null;

            byte[] pixels = new byte[AvatarRawBytes];
            try
            {
                using (MemoryStream input = new MemoryStream(blob, dataStart, blob.Length - dataStart, false))
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
            profile.Pixels = pixels;
            return profile;
        }

        // ----- Sending chunks -----

        private static byte[] BuildChunk(uint memberId, bool withMember, uint version, int index, int count, byte[] blob)
        {
            int offset = index * ProfileChunkBytes;
            int length = Math.Min(ProfileChunkBytes, blob.Length - offset);
            byte[] payload = new byte[(withMember ? 4 : 0) + 4 + 1 + 1 + 2 + length];
            int p = 0;
            if (withMember)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(memberId), 0, payload, 0, 4);
                p = 4;
            }
            Buffer.BlockCopy(BitConverter.GetBytes(version), 0, payload, p, 4);
            payload[p + 4] = (byte)index;
            payload[p + 5] = (byte)count;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)blob.Length), 0, payload, p + 6, 2);
            Buffer.BlockCopy(blob, offset, payload, p + 8, length);
            return payload;
        }

        private static int ChunkCount(int length)
        {
            return Math.Max(1, (length + ProfileChunkBytes - 1) / ProfileChunkBytes);
        }

        /// <summary>
        /// Adds one chunk to an assembly. Checks every field against the assembly and the caps;
        /// returns false (and changes nothing) for anything inconsistent.
        /// </summary>
        private static bool AddChunk(Dictionary<uint, ProfileAssembly> table, uint key, uint version, int index, int count, int total, byte[] payload, int dataOffset)
        {
            return AddChunk(table, key, version, index, count, total, payload, dataOffset, MaxProfileBytes);
        }

        private static bool AddChunk(Dictionary<uint, ProfileAssembly> table, uint key, uint version, int index, int count, int total, byte[] payload, int dataOffset, int maxBytes)
        {
            int length = payload.Length - dataOffset;
            if (total < 2 || total > maxBytes || count != ChunkCount(total) || count > ChunkCount(maxBytes) || index < 0 || index >= count)
                return false;
            int expected = Math.Min(ProfileChunkBytes, total - index * ProfileChunkBytes);
            if (length != expected)
                return false;

            ProfileAssembly assembly;
            if (!table.TryGetValue(key, out assembly) || assembly.Version != version || assembly.Total != total)
            {
                assembly = new ProfileAssembly();
                assembly.Version = version;
                assembly.Total = total;
                assembly.Chunks = new byte[count][];
                table[key] = assembly;
            }
            if (assembly.Chunks[index] == null)
            {
                byte[] data = new byte[length];
                Buffer.BlockCopy(payload, dataOffset, data, 0, length);
                assembly.Chunks[index] = data;
            }
            return true;
        }

        /// <summary>Client: (re)send our profile to the host until it confirms.</summary>
        private void SendLocalProfile()
        {
            byte[] blob;
            uint version;
            lock (_stateLock)
            {
                if (_localProfileAcked || _localProfileBlob == null || !_connected || _hostEndpoint == null)
                    return;
                blob = _localProfileBlob;
                version = _localProfileVersion;
                _localProfileSentUtc = DateTime.UtcNow;
            }
            int count = ChunkCount(blob.Length);
            for (int i = 0; i < count; i++)
                SendPacket(_hostEndpoint, PacketProfilePut, _localId, 0, BuildChunk(0, false, version, i, count, blob));
        }

        // ----- Host -----

        /// <summary>Host: stores a checked profile. Call with _stateLock held.</summary>
        private bool StoreProfileLocked(uint memberId, uint version, byte[] blob)
        {
            MemberProfile profile = DecodeProfile(blob);
            if (profile == null)
                return false;
            profile.Version = version;
            _profiles[memberId] = profile;
            _profileBlobs[memberId] = blob;
            _profilesVersion++;
            return true;
        }

        private readonly Dictionary<uint, byte[]> _profileBlobs = new Dictionary<uint, byte[]>(); // Host: what we forward.

        /// <summary>Host: profile packets from a known member. Returns true if handled.</summary>
        private bool HandleHostProfilePacket(Participant known, Packet packet)
        {
            if (packet.Type == PacketProfilePut)
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
                    MemberProfile current;
                    if (_profiles.TryGetValue(known.Id, out current) && current.Version == version)
                        ack = true; // Already have it: the ack was lost.
                    else
                    {
                        DateTime last;
                        if (_lastProfileChangeUtc.TryGetValue(known.Id, out last) && (DateTime.UtcNow - last).TotalSeconds < ProfileMinIntervalSeconds)
                            return true; // Too soon; the client keeps retrying.
                        if (!AddChunk(_profileUploads, known.Id, version, index, count, total, p, 8))
                            return true;
                        ProfileAssembly assembly = _profileUploads[known.Id];
                        if (assembly.Complete)
                        {
                            _profileUploads.Remove(known.Id);
                            if (StoreProfileLocked(known.Id, version, assembly.Join()))
                            {
                                _lastProfileChangeUtc[known.Id] = DateTime.UtcNow;
                                stored = true;
                                ack = true;
                            }
                        }
                    }
                }
                if (ack)
                    SendPacket(known.Endpoint, PacketProfileAck, _localId, 0, BitConverter.GetBytes(version));
                if (stored)
                    BroadcastPeerList();
                return true;
            }

            if (packet.Type == PacketProfileGet)
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
                    MemberProfile profile;
                    if (!_profiles.TryGetValue(memberId, out profile) || profile.Version != version || !_profileBlobs.TryGetValue(memberId, out blob))
                        return true;
                }
                int count = ChunkCount(blob.Length);
                for (int i = 0; i < count; i++)
                {
                    if ((wanted & (1u << i)) != 0)
                        SendPacket(known.Endpoint, PacketProfileData, _localId, 0, BuildChunk(memberId, true, version, i, count, blob));
                }
                return true;
            }
            return false;
        }

        // ----- Client -----

        private bool HandleClientProfilePacket(Packet packet)
        {
            if (packet.Type == PacketProfileAck)
            {
                if (packet.Payload != null && packet.Payload.Length == 4)
                {
                    lock (_stateLock)
                    {
                        if (BitConverter.ToUInt32(packet.Payload, 0) == _localProfileVersion)
                            _localProfileAcked = true;
                    }
                }
                return true;
            }

            if (packet.Type == PacketProfileData)
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
                    if (!_advertisedProfiles.TryGetValue(memberId, out advertised) || advertised != version)
                        return true; // Only what the host told us about.
                    if (!AddChunk(_profileDownloads, memberId, version, index, count, total, p, 12))
                        return true;
                    ProfileAssembly assembly = _profileDownloads[memberId];
                    if (!assembly.Complete)
                        return true;
                    _profileDownloads.Remove(memberId);
                    MemberProfile profile = DecodeProfile(assembly.Join());
                    if (profile == null)
                        return true;
                    profile.Version = version;
                    _profiles[memberId] = profile;
                    _profilesVersion++;
                }
                return true;
            }
            return false;
        }

        /// <summary>Client maintenance: send our profile until acked, fetch profiles that changed.</summary>
        private void ClientProfileMaintenance(DateTime now)
        {
            ClientRoomArtMaintenance(now);
            bool resend;
            lock (_stateLock)
                resend = !_localProfileAcked && _localProfileBlob != null && (now - _localProfileSentUtc).TotalMilliseconds >= 1500.0;
            if (resend)
                SendLocalProfile();

            List<byte[]> requests = new List<byte[]>();
            lock (_stateLock)
            {
                foreach (KeyValuePair<uint, uint> pair in _advertisedProfiles)
                {
                    MemberProfile have;
                    if (_profiles.TryGetValue(pair.Key, out have) && have.Version == pair.Value)
                        continue;
                    ProfileAssembly assembly;
                    if (!_profileDownloads.TryGetValue(pair.Key, out assembly) || assembly.Version != pair.Value)
                    {
                        assembly = null;
                        _profileDownloads.Remove(pair.Key);
                    }
                    else if ((now - assembly.RequestedUtc).TotalMilliseconds < 1200.0)
                        continue;
                    else if ((now - assembly.StartedUtc).TotalSeconds > 30.0)
                    {
                        _profileDownloads.Remove(pair.Key); // Start over.
                        assembly = null;
                    }

                    uint wanted = 0xFFFF; // All chunks.
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
                        _profileDownloads[pair.Key] = placeholder;
                    }
                    byte[] payload = new byte[12];
                    Buffer.BlockCopy(BitConverter.GetBytes(pair.Key), 0, payload, 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(pair.Value), 0, payload, 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(wanted), 0, payload, 8, 4);
                    requests.Add(payload);
                    if (requests.Count >= 4)
                        break; // A few at a time.
                }
            }
            foreach (byte[] request in requests)
                SendPacket(_hostEndpoint, PacketProfileGet, _localId, 0, request);
        }

        /// <summary>Called after a welcome: our profile must be sent again (the host is new to us).</summary>
        private void ProfileReconnected()
        {
            lock (_stateLock)
            {
                _localProfileAcked = _localProfileBlob == null;
                _localProfileSentUtc = DateTime.MinValue;
                _advertisedProfiles.Clear();
                _profileDownloads.Clear();
            }
        }

        // ----- Peer list section -----

        /// <summary>Host: writes (member, profile version) for everyone with a profile. _stateLock held.</summary>
        private void WriteProfileSection(BinaryWriter writer)
        {
            List<KeyValuePair<uint, uint>> list = new List<KeyValuePair<uint, uint>>();
            foreach (KeyValuePair<uint, MemberProfile> pair in _profiles)
            {
                if (_memberNames.ContainsKey(pair.Key))
                    list.Add(new KeyValuePair<uint, uint>(pair.Key, pair.Value.Version));
            }
            if (list.Count == 0)
                return;
            writer.Write(PeerListSectionProfiles);
            writer.Write((byte)Math.Min(255, list.Count));
            for (int i = 0; i < list.Count && i < 255; i++)
            {
                writer.Write(list[i].Key);
                writer.Write(list[i].Value);
            }
        }

        /// <summary>Client: reads the profile section. Returns the new offset, or -1 if malformed.</summary>
        private static int ReadProfileSection(byte[] payload, int offset, Dictionary<uint, uint> into)
        {
            if (offset >= payload.Length)
                return -1;
            int count = payload[offset++];
            if (offset + count * 8 > payload.Length)
                return -1;
            for (int i = 0; i < count; i++)
            {
                into[BitConverter.ToUInt32(payload, offset)] = BitConverter.ToUInt32(payload, offset + 4);
                offset += 8;
            }
            return offset;
        }

        /// <summary>Client: apply the advertised versions and forget profiles of people who left. _stateLock held.</summary>
        private void ApplyAdvertisedProfilesLocked(Dictionary<uint, uint> advertised)
        {
            _advertisedProfiles.Clear();
            foreach (KeyValuePair<uint, uint> pair in advertised)
            {
                if (pair.Key != _localId)
                    _advertisedProfiles[pair.Key] = pair.Value;
            }
            List<uint> gone = new List<uint>();
            foreach (uint id in _profiles.Keys)
            {
                if (id != _localId && !advertised.ContainsKey(id))
                    gone.Add(id);
            }
            foreach (uint id in gone)
            {
                _profiles.Remove(id);
                _profileDownloads.Remove(id);
            }
            if (gone.Count > 0)
                _profilesVersion++;
        }

        /// <summary>Host: forget a member's profile when they leave. _stateLock held.</summary>
        private void ForgetProfileLocked(uint memberId)
        {
            if (_profiles.Remove(memberId))
                _profilesVersion++;
            _profileBlobs.Remove(memberId);
            _profileUploads.Remove(memberId);
            _lastProfileChangeUtc.Remove(memberId);
        }

        /// <summary>Tests only: send any inner packet to the host, as a hostile client could.</summary>
        internal void SendToHostForTests(byte type, byte[] payload)
        {
            if (_hostEndpoint != null)
                SendPacket(_hostEndpoint, type, _localId, 0, payload);
        }

        internal const byte ProfilePutTypeForTests = PacketProfilePut;
        internal const byte ProfileGetTypeForTests = PacketProfileGet;

        /// <summary>Our own profile (shown immediately, before the host confirms).</summary>
        private void ApplyLocalProfileLocked()
        {
            if (_localProfileBlob == null || _localId == 0)
                return;
            MemberProfile mine = DecodeProfile(_localProfileBlob);
            if (mine == null)
                return;
            mine.Version = _localProfileVersion;
            _profiles[_localId] = mine;
            _profilesVersion++;
        }
    }
}
