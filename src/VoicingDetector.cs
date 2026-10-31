using System;

namespace Maela
{
    /// <summary>
    /// Tells voiced speech apart from other loud sounds (key clicks, taps, bumps). Voiced
    /// speech repeats with the pitch of your voice (70-400 Hz); clicks don't. Works on a
    /// 12 kHz copy of the last 40 ms using normalised autocorrelation. Cheap and allocation-free.
    /// </summary>
    internal sealed class VoicingDetector
    {
        private const int Decimation = 4;           // 48 kHz -> 12 kHz
        private const int History = 480;            // 40 ms at 12 kHz
        private const int Window = 240;             // correlate the newest 20 ms
        private const int MinLag = 30;              // 400 Hz
        private const int MaxLag = 171;             // 70 Hz
        private const double Threshold = 0.55;

        private readonly float[] _history = new float[History];
        private float _carry;
        private int _carryCount;
        private float _lowPass;

        /// <summary>Strength of the pitch found in the last analysis (0..1).</summary>
        public double LastCorrelation { get; private set; }

        public bool Analyze(byte[] pcm16)
        {
            int count = pcm16.Length / 2;
            int i;
            for (i = 0; i < count; i++)
                Add((short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8)));
            return Decide();
        }

        public bool Analyze(short[] samples, int count)
        {
            int i;
            for (i = 0; i < count; i++)
                Add(samples[i]);
            return Decide();
        }

        private void Add(short sample)
        {
            // Gentle low-pass (keeps the pitch region) then average groups of 4 samples.
            _lowPass += 0.35f * (sample - _lowPass);
            _carry += _lowPass;
            if (++_carryCount < Decimation)
                return;
            Array.Copy(_history, 1, _history, 0, History - 1);
            _history[History - 1] = _carry / Decimation;
            _carry = 0;
            _carryCount = 0;
        }

        private bool Decide()
        {
            int start = History - Window;
            double e0 = 0.0;
            int i;
            for (i = 0; i < Window; i++)
                e0 += (double)_history[start + i] * _history[start + i];
            double best = 0.0;
            if (e0 > 1.0)
            {
                int lag;
                for (lag = MinLag; lag <= MaxLag; lag++)
                {
                    double c = 0.0, el = 0.0;
                    for (i = 0; i < Window; i++)
                    {
                        double a = _history[start + i];
                        double b = _history[start + i - lag];
                        c += a * b;
                        el += b * b;
                    }
                    if (el > 1.0)
                    {
                        double r = c / Math.Sqrt(e0 * el);
                        if (r > best)
                            best = r;
                    }
                }
            }
            LastCorrelation = best;
            return best >= Threshold;
        }
    }
}
