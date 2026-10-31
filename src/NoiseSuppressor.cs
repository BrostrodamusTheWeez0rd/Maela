using System;

namespace Maela
{
    /// <summary>
    /// Removes steady background noise (fans, hum, keyboard hiss) from 48 kHz mono speech.
    ///
    /// Short-time spectral processing (512-point FFT, 50% overlap, square-root Hann windows):
    ///   1. Track the noise floor in each frequency band. The estimate follows quiet levels
    ///      down quickly and creeps up slowly, so speech never gets mistaken for noise.
    ///   2. Apply a decision-directed Wiener gain per band, limited to -20 dB so voices stay
    ///      natural and the background is reduced rather than gated into silence.
    /// Adds exactly one 10 ms frame of latency. The per-block path does not allocate.
    /// </summary>
    internal sealed class NoiseSuppressor
    {
        private const int Block = 256;
        private const int FftSize = 512;
        private const int Bins = FftSize / 2 + 1;
        private const int OutputPrefill = 224;
        private const int FifoCapacity = 8192;
        private const float MinGain = 0.10f;          // -20 dB
        private const float NoiseRise = 1.0025f;      // ~ +2 dB per second
        private const float PriorSmoothing = 0.97f;
        private const float NoiseBias = 1.3f;

        private readonly Fft _fft = new Fft(FftSize);
        private readonly float[] _window = new float[FftSize];
        private readonly float[] _history = new float[FftSize];
        private readonly float[] _overlap = new float[Block];
        private readonly float[] _re = new float[FftSize];
        private readonly float[] _im = new float[FftSize];
        private readonly float[] _noise = new float[Bins];
        private readonly float[] _smoothed = new float[Bins];
        private readonly float[] _gain = new float[Bins];
        private readonly float[] _prevPost = new float[Bins];
        private readonly float[] _inFifo = new float[FifoCapacity];
        private readonly float[] _outFifo = new float[FifoCapacity];
        private int _inCount;
        private int _outCount;
        private int _blocks;

        public NoiseSuppressor()
        {
            int i;
            for (i = 0; i < FftSize; i++)
                _window[i] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / FftSize));
            Reset();
        }

        public void Reset()
        {
            Array.Clear(_history, 0, _history.Length);
            Array.Clear(_overlap, 0, _overlap.Length);
            Array.Clear(_noise, 0, _noise.Length);
            Array.Clear(_smoothed, 0, _smoothed.Length);
            Array.Clear(_prevPost, 0, _prevPost.Length);
            int k;
            for (k = 0; k < Bins; k++)
                _gain[k] = 1.0f;
            Array.Clear(_outFifo, 0, _outFifo.Length);
            _inCount = 0;
            _outCount = OutputPrefill;
            _blocks = 0;
        }

        /// <summary>Processes 16-bit little-endian PCM in place.</summary>
        public void Process(byte[] pcm16)
        {
            int count = pcm16.Length / 2;
            if (count == 0 || count > FifoCapacity / 2)
                return;

            int i;
            for (i = 0; i < count; i++)
                _inFifo[_inCount + i] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            _inCount += count;

            while (_inCount >= Block)
            {
                ProcessBlock();
                Array.Copy(_inFifo, Block, _inFifo, 0, _inCount - Block);
                _inCount -= Block;
            }

            int available = Math.Min(count, _outCount);
            for (i = 0; i < count; i++)
            {
                int sample = i < available ? (int)Math.Round(_outFifo[i]) : 0;
                if (sample > Int16.MaxValue) sample = Int16.MaxValue;
                else if (sample < Int16.MinValue) sample = Int16.MinValue;
                pcm16[i * 2] = (byte)(sample & 0xFF);
                pcm16[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
            Array.Copy(_outFifo, available, _outFifo, 0, _outCount - available);
            _outCount -= available;
        }

        private void ProcessBlock()
        {
            int i;
            int k;
            Array.Copy(_history, Block, _history, 0, Block);
            Array.Copy(_inFifo, 0, _history, Block, Block);
            for (i = 0; i < FftSize; i++)
            {
                _re[i] = _history[i] * _window[i];
                _im[i] = 0.0f;
            }
            _fft.Forward(_re, _im);
            _blocks++;

            for (k = 0; k < Bins; k++)
            {
                float power = _re[k] * _re[k] + _im[k] * _im[k];
                _smoothed[k] = _blocks == 1 ? power : 0.75f * _smoothed[k] + 0.25f * power;

                if (_blocks <= 25)
                {
                    // Warm-up: assume the first ~130 ms is mostly background.
                    _noise[k] = _blocks == 1 ? _smoothed[k] : 0.9f * _noise[k] + 0.1f * _smoothed[k];
                }
                else if (_smoothed[k] < _noise[k])
                {
                    _noise[k] = 0.85f * _noise[k] + 0.15f * _smoothed[k];
                }
                else
                {
                    _noise[k] *= NoiseRise;
                }

                // Tracking the quieter moments underestimates the average noise power;
                // correct for that bias (standard for minimum-tracking estimators).
                float noise = _noise[k] * NoiseBias + 1.0f;
                float post = power / noise;
                float instant = post - 1.0f;
                if (instant < 0.0f)
                    instant = 0.0f;
                float prior = PriorSmoothing * _gain[k] * _gain[k] * _prevPost[k] + (1.0f - PriorSmoothing) * instant;
                float gain = prior / (1.0f + prior);
                if (gain < MinGain)
                    gain = MinGain;
                _gain[k] = gain;
                _prevPost[k] = post;

                _re[k] *= gain;
                _im[k] *= gain;
            }

            _im[0] = 0.0f;
            _im[FftSize / 2] = 0.0f;
            for (k = 1; k < FftSize / 2; k++)
            {
                _re[FftSize - k] = _re[k];
                _im[FftSize - k] = -_im[k];
            }
            _fft.Inverse(_re, _im);

            if (_outCount + Block > FifoCapacity)
                return;
            for (i = 0; i < Block; i++)
            {
                _outFifo[_outCount + i] = _overlap[i] + _re[i] * _window[i];
                _overlap[i] = _re[Block + i] * _window[Block + i];
            }
            _outCount += Block;
        }
    }
}
