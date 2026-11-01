using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Maela
{
    /// <summary>Encryption keys for one host-client connection.</summary>
    internal sealed class SecureSession : IDisposable
    {
        public PacketCipher Send;
        public PacketCipher Receive;
        public DateTime CreatedUtc = DateTime.UtcNow;
        public byte[] Transcript; // Unique to this connection: a password proof only works once.

        public void Dispose()
        {
            if (Send != null) Send.Dispose();
            if (Receive != null) Receive.Dispose();
        }
    }

    /// <summary>
    /// Transport security for every packet between a client and the host.
    ///
    /// Handshake (the Noise "NK" pattern: the client already knows the host's key):
    ///   1. client -> host  HELLO        client ephemeral key Ec (padded so replies can't be used
    ///                                   to amplify traffic)
    ///   2. host  -> client HELLO_REPLY  host static key S, host ephemeral key Eh
    ///   The client checks S against the fingerprint in the room code, then both sides compute
    ///   X25519(ec, S) and X25519(ec, Eh) and derive two 64-byte keys with HKDF, bound to a hash
    ///   of all three public keys. Only the real host (holder of S's private key) can derive
    ///   them, so a man in the middle can neither read nor impersonate. The fresh ephemeral keys
    ///   give forward secrecy.
    ///   3. client -> host  JOIN (encrypted) carries the display name and a proof of the room
    ///   password: an HMAC of the handshake, keyed by the password's hash. The password itself
    ///   never leaves your PC. A modified host could still try guessing it against the proof.
    ///
    /// Afterwards every packet is DATA: header || counter || AES-256-CTR ciphertext || HMAC tag,
    /// with replay protection (see PacketCipher). The host decrypts and re-encrypts when relaying,
    /// so the host can hear the room, just as a server would.
    ///
    /// PROBE / PROBE_REPLY stay unencrypted and only say "a mæla room is online here".
    /// </summary>
    internal sealed partial class VoipEngine
    {
        public const byte WireVersion = 6;

        private const byte OuterHello = 0x40;
        private const byte OuterHelloReply = 0x41;
        private const byte OuterData = 0x42;
        internal const byte OuterProbe = 0x43;
        internal const byte OuterProbeReply = 0x44;
        internal const int OuterHeaderBytes = 6;
        private const int HelloBytes = 160;
        private const int FingerprintBytes = 10;
        private const int MaxPendingSessions = 64;

        private static readonly byte[] ProtocolLabel = Encoding.ASCII.GetBytes("maela/6 NK X25519 HKDF-SHA256 AES256-CTR HMAC-SHA256");

        // Host side.
        private byte[] _hostPrivateKey;
        private byte[] _hostPublicKey;
        private byte[] _passwordHash;   // null = no password
        private readonly Dictionary<string, SecureSession> _pendingSessions = new Dictionary<string, SecureSession>();
        private readonly Dictionary<string, HelloLimiter> _helloLimits = new Dictionary<string, HelloLimiter>(StringComparer.Ordinal);

        // Client side.
        private byte[] _expectedFingerprint;
        private string _joinPassword;
        private SecureSession _clientSession;
        private byte[] _clientEphemeral;
        private int _encryptedJoinAttempts;

        /// <summary>The host's public-key fingerprint, embedded in room codes.</summary>
        public byte[] HostFingerprint
        {
            get { return _hostPublicKey == null ? null : Fingerprint(_hostPublicKey); }
        }

        public bool HasPassword { get { return _passwordHash != null; } }

        public static byte[] Fingerprint(byte[] hostPublicKey)
        {
            byte[] hash = CryptoUtil.Sha256(Encoding.ASCII.GetBytes("maela host key"), hostPublicKey);
            byte[] fp = new byte[FingerprintBytes];
            Buffer.BlockCopy(hash, 0, fp, 0, FingerprintBytes);
            return fp;
        }

        private const string ProofPrefix = "proof1:";

        /// <summary>What JOIN carries in place of the password. Empty stays empty: no password.</summary>
        internal static string JoinPasswordField(string password, byte[] transcript)
        {
            byte[] hash = HashPassword(password);
            if (hash == null)
                return String.Empty;
            using (System.Security.Cryptography.HMACSHA256 mac = new System.Security.Cryptography.HMACSHA256(hash))
                return ProofPrefix + Convert.ToBase64String(mac.ComputeHash(transcript));
        }

        /// <summary>Accepts a proof, or the plain password from copies of mæla older than proofs.</summary>
        internal static bool JoinPasswordValid(string field, byte[] passwordHash, byte[] transcript)
        {
            if (passwordHash == null)
                return true;
            if (field != null && field.StartsWith(ProofPrefix, StringComparison.Ordinal))
            {
                if (transcript == null)
                    return false;
                using (System.Security.Cryptography.HMACSHA256 mac = new System.Security.Cryptography.HMACSHA256(passwordHash))
                {
                    byte[] proof;
                    try { proof = Convert.FromBase64String(field.Substring(ProofPrefix.Length)); }
                    catch { return false; }
                    return CryptoUtil.ConstantTimeEquals(proof, mac.ComputeHash(transcript));
                }
            }
            return CryptoUtil.ConstantTimeEquals(HashPassword(field) ?? new byte[32], passwordHash);
        }

        internal static byte[] HashPassword(string password)
        {
            if (String.IsNullOrEmpty(password))
                return null;
            return CryptoUtil.Sha256(Encoding.ASCII.GetBytes("maela room password"), Encoding.UTF8.GetBytes(password));
        }

        private void ConfigureHostSecurity(byte[] hostPrivateKey, string password)
        {
            _hostPrivateKey = hostPrivateKey != null && hostPrivateKey.Length == X25519.KeySize ? (byte[])hostPrivateKey.Clone() : X25519.GeneratePrivateKey();
            _hostPublicKey = X25519.PublicKey(_hostPrivateKey);
            _passwordHash = HashPassword(password);
        }

        private void ConfigureClientSecurity(byte[] fingerprint, string password)
        {
            if (fingerprint == null || fingerprint.Length != FingerprintBytes)
                throw new ArgumentException("This room code does not include the host's security key. Ask the host for a new code.");
            _expectedFingerprint = (byte[])fingerprint.Clone();
            _joinPassword = password ?? String.Empty;
        }

        private void ResetSecurity()
        {
            lock (_stateLock)
            {
                foreach (KeyValuePair<string, SecureSession> pair in _pendingSessions)
                    pair.Value.Dispose();
                _pendingSessions.Clear();
                _helloLimits.Clear();
            }
            DropClientSession();
        }

        private void DropClientSession()
        {
            SecureSession old = _clientSession;
            _clientSession = null;
            _clientEphemeral = null;
            _encryptedJoinAttempts = 0;
            if (old != null)
                old.Dispose();
        }

        // ----- Receiving -----

        /// <summary>Handles one datagram. Returns the decrypted inner packet to process, or null.</summary>
        private Packet ReceiveSecure(IPEndPoint sender, byte[] data, int length, out SecureSession viaPending)
        {
            viaPending = null;
            if (length < OuterHeaderBytes || BitConverter.ToUInt32(data, 0) != Magic || data[4] != WireVersion)
                return null;
            byte type = data[5];

            if (_isHost)
            {
                if (type == OuterProbe)
                {
                    if (length >= OuterHeaderBytes)
                        SendOuter(sender, OuterProbeReply, null);
                    return null;
                }
                if (type == OuterHello)
                {
                    HandleHello(sender, data, length);
                    return null;
                }
                if (type != OuterData)
                    return null;

                string key = EndpointKey(sender);
                SecureSession established = null;
                SecureSession pending;
                lock (_stateLock)
                {
                    Participant participant;
                    if (_hostParticipants.TryGetValue(key, out participant))
                        established = participant.Session;
                    _pendingSessions.TryGetValue(key, out pending);
                }

                byte[] inner = established == null ? null : established.Receive.Open(data, length, OuterHeaderBytes);
                if (inner == null && pending != null)
                {
                    inner = pending.Receive.Open(data, length, OuterHeaderBytes);
                    if (inner != null)
                        viaPending = pending;
                }
                if (inner == null)
                    return null;

                Packet packet;
                if (!TryParsePacket(inner, inner.Length, out packet))
                    return null;
                if (viaPending != null && packet.Type != PacketJoin)
                    return null; // A new session must start with JOIN.
                return packet;
            }

            // Client.
            if (type == OuterHelloReply)
            {
                HandleHelloReply(sender, data, length);
                return null;
            }
            if (type != OuterData || _clientSession == null)
                return null;
            byte[] plain = _clientSession.Receive.Open(data, length, OuterHeaderBytes);
            if (plain == null)
                return null;
            Packet clientPacket;
            return TryParsePacket(plain, plain.Length, out clientPacket) ? clientPacket : null;
        }

        private void HandleHello(IPEndPoint sender, byte[] data, int length)
        {
            if (length != HelloBytes)
                return;
            string key = EndpointKey(sender);
            lock (_stateLock)
            {
                if (!AllowHello(sender.Address.ToString()))
                    return;
                if (_pendingSessions.Count >= MaxPendingSessions && !_pendingSessions.ContainsKey(key))
                    return;
            }

            byte[] clientEphemeral = new byte[32];
            Buffer.BlockCopy(data, OuterHeaderBytes, clientEphemeral, 0, 32);
            byte[] hostEphemeral = X25519.GeneratePrivateKey();
            byte[] hostEphemeralPublic = X25519.PublicKey(hostEphemeral);
            SecureSession session;
            try
            {
                byte[] dh1 = X25519.SharedSecret(_hostPrivateKey, clientEphemeral);
                byte[] dh2 = X25519.SharedSecret(hostEphemeral, clientEphemeral);
                session = DeriveSession(dh1, dh2, clientEphemeral, _hostPublicKey, hostEphemeralPublic, false);
            }
            catch
            {
                return; // Bad key from the client.
            }
            finally
            {
                Array.Clear(hostEphemeral, 0, hostEphemeral.Length);
            }

            lock (_stateLock)
            {
                SecureSession old;
                if (_pendingSessions.TryGetValue(key, out old))
                    old.Dispose();
                _pendingSessions[key] = session;
            }

            byte[] reply = new byte[64];
            Buffer.BlockCopy(_hostPublicKey, 0, reply, 0, 32);
            Buffer.BlockCopy(hostEphemeralPublic, 0, reply, 32, 32);
            SendOuter(sender, OuterHelloReply, reply);
        }

        private void HandleHelloReply(IPEndPoint sender, byte[] data, int length)
        {
            byte[] ephemeral = _clientEphemeral;
            if (ephemeral == null || _clientSession != null || length != OuterHeaderBytes + 64)
                return;
            if (_hostEndpoint == null || !sender.Address.Equals(_hostEndpoint.Address))
                return;

            byte[] hostStatic = new byte[32];
            byte[] hostEphemeral = new byte[32];
            Buffer.BlockCopy(data, OuterHeaderBytes, hostStatic, 0, 32);
            Buffer.BlockCopy(data, OuterHeaderBytes + 32, hostEphemeral, 0, 32);

            if (!CryptoUtil.ConstantTimeEquals(Fingerprint(hostStatic), _expectedFingerprint))
            {
                SetLastMessage("Security check failed: this host's key does not match the room code. Ask the host for a fresh code.");
                return;
            }

            try
            {
                byte[] dh1 = X25519.SharedSecret(ephemeral, hostStatic);
                byte[] dh2 = X25519.SharedSecret(ephemeral, hostEphemeral);
                _clientSession = DeriveSession(dh1, dh2, X25519.PublicKey(ephemeral), hostStatic, hostEphemeral, true);
            }
            catch
            {
                return;
            }
            _encryptedJoinAttempts = 0;
            SendEncryptedJoin();
        }

        private static SecureSession DeriveSession(byte[] dh1, byte[] dh2, byte[] clientEphemeral, byte[] hostStatic, byte[] hostEphemeral, bool isClient)
        {
            byte[] transcript = CryptoUtil.Sha256(ProtocolLabel, clientEphemeral, hostStatic, hostEphemeral);
            byte[] ikm = new byte[64];
            Buffer.BlockCopy(dh1, 0, ikm, 0, 32);
            Buffer.BlockCopy(dh2, 0, ikm, 32, 32);
            byte[] keys = Hkdf.DeriveKey(transcript, ikm, ProtocolLabel, 128);
            byte[] clientToHost = new byte[64];
            byte[] hostToClient = new byte[64];
            Buffer.BlockCopy(keys, 0, clientToHost, 0, 64);
            Buffer.BlockCopy(keys, 64, hostToClient, 0, 64);
            Array.Clear(ikm, 0, ikm.Length);
            Array.Clear(keys, 0, keys.Length);
            Array.Clear(dh1, 0, dh1.Length);
            Array.Clear(dh2, 0, dh2.Length);

            SecureSession session = new SecureSession();
            session.Send = new PacketCipher(isClient ? clientToHost : hostToClient);
            session.Receive = new PacketCipher(isClient ? hostToClient : clientToHost);
            session.Transcript = transcript;
            Array.Clear(clientToHost, 0, 64);
            Array.Clear(hostToClient, 0, 64);
            return session;
        }

        private bool AllowHello(string ip)
        {
            DateTime now = DateTime.UtcNow;
            HelloLimiter limiter;
            if (!_helloLimits.TryGetValue(ip, out limiter))
            {
                if (_helloLimits.Count > 1024)
                    _helloLimits.Clear();
                limiter = new HelloLimiter();
                _helloLimits[ip] = limiter;
            }
            if (now < limiter.BlockedUntilUtc)
                return false;
            if ((now - limiter.WindowStartUtc).TotalSeconds >= 10.0)
            {
                limiter.WindowStartUtc = now;
                limiter.Count = 0;
            }
            limiter.Count++;
            return limiter.Count <= 10; // At most 10 handshakes per IP per 10 s.
        }

        /// <summary>Called by the host after a wrong password; repeated failures lock the IP out for a minute.</summary>
        private void RecordPasswordFailure(string ip)
        {
            lock (_stateLock)
            {
                HelloLimiter limiter;
                if (!_helloLimits.TryGetValue(ip, out limiter))
                {
                    limiter = new HelloLimiter();
                    _helloLimits[ip] = limiter;
                }
                limiter.PasswordFailures++;
                if (limiter.PasswordFailures >= 5)
                {
                    limiter.PasswordFailures = 0;
                    limiter.BlockedUntilUtc = DateTime.UtcNow.AddMinutes(1);
                }
            }
        }

        private void PruneSecurityState()
        {
            DateTime now = DateTime.UtcNow;
            lock (_stateLock)
            {
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, SecureSession> pair in _pendingSessions)
                {
                    if ((now - pair.Value.CreatedUtc).TotalSeconds > 30.0)
                        stale.Add(pair.Key);
                }
                int i;
                for (i = 0; i < stale.Count; i++)
                {
                    _pendingSessions[stale[i]].Dispose();
                    _pendingSessions.Remove(stale[i]);
                }
            }
        }

        /// <summary>Host: adopts the pending session for a participant once their JOIN is accepted.</summary>
        private void AdoptSession(string key, Participant participant, SecureSession session)
        {
            if (session == null)
                return;
            lock (_stateLock)
            {
                SecureSession current;
                if (_pendingSessions.TryGetValue(key, out current) && Object.ReferenceEquals(current, session))
                    _pendingSessions.Remove(key);
                if (participant.Session != null && !Object.ReferenceEquals(participant.Session, session))
                    participant.Session.Dispose();
                participant.Session = session;
            }
        }

        // ----- Sending -----

        /// <summary>Client: starts or continues connecting. Called every second until welcomed.</summary>
        private void SendJoinSecure()
        {
            if (_clientSession == null || _encryptedJoinAttempts >= 4)
            {
                // (Re)start the handshake. A fresh ephemeral key each time keeps sessions independent.
                DropClientSession();
                _clientEphemeral = X25519.GeneratePrivateKey();
                byte[] hello = new byte[HelloBytes - OuterHeaderBytes];
                Buffer.BlockCopy(X25519.PublicKey(_clientEphemeral), 0, hello, 0, 32);
                SendOuter(_hostEndpoint, OuterHello, hello);
                return;
            }
            SendEncryptedJoin();
        }

        private void SendEncryptedJoin()
        {
            _encryptedJoinAttempts++;
            byte[] name = Encoding.UTF8.GetBytes(_localName ?? String.Empty);
            byte[] password = Encoding.ASCII.GetBytes(JoinPasswordField(_joinPassword, _clientSession.Transcript));
            if (name.Length > 80) Array.Resize(ref name, 80);
            if (password.Length > 200) Array.Resize(ref password, 200);
            byte[] payload = new byte[2 + name.Length + password.Length];
            payload[0] = (byte)name.Length;
            Buffer.BlockCopy(name, 0, payload, 1, name.Length);
            payload[1 + name.Length] = (byte)password.Length;
            Buffer.BlockCopy(password, 0, payload, 2 + name.Length, password.Length);
            SendPacket(_hostEndpoint, PacketJoin, 0, 0, payload);
        }

        private static bool TryParseJoin(byte[] payload, out string name, out string password)
        {
            name = String.Empty;
            password = String.Empty;
            if (payload == null || payload.Length < 2)
                return false;
            int nameLength = payload[0];
            if (1 + nameLength + 1 > payload.Length)
                return false;
            int passwordLength = payload[1 + nameLength];
            if (2 + nameLength + passwordLength != payload.Length)
                return false;
            try
            {
                name = Encoding.UTF8.GetString(payload, 1, nameLength).Trim();
                password = Encoding.UTF8.GetString(payload, 2 + nameLength, passwordLength);
            }
            catch
            {
                return false;
            }
            if (name.Length > 24)
                name = name.Substring(0, 24);
            return true;
        }

        private bool PasswordMatches(string field, SecureSession session)
        {
            return JoinPasswordValid(field, _passwordHash, session == null ? null : session.Transcript);
        }

        /// <summary>Finds the session used to send to <paramref name="target"/>. Null = drop the packet.</summary>
        private SecureSession SessionFor(IPEndPoint target, SecureSession preferred)
        {
            if (preferred != null)
                return preferred;
            if (!_isHost)
                return _clientSession;
            lock (_stateLock)
            {
                Participant participant;
                if (_hostParticipants.TryGetValue(EndpointKey(target), out participant) && participant.Session != null)
                    return participant.Session;
                SecureSession pending;
                return _pendingSessions.TryGetValue(EndpointKey(target), out pending) ? pending : null;
            }
        }

        private static byte[] OuterHeader(byte type)
        {
            byte[] header = new byte[OuterHeaderBytes];
            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, header, 0, 4);
            header[4] = WireVersion;
            header[5] = type;
            return header;
        }

        private void SendOuter(IPEndPoint target, byte type, byte[] body)
        {
            byte[] header = OuterHeader(type);
            int bodyLength = body == null ? 0 : body.Length;
            byte[] raw = new byte[OuterHeaderBytes + bodyLength];
            Buffer.BlockCopy(header, 0, raw, 0, OuterHeaderBytes);
            if (bodyLength > 0)
                Buffer.BlockCopy(body, 0, raw, OuterHeaderBytes, bodyLength);
            SendDatagram(target, raw);
        }

        private sealed class HelloLimiter
        {
            public DateTime WindowStartUtc;
            public int Count;
            public int PasswordFailures;
            public DateTime BlockedUntilUtc;
        }
    }
}
