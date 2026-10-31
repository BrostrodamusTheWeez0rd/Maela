using System;
using System.Collections.Generic;
#if !MAELA_STUB_CODEC
using Concentus.Enums;
using Concentus.Structs;
#endif

namespace Maela
{
    /// <summary>One encoded 20 ms voice frame ready to send.</summary>
    internal sealed class EncodedVoice
    {
        public uint Sequence;
        public byte[] Data;
    }

    /// <summary>
    /// Turns 10 ms microphone frames into 20 ms Opus packets and decides when to send them.
    ///
    /// Silence detection: a frame counts as speech when it is clearly above the room's
    /// background level AND has the pitch of a voice (so typing, clicks and taps on their own
    /// never open the mic). Sending continues for a short hangover after speech so word endings
    /// aren't clipped, and the 60 ms before speech starts is sent too so first syllables aren't
    /// lost. Nothing is sent while quiet, which is most of the time in a voice chat.
    ///
    /// Every frame is encoded (even silent ones) so the encoder's state stays continuous; the
    /// sequence number advances every 20 ms so receivers can tell loss from silence.
    /// </summary>
    internal sealed class VoiceEncoder
    {
        public const int SampleRate = 48000;
        public const int FrameSamples = 960;          // 20 ms
        public const int MaxPacketBytes = 400;
        public const int Bitrate = 32000;

        private const int HangoverFrames = 20;        // 400 ms
        private const int PreRollFrames = 3;          // 60 ms sent ahead of the first voiced frame
        private const float SpeechAboveFloorDb = 10.0f;
        private const float AbsoluteSpeechDb = -55.0f;
        private const float FloorRiseDbPerFrame = 0.08f;

#if !MAELA_STUB_CODEC
        private readonly OpusEncoder _opus;
#endif
        private readonly short[] _frame = new short[FrameSamples];
        private readonly byte[] _packet = new byte[MaxPacketBytes];
        private int _fill;
        private uint _sequence;
        private float _noiseFloorDb = -60.0f;
        private int _hangover;
        private bool _open;
        private readonly Queue<EncodedVoice> _recent = new Queue<EncodedVoice>();
        private readonly VoicingDetector _voicing = new VoicingDetector();

        public VoiceEncoder()
        {
#if !MAELA_STUB_CODEC
            _opus = OpusEncoder.Create(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _opus.Bitrate = Bitrate;
            _opus.UseVBR = true;
            _opus.Complexity = 5;
            _opus.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            _opus.UseInbandFEC = true;   // Lets receivers rebuild a single lost packet.
            _opus.PacketLossPercent = 10;
            _opus.UseDTX = false;        // We do our own gating.
#endif
        }

        /// <summary>True while the gate is open (we are talking).</summary>
        public bool IsSpeaking { get { return _open; } }

        /// <summary>When true every frame is sent (used by tests; never set in the app).</summary>
        public bool AlwaysSend { get; set; }

        /// <summary>Adds 16-bit mono PCM (any length) and appends packets that should be sent.</summary>
        public void Push(byte[] pcm16, List<EncodedVoice> output)
        {
            int count = pcm16.Length / 2;
            int i;
            for (i = 0; i < count; i++)
            {
                _frame[_fill++] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                if (_fill == FrameSamples)
                {
                    _fill = 0;
                    EncodeFrame(output);
                }
            }
        }

        private void EncodeFrame(List<EncodedVoice> output)
        {
            uint sequence = ++_sequence;
            // Only a voice opens the mic: loud AND pitched. Key clicks, taps and bumps are
            // loud but have no pitch, so on their own they're never sent.
            bool loud = UpdateGate();
            bool voiced = _voicing.Analyze(_frame, FrameSamples);
            bool speech = loud && voiced;

#if !MAELA_STUB_CODEC
            int length = _opus.Encode(_frame, 0, FrameSamples, _packet, 0, _packet.Length);
#else
            int length = StubCodec.Encode(_frame, _packet);
#endif
            if (length <= 0)
                return;
            byte[] data = new byte[length];
            Buffer.BlockCopy(_packet, 0, data, 0, length);

            bool wasOpen = _open;
            if (speech || AlwaysSend)
            {
                _hangover = HangoverFrames;
                _open = true;
            }
            else if (_hangover > 0)
            {
                _hangover--;
            }
            else
            {
                _open = false;
            }

            if (_open)
            {
                if (!wasOpen)
                {
                    // Pre-roll: send the few frames before the first voiced one, so consonants
                    // that start a word ("s", "f", "t") aren't clipped.
                    foreach (EncodedVoice earlier in _recent)
                        output.Add(earlier);
                }
                output.Add(Packet(sequence, data));
                _recent.Clear();
            }
            else
            {
                _recent.Enqueue(Packet(sequence, data));
                while (_recent.Count > PreRollFrames)
                    _recent.Dequeue();
            }
        }

        private bool UpdateGate()
        {
            double energy = 0.0;
            int i;
            for (i = 0; i < FrameSamples; i++)
                energy += (double)_frame[i] * _frame[i];
            energy /= FrameSamples;
            float levelDb = (float)(10.0 * Math.Log10(energy / (32768.0 * 32768.0) + 1e-10));

            if (levelDb < _noiseFloorDb)
                _noiseFloorDb = 0.7f * _noiseFloorDb + 0.3f * levelDb;   // Fall quickly to quiet levels.
            else
                _noiseFloorDb = Math.Min(levelDb, _noiseFloorDb + FloorRiseDbPerFrame); // Rise slowly.
            if (_noiseFloorDb < -90.0f) _noiseFloorDb = -90.0f;
            if (_noiseFloorDb > -25.0f) _noiseFloorDb = -25.0f;

            return levelDb > Math.Max(_noiseFloorDb + SpeechAboveFloorDb, AbsoluteSpeechDb);
        }

        private static EncodedVoice Packet(uint sequence, byte[] data)
        {
            EncodedVoice v = new EncodedVoice();
            v.Sequence = sequence;
            v.Data = data;
            return v;
        }
    }

    /// <summary>
    /// Decodes one remote speaker's Opus packets into 20 ms PCM frames, hiding packet loss:
    /// a single missing packet is rebuilt from the next packet's forward error correction,
    /// two missing packets use concealment plus FEC. Larger gaps are pauses in speech (the
    /// sender stops transmitting while quiet) and are left silent.
    /// </summary>
    internal sealed class VoiceDecoder
    {
        private const int MaxConcealedGap = 2;

#if !MAELA_STUB_CODEC
        private readonly OpusDecoder _opus = OpusDecoder.Create(VoiceEncoder.SampleRate, 1);
#endif
        private readonly short[] _pcm = new short[VoiceEncoder.FrameSamples];
        private bool _haveSequence;
        private uint _lastSequence;

        /// <summary>Frames rebuilt from FEC or concealment so far (for diagnostics and tests).</summary>
        public int RecoveredFrames { get; private set; }

        /// <summary>Decodes a packet; appends 0-3 PCM frames (16-bit little-endian bytes) to output.</summary>
        public void Decode(uint sequence, byte[] data, List<byte[]> output)
        {
            if (data == null || data.Length == 0)
                return;

            if (_haveSequence)
            {
                int delta = unchecked((int)(sequence - _lastSequence));
                if (delta <= 0)
                    return; // Duplicate or arrived too late to play.

                int gap = delta - 1;
                if (gap >= 1 && gap <= MaxConcealedGap)
                {
                    int k;
                    for (k = 0; k < gap - 1; k++)
                    {
                        if (Run(null, false, output))   // Packet loss concealment.
                            RecoveredFrames++;
                    }
                    if (Run(data, true, output))        // FEC copy of the frame just before this one.
                        RecoveredFrames++;
                }
            }

            _haveSequence = true;
            _lastSequence = sequence;
            Run(data, false, output);
        }

        private bool Run(byte[] data, bool fec, List<byte[]> output)
        {
            int samples;
            try
            {
#if !MAELA_STUB_CODEC
                samples = data == null
                    ? _opus.Decode(null, 0, 0, _pcm, 0, VoiceEncoder.FrameSamples, false)
                    : _opus.Decode(data, 0, data.Length, _pcm, 0, VoiceEncoder.FrameSamples, fec);
#else
                samples = StubCodec.Decode(data, fec, _pcm);
#endif
            }
            catch
            {
                return false; // Corrupt packet: skip it rather than kill the stream.
            }
            if (samples <= 0)
                return false;

            byte[] frame = new byte[samples * 2];
            int i;
            for (i = 0; i < samples; i++)
            {
                frame[i * 2] = (byte)(_pcm[i] & 0xFF);
                frame[i * 2 + 1] = (byte)((_pcm[i] >> 8) & 0xFF);
            }
            output.Add(frame);
            return true;
        }
    }

#if MAELA_STUB_CODEC
    /// <summary>
    /// Test-only stand-in for Opus (compiled only with MAELA_STUB_CODEC): 8 kHz 8-bit,
    /// 160 bytes per frame. Lets network tests run without the codec.
    /// </summary>
    internal static class StubCodec
    {
        public static int Encode(short[] frame, byte[] packet)
        {
            int i;
            for (i = 0; i < 160; i++)
                packet[i] = (byte)((frame[i * 6] >> 8) + 128);
            return 160;
        }

        public static int Decode(byte[] data, bool fec, short[] pcm)
        {
            int i;
            for (i = 0; i < pcm.Length; i++)
                pcm[i] = data == null || data.Length < 160 ? (short)0 : (short)((data[i / 6] - 128) << 8);
            return pcm.Length;
        }
    }
#endif
}
