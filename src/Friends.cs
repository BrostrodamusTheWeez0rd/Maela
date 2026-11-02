using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Maela
{
    internal enum FriendStatus : byte
    {
        Offline = 0,
        Online = 1,
        Away = 2,
        Busy = 3
    }

    internal enum FriendState : byte
    {
        Friend = 0,
        RequestSent = 1,
        RequestReceived = 2,
        Blocked = 3
    }

    internal enum AddFriendResult
    {
        Sent,
        AddressUpdated,
        InvalidCode,
        ThatsYou,
        Blocked,
        NotRunning
    }

    internal enum RestoreResult
    {
        Restored,
        WrongPassword,
        NotABackup,
        StillOnline,
        CouldNotSave
    }

    /// <summary>
    /// Friend codes: everything needed to reach and verify one person, in one pasteable string.
    ///
    /// Layout (25 bytes, Base32, shown as F-XXXXX-XXXXX-...):
    ///   version (1) | IPv4 (4) | UDP port (2) | key fingerprint (16) | CRC-16 (2)
    /// The key is the account, so the fingerprint is the long-lived part; the address only says
    /// where that person was when the code was made. The bytes are scrambled so codes don't
    /// visibly contain an IP address, but anyone with a code can recover it.
    /// </summary>
    internal static class FriendCode
    {
        public const int FingerprintLength = 16;
        private const byte CodeVersion = 1;
        private const int DataLength = 25;
        private const int CharLength = 40;
        private static readonly byte[] Mask = new byte[]
        {
            0x5C, 0xE2, 0x19, 0xB7, 0x83, 0x2E, 0xD4, 0x61, 0xF8, 0x0B, 0x96, 0x4A, 0xC3,
            0x37, 0x7D, 0xA1, 0x1F, 0xEC, 0x58, 0x06, 0xBB, 0x92, 0x64, 0xD9, 0x2B
        };

        public static byte[] Fingerprint(byte[] publicKey)
        {
            byte[] hash = CryptoUtil.Sha256(Encoding.ASCII.GetBytes("maela friend key"), publicKey);
            byte[] fingerprint = new byte[FingerprintLength];
            Buffer.BlockCopy(hash, 0, fingerprint, 0, FingerprintLength);
            return fingerprint;
        }

        public static string Encode(IPAddress ip, int port, byte[] fingerprint)
        {
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Friend codes currently require IPv4.");
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException("port");
            if (fingerprint == null || fingerprint.Length != FingerprintLength)
                throw new ArgumentException("A key fingerprint is required.");

            byte[] data = new byte[DataLength];
            data[0] = CodeVersion;
            Buffer.BlockCopy(ip.GetAddressBytes(), 0, data, 1, 4);
            data[5] = (byte)((port >> 8) & 0xFF);
            data[6] = (byte)(port & 0xFF);
            Buffer.BlockCopy(fingerprint, 0, data, 7, FingerprintLength);
            ushort crc = RoomCode.Crc16(data, DataLength - 2);
            data[23] = (byte)(crc >> 8);
            data[24] = (byte)(crc & 0xFF);

            int i;
            for (i = 0; i < data.Length; i++)
                data[i] = (byte)(data[i] ^ Mask[i]);

            string raw = RoomCode.Base32Encode(data);
            StringBuilder code = new StringBuilder("F");
            for (i = 0; i < raw.Length; i += 5)
                code.Append('-').Append(raw, i, 5);
            return code.ToString();
        }

        public static bool TryDecode(string code, out IPAddress ip, out int port, out byte[] fingerprint)
        {
            ip = null;
            port = 0;
            fingerprint = null;
            string clean = RoomCode.Clean(code);
            if (clean.Length != CharLength + 1 || clean[0] != 'F')
                return false;

            byte[] data;
            if (!RoomCode.TryBase32Decode(clean.Substring(1), out data) || data.Length != DataLength)
                return false;

            int i;
            for (i = 0; i < data.Length; i++)
                data[i] = (byte)(data[i] ^ Mask[i]);

            ushort crc = RoomCode.Crc16(data, DataLength - 2);
            if (data[0] != CodeVersion || data[23] != (byte)(crc >> 8) || data[24] != (byte)(crc & 0xFF))
                return false;

            int decodedPort = (data[5] << 8) | data[6];
            if (decodedPort < 1)
                return false;

            ip = new IPAddress(new byte[] { data[1], data[2], data[3], data[4] });
            port = decodedPort;
            fingerprint = new byte[FingerprintLength];
            Buffer.BlockCopy(data, 7, fingerprint, 0, FingerprintLength);
            return true;
        }

        public static bool IsValid(string code)
        {
            IPAddress ip;
            int port;
            byte[] fingerprint;
            return TryDecode(code, out ip, out port, out fingerprint);
        }
    }

    /// <summary>
    /// The password-protected backup file. PBKDF2-HMAC-SHA256 turns the password into a key, which
    /// encrypts with the same AES-256-CTR and HMAC-SHA256 as everything else.
    ///
    /// Layout: magic "MAELABK1" (8) | iterations (4) | salt (16) | counter (8) | ciphertext | tag (16)
    /// </summary>
    internal static class FriendBackup
    {
        public const int MaxFileBytes = 1024 * 1024;
        public const int MinPasswordLength = 10;
        private const int Iterations = 600000;
        private const int MinIterations = 100000;
        private const int MaxIterations = 5000000; // A crafted file can't freeze mæla for hours.
        private const int HeaderBytes = 28;
        private static readonly byte[] FileMagic = Encoding.ASCII.GetBytes("MAELABK1");

        public static byte[] Seal(byte[] plaintext, string password)
        {
            byte[] header = new byte[HeaderBytes];
            Buffer.BlockCopy(FileMagic, 0, header, 0, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(Iterations), 0, header, 8, 4);
            Buffer.BlockCopy(CryptoUtil.Random(16), 0, header, 12, 16);
            using (PacketCipher cipher = new PacketCipher(DeriveKey(password, header)))
                return cipher.Seal(header, plaintext);
        }

        public static bool LooksLikeBackup(byte[] data)
        {
            if (data == null || data.Length < HeaderBytes + 24 || data.Length > MaxFileBytes)
                return false;
            int i;
            for (i = 0; i < 8; i++)
            {
                if (data[i] != FileMagic[i])
                    return false;
            }
            int iterations = BitConverter.ToInt32(data, 8);
            return iterations >= MinIterations && iterations <= MaxIterations;
        }

        /// <summary>Returns null if the password is wrong or the file has been changed.</summary>
        public static byte[] Open(byte[] data, string password)
        {
            if (!LooksLikeBackup(data))
                return null;
            byte[] header = new byte[HeaderBytes];
            Buffer.BlockCopy(data, 0, header, 0, HeaderBytes);
            using (PacketCipher cipher = new PacketCipher(DeriveKey(password, header)))
                return cipher.Open(data, data.Length, HeaderBytes);
        }

        private static byte[] DeriveKey(string password, byte[] header)
        {
            byte[] salt = new byte[16];
            Buffer.BlockCopy(header, 12, salt, 0, 16);
            byte[] stretched = Pbkdf2(Encoding.UTF8.GetBytes(password ?? String.Empty), salt, BitConverter.ToInt32(header, 8));
            return Hkdf.DeriveKey(salt, stretched, FileMagic, 64);
        }

        /// <summary>PBKDF2-HMAC-SHA256 (RFC 8018), one 32-byte block. Checked against a published vector.</summary>
        internal static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations)
        {
            using (HMACSHA256 mac = new HMACSHA256(password))
            {
                byte[] first = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, first, 0, salt.Length);
                first[first.Length - 1] = 1;
                byte[] block = mac.ComputeHash(first);
                byte[] result = (byte[])block.Clone();
                int i;
                for (i = 1; i < iterations; i++)
                {
                    block = mac.ComputeHash(block);
                    int k;
                    for (k = 0; k < result.Length; k++)
                        result[k] ^= block[k];
                }
                return result;
            }
        }
    }

    internal enum FriendCallEvent
    {
        Incoming,    // A friend is calling you.
        HostNow,     // It's your turn to start the private room.
        JoinRoom,    // Their private room is ready, and you asked for this call.
        Declined,
        Ended        // Cancelled, or nobody answered in time.
    }

    /// <summary>What the UI sees of one person. A copy: changing it changes nothing.</summary>
    internal sealed class FriendInfo
    {
        public string Id;
        public string Name;
        public string ShortId;
        public FriendState State;
        public FriendStatus Status;
        public int Unread;
        public DateTime LastSeenUtc;
    }

    internal sealed class FriendChatEntry
    {
        public bool Mine;
        public DateTime TimeUtc;
        public string Text;
        public ulong Sequence;
        public bool Delivered;
        public ulong MessageId;  // Stays the same if numbering restarts. 0: can't be reacted to.
        public ulong ReactionTo; // Not 0: this is a reaction to that message, and Text is the emoji.
    }

    internal sealed class FriendReaction
    {
        public string Emoji;
        public int Count;
        public bool Mine;
    }

    /// <summary>
    /// One friend's chat. Nothing here refers to any other friend, and each has its own encrypted
    /// file, so there is no path from one person's chat to another's.
    /// </summary>
    internal sealed class FriendChat
    {
        private const int MaxEntries = 2000;
        private readonly string _path;
        public readonly List<FriendChatEntry> Entries = new List<FriendChatEntry>();

        public FriendChat(string path)
        {
            _path = path;
            Load();
        }

        /// <summary>Each person's reaction turns on and off in order; what's left on is what shows.</summary>
        public static Dictionary<ulong, List<FriendReaction>> Summarize(List<FriendChatEntry> entries)
        {
            Dictionary<ulong, List<FriendReaction>> result = new Dictionary<ulong, List<FriendReaction>>();
            Dictionary<string, bool> on = new Dictionary<string, bool>(StringComparer.Ordinal);
            List<FriendChatEntry> order = new List<FriendChatEntry>();
            foreach (FriendChatEntry entry in entries)
            {
                if (entry.ReactionTo == 0)
                    continue;
                string key = entry.ReactionTo + "|" + entry.Text + "|" + entry.Mine;
                bool current;
                on.TryGetValue(key, out current);
                on[key] = !current;
                order.Add(entry);
            }
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (FriendChatEntry entry in order)
            {
                string key = entry.ReactionTo + "|" + entry.Text + "|" + entry.Mine;
                if (!seen.Add(key) || !on[key])
                    continue;
                List<FriendReaction> list;
                if (!result.TryGetValue(entry.ReactionTo, out list))
                {
                    list = new List<FriendReaction>();
                    result[entry.ReactionTo] = list;
                }
                FriendReaction reaction = list.Find(delegate(FriendReaction r) { return r.Emoji == entry.Text; });
                if (reaction == null)
                {
                    reaction = new FriendReaction();
                    reaction.Emoji = entry.Text;
                    list.Add(reaction);
                }
                reaction.Count++;
                reaction.Mine |= entry.Mine;
            }
            return result;
        }

        public bool HasMessage(ulong messageId)
        {
            return messageId != 0 && Entries.Exists(delegate(FriendChatEntry e) { return e.MessageId == messageId && e.ReactionTo == 0; });
        }

        public void Add(FriendChatEntry entry)
        {
            Entries.Add(entry);
            if (Entries.Count > MaxEntries)
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            Save();
        }

        public void MarkDelivered(ulong upTo)
        {
            bool changed = false;
            int i;
            for (i = Entries.Count - 1; i >= 0; i--)
            {
                FriendChatEntry entry = Entries[i];
                if (entry.Mine && !entry.Delivered && entry.Sequence <= upTo)
                {
                    entry.Delivered = true;
                    changed = true;
                }
            }
            if (changed)
                Save();
        }

        /// <summary>A friend restored a backup, so your unsent messages start numbering again.</summary>
        public void Renumber(ulong oldSequence, ulong newSequence)
        {
            foreach (FriendChatEntry entry in Entries)
            {
                if (entry.Mine && !entry.Delivered && entry.Sequence == oldSequence)
                {
                    entry.Sequence = newSequence;
                    break;
                }
            }
        }

        public void SaveNow()
        {
            Save();
        }

        /// <summary>After restoring a backup, messages still waiting here can no longer be sent.</summary>
        public void DropUnsent()
        {
            if (Entries.RemoveAll(delegate(FriendChatEntry e) { return e.Mine && !e.Delivered; }) > 0)
                Save();
        }

        public void Delete()
        {
            Entries.Clear();
            try { File.Delete(_path); }
            catch { }
        }

        private void Load()
        {
            if (!File.Exists(_path))
                return;
            byte[] data;
            try { data = EncryptedFile.Read(_path); }
            catch { return; }
            if (data == null)
            {
                EncryptedFile.SetAside(_path);
                return;
            }
            string[] lines = Encoding.UTF8.GetString(data).Split('\n');
            int i;
            for (i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('|');
                if (parts.Length != 5 && parts.Length != 7)
                    continue;
                long ticks;
                ulong sequence;
                if (!Int64.TryParse(parts[1], out ticks) || !UInt64.TryParse(parts[2], out sequence) || ticks < 0 || ticks > DateTime.MaxValue.Ticks)
                    continue;
                FriendChatEntry entry = new FriendChatEntry();
                entry.Mine = parts[0] == "1";
                entry.TimeUtc = new DateTime(ticks, DateTimeKind.Utc);
                entry.Sequence = sequence;
                entry.Delivered = parts[3] == "1";
                try { entry.Text = Encoding.UTF8.GetString(Convert.FromBase64String(parts[4])); }
                catch { continue; }
                if (parts.Length == 7)
                {
                    UInt64.TryParse(parts[5], out entry.MessageId);
                    UInt64.TryParse(parts[6], out entry.ReactionTo);
                }
                Entries.Add(entry);
            }
        }

        private void Save()
        {
            try
            {
                StringBuilder text = new StringBuilder();
                foreach (FriendChatEntry entry in Entries)
                {
                    text.Append(entry.Mine ? "1" : "0").Append('|').Append(entry.TimeUtc.Ticks).Append('|').Append(entry.Sequence).Append('|')
                        .Append(entry.Delivered ? "1" : "0").Append('|').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Text))).Append('|')
                        .Append(entry.MessageId).Append('|').Append(entry.ReactionTo).Append('\n');
                }
                EncryptedFile.Write(_path, Encoding.UTF8.GetBytes(text.ToString()));
            }
            catch { }
        }
    }

    /// <summary>
    /// The small background listener behind a friend code. While you appear online it accepts
    /// friend requests and text from accepted friends, and nothing else: it has no members,
    /// channels or relay, and never passes anything from one person to another.
    ///
    /// Handshake (the Noise "XX" pattern: both sides prove their long-term key):
    ///   1. initiator -> responder  HELLO   session id, ephemeral key e, gate, padding
    ///   2. responder -> initiator  REPLY   session ids, ephemeral key e, encrypted static key s
    ///   3. initiator -> responder  FINISH  session id, encrypted static key s
    /// The gate is an HMAC of e keyed by the responder's fingerprint. Without it the listener
    /// doesn't answer at all, so a scan can't discover it. HELLO is padded so replies can't be
    /// used to amplify traffic. The initiator checks the responder's key against the fingerprint
    /// it was given; the responder looks the initiator's key up in its own list.
    ///
    /// That list decides everything. Only AcceptRequest, called from your own click, can add
    /// someone, and no packet can. A stranger with a valid code gets a session on which only
    /// FRIEND_REQUEST is accepted and which never carries your status.
    ///
    /// Afterwards packets are DATA: header || session id || counter || AES-256-CTR ciphertext ||
    /// HMAC tag (see PacketCipher). Messages are numbered per direction, resent until
    /// acknowledged and kept encrypted on the sender's PC until then.
    /// </summary>
    internal sealed class FriendService : IDisposable
    {
        private const uint Magic = 0x3152464D; // "MFR1"
        private const byte OuterHello = 1;
        private const byte OuterReply = 2;
        private const byte OuterFinish = 3;
        private const byte OuterData = 4;
        private const int HeaderBytes = 5;
        private const int HelloBytes = 160;
        private const int ReplyBytes = HeaderBytes + 8 + 32 + 56;
        private const int FinishBytes = HeaderBytes + 4 + 56;
        private const int DataHeaderBytes = HeaderBytes + 4;
        private const int MaxDatagram = 1500;
        private const int MaxPendingHandshakes = 64;
        private const int MaxSessions = 512;
        private const int MaxReceivedRequests = 32;
        private const int MaxOutbox = 500;
        private const int MaxSendWindow = 8;
        public const int MaxMessageChars = 400;
        private const int MaxMessageBytes = 1100;
        private const int MaxNameChars = 24;
        private const int MessageHeaderBytes = 37;

        private const byte InnerReady = 0;
        private const byte InnerPing = 1;
        private const byte InnerRequest = 2;
        private const byte InnerAccept = 3;
        private const byte InnerMessage = 4;
        private const byte InnerAck = 5;
        private const byte InnerBye = 6;
        private const byte InnerCall = 7;

        private const byte CallInvite = 1;
        private const byte CallAccept = 2;
        private const byte CallRoomReady = 3;
        private const byte CallJoining = 4;
        private const byte CallDecline = 5;
        private const byte CallCancel = 6;

        private static readonly byte[] ProtocolLabel = Encoding.ASCII.GetBytes("maela/friend/1 XX X25519 HKDF-SHA256 AES256-CTR HMAC-SHA256");

        // How often things happen. Tests shorten these.
        internal int ConnectIntervalMs = 20000;
        internal int KeepAliveMs = 15000;
        internal int SessionTimeoutMs = 45000;
        internal int ResendMs = 1000;
        internal int CallRingSeconds = 30;

        private readonly object _lock = new object();
        private readonly string _folder;
        private readonly Dictionary<string, Person> _people = new Dictionary<string, Person>(StringComparer.Ordinal);
        private readonly Dictionary<uint, Session> _sessions = new Dictionary<uint, Session>();
        private readonly Dictionary<uint, Handshake> _handshakes = new Dictionary<uint, Handshake>();
        private readonly Dictionary<string, HelloLimit> _helloLimits = new Dictionary<string, HelloLimit>(StringComparer.Ordinal);
        private byte[] _privateKey;
        private byte[] _publicKey;
        private byte[] _fingerprint;
        private int _preferredPort;
        private bool _saveBlocked;
        private Socket _socket;
        private Thread _thread;
        private volatile bool _running;
        private FriendStatus _status = FriendStatus.Offline;
        private string _name = String.Empty;
        private string _code = String.Empty;
        private string _lastCode = String.Empty; // Survives restarts, to notice your address changing while you were away.
        private int _version;
        private uint _incarnation; // Changes on every restore, so friends know to start numbering again.
        private bool _backedUp;
        private ActiveCall _call;
        private ulong _lastEndedCall;

        /// <summary>
        /// Raised on the service's thread: event, friend id, then a room code and password for
        /// JoinRoom. Nothing here joins anything; the app acts only on a call you started or accepted.
        /// </summary>
        public event Action<FriendCallEvent, string, string, string> CallEvent;

        /// <summary>Raised on the service's thread when someone sends you a message.</summary>
        public event Action<string, string> MessageReceived;

        /// <summary>Raised on the service's thread when someone sends you a friend request.</summary>
        public event Action<string> RequestReceived;

        /// <summary>Raised on the service's thread when someone accepts your friend request.</summary>
        public event Action<string> RequestAccepted;

        public FriendService(string folder)
        {
            _folder = folder;
            Load();
        }

        /// <summary>True if your identity file couldn't be decrypted, so a new one was made. The old file is kept.</summary>
        public bool IdentityWasLost { get; private set; }

        /// <summary>True if the identity file couldn't be read at all. Going online is refused so it can't be replaced.</summary>
        public bool StorageFailed { get { return _saveBlocked; } }

        public int Port { get; private set; }

        /// <summary>False until you've made a backup. Nothing else can recover your account.</summary>
        public bool BackedUp { get { lock (_lock) return _backedUp; } }
        public FriendStatus Status { get { lock (_lock) return _status; } }
        public string Id { get { return Hex(_fingerprint); } }
        public byte[] PublicKey { get { return (byte[])_publicKey.Clone(); } }

        /// <summary>Goes up whenever anything visible changes, so the UI knows when to redraw.</summary>
        public int Version { get { return Thread.VolatileRead(ref _version); } }

        /// <summary>Your current friend code, or empty if your address isn't known yet.</summary>
        public string Code { get { lock (_lock) return _code; } }

        // ----- Going online and offline -----

        /// <summary>Any status but Offline runs the listener. Offline closes it completely.</summary>
        public bool SetStatus(FriendStatus status, string name)
        {
            lock (_lock)
                _name = CleanName(name);
            if (status == FriendStatus.Offline)
            {
                Stop();
                return true;
            }
            if (_saveBlocked)
                return false;
            if (!_running && !Start())
                return false;
            lock (_lock)
            {
                _status = status;
                BroadcastPing();
                Bump();
            }
            return true;
        }

        private bool Start()
        {
            lock (_lock)
            {
                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    try { socket.Bind(new IPEndPoint(IPAddress.Any, _preferredPort)); }
                    catch (SocketException)
                    {
                        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    }
                    // Sending to a closed port must not break the socket for everyone else.
                    try { socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null); }
                    catch { }
                }
                catch
                {
                    socket.Close();
                    return false;
                }
                _socket = socket;
                Port = ((IPEndPoint)socket.LocalEndPoint).Port;
                if (Port != _preferredPort)
                {
                    _preferredPort = Port;
                    Save();
                }
                _running = true;
                _thread = new Thread(Run);
                _thread.IsBackground = true;
                _thread.Name = "Friends";
                _thread.Start();
                return true;
            }
        }

        private void Stop()
        {
            Thread thread;
            lock (_lock)
            {
                if (_running)
                {
                    foreach (Person person in _people.Values)
                    {
                        Session session = person.Current;
                        if (session != null && session.Confirmed)
                            SendInner(session, new byte[] { InnerBye });
                    }
                }
                _running = false;
                _status = FriendStatus.Offline;
                _code = String.Empty;
                thread = _thread;
                _thread = null;
                foreach (Session session in _sessions.Values)
                    session.Dispose();
                _sessions.Clear();
                _handshakes.Clear();
                _helloLimits.Clear();
                _call = null;
                foreach (Person person in _people.Values)
                {
                    person.Current = null;
                    person.PeerStatus = FriendStatus.Offline;
                    person.Observed = null;
                }
                if (_socket != null)
                {
                    try { _socket.Close(); }
                    catch { }
                    _socket = null;
                }
                Save();
                Bump();
            }
            if (thread != null && thread != Thread.CurrentThread)
                thread.Join(1000);
        }

        /// <summary>Sets where you can be reached. Returns true if your code changed.</summary>
        public bool SetAddress(IPAddress ip, int port)
        {
            lock (_lock)
            {
                if (!_running || ip == null)
                    return false;
                string code = FriendCode.Encode(ip, port, _fingerprint);
                if (code == _code)
                    return false;
                bool changed = _lastCode.Length > 0 && code != _lastCode;
                _code = code;
                _lastCode = code;
                Save();
                BroadcastPing(); // Friends who can hear you learn the new address straight away.
                Bump();
                return changed;
            }
        }

        /// <summary>Lets tests behave like a modified copy, which can send anything down its own session.</summary>
        internal void SendInnerForTests(string id, byte[] inner)
        {
            lock (_lock)
            {
                Person person;
                if (_people.TryGetValue(id, out person))
                    SendInner(person.Current, inner);
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>Your display name changed: friends who can hear you learn it straight away.</summary>
        public void SetName(string name)
        {
            lock (_lock)
            {
                string clean = CleanName(name);
                if (clean == _name)
                    return;
                _name = clean;
                BroadcastPing();
            }
        }

        // ----- Calls -----

        /// <summary>Starts ringing a friend. canHost: your router accepts incoming connections.</summary>
        public bool StartCall(string id, bool canHost)
        {
            lock (_lock)
            {
                Person person;
                if (_call != null || !_people.TryGetValue(id, out person) || person.State != FriendState.Friend || person.PeerStatus == FriendStatus.Offline)
                    return false;
                _call = new ActiveCall();
                _call.Id = BitConverter.ToUInt64(CryptoUtil.Random(8), 0);
                _call.FriendId = id;
                _call.ExpiresUtc = DateTime.UtcNow.AddSeconds(CallRingSeconds);
                _call.CanHost = canHost;
                _call.Resend = CallPacket(CallInvite, _call.Id, new byte[] { (byte)(canHost ? 1 : 0) });
                SendCall(person);
                return true;
            }
        }

        /// <summary>Only ever called from your own click on Accept.</summary>
        public void AcceptCall(bool canHost)
        {
            FriendCallEvent next = FriendCallEvent.HostNow;
            string friend = null;
            lock (_lock)
            {
                if (_call == null || !_call.Incoming || _call.Answered)
                    return;
                _call.Answered = true;
                _call.ExpiresUtc = DateTime.UtcNow.AddSeconds(CallRingSeconds);
                friend = _call.FriendId;
                Person person;
                if (!_people.TryGetValue(friend, out person))
                    return;
                bool callerHosts = !canHost && _call.CallerCanHost;
                _call.WaitingForRoom = callerHosts;
                _call.Resend = CallPacket(CallAccept, _call.Id, new byte[] { (byte)(callerHosts ? 1 : 0) });
                SendCall(person);
                if (callerHosts)
                    return; // They host; you wait for their room.
            }
            RaiseCall(next, friend, null, null);
        }

        /// <summary>Your private room is up: gives your friend what they need to join it.</summary>
        public void CallRoomIsReady(string roomCode, string password)
        {
            lock (_lock)
            {
                Person person;
                if (_call == null || !_people.TryGetValue(_call.FriendId, out person))
                    return;
                byte[] code = Encoding.ASCII.GetBytes(roomCode);
                byte[] secret = Encoding.ASCII.GetBytes(password);
                byte[] body = new byte[2 + code.Length + secret.Length];
                body[0] = (byte)code.Length;
                Buffer.BlockCopy(code, 0, body, 1, code.Length);
                body[1 + code.Length] = (byte)secret.Length;
                Buffer.BlockCopy(secret, 0, body, 2 + code.Length, secret.Length);
                _call.Resend = CallPacket(CallRoomReady, _call.Id, body);
                _call.Settled = true;
                _call.ExpiresUtc = DateTime.UtcNow.AddSeconds(CallRingSeconds);
                SendCall(person);
            }
        }

        /// <summary>Declines, cancels or forgets the current call.</summary>
        public void EndCall()
        {
            lock (_lock)
            {
                if (_call == null)
                    return;
                Person person;
                if (_people.TryGetValue(_call.FriendId, out person) && person.Current != null && person.Current.Confirmed)
                {
                    byte[] bye = CallPacket(_call.Incoming && !_call.Answered ? CallDecline : CallCancel, _call.Id, new byte[0]);
                    int i;
                    for (i = 0; i < 3; i++)
                        SendInner(person.Current, bye); // No acknowledgement, and UDP can drop one.
                }
                _lastEndedCall = _call.Id;
                _call = null;
            }
        }

        /// <summary>Adds "Missed call" to a friend's chat, with no sound of its own.</summary>
        public void NoteMissedCall(string id)
        {
            lock (_lock)
            {
                Person person;
                if (!_people.TryGetValue(id, out person) || person.State != FriendState.Friend)
                    return;
                FriendChatEntry entry = new FriendChatEntry();
                entry.TimeUtc = DateTime.UtcNow;
                entry.Text = "Missed call";
                entry.Delivered = true;
                ChatOf(person).Add(entry);
                person.Unread++;
                Save();
                Bump();
            }
        }

        private static byte[] CallPacket(byte kind, ulong id, byte[] body)
        {
            byte[] inner = new byte[10 + body.Length];
            inner[0] = InnerCall;
            inner[1] = kind;
            Buffer.BlockCopy(BitConverter.GetBytes(id), 0, inner, 2, 8);
            Buffer.BlockCopy(body, 0, inner, 10, body.Length);
            return inner;
        }

        private void SendCall(Person person)
        {
            _call.LastSentUtc = DateTime.UtcNow;
            if (_call.Resend != null && person.Current != null && person.Current.Confirmed)
                SendInner(person.Current, _call.Resend);
        }

        private void RaiseCall(FriendCallEvent kind, string friend, string code, string password)
        {
            Action<FriendCallEvent, string, string, string> handler = CallEvent;
            if (handler != null)
                ThreadPool.QueueUserWorkItem(delegate { handler(kind, friend, code, password); });
        }

        private void TickCall(DateTime now)
        {
            if (_call == null)
                return;
            if (now > _call.ExpiresUtc)
            {
                string friend = _call.FriendId;
                bool settled = _call.Settled;
                _lastEndedCall = _call.Id;
                _call = null;
                // Once the room has been handed over, the ringing is done, not the call.
                if (!settled)
                    RaiseCall(FriendCallEvent.Ended, friend, null, null);
                return;
            }
            if (_call.Resend != null && (now - _call.LastSentUtc).TotalMilliseconds >= ResendMs)
            {
                Person person;
                if (_people.TryGetValue(_call.FriendId, out person))
                    SendCall(person);
            }
        }

        /// <summary>Friends only, and a room is only ever passed on for the call you're actually in.</summary>
        private void HandleCall(Person person, byte[] inner)
        {
            if (person == null || person.State != FriendState.Friend || inner.Length < 10)
                return;
            byte kind = inner[1];
            ulong id = BitConverter.ToUInt64(inner, 2);
            if (id == _lastEndedCall)
                return; // A resend of a call that's over.

            if (kind == CallInvite)
            {
                if (inner.Length != 11)
                    return;
                if (_call != null)
                {
                    if (_call.Id != id && person.Current != null)
                        SendInner(person.Current, CallPacket(CallDecline, id, new byte[0])); // Already in a call.
                    return;
                }
                _call = new ActiveCall();
                _call.Id = id;
                _call.FriendId = person.Id;
                _call.Incoming = true;
                _call.CallerCanHost = inner[10] == 1;
                _call.ExpiresUtc = DateTime.UtcNow.AddSeconds(CallRingSeconds);
                RaiseCall(FriendCallEvent.Incoming, person.Id, null, null);
                return;
            }

            if (_call == null || _call.Id != id || _call.FriendId != person.Id)
                return; // Nothing you asked for: a friend can't make you join a room.

            switch (kind)
            {
                case CallAccept:
                    if (_call.Incoming || inner.Length != 11 || _call.Accepted)
                        return;
                    _call.Accepted = true;
                    _call.Resend = null;
                    _call.ExpiresUtc = DateTime.UtcNow.AddSeconds(CallRingSeconds);
                    if (inner[10] == 1)
                        RaiseCall(FriendCallEvent.HostNow, person.Id, null, null);
                    else
                        _call.WaitingForRoom = true;
                    break;

                case CallRoomReady:
                    // On the caller's side the room also proves they answered, if Accept got lost.
                    if (_call.Incoming && !_call.WaitingForRoom)
                        return;
                    _call.Accepted = true;
                    string code;
                    string password;
                    if (!TryParseRoom(inner, out code, out password))
                        return;
                    if (person.Current != null)
                        SendInner(person.Current, CallPacket(CallJoining, id, new byte[0]));
                    if (_call.RoomPassedOn)
                        return;
                    _call.RoomPassedOn = true;
                    _call.Settled = true;
                    _call.Resend = null;
                    _call.ExpiresUtc = DateTime.UtcNow.AddSeconds(CallRingSeconds);
                    RaiseCall(FriendCallEvent.JoinRoom, person.Id, code, password);
                    break;

                case CallJoining:
                    _call.Resend = null;
                    break;

                case CallDecline:
                case CallCancel:
                    _lastEndedCall = id;
                    _call = null;
                    RaiseCall(kind == CallDecline ? FriendCallEvent.Declined : FriendCallEvent.Ended, person.Id, null, null);
                    break;
            }
        }

        private static bool TryParseRoom(byte[] inner, out string code, out string password)
        {
            code = null;
            password = null;
            if (inner.Length < 12)
                return false;
            int codeLength = inner[10];
            if (11 + codeLength + 1 > inner.Length || codeLength > 64)
                return false;
            int secretLength = inner[11 + codeLength];
            if (12 + codeLength + secretLength != inner.Length || secretLength < 1 || secretLength > 64)
                return false;
            code = Encoding.ASCII.GetString(inner, 11, codeLength);
            password = Encoding.ASCII.GetString(inner, 12 + codeLength, secretLength);
            return RoomCode.IsValid(code);
        }

        // ----- What the UI can do -----

        public AddFriendResult AddByCode(string code)
        {
            IPAddress ip;
            int port;
            byte[] fingerprint;
            if (!FriendCode.TryDecode(code, out ip, out port, out fingerprint))
                return AddFriendResult.InvalidCode;
            string id = Hex(fingerprint);
            lock (_lock)
            {
                if (id == Hex(_fingerprint))
                    return AddFriendResult.ThatsYou;
                if (!_running)
                    return AddFriendResult.NotRunning;
                Person person;
                if (_people.TryGetValue(id, out person))
                {
                    if (person.State == FriendState.Blocked)
                        return AddFriendResult.Blocked;
                    person.Address = new IPEndPoint(ip, port);
                    person.NextConnectUtc = DateTime.MinValue;
                    if (person.State == FriendState.RequestReceived)
                        MakeFriend(person);
                    Save();
                    Bump();
                    return AddFriendResult.AddressUpdated;
                }
                person = new Person();
                person.Fingerprint = fingerprint;
                person.Id = id;
                person.Name = "Friend " + ShortId(id);
                person.State = FriendState.RequestSent;
                person.Address = new IPEndPoint(ip, port);
                _people[id] = person;
                Save();
                Bump();
                return AddFriendResult.Sent;
            }
        }

        public List<FriendInfo> GetPeople()
        {
            List<FriendInfo> result = new List<FriendInfo>();
            lock (_lock)
            {
                foreach (Person person in _people.Values)
                {
                    FriendInfo info = new FriendInfo();
                    info.Id = person.Id;
                    info.Name = person.Name;
                    info.ShortId = ShortId(person.Id);
                    info.State = person.State;
                    info.Status = person.State == FriendState.Friend ? person.PeerStatus : FriendStatus.Offline;
                    info.Unread = person.Unread;
                    info.LastSeenUtc = person.LastSeenUtc;
                    result.Add(info);
                }
            }
            return result;
        }

        public List<FriendChatEntry> GetChat(string id)
        {
            List<FriendChatEntry> result = new List<FriendChatEntry>();
            lock (_lock)
            {
                Person person;
                if (_people.TryGetValue(id, out person) && person.State == FriendState.Friend)
                    result.AddRange(ChatOf(person).Entries);
            }
            return result;
        }

        public void MarkRead(string id)
        {
            lock (_lock)
            {
                Person person;
                if (_people.TryGetValue(id, out person) && person.Unread != 0)
                {
                    person.Unread = 0;
                    Save();
                    Bump();
                }
            }
        }

        /// <summary>Only ever called from your own click: this is what lets someone reach you.</summary>
        public void AcceptRequest(string id)
        {
            lock (_lock)
            {
                Person person;
                if (!_people.TryGetValue(id, out person) || person.State != FriendState.RequestReceived)
                    return;
                MakeFriend(person);
                Save();
                Bump();
            }
        }

        /// <summary>Blocked people get silence, from the handshake on.</summary>
        public void Block(string id)
        {
            lock (_lock)
            {
                Person person;
                if (!_people.TryGetValue(id, out person))
                    return;
                person.State = FriendState.Blocked;
                DropSessions(person);
                person.PeerStatus = FriendStatus.Offline;
                person.Outbox.Clear();
                Save();
                Bump();
            }
        }

        /// <summary>Forgets someone completely, including your chat with them.</summary>
        public void Remove(string id)
        {
            lock (_lock)
            {
                Person person;
                if (!_people.TryGetValue(id, out person))
                    return;
                DropSessions(person);
                ChatOf(person).Delete();
                _people.Remove(id);
                Save();
                Bump();
            }
        }

        /// <summary>Queues a message for a friend. Returns false if they aren't one or the text is empty.</summary>
        public bool SendMessage(string id, string text)
        {
            string clean = CleanText(text);
            if (clean.Length == 0)
                return false;
            lock (_lock)
            {
                Person person;
                if (!_people.TryGetValue(id, out person) || person.State != FriendState.Friend || person.Outbox.Count >= MaxOutbox)
                    return false;
                FriendChatEntry entry = new FriendChatEntry();
                entry.Mine = true;
                entry.TimeUtc = DateTime.UtcNow;
                entry.Text = clean;
                entry.MessageId = NewMessageId();
                entry.Sequence = ++person.LastSent;
                person.Outbox.Add(entry);
                Save();
                ChatOf(person).Add(entry);
                Bump();
                FlushOutbox(person);
                return true;
            }
        }

        /// <summary>Turns your reaction on a message on or off. Delivered like a message, so it can wait too.</summary>
        public bool ToggleReaction(string id, ulong messageId, string emoji)
        {
            EmojiEntry known = EmojiPack.Find(emoji);
            if (known == null)
                return false;
            lock (_lock)
            {
                Person person;
                if (!_people.TryGetValue(id, out person) || person.State != FriendState.Friend || person.Outbox.Count >= MaxOutbox || !ChatOf(person).HasMessage(messageId))
                    return false;
                FriendChatEntry entry = new FriendChatEntry();
                entry.Mine = true;
                entry.TimeUtc = DateTime.UtcNow;
                entry.Text = known.Text;
                entry.MessageId = NewMessageId();
                entry.ReactionTo = messageId;
                entry.Sequence = ++person.LastSent;
                person.Outbox.Add(entry);
                Save();
                ChatOf(person).Add(entry);
                Bump();
                FlushOutbox(person);
                return true;
            }
        }

        private static ulong NewMessageId()
        {
            ulong id = 0;
            while (id == 0)
                id = BitConverter.ToUInt64(CryptoUtil.Random(8), 0);
            return id;
        }

        // ----- Backup -----

        /// <summary>
        /// Writes your key and friends list to a file encrypted with a key derived from the
        /// password. Chats and unsent messages aren't included: they stay on this PC.
        /// </summary>
        public void ExportBackup(string path, string password)
        {
            byte[] plain;
            lock (_lock)
                plain = Encoding.UTF8.GetBytes(Serialize(true));
            byte[] data = FriendBackup.Seal(plain, password);
            Array.Clear(plain, 0, plain.Length);
            File.WriteAllBytes(path, data);
            lock (_lock)
            {
                _backedUp = true;
                Save();
                Bump();
            }
        }

        /// <summary>Replaces your account with a backup's. You must be Offline first. The old file is kept.</summary>
        public RestoreResult RestoreBackup(string path, string password)
        {
            byte[] data;
            try
            {
                if (new FileInfo(path).Length > FriendBackup.MaxFileBytes)
                    return RestoreResult.NotABackup;
                data = File.ReadAllBytes(path);
            }
            catch
            {
                return RestoreResult.NotABackup;
            }
            if (!FriendBackup.LooksLikeBackup(data))
                return RestoreResult.NotABackup;
            byte[] plain = FriendBackup.Open(data, password);
            if (plain == null)
                return RestoreResult.WrongPassword;

            lock (_lock)
            {
                if (_running)
                    return RestoreResult.StillOnline;

                // Check it all first: a bad file mustn't cost you the account you have.
                Dictionary<string, Person> oldPeople = new Dictionary<string, Person>(_people);
                byte[] oldKey = _privateKey;
                int oldPort = _preferredPort;
                string oldCode = _lastCode;
                uint oldIncarnation = _incarnation;
                bool oldBackedUp = _backedUp;
                Action putBack = delegate
                {
                    _people.Clear();
                    foreach (KeyValuePair<string, Person> pair in oldPeople)
                        _people[pair.Key] = pair.Value;
                    UseKey(oldKey);
                    _preferredPort = oldPort;
                    _lastCode = oldCode;
                    _incarnation = oldIncarnation;
                    _backedUp = oldBackedUp;
                };
                _people.Clear();
                _privateKey = null;
                if (!Parse(Encoding.UTF8.GetString(plain)))
                {
                    putBack();
                    return RestoreResult.NotABackup;
                }

                string store = StorePath;
                if (File.Exists(store))
                {
                    string kept = Path.Combine(_folder, "friends-v1.replaced");
                    int n = 1;
                    while (File.Exists(kept))
                        kept = Path.Combine(_folder, "friends-v1.replaced" + n++);
                    try { File.Move(store, kept); }
                    catch
                    {
                        putBack();
                        return RestoreResult.CouldNotSave;
                    }
                }

                // The backup's numbering may be out of date. Friends see the new incarnation and start again.
                _incarnation = NewIncarnation();
                _lastCode = String.Empty;
                _backedUp = true;
                _saveBlocked = false;
                IdentityWasLost = false;
                foreach (Person person in _people.Values)
                {
                    person.LastSent = 0;
                    person.LastReceived = 0;
                    person.PeerIncarnation = 0;
                    ChatOf(person).DropUnsent();
                }
                Save();
                Bump();
                return RestoreResult.Restored;
            }
        }

        // ----- The network thread -----

        private void Run()
        {
            byte[] buffer = new byte[MaxDatagram];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            DateTime nextTick = DateTime.UtcNow;
            Socket socket = _socket;
            while (_running)
            {
                try
                {
                    if (socket.Poll(50000, SelectMode.SelectRead))
                    {
                        int length = socket.ReceiveFrom(buffer, ref from);
                        lock (_lock)
                        {
                            if (_running)
                                Receive((IPEndPoint)from, buffer, length);
                        }
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }

                DateTime now = DateTime.UtcNow;
                if (now >= nextTick)
                {
                    nextTick = now.AddMilliseconds(100);
                    lock (_lock)
                    {
                        if (_running)
                            Tick(now);
                    }
                }
            }
        }

        private void Tick(DateTime now)
        {
            TickCall(now);
            List<uint> stale = new List<uint>();
            foreach (KeyValuePair<uint, Handshake> pair in _handshakes)
            {
                Handshake h = pair.Value;
                if ((now - h.CreatedUtc).TotalSeconds > 30.0)
                    stale.Add(pair.Key);
            }
            foreach (uint sid in stale)
                _handshakes.Remove(sid);

            stale.Clear();
            foreach (KeyValuePair<uint, Session> pair in _sessions)
            {
                Session session = pair.Value;
                double idle = (now - session.LastReceivedUtc).TotalMilliseconds;
                Person person = PersonOf(session);
                bool current = person != null && Object.ReferenceEquals(person.Current, session);
                bool isFriend = person != null && person.State == FriendState.Friend;
                if (idle > SessionTimeoutMs || (!current && idle > 5000 && !isFriend))
                {
                    stale.Add(pair.Key);
                    continue;
                }
                if (!session.Confirmed && session.Finish != null && (now - session.LastSentUtc).TotalMilliseconds >= ResendMs)
                {
                    SendRaw(session.Endpoint, session.Finish);
                    session.LastSentUtc = now;
                }
            }
            foreach (uint sid in stale)
            {
                Session session = _sessions[sid];
                Person person = PersonOf(session);
                _sessions.Remove(sid);
                if (person != null && Object.ReferenceEquals(person.Current, session))
                {
                    person.Current = null;
                    person.PeerStatus = FriendStatus.Offline;
                    Bump();
                }
                session.Dispose();
            }

            foreach (Person person in _people.Values)
            {
                if (person.State != FriendState.Friend && person.State != FriendState.RequestSent)
                    continue;
                Session session = person.Current;
                if (session == null)
                {
                    TryConnect(person, now);
                    continue;
                }
                if (!session.Confirmed)
                    continue;
                if (person.State == FriendState.RequestSent)
                {
                    if ((now - person.LastRequestUtc).TotalMilliseconds >= ConnectIntervalMs)
                        SendRequest(person, session, now);
                    continue;
                }
                if ((now - session.LastPingUtc).TotalMilliseconds >= KeepAliveMs)
                    SendPing(session, now);
                if ((now - person.LastFlushUtc).TotalMilliseconds >= ResendMs)
                    FlushOutbox(person);
            }
        }

        private void Receive(IPEndPoint sender, byte[] data, int length)
        {
            if (length < HeaderBytes || BitConverter.ToUInt32(data, 0) != Magic)
                return;
            switch (data[4])
            {
                case OuterHello: HandleHello(sender, data, length); break;
                case OuterReply: HandleReply(sender, data, length); break;
                case OuterFinish: HandleFinish(sender, data, length); break;
                case OuterData: HandleData(sender, data, length); break;
            }
        }

        // ----- Handshake -----

        private void TryConnect(Person person, DateTime now)
        {
            if (person.Address == null && person.Observed == null)
                return;
            if (now < person.NextConnectUtc)
                return;
            person.NextConnectUtc = now.AddMilliseconds(ConnectIntervalMs);
            person.ConnectFlip = !person.ConnectFlip;
            IPEndPoint target = person.Address;
            if (target == null || (person.ConnectFlip && person.Observed != null))
                target = person.Observed;

            Handshake h = new Handshake();
            h.Target = person;
            h.Endpoint = target;
            h.Initiator = true;
            h.Sid = NewSid();
            h.Ephemeral = X25519.GeneratePrivateKey();
            byte[] ephemeralPublic = X25519.PublicKey(h.Ephemeral);
            h.Start(ProtocolLabel);
            h.MixHash(BitConverter.GetBytes(h.Sid), ephemeralPublic);
            _handshakes[h.Sid] = h;

            byte[] hello = NewPacket(OuterHello, HelloBytes);
            CryptoUtil.Random(HelloBytes - HeaderBytes).CopyTo(hello, HeaderBytes);
            Buffer.BlockCopy(BitConverter.GetBytes(h.Sid), 0, hello, HeaderBytes, 4);
            Buffer.BlockCopy(ephemeralPublic, 0, hello, HeaderBytes + 4, 32);
            Buffer.BlockCopy(Gate(person.Fingerprint, ephemeralPublic), 0, hello, HeaderBytes + 36, 16);
            SendRaw(target, hello);
        }

        private void HandleHello(IPEndPoint sender, byte[] data, int length)
        {
            if (length != HelloBytes || !AllowHello(sender.Address.ToString()) || _handshakes.Count >= MaxPendingHandshakes)
                return;
            byte[] initiatorEphemeral = new byte[32];
            Buffer.BlockCopy(data, HeaderBytes + 4, initiatorEphemeral, 0, 32);
            byte[] gate = new byte[16];
            Buffer.BlockCopy(data, HeaderBytes + 36, gate, 0, 16);
            if (!CryptoUtil.ConstantTimeEquals(Gate(_fingerprint, initiatorEphemeral), gate))
                return; // Doesn't know your code: as far as they can tell, nothing is here.

            uint peerSid = BitConverter.ToUInt32(data, HeaderBytes);
            Handshake h = new Handshake();
            h.Endpoint = sender;
            h.Sid = NewSid();
            h.PeerSid = peerSid;
            h.PeerEphemeral = initiatorEphemeral;
            h.Ephemeral = X25519.GeneratePrivateKey();
            byte[] ephemeralPublic = X25519.PublicKey(h.Ephemeral);
            h.Start(ProtocolLabel);
            h.MixHash(BitConverter.GetBytes(peerSid), initiatorEphemeral);
            h.MixHash(BitConverter.GetBytes(h.Sid), ephemeralPublic);
            byte[] sealedKey;
            try
            {
                h.MixKey(DiffieHellman(h.Ephemeral, initiatorEphemeral));
                sealedKey = h.Seal(_publicKey);
                h.MixKey(DiffieHellman(_privateKey, initiatorEphemeral));
            }
            catch (CryptographicException)
            {
                return; // A weak key from the other side.
            }
            _handshakes[h.Sid] = h;

            byte[] reply = NewPacket(OuterReply, ReplyBytes);
            Buffer.BlockCopy(BitConverter.GetBytes(peerSid), 0, reply, HeaderBytes, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(h.Sid), 0, reply, HeaderBytes + 4, 4);
            Buffer.BlockCopy(ephemeralPublic, 0, reply, HeaderBytes + 8, 32);
            Buffer.BlockCopy(sealedKey, 0, reply, HeaderBytes + 40, 56);
            SendRaw(sender, reply);
        }

        private void HandleReply(IPEndPoint sender, byte[] data, int length)
        {
            if (length != ReplyBytes)
                return;
            uint mine = BitConverter.ToUInt32(data, HeaderBytes);
            Handshake h;
            if (!_handshakes.TryGetValue(mine, out h) || !h.Initiator || !h.Endpoint.Equals(sender))
                return;
            uint peerSid = BitConverter.ToUInt32(data, HeaderBytes + 4);
            byte[] responderEphemeral = new byte[32];
            byte[] sealedKey = new byte[56];
            Buffer.BlockCopy(data, HeaderBytes + 8, responderEphemeral, 0, 32);
            Buffer.BlockCopy(data, HeaderBytes + 40, sealedKey, 0, 56);

            Handshake attempt = h.Clone();
            byte[] finish;
            Session session;
            byte[] responderKey;
            try
            {
                attempt.MixHash(BitConverter.GetBytes(peerSid), responderEphemeral);
                attempt.MixKey(DiffieHellman(attempt.Ephemeral, responderEphemeral));
                responderKey = attempt.Open(sealedKey);
                if (responderKey == null || responderKey.Length != 32)
                    return;
                // The real check: is this the key the friend code promised?
                if (!CryptoUtil.ConstantTimeEquals(FriendCode.Fingerprint(responderKey), h.Target.Fingerprint))
                    return;
                attempt.MixKey(DiffieHellman(attempt.Ephemeral, responderKey));
                byte[] sealedOwn = attempt.Seal(_publicKey);
                attempt.MixKey(DiffieHellman(_privateKey, responderEphemeral));
                session = attempt.Split(true);

                finish = NewPacket(OuterFinish, FinishBytes);
                Buffer.BlockCopy(BitConverter.GetBytes(peerSid), 0, finish, HeaderBytes, 4);
                Buffer.BlockCopy(sealedOwn, 0, finish, HeaderBytes + 4, 56);
            }
            catch (CryptographicException)
            {
                return;
            }

            _handshakes.Remove(mine);
            Person person = h.Target;
            if (!_people.ContainsKey(person.Id) || person.State == FriendState.Blocked)
            {
                session.Dispose();
                return;
            }
            if (person.PublicKey == null)
                person.PublicKey = responderKey;

            session.Sid = mine;
            session.PeerSid = peerSid;
            session.PersonId = person.Id;
            session.Endpoint = sender;
            session.Finish = finish;
            session.LastSentUtc = DateTime.UtcNow;
            AddSession(session);
            person.Current = session;
            SendRaw(sender, finish);
        }

        private void HandleFinish(IPEndPoint sender, byte[] data, int length)
        {
            if (length != FinishBytes)
                return;
            uint mine = BitConverter.ToUInt32(data, HeaderBytes);

            Session existing;
            if (_sessions.TryGetValue(mine, out existing))
            {
                // Our confirmation got lost; the initiator is still waiting for it.
                SendInner(existing, new byte[] { InnerReady });
                return;
            }

            Handshake h;
            if (!_handshakes.TryGetValue(mine, out h) || h.Initiator || !h.Endpoint.Equals(sender))
                return;
            byte[] sealedKey = new byte[56];
            Buffer.BlockCopy(data, HeaderBytes + 4, sealedKey, 0, 56);

            Handshake attempt = h.Clone();
            Session session;
            byte[] initiatorKey;
            try
            {
                initiatorKey = attempt.Open(sealedKey);
                if (initiatorKey == null || initiatorKey.Length != 32)
                    return;
                attempt.MixKey(DiffieHellman(attempt.Ephemeral, initiatorKey));
                session = attempt.Split(false);
            }
            catch (CryptographicException)
            {
                return;
            }
            _handshakes.Remove(mine);

            string id = Hex(FriendCode.Fingerprint(initiatorKey));
            Person person;
            _people.TryGetValue(id, out person);
            if (id == Hex(_fingerprint) || (person != null && person.State == FriendState.Blocked))
            {
                session.Dispose();
                return; // Blocked people get silence.
            }
            if (person != null && person.PublicKey == null)
                person.PublicKey = initiatorKey;

            session.Sid = mine;
            session.PeerSid = h.PeerSid;
            session.PersonId = id;
            session.PeerKey = initiatorKey;
            session.Endpoint = sender;
            session.Confirmed = true;
            AddSession(session);
            if (person != null)
                person.Current = session;
            SendInner(session, new byte[] { InnerReady });
            if (person != null)
                SessionLive(person, session);
        }

        private void AddSession(Session session)
        {
            if (_sessions.Count >= MaxSessions)
            {
                // Strangers go first; friends' sessions matter more.
                uint victim = 0;
                bool found = false;
                foreach (KeyValuePair<uint, Session> pair in _sessions)
                {
                    Person person = PersonOf(pair.Value);
                    if (person == null || person.State != FriendState.Friend)
                    {
                        victim = pair.Key;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    _sessions[victim].Dispose();
                    _sessions.Remove(victim);
                }
            }
            session.LastReceivedUtc = DateTime.UtcNow;
            _sessions[session.Sid] = session;
        }

        // ----- Inside a session -----

        private void HandleData(IPEndPoint sender, byte[] data, int length)
        {
            if (length < DataHeaderBytes)
                return;
            uint sid = BitConverter.ToUInt32(data, HeaderBytes);
            Session session;
            if (!_sessions.TryGetValue(sid, out session))
                return;
            byte[] inner = session.Receive.Open(data, length, DataHeaderBytes);
            if (inner == null || inner.Length < 1)
                return;

            // From here on the packet is proven to come from this session's key.
            session.LastReceivedUtc = DateTime.UtcNow;
            session.Endpoint = sender;
            Person person = PersonOf(session);
            if (!session.Confirmed)
            {
                session.Confirmed = true;
                session.Finish = null;
                if (person != null)
                    SessionLive(person, session);
            }
            if (person != null)
                person.Observed = sender;

            switch (inner[0])
            {
                case InnerRequest: HandleRequest(session, person, inner); break;
                case InnerAccept: HandleAccept(session, person, inner); break;
                case InnerPing: HandlePing(person, inner); break;
                case InnerMessage: HandleMessage(person, inner); break;
                case InnerAck: HandleAck(person, inner); break;
                case InnerCall: HandleCall(person, inner); break;
                case InnerBye:
                    if (person != null && person.State == FriendState.Friend && Object.ReferenceEquals(person.Current, session))
                    {
                        person.PeerStatus = FriendStatus.Offline;
                        person.Current = null;
                        person.NextConnectUtc = DateTime.UtcNow.AddMilliseconds(ConnectIntervalMs);
                        _sessions.Remove(sid);
                        session.Dispose();
                        Bump();
                    }
                    break;
            }
        }

        private void SessionLive(Person person, Session session)
        {
            DateTime now = DateTime.UtcNow;
            person.LastSeenUtc = now;
            if (person.State == FriendState.Friend)
            {
                SendPing(session, now);
                FlushOutbox(person);
            }
            else if (person.State == FriendState.RequestSent)
            {
                SendRequest(person, session, now);
            }
        }

        private void SendRequest(Person person, Session session, DateTime now)
        {
            person.LastRequestUtc = now;
            byte[] name = LimitedUtf8(_name, 80);
            byte[] code = Encoding.ASCII.GetBytes(_code);
            byte[] inner = new byte[3 + name.Length + code.Length];
            inner[0] = InnerRequest;
            inner[1] = (byte)name.Length;
            Buffer.BlockCopy(name, 0, inner, 2, name.Length);
            inner[2 + name.Length] = (byte)code.Length;
            Buffer.BlockCopy(code, 0, inner, 3 + name.Length, code.Length);
            SendInner(session, inner);
        }

        private void HandleRequest(Session session, Person person, byte[] inner)
        {
            string name;
            string code;
            if (!TryParseNameAndCode(inner, out name, out code))
                return;
            IPEndPoint address = null;
            if (code.Length > 0)
            {
                IPAddress ip;
                int port;
                byte[] fingerprint;
                if (!FriendCode.TryDecode(code, out ip, out port, out fingerprint) || Hex(fingerprint) != session.PersonId)
                    return; // A code that isn't theirs.
                address = new IPEndPoint(ip, port);
            }

            if (person == null)
            {
                int received = 0;
                foreach (Person other in _people.Values)
                {
                    if (other.State == FriendState.RequestReceived)
                        received++;
                }
                if (received >= MaxReceivedRequests)
                    return;
                person = new Person();
                person.Id = session.PersonId;
                person.Fingerprint = FriendCode.Fingerprint(session.PeerKey);
                person.PublicKey = session.PeerKey;
                person.Name = name.Length > 0 ? name : "Someone";
                person.State = FriendState.RequestReceived;
                person.Address = address;
                person.Current = session;
                person.LastSeenUtc = DateTime.UtcNow;
                _people[person.Id] = person;
                Save();
                Bump();
                Action<string> received2 = RequestReceived;
                if (received2 != null)
                    ThreadPool.QueueUserWorkItem(delegate { received2(person.Name); });
                return;
            }

            if (person.State == FriendState.RequestSent)
            {
                // You both asked. You already chose to add this exact key by pasting their code.
                if (name.Length > 0)
                    person.Name = name;
                if (address != null)
                    person.Address = address;
                person.Current = session;
                MakeFriend(person);
                Save();
                Bump();
            }
            else if (person.State == FriendState.Friend)
            {
                // They didn't hear the accept (or lost it): tell them again.
                if (address != null)
                    person.Address = address;
                person.Current = session;
                SendAccept(session);
                SessionLive(person, session);
            }
        }

        private void MakeFriend(Person person)
        {
            person.State = FriendState.Friend;
            person.NextConnectUtc = DateTime.MinValue;
            Session session = person.Current;
            if (session != null && session.Confirmed)
            {
                SendAccept(session);
                SessionLive(person, session);
            }
        }

        private void SendAccept(Session session)
        {
            byte[] name = LimitedUtf8(_name, 80);
            byte[] code = Encoding.ASCII.GetBytes(_code);
            byte[] inner = new byte[3 + name.Length + code.Length];
            inner[0] = InnerAccept;
            inner[1] = (byte)name.Length;
            Buffer.BlockCopy(name, 0, inner, 2, name.Length);
            inner[2 + name.Length] = (byte)code.Length;
            Buffer.BlockCopy(code, 0, inner, 3 + name.Length, code.Length);
            SendInner(session, inner);
        }

        /// <summary>Believed only from the exact key you asked, and only if you asked.</summary>
        private void HandleAccept(Session session, Person person, byte[] inner)
        {
            if (person == null || person.State != FriendState.RequestSent)
                return;
            string name;
            string code;
            if (!TryParseNameAndCode(inner, out name, out code))
                return;
            if (name.Length > 0)
                person.Name = name;
            ApplyCode(person, code);
            person.State = FriendState.Friend;
            person.Current = session;
            Save();
            Bump();
            SessionLive(person, session);
            Action<string> accepted = RequestAccepted;
            string acceptedName = person.Name;
            if (accepted != null)
                ThreadPool.QueueUserWorkItem(delegate { accepted(acceptedName); });
        }

        private void BroadcastPing()
        {
            DateTime now = DateTime.UtcNow;
            foreach (Person person in _people.Values)
            {
                if (person.State == FriendState.Friend && person.Current != null && person.Current.Confirmed)
                    SendPing(person.Current, now);
            }
        }

        /// <summary>Status, name and current code. Only ever sent to accepted friends.</summary>
        private void SendPing(Session session, DateTime now)
        {
            Person person = PersonOf(session);
            if (person == null || person.State != FriendState.Friend)
                return;
            session.LastPingUtc = now;
            byte[] name = LimitedUtf8(_name, 80);
            byte[] code = Encoding.ASCII.GetBytes(_code);
            byte[] inner = new byte[8 + name.Length + code.Length];
            inner[0] = InnerPing;
            inner[1] = (byte)_status;
            Buffer.BlockCopy(BitConverter.GetBytes(_incarnation), 0, inner, 2, 4);
            inner[6] = (byte)name.Length;
            Buffer.BlockCopy(name, 0, inner, 7, name.Length);
            inner[7 + name.Length] = (byte)code.Length;
            Buffer.BlockCopy(code, 0, inner, 8 + name.Length, code.Length);
            SendInner(session, inner);
        }

        private void HandlePing(Person person, byte[] inner)
        {
            if (person == null || person.State != FriendState.Friend || inner.Length < 8)
                return;
            byte status = inner[1];
            if (status < (byte)FriendStatus.Online || status > (byte)FriendStatus.Busy)
                return;
            int nameLength = inner[6];
            if (7 + nameLength + 1 > inner.Length)
                return;
            int codeLength = inner[7 + nameLength];
            if (8 + nameLength + codeLength != inner.Length || nameLength > 80 || codeLength > 64)
                return;

            ApplyIncarnation(person, BitConverter.ToUInt32(inner, 2));
            string name = CleanName(Encoding.UTF8.GetString(inner, 7, nameLength));
            string code = Encoding.ASCII.GetString(inner, 8 + nameLength, codeLength);
            bool changed = person.PeerStatus != (FriendStatus)status;
            person.PeerStatus = (FriendStatus)status;
            if (name.Length > 0 && name != person.Name)
            {
                person.Name = name;
                changed = true;
            }
            person.LastSeenUtc = DateTime.UtcNow;
            bool addressChanged = ApplyCode(person, code);
            if (addressChanged || changed)
                Save();
            if (changed)
                Bump();
        }

        /// <summary>
        /// A friend who restored a backup has lost track of the numbering, in both directions.
        /// Both of you start again from 1, and anything you hadn't delivered goes again.
        /// </summary>
        private void ApplyIncarnation(Person person, uint incarnation)
        {
            if (incarnation == 0 || incarnation == person.PeerIncarnation)
                return;
            bool restored = person.PeerIncarnation != 0;
            person.PeerIncarnation = incarnation;
            if (restored)
            {
                person.LastReceived = 0;
                ulong next = 0;
                FriendChat chat = ChatOf(person);
                foreach (FriendChatEntry entry in person.Outbox)
                {
                    next++;
                    chat.Renumber(entry.Sequence, next);
                    entry.Sequence = next;
                }
                person.LastSent = next;
                chat.SaveNow();
            }
            Save();
        }

        /// <summary>Takes a new address from a friend, only if the code is really theirs.</summary>
        private bool ApplyCode(Person person, string code)
        {
            if (String.IsNullOrEmpty(code))
                return false;
            IPAddress ip;
            int port;
            byte[] fingerprint;
            if (!FriendCode.TryDecode(code, out ip, out port, out fingerprint) || Hex(fingerprint) != person.Id)
                return false;
            IPEndPoint address = new IPEndPoint(ip, port);
            if (address.Equals(person.Address))
                return false;
            person.Address = address;
            return true;
        }

        private void FlushOutbox(Person person)
        {
            Session session = person.Current;
            if (session == null || !session.Confirmed || person.State != FriendState.Friend)
                return;
            person.LastFlushUtc = DateTime.UtcNow;
            int i;
            for (i = 0; i < person.Outbox.Count && i < MaxSendWindow; i++)
            {
                FriendChatEntry entry = person.Outbox[i];
                byte[] text = Encoding.UTF8.GetBytes(entry.Text);
                byte[] inner = new byte[MessageHeaderBytes + text.Length];
                inner[0] = InnerMessage;
                Buffer.BlockCopy(BitConverter.GetBytes(_incarnation), 0, inner, 1, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(entry.Sequence), 0, inner, 5, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(entry.TimeUtc.Ticks), 0, inner, 13, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(entry.MessageId), 0, inner, 21, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(entry.ReactionTo), 0, inner, 29, 8);
                Buffer.BlockCopy(text, 0, inner, MessageHeaderBytes, text.Length);
                SendInner(session, inner);
            }
        }

        /// <summary>Only accepted friends get here. Everyone else's messages are dropped unread.</summary>
        private void HandleMessage(Person person, byte[] inner)
        {
            if (person == null || person.State != FriendState.Friend || person.Current == null || inner.Length <= MessageHeaderBytes || inner.Length > MessageHeaderBytes + MaxMessageBytes)
                return;
            ApplyIncarnation(person, BitConverter.ToUInt32(inner, 1));
            ulong sequence = BitConverter.ToUInt64(inner, 5);
            if (sequence <= person.LastReceived)
            {
                SendAck(person); // A resend: our acknowledgement got lost.
                return;
            }
            if (sequence != person.LastReceived + 1 || !person.AllowMessage())
                return; // Out of order or too fast. No acknowledgement, so it comes again.

            string text = CleanText(Encoding.UTF8.GetString(inner, MessageHeaderBytes, inner.Length - MessageHeaderBytes));
            long ticks = BitConverter.ToInt64(inner, 13);
            ulong messageId = BitConverter.ToUInt64(inner, 21);
            ulong reactionTo = BitConverter.ToUInt64(inner, 29);
            person.LastReceived = sequence;
            if (reactionTo != 0)
            {
                // A known emoji on a real message, or nothing. Reactions make no sound and aren't unread.
                EmojiEntry emoji = EmojiPack.Find(text);
                if (emoji != null && ChatOf(person).HasMessage(reactionTo))
                {
                    FriendChatEntry reaction = new FriendChatEntry();
                    reaction.TimeUtc = DateTime.UtcNow;
                    reaction.Text = emoji.Text;
                    reaction.Sequence = sequence;
                    reaction.Delivered = true;
                    reaction.MessageId = messageId;
                    reaction.ReactionTo = reactionTo;
                    ChatOf(person).Add(reaction);
                }
                text = String.Empty;
            }
            else if (text.Length > 0)
            {
                FriendChatEntry entry = new FriendChatEntry();
                // The sender's clock can't push a message into the past or future.
                DateTime now = DateTime.UtcNow;
                DateTime sent = ticks > 0 && ticks < now.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : now;
                entry.TimeUtc = sent < now.AddDays(-365) ? now : sent;
                entry.Text = text;
                entry.Sequence = sequence;
                entry.Delivered = true;
                entry.MessageId = messageId;
                ChatOf(person).Add(entry);
                person.Unread++;
            }
            Save();
            SendAck(person);
            Bump();
            if (text.Length > 0)
            {
                Action<string, string> received = MessageReceived;
                if (received != null)
                {
                    string name = person.Id;
                    ThreadPool.QueueUserWorkItem(delegate { received(name, text); });
                }
            }
        }

        private void SendAck(Person person)
        {
            byte[] inner = new byte[9];
            inner[0] = InnerAck;
            Buffer.BlockCopy(BitConverter.GetBytes(person.LastReceived), 0, inner, 1, 8);
            SendInner(person.Current, inner);
        }

        private void HandleAck(Person person, byte[] inner)
        {
            if (person == null || person.State != FriendState.Friend || inner.Length != 9)
                return;
            ulong upTo = BitConverter.ToUInt64(inner, 1);
            if (upTo > person.LastSent)
                return;
            int removed = person.Outbox.RemoveAll(delegate(FriendChatEntry e) { return e.Sequence <= upTo; });
            if (removed > 0)
            {
                ChatOf(person).MarkDelivered(upTo);
                Save();
                Bump();
                FlushOutbox(person);
            }
        }

        // ----- Helpers -----

        private Person PersonOf(Session session)
        {
            Person person;
            return session.PersonId != null && _people.TryGetValue(session.PersonId, out person) ? person : null;
        }

        private FriendChat ChatOf(Person person)
        {
            if (person.Chat == null)
                person.Chat = new FriendChat(Path.Combine(_folder, "friend-" + person.Id + ".dat"));
            return person.Chat;
        }

        private void DropSessions(Person person)
        {
            List<uint> sids = new List<uint>();
            foreach (KeyValuePair<uint, Session> pair in _sessions)
            {
                if (pair.Value.PersonId == person.Id)
                    sids.Add(pair.Key);
            }
            foreach (uint sid in sids)
            {
                _sessions[sid].Dispose();
                _sessions.Remove(sid);
            }
            person.Current = null;
        }

        private void SendInner(Session session, byte[] inner)
        {
            if (session == null || _socket == null)
                return;
            byte[] ad = NewPacket(OuterData, DataHeaderBytes);
            Buffer.BlockCopy(BitConverter.GetBytes(session.PeerSid), 0, ad, HeaderBytes, 4);
            SendRaw(session.Endpoint, session.Send.Seal(ad, inner));
        }

        private void SendRaw(IPEndPoint target, byte[] packet)
        {
            try
            {
                if (_socket != null && target != null)
                    _socket.SendTo(packet, target);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private static byte[] NewPacket(byte type, int length)
        {
            byte[] packet = new byte[length];
            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, packet, 0, 4);
            packet[4] = type;
            return packet;
        }

        private static byte[] Gate(byte[] fingerprint, byte[] ephemeralPublic)
        {
            using (HMACSHA256 mac = new HMACSHA256(fingerprint))
            {
                byte[] gate = new byte[16];
                Buffer.BlockCopy(mac.ComputeHash(ephemeralPublic), 0, gate, 0, 16);
                return gate;
            }
        }

        private static byte[] DiffieHellman(byte[] privateKey, byte[] publicKey)
        {
            byte[] shared = X25519.SharedSecret(privateKey, publicKey);
            if (CryptoUtil.ConstantTimeEquals(shared, new byte[shared.Length]))
                throw new CryptographicException("Weak key.");
            return shared;
        }

        private uint NewSid()
        {
            while (true)
            {
                uint sid = BitConverter.ToUInt32(CryptoUtil.Random(4), 0);
                if (sid != 0 && !_sessions.ContainsKey(sid) && !_handshakes.ContainsKey(sid))
                    return sid;
            }
        }

        private bool AllowHello(string ip)
        {
            DateTime now = DateTime.UtcNow;
            HelloLimit limit;
            if (!_helloLimits.TryGetValue(ip, out limit))
            {
                if (_helloLimits.Count > 1024)
                    _helloLimits.Clear();
                limit = new HelloLimit();
                _helloLimits[ip] = limit;
            }
            if ((now - limit.WindowStartUtc).TotalSeconds >= 10.0)
            {
                limit.WindowStartUtc = now;
                limit.Count = 0;
            }
            limit.Count++;
            return limit.Count <= 10; // At most 10 handshakes per IP per 10 s.
        }

        private static bool TryParseNameAndCode(byte[] inner, out string name, out string code)
        {
            name = String.Empty;
            code = String.Empty;
            if (inner.Length < 3)
                return false;
            int nameLength = inner[1];
            if (2 + nameLength + 1 > inner.Length || nameLength > 80)
                return false;
            int codeLength = inner[2 + nameLength];
            if (3 + nameLength + codeLength != inner.Length || codeLength > 64)
                return false;
            name = CleanName(Encoding.UTF8.GetString(inner, 2, nameLength));
            code = Encoding.ASCII.GetString(inner, 3 + nameLength, codeLength);
            return true;
        }

        private static byte[] LimitedUtf8(string text, int maxBytes)
        {
            string value = text ?? String.Empty;
            while (Encoding.UTF8.GetByteCount(value) > maxBytes)
                value = value.Substring(0, value.Length - 1);
            return Encoding.UTF8.GetBytes(value);
        }

        /// <summary>Names can't contain invisible or direction-changing characters, which could fake another name.</summary>
        internal static string CleanName(string name)
        {
            if (String.IsNullOrEmpty(name))
                return String.Empty;
            StringBuilder clean = new StringBuilder();
            foreach (char c in name)
            {
                if (Char.IsControl(c) || Char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                    continue;
                clean.Append(c);
            }
            string result = clean.ToString().Trim();
            if (result.Length > MaxNameChars)
                result = result.Substring(0, MaxNameChars).Trim();
            if (result.Length > 0 && Char.IsHighSurrogate(result[result.Length - 1]))
                result = result.Substring(0, result.Length - 1);
            return result;
        }

        /// <summary>Same limits as room chat, without the characters that reverse how text displays.</summary>
        internal static string CleanText(string text)
        {
            if (String.IsNullOrEmpty(text))
                return String.Empty;
            StringBuilder clean = new StringBuilder();
            foreach (char c in text)
            {
                if ((Char.IsControl(c) && c != '\n') || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩'))
                    continue;
                clean.Append(c);
            }
            string result = clean.ToString().Trim();
            if (result.Length > MaxMessageChars)
                result = result.Substring(0, MaxMessageChars);
            while (result.Length > 0 && Encoding.UTF8.GetByteCount(result) > MaxMessageBytes)
                result = result.Substring(0, result.Length - 1);
            if (result.Length > 0 && Char.IsHighSurrogate(result[result.Length - 1]))
                result = result.Substring(0, result.Length - 1);
            return result;
        }

        private void Bump()
        {
            Interlocked.Increment(ref _version);
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", String.Empty).ToLowerInvariant();
        }

        private static byte[] FromHex(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            int i;
            for (i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        /// <summary>Lets people tell apart two friends with the same name.</summary>
        private static string ShortId(string id)
        {
            return id.Substring(0, 4).ToUpperInvariant() + "-" + id.Substring(4, 4).ToUpperInvariant();
        }

        // ----- Storage -----

        private string StorePath { get { return Path.Combine(_folder, "friends-v1.dat"); } }

        private void Load()
        {
            Directory.CreateDirectory(_folder);
            string path = StorePath;
            if (File.Exists(path))
            {
                byte[] data;
                try { data = EncryptedFile.Read(path); }
                catch
                {
                    // Can't tell what's in it, so nothing may replace it. Keep a key for this run only.
                    _saveBlocked = true;
                    NewIdentity();
                    return;
                }
                if (data != null && Parse(Encoding.UTF8.GetString(data)))
                    return;
                IdentityWasLost = true;
                if (!EncryptedFile.SetAside(path))
                    _saveBlocked = true;
            }
            NewIdentity();
            Save();
        }

        private void NewIdentity()
        {
            UseKey(X25519.GeneratePrivateKey());
            _incarnation = NewIncarnation();
        }

        private static uint NewIncarnation()
        {
            uint value = 0;
            while (value == 0)
                value = BitConverter.ToUInt32(CryptoUtil.Random(4), 0);
            return value;
        }

        private void UseKey(byte[] privateKey)
        {
            _privateKey = privateKey;
            _publicKey = X25519.PublicKey(privateKey);
            _fingerprint = FriendCode.Fingerprint(_publicKey);
        }

        private bool Parse(string text)
        {
            string[] lines = text.Split('\n');
            Dictionary<string, List<FriendChatEntry>> outboxes = new Dictionary<string, List<FriendChatEntry>>();
            int i;
            for (i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('|');
                try
                {
                    if (parts[0] == "me" && parts.Length == 4)
                    {
                        byte[] key = Convert.FromBase64String(parts[1]);
                        if (key.Length != X25519.KeySize)
                            return false;
                        UseKey(key);
                        Int32.TryParse(parts[2], out _preferredPort);
                        _lastCode = parts[3];
                    }
                    else if (parts[0] == "state" && parts.Length == 3)
                    {
                        _incarnation = UInt32.Parse(parts[1]);
                        _backedUp = parts[2] == "1";
                    }
                    else if (parts[0] == "p" && parts.Length == 11)
                    {
                        Person person = new Person();
                        person.State = (FriendState)Byte.Parse(parts[1]);
                        person.Id = parts[2];
                        person.Fingerprint = FromHex(parts[2]);
                        if (person.Fingerprint.Length != FriendCode.FingerprintLength || person.State > FriendState.Blocked)
                            return false;
                        person.PublicKey = parts[3].Length == 0 ? null : Convert.FromBase64String(parts[3]);
                        person.Name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[4]));
                        if (parts[5].Length > 0)
                        {
                            string[] address = parts[5].Split(':');
                            person.Address = new IPEndPoint(IPAddress.Parse(address[0]), Int32.Parse(address[1]));
                        }
                        person.LastSeenUtc = new DateTime(Int64.Parse(parts[6]), DateTimeKind.Utc);
                        person.LastSent = UInt64.Parse(parts[7]);
                        person.LastReceived = UInt64.Parse(parts[8]);
                        person.Unread = Int32.Parse(parts[9]);
                        person.PeerIncarnation = UInt32.Parse(parts[10]);
                        _people[person.Id] = person;
                    }
                    else if (parts[0] == "o" && (parts.Length == 5 || parts.Length == 7))
                    {
                        FriendChatEntry entry = new FriendChatEntry();
                        entry.Mine = true;
                        entry.Sequence = UInt64.Parse(parts[2]);
                        entry.TimeUtc = new DateTime(Int64.Parse(parts[3]), DateTimeKind.Utc);
                        entry.Text = Encoding.UTF8.GetString(Convert.FromBase64String(parts[4]));
                        if (parts.Length == 7)
                        {
                            entry.MessageId = UInt64.Parse(parts[5]);
                            entry.ReactionTo = UInt64.Parse(parts[6]);
                        }
                        Person owner;
                        if (_people.TryGetValue(parts[1], out owner))
                            owner.Outbox.Add(entry);
                    }
                }
                catch
                {
                    return false;
                }
            }
            if (_privateKey == null)
                return false;
            if (_incarnation == 0)
                _incarnation = NewIncarnation();
            return true;
        }

        private void Save()
        {
            if (_saveBlocked)
                return;
            try
            {
                EncryptedFile.Write(StorePath, Encoding.UTF8.GetBytes(Serialize(false)));
            }
            catch { }
        }

        /// <summary>A backup leaves out unsent messages, which belong to this PC.</summary>
        private string Serialize(bool forBackup)
        {
            StringBuilder text = new StringBuilder();
            text.Append("me|").Append(Convert.ToBase64String(_privateKey)).Append('|').Append(_preferredPort).Append('|').Append(_lastCode).Append('\n');
            text.Append("state|").Append(_incarnation).Append('|').Append(_backedUp ? "1" : "0").Append('\n');
            foreach (Person person in _people.Values)
            {
                string address = person.Address == null ? String.Empty : person.Address.Address + ":" + person.Address.Port;
                text.Append("p|").Append((byte)person.State).Append('|').Append(person.Id).Append('|')
                    .Append(person.PublicKey == null ? String.Empty : Convert.ToBase64String(person.PublicKey)).Append('|')
                    .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(person.Name))).Append('|')
                    .Append(address).Append('|').Append(person.LastSeenUtc.Ticks).Append('|')
                    .Append(person.LastSent).Append('|').Append(person.LastReceived).Append('|').Append(forBackup ? 0 : person.Unread).Append('|')
                    .Append(person.PeerIncarnation).Append('\n');
                if (forBackup)
                    continue;
                foreach (FriendChatEntry entry in person.Outbox)
                {
                    text.Append("o|").Append(person.Id).Append('|').Append(entry.Sequence).Append('|').Append(entry.TimeUtc.Ticks).Append('|')
                        .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Text))).Append('|')
                        .Append(entry.MessageId).Append('|').Append(entry.ReactionTo).Append('\n');
                }
            }
            return text.ToString();
        }

        // ----- Types -----

        private sealed class Person
        {
            public string Id;
            public byte[] Fingerprint;
            public byte[] PublicKey;
            public string Name = String.Empty;
            public FriendState State;
            public FriendStatus PeerStatus;
            public IPEndPoint Address;   // Where they say they can be reached.
            public IPEndPoint Observed;  // Where they last sent from. Never saved.
            public DateTime LastSeenUtc = new DateTime(0, DateTimeKind.Utc);
            public ulong LastSent;
            public ulong LastReceived;
            public uint PeerIncarnation;
            public int Unread;
            public readonly List<FriendChatEntry> Outbox = new List<FriendChatEntry>();
            public FriendChat Chat;
            public Session Current;
            public DateTime NextConnectUtc = DateTime.MinValue;
            public DateTime LastRequestUtc = DateTime.MinValue;
            public DateTime LastFlushUtc = DateTime.MinValue;
            public bool ConnectFlip;
            private DateTime _messageWindowUtc;
            private int _messageCount;

            /// <summary>At most 30 messages per 10 s from one friend.</summary>
            public bool AllowMessage()
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _messageWindowUtc).TotalSeconds >= 10.0)
                {
                    _messageWindowUtc = now;
                    _messageCount = 0;
                }
                _messageCount++;
                return _messageCount <= 30;
            }
        }

        private sealed class Session : IDisposable
        {
            public uint Sid;
            public uint PeerSid;
            public string PersonId;
            public byte[] PeerKey;
            public PacketCipher Send;
            public PacketCipher Receive;
            public IPEndPoint Endpoint;
            public bool Confirmed;
            public byte[] Finish;
            public DateTime LastReceivedUtc;
            public DateTime LastSentUtc;
            public DateTime LastPingUtc = DateTime.MinValue;

            public void Dispose()
            {
                if (Send != null) Send.Dispose();
                if (Receive != null) Receive.Dispose();
            }
        }

        private sealed class ActiveCall
        {
            public ulong Id;
            public string FriendId;
            public bool Incoming;
            public bool Answered;
            public bool Accepted;
            public bool CanHost;
            public bool CallerCanHost;
            public bool WaitingForRoom;
            public bool RoomPassedOn;
            public bool Settled;
            public byte[] Resend;
            public DateTime LastSentUtc;
            public DateTime ExpiresUtc;
        }

        private sealed class HelloLimit
        {
            public DateTime WindowStartUtc;
            public int Count;
        }

        /// <summary>The Noise symmetric state, built from the same primitives as room connections.</summary>
        private sealed class Handshake
        {
            public Person Target;
            public IPEndPoint Endpoint;
            public bool Initiator;
            public uint Sid;
            public uint PeerSid;
            public byte[] Ephemeral;
            public byte[] PeerEphemeral;
            public DateTime CreatedUtc = DateTime.UtcNow;
            private byte[] _chain;
            private byte[] _hash;
            private byte[] _key;

            public Handshake Clone()
            {
                return (Handshake)MemberwiseClone();
            }

            public void Start(byte[] label)
            {
                _chain = CryptoUtil.Sha256(label);
                _hash = CryptoUtil.Sha256(_chain, label);
            }

            public void MixHash(params byte[][] parts)
            {
                foreach (byte[] part in parts)
                    _hash = CryptoUtil.Sha256(_hash, part);
            }

            public void MixKey(byte[] sharedSecret)
            {
                byte[] output = Hkdf.DeriveKey(_chain, sharedSecret, Encoding.ASCII.GetBytes("mix"), 96);
                byte[] chain = new byte[32];
                byte[] key = new byte[64];
                Buffer.BlockCopy(output, 0, chain, 0, 32);
                Buffer.BlockCopy(output, 32, key, 0, 64);
                _chain = chain;
                _key = key;
            }

            /// <summary>Encrypts with the current key, bound to everything said so far.</summary>
            public byte[] Seal(byte[] plaintext)
            {
                using (PacketCipher cipher = new PacketCipher(_key))
                {
                    byte[] output = cipher.Seal(_hash, plaintext);
                    byte[] result = new byte[output.Length - _hash.Length];
                    Buffer.BlockCopy(output, _hash.Length, result, 0, result.Length);
                    MixHash(result);
                    return result;
                }
            }

            public byte[] Open(byte[] sealedBytes)
            {
                using (PacketCipher cipher = new PacketCipher(_key))
                {
                    byte[] packet = new byte[_hash.Length + sealedBytes.Length];
                    Buffer.BlockCopy(_hash, 0, packet, 0, _hash.Length);
                    Buffer.BlockCopy(sealedBytes, 0, packet, _hash.Length, sealedBytes.Length);
                    byte[] plaintext = cipher.Open(packet, packet.Length, _hash.Length);
                    if (plaintext != null)
                        MixHash(sealedBytes);
                    return plaintext;
                }
            }

            public Session Split(bool isInitiator)
            {
                byte[] keys = Hkdf.DeriveKey(_chain, _hash, Encoding.ASCII.GetBytes("split"), 128);
                byte[] first = new byte[64];
                byte[] second = new byte[64];
                Buffer.BlockCopy(keys, 0, first, 0, 64);
                Buffer.BlockCopy(keys, 64, second, 0, 64);
                Session session = new Session();
                session.Send = new PacketCipher(isInitiator ? first : second);
                session.Receive = new PacketCipher(isInitiator ? second : first);
                Array.Clear(keys, 0, keys.Length);
                Array.Clear(first, 0, 64);
                Array.Clear(second, 0, 64);
                return session;
            }
        }
    }
}
