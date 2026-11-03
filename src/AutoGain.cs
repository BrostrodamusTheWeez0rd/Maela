using System;

namespace Maela
{
    /// <summary>
    /// Automatic microphone level (AGC), so quiet mics are heard clearly and loud ones don't blast.
    ///
    /// Per 10 ms frame:
    ///   1. Decide whether you're talking: the frame must be well above a tracked noise floor.
    ///   2. While talking, follow your speech level and steer gain towards a normal speaking
    ///      level (about -20 dBFS RMS). Gain rises slowly (about 6 dB per second) and falls
    ///      quickly (about 30 dB per second), so a sudden shout is tamed straight away.
    ///   3. Between words the gain is held, so background noise is never pumped up.
    ///   4. A soft limiter keeps peaks below full scale, so boosted speech never clips.
    /// Gain is limited to between -12 dB and +24 dB. No added latency, no allocations.
    /// </summary>
    internal sealed class AutoGain
    {
        private const double TargetDb = -20.0;
        private const double MaxGainDb = 24.0;
        private const double MinGainDb = -12.0;
        private const double RiseDbPerFrame = 0.06;   // 6 dB/s at 100 frames/s
        private const double FallDbPerFrame = 0.30;   // 30 dB/s
        private const double SpeechAboveFloorDb = 9.0;
        private const double AbsoluteSpeechDb = -62.0;
        private const double LimitStart = 0.80;       // soft limiting begins at about -2 dBFS

        private double _gainDb;
        private double _levelDb = -40.0;
        private double _floorDb = -70.0;
        private double _previousGain = 1.0;
        private readonly VoicingDetector _voicing = new VoicingDetector();

        /// <summary>Current gain in dB (for display and tests).</summary>
        public double GainDb { get { return _gainDb; } }

        public void Reset()
        {
            _gainDb = 0.0;
            _levelDb = -40.0;
            _floorDb = -70.0;
            _previousGain = 1.0;
        }

        /// <summary>Adjusts 16-bit little-endian PCM in place.</summary>
        public void Process(byte[] pcm16)
        {
            int count = pcm16.Length / 2;
            if (count == 0)
                return;

            double energy = 0.0;
            int i;
            for (i = 0; i < count; i++)
            {
                double v = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                energy += v * v;
            }
            double rmsDb = 10.0 * Math.Log10(energy / count / (32768.0 * 32768.0) + 1e-12);

            // Noise floor: falls quickly to quiet frames, rises slowly.
            if (rmsDb < _floorDb)
                _floorDb = 0.8 * _floorDb + 0.2 * rmsDb;
            else
                _floorDb += 0.02;
            if (_floorDb < -90.0) _floorDb = -90.0;

            // Adapt only to a real voice (loud and pitched), so key clicks and bumps can't
            // push the gain around.
            bool voiced = _voicing.Analyze(pcm16);
            bool speech = voiced && rmsDb > Math.Max(_floorDb + SpeechAboveFloorDb, AbsoluteSpeechDb);
            if (speech)
                Adapt(rmsDb, 1.0);

            double gain = Math.Pow(10.0, _gainDb / 20.0);
            double step = (gain - _previousGain) / count; // Ramp across the frame: no zipper noise.
            double g = _previousGain;
            for (i = 0; i < count; i++)
            {
                g += step;
                double v = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8)) / 32768.0 * g;
                double a = Math.Abs(v);
                if (a > LimitStart)
                {
                    // Smoothly approach (never reach) full scale.
                    double over = (a - LimitStart) / (1.0 - LimitStart);
                    a = LimitStart + (1.0 - LimitStart) * (over / (1.0 + over));
                    v = v < 0 ? -a : a;
                }
                int sample = (int)Math.Round(v * 32767.0);
                pcm16[i * 2] = (byte)(sample & 0xFF);
                pcm16[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
            _previousGain = gain;
        }

        /// <summary>
        /// For the encoder, after its mic gate: adapts only when the gate says this is speech, so
        /// the gate, not a second guess, decides what the gain learns from. Any frame length.
        /// </summary>
        public void Process(short[] samples, int count, bool speech)
        {
            if (count == 0)
                return;
            double energy = 0.0;
            int i;
            for (i = 0; i < count; i++)
                energy += (double)samples[i] * samples[i];
            if (speech)
                Adapt(10.0 * Math.Log10(energy / count / (32768.0 * 32768.0) + 1e-12), count / 480.0);

            double gain = Math.Pow(10.0, _gainDb / 20.0);
            double step = (gain - _previousGain) / count;
            double g = _previousGain;
            for (i = 0; i < count; i++)
            {
                g += step;
                samples[i] = (short)Math.Round(Limit(samples[i] / 32768.0 * g) * 32767.0);
            }
            _previousGain = gain;
        }

        /// <summary>Follows the speech level and steers the gain. frames: how many 10 ms this covers.</summary>
        private void Adapt(double rmsDb, double frames)
        {
            // Follow peaks of speech quickly, let the level relax slowly.
            double rate = rmsDb > _levelDb ? 0.25 : 0.04;
            _levelDb += (1 - Math.Pow(1 - rate, frames)) * (rmsDb - _levelDb);

            double desired = Math.Max(MinGainDb, Math.Min(MaxGainDb, TargetDb - _levelDb));
            if (desired > _gainDb)
                _gainDb = Math.Min(desired, _gainDb + RiseDbPerFrame * frames);
            else
                _gainDb = Math.Max(desired, _gainDb - FallDbPerFrame * frames);
        }

        /// <summary>Smoothly approaches (never reaches) full scale above about -2 dBFS.</summary>
        private static double Limit(double v)
        {
            double a = Math.Abs(v);
            if (a <= LimitStart)
                return v;
            double over = (a - LimitStart) / (1.0 - LimitStart);
            a = LimitStart + (1.0 - LimitStart) * (over / (1.0 + over));
            return v < 0 ? -a : a;
        }
    }
}
