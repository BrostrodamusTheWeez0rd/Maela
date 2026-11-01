using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Maela
{
    /// <summary>
    /// Member status shared with the room (muted / deafened), deafen, round-trip ping, and
    /// microphone noise suppression.
    ///
    /// Status rides on the keepalive a client already sends every 1.5 s (one extra byte), so a
    /// lost update is corrected automatically; the host includes everyone's status in the
    /// member list. Ping is a timestamp the host echoes back.
    /// </summary>
    internal sealed partial class VoipEngine
    {
        public const byte FlagMuted = 1;
        public const byte FlagDeafened = 2;

        private const byte PacketPing = 15;
        private const byte PacketPong = 16;

        private readonly Dictionary<uint, byte> _memberFlags = new Dictionary<uint, byte>();
        private readonly NoiseSuppressor _noiseSuppressor = new NoiseSuppressor();
        private readonly AutoGain _autoGain = new AutoGain();
        private volatile bool _autoGainEnabled = true;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private volatile bool _deafened;
        private volatile bool _noiseSuppression = true;
        private volatile bool _noiseResetPending;
        private double _pingMs = -1.0;

        /// <summary>Deafened: you hear nobody and your microphone is off.</summary>
        public bool Deafened
        {
            get { return _deafened; }
            set
            {
                if (_deafened == value)
                    return;
                _deafened = value;
                UpdateAllPlaybackGains();
                LocalStateChanged();
            }
        }

        /// <summary>Removes steady background noise from your microphone.</summary>
        public bool NoiseSuppression
        {
            get { return _noiseSuppression; }
            set
            {
                if (value && !_noiseSuppression)
                    _noiseResetPending = true;
                _noiseSuppression = value;
            }
        }

        /// <summary>Evens out your mic level so quiet mics are heard clearly and loud ones don't blast.</summary>
        public bool AutoGain
        {
            get { return _autoGainEnabled; }
            set { _autoGainEnabled = value; }
        }

        private void ApplyAutoGain(byte[] pcm)
        {
            if (!_autoGainEnabled)
                return;
            try { _autoGain.Process(pcm); }
            catch { }
        }

        /// <summary>Round-trip time to the host in milliseconds, or -1 if unknown (or you are the host).</summary>
        public int PingMs
        {
            get
            {
                if (_isHost || !_connected)
                    return -1;
                lock (_stateLock)
                    return _pingMs < 0 ? -1 : (int)Math.Round(_pingMs);
            }
        }

        private bool MicrophoneOpen { get { return !_muted && !_deafened; } }

        private byte LocalFlags
        {
            get { return (byte)((_muted ? FlagMuted : 0) | (_deafened ? FlagDeafened : 0)); }
        }

        private byte FlagsOf(uint memberId)
        {
            if (memberId == _localId)
                return LocalFlags;
            byte flags;
            return _memberFlags.TryGetValue(memberId, out flags) ? flags : (byte)0;
        }

        /// <summary>Called when mute/deafen changes, to tell the room.</summary>
        private void LocalStateChanged()
        {
            if (!_running)
                return;
            if (_isHost)
            {
                lock (_stateLock)
                    _memberFlags[_localId] = LocalFlags;
                BroadcastPeerList();
            }
            else if (_connected && _hostEndpoint != null)
            {
                SendPacket(_hostEndpoint, PacketKeepAlive, _localId, 0, new byte[] { LocalFlags });
            }
        }

        private void ApplyNoiseSuppression(byte[] pcm)
        {
            if (!_noiseSuppression)
                return;
            try
            {
                if (_noiseResetPending)
                {
                    _noiseResetPending = false;
                    _noiseSuppressor.Reset();
                }
                _noiseSuppressor.Process(pcm);
            }
            catch
            {
                // Never let audio processing take the microphone down.
            }
        }

        /// <summary>Host: a keepalive may carry the member's status byte.</summary>
        private void HostHandleKeepAlive(Participant known, Packet packet)
        {
            if (packet.Payload == null || packet.Payload.Length < 1)
                return;
            byte flags = (byte)(packet.Payload[0] & (FlagMuted | FlagDeafened));
            bool changed;
            lock (_stateLock)
            {
                byte old;
                changed = !_memberFlags.TryGetValue(known.Id, out old) || old != flags;
                _memberFlags[known.Id] = flags;
            }
            if (changed)
                BroadcastPeerList();
        }

        /// <summary>Host: ping/pong. Returns true if handled.</summary>
        private bool HostHandlePing(Participant known, Packet packet)
        {
            if (packet.Type != PacketPing)
                return false;
            if (packet.Payload != null && packet.Payload.Length == 8)
                SendPacket(known.Endpoint, PacketPong, _localId, 0, packet.Payload);
            return true;
        }

        /// <summary>Client: pong. Returns true if handled.</summary>
        private bool ClientHandlePong(Packet packet)
        {
            if (packet.Type != PacketPong)
                return false;
            if (packet.Payload == null || packet.Payload.Length != 8)
                return true;
            long sent = BitConverter.ToInt64(packet.Payload, 0);
            double rtt = (_clock.ElapsedTicks - sent) * 1000.0 / Stopwatch.Frequency;
            if (rtt >= 0 && rtt < 10000)
            {
                lock (_stateLock)
                    _pingMs = _pingMs < 0 ? rtt : 0.7 * _pingMs + 0.3 * rtt;
            }
            return true;
        }

        private void SendPing()
        {
            if (_connected && _hostEndpoint != null)
                SendPacket(_hostEndpoint, PacketPing, _localId, 0, BitConverter.GetBytes(_clock.ElapsedTicks));
        }

        private const uint CueStreamId = 0xFFFFFFFF;

        /// <summary>
        /// Plays a short sound through your chosen output device at your output volume. It goes
        /// through the same path as voice, so the echo canceller knows about it and your
        /// microphone won't send it to the room.
        /// </summary>
        public void PlayCue(byte[] pcm)
        {
            if (!_running || pcm == null || pcm.Length == 0 || HeadlessForTests)
                return;
            PlaybackStream stream;
            lock (_stateLock)
                _playbackStreams.TryGetValue(CueStreamId, out stream);
            if (stream == null)
                stream = OpenPlaybackStream(CueStreamId); // Opened with no lock held (see OpenPlaybackStream).
            if (stream == null)
                return;
            stream.SetGain(_masterVolumePercent / 100.0f);
            stream.PlayClip(pcm);
        }

        private void ResetPresence()
        {
            lock (_stateLock)
            {
                _memberFlags.Clear();
                _pingMs = -1.0;
            }
            _noiseResetPending = true;
        }
    }
}
