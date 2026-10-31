using System;
using System.Threading;

namespace Maela
{
    /// <summary>
    /// Shared timeline of everything mæla is sending to the speakers.
    ///
    /// Positions are absolute sample indices on the microphone's clock: every captured
    /// microphone frame advances <see cref="CaptureClock"/>, and every playback stream writes
    /// the exact (gain-adjusted) samples it hands to WinMM at the position where they will
    /// start playing relative to that clock. Several remote speakers are mixed together, so
    /// the echo canceller sees one reference signal that matches what leaves the speakers.
    /// </summary>
    internal sealed class FarEndReference
    {
        private const int RingBits = 17; // 131072 samples, about 2.7 seconds at 48 kHz.
        private const int RingSize = 1 << RingBits;
        private const int RingMask = RingSize - 1;

        private readonly object _lock = new object();
        private readonly float[] _ring = new float[RingSize];
        private long _writtenEnd;
        private long _captureClock;

        /// <summary>Total number of microphone samples captured so far.</summary>
        public long CaptureClock
        {
            get { return Interlocked.Read(ref _captureClock); }
        }

        /// <summary>Advances the capture clock and returns the new end position.</summary>
        public long AdvanceCapture(int samples)
        {
            return Interlocked.Add(ref _captureClock, samples);
        }

        /// <summary>Mixes 16-bit little-endian PCM into the timeline at <paramref name="position"/>.</summary>
        public void Write(long position, byte[] pcm16)
        {
            if (pcm16 == null || position < 0)
                return;

            int count = pcm16.Length / 2;
            lock (_lock)
            {
                long end = position + count;
                ClearUpToLocked(end);

                long oldest = _writtenEnd - RingSize;
                int i;
                for (i = 0; i < count; i++)
                {
                    long p = position + i;
                    if (p < oldest)
                        continue;
                    short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                    _ring[(int)(p & RingMask)] += sample;
                }
            }
        }

        /// <summary>Reads <paramref name="count"/> samples starting at <paramref name="position"/>. Unknown samples read as silence.</summary>
        public void Read(long position, float[] destination, int offset, int count)
        {
            lock (_lock)
            {
                long oldest = _writtenEnd - RingSize;
                int i;
                for (i = 0; i < count; i++)
                {
                    long p = position + i;
                    destination[offset + i] = (p < 0 || p < oldest || p >= _writtenEnd) ? 0.0f : _ring[(int)(p & RingMask)];
                }
            }
        }

        private void ClearUpToLocked(long end)
        {
            if (end <= _writtenEnd)
                return;

            long start = _writtenEnd;
            if (end - start > RingSize)
                start = end - RingSize;

            long p;
            for (p = start; p < end; p++)
                _ring[(int)(p & RingMask)] = 0.0f;
            _writtenEnd = end;
        }
    }

    /// <summary>
    /// Acoustic echo canceller for 48 kHz mono 16-bit voice.
    ///
    /// Pipeline per 256-sample block:
    ///   1. Delay estimation – correlates near-end and far-end energy envelopes to find the
    ///      bulk speaker-to-microphone delay (output buffering, driver latency, air path).
    ///   2. Linear echo removal – a partitioned-block frequency-domain adaptive filter (MDF
    ///      style) with an ~85 ms tail placed around the estimated delay. A background filter
    ///      adapts continuously and is only promoted to the foreground filter when it is
    ///      measurably better, which keeps cancellation stable during double-talk.
    ///   3. Residual echo suppression – a spectral Wiener post-filter driven by the linear
    ///      echo estimate and an adaptively estimated leakage factor.
    ///
    /// Added latency is exactly one 10 ms frame (480 samples).
    /// The per-block audio path does not allocate after construction.
    /// </summary>
    internal sealed class EchoCanceller
    {
        public const int BlockSize = 256;

        private const int FftSize = BlockSize * 2;
        private const int Bins = FftSize / 2 + 1;
        private const int Partitions = 16;          // 16 x 256 samples = 85 ms adaptive tail.
        private const int PreDelayBlocks = 2;       // Keep a little of the tail before the estimated delay.
        private const int MaxDelayBlocks = 128;     // Search up to ~680 ms of bulk delay.
        private const int OutputPrefill = 224;      // 224 + 256 overlap-add = one 480-sample frame of latency.
        private const int FifoCapacity = 8192;

        private const float FarActiveEnergy = 120.0f * 120.0f;   // ~ -49 dBFS mean square.
        private const float StepSize = 0.6f;
        private const float MinSuppressionGain = 0.04f;           // ~ -28 dB.

        private readonly FarEndReference _far;
        private readonly Fft _fft = new Fft(FftSize);

        // FIFOs between WinMM frame size and the processing block size.
        private readonly float[] _inFifo = new float[FifoCapacity];
        private readonly float[] _outFifo = new float[FifoCapacity];
        private int _inCount;
        private int _outCount;
        private long _inEndPosition = -1;

        // Adaptive filter state. Partition 0 holds the newest reference spectrum.
        private readonly float[][] _xRe = NewMatrix(Partitions, Bins);
        private readonly float[][] _xIm = NewMatrix(Partitions, Bins);
        private readonly float[][] _wfRe = NewMatrix(Partitions, Bins);
        private readonly float[][] _wfIm = NewMatrix(Partitions, Bins);
        private readonly float[][] _wbRe = NewMatrix(Partitions, Bins);
        private readonly float[][] _wbIm = NewMatrix(Partitions, Bins);
        private readonly float[] _xPower = new float[Bins];
        private readonly float[] _xTime = new float[FftSize];
        private int _xHead;
        private int _constrainIndex;
        private float _foregroundError;
        private float _backgroundError;
        private float _nearEnergy;
        private int _backgroundBetterCount;

        // Residual echo post-filter.
        private readonly float[] _window = new float[FftSize];
        private readonly float[] _errorHistory = new float[FftSize];
        private readonly float[] _echoHistory = new float[FftSize];
        private readonly float[] _overlap = new float[BlockSize];
        private readonly float[] _echoPowerAvg = new float[Bins];
        private readonly float[] _errorPowerAvg = new float[Bins];
        private readonly float[] _priorSnr = new float[Bins];
        private float _leakCross;
        private float _leakAuto;
        private float _leak = 0.25f;

        // Bulk delay estimator.
        private readonly float[] _farEnvelope = new float[MaxDelayBlocks];
        private readonly float[] _correlation = new float[MaxDelayBlocks];
        private int _farEnvelopeHead;
        private int _farActiveBlocks;
        private readonly bool[] _farActiveHistory = new bool[MaxDelayBlocks];
        private float _nearMean;
        private float _farMean;
        private float _nearVariance;
        private float _farVariance;
        private int _delayUpdates;
        private int _delayBlocks;
        private bool _haveDelay;
        private int _delayCandidate = -1;
        private int _delayCandidateCount;

        // Scratch buffers.
        private readonly float[] _near = new float[BlockSize];
        private readonly float[] _reference = new float[BlockSize];
        private readonly float[] _rawFar = new float[BlockSize];
        private readonly float[] _outBlock = new float[BlockSize];
        private readonly float[] _re = new float[FftSize];
        private readonly float[] _im = new float[FftSize];
        private readonly float[] _re2 = new float[FftSize];
        private readonly float[] _im2 = new float[FftSize];
        private readonly float[] _echoForeground = new float[BlockSize];
        private readonly float[] _errorForeground = new float[BlockSize];
        private readonly float[] _errorBackground = new float[BlockSize];
        private readonly float[] _eRe = new float[Bins];
        private readonly float[] _eIm = new float[Bins];

        private volatile int _reportedDelayMs = -1;
        private volatile float _reportedErle;

        public EchoCanceller(FarEndReference far)
        {
            if (far == null)
                throw new ArgumentNullException("far");
            _far = far;

            int i;
            for (i = 0; i < FftSize; i++)
                _window[i] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / FftSize));
            for (i = 0; i < Bins; i++)
                _xPower[i] = 1.0f;
        }

        /// <summary>Estimated speaker-to-microphone delay in milliseconds, or -1 before it is known.</summary>
        public int EstimatedDelayMs { get { return _reportedDelayMs; } }

        /// <summary>Smoothed echo return loss enhancement of the linear filter, in dB.</summary>
        public float ErleDb { get { return _reportedErle; } }

        /// <summary>
        /// Removes speaker echo from one microphone frame in place.
        /// <paramref name="endPosition"/> is the capture clock value at the end of this frame.
        /// </summary>
        public void Process(byte[] pcm16, long endPosition)
        {
            if (pcm16 == null)
                return;
            int count = pcm16.Length / 2;
            if (count == 0)
                return;
            if (count > FifoCapacity / 2)
                return;

            long startPosition = endPosition - count;
            if (startPosition != _inEndPosition)
                Resync(startPosition);

            int i;
            for (i = 0; i < count; i++)
                _inFifo[_inCount + i] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            _inCount += count;
            _inEndPosition = endPosition;

            while (_inCount >= BlockSize)
            {
                long blockPosition = _inEndPosition - _inCount;
                Array.Copy(_inFifo, 0, _near, 0, BlockSize);
                Array.Copy(_inFifo, BlockSize, _inFifo, 0, _inCount - BlockSize);
                _inCount -= BlockSize;

                ProcessBlock(blockPosition);

                if (_outCount + BlockSize <= FifoCapacity)
                {
                    Array.Copy(_outBlock, 0, _outFifo, _outCount, BlockSize);
                    _outCount += BlockSize;
                }
            }

            int available = Math.Min(count, _outCount);
            for (i = 0; i < count; i++)
            {
                float value = i < available ? _outFifo[i] : 0.0f;
                int sample = (int)Math.Round(value);
                if (sample > Int16.MaxValue)
                    sample = Int16.MaxValue;
                else if (sample < Int16.MinValue)
                    sample = Int16.MinValue;
                pcm16[i * 2] = (byte)(sample & 0xFF);
                pcm16[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
            Array.Copy(_outFifo, available, _outFifo, 0, _outCount - available);
            _outCount -= available;
        }

        /// <summary>Clears all adaptive state, for example after the audio devices change.</summary>
        public void Reset()
        {
            ClearFilters();
            Array.Clear(_farEnvelope, 0, _farEnvelope.Length);
            Array.Clear(_farActiveHistory, 0, _farActiveHistory.Length);
            Array.Clear(_correlation, 0, _correlation.Length);
            _farActiveBlocks = 0;
            _nearMean = 0.0f;
            _farMean = 0.0f;
            _nearVariance = 0.0f;
            _farVariance = 0.0f;
            _delayUpdates = 0;
            _delayBlocks = 0;
            _haveDelay = false;
            _delayCandidate = -1;
            _delayCandidateCount = 0;
            _reportedDelayMs = -1;
            _reportedErle = 0.0f;
            _leak = 0.25f;
            _leakCross = 0.0f;
            _leakAuto = 0.0f;
            _inEndPosition = -1;
        }

        private void Resync(long startPosition)
        {
            _inCount = 0;
            Array.Clear(_outFifo, 0, _outFifo.Length);
            _outCount = OutputPrefill;
            Array.Clear(_overlap, 0, _overlap.Length);
            Array.Clear(_errorHistory, 0, _errorHistory.Length);
            Array.Clear(_echoHistory, 0, _echoHistory.Length);
            _inEndPosition = startPosition;
            RebuildReferenceHistory(startPosition);
        }

        private void ClearFilters()
        {
            int m;
            for (m = 0; m < Partitions; m++)
            {
                Array.Clear(_wfRe[m], 0, Bins);
                Array.Clear(_wfIm[m], 0, Bins);
                Array.Clear(_wbRe[m], 0, Bins);
                Array.Clear(_wbIm[m], 0, Bins);
            }
            Array.Clear(_priorSnr, 0, _priorSnr.Length);
            Array.Clear(_echoPowerAvg, 0, _echoPowerAvg.Length);
            Array.Clear(_errorPowerAvg, 0, _errorPowerAvg.Length);
            _foregroundError = 0.0f;
            _backgroundError = 0.0f;
            _nearEnergy = 0.0f;
            _backgroundBetterCount = 0;
        }

        private long ReferenceOffset
        {
            get
            {
                int blocks = _delayBlocks - PreDelayBlocks;
                if (blocks < 0)
                    blocks = 0;
                return (long)blocks * BlockSize;
            }
        }

        private void ProcessBlock(long position)
        {
            int i;
            int k;
            int m;

            // 1. Delay estimation on the unshifted reference.
            _far.Read(position, _rawFar, 0, BlockSize);
            UpdateDelayEstimate();

            // 2. Reference block aligned to the estimated delay.
            _far.Read(position - ReferenceOffset, _reference, 0, BlockSize);
            float farEnergy = 0.0f;
            for (i = 0; i < BlockSize; i++)
                farEnergy += _reference[i] * _reference[i];
            farEnergy /= BlockSize;
            bool farActive = farEnergy > FarActiveEnergy;

            PushReferenceBlock(_reference);

            // Echo estimates from both filters.
            FilterOutput(_wfRe, _wfIm, _echoForeground);
            FilterOutput(_wbRe, _wbIm, _errorBackground); // Temporarily holds the background echo.

            float nearEnergy = 0.0f;
            float foregroundError = 0.0f;
            float backgroundError = 0.0f;
            for (i = 0; i < BlockSize; i++)
            {
                float d = _near[i];
                float ef = d - _echoForeground[i];
                float eb = d - _errorBackground[i];
                _errorForeground[i] = ef;
                _errorBackground[i] = eb;
                nearEnergy += d * d;
                foregroundError += ef * ef;
                backgroundError += eb * eb;
            }

            // 3. Adapt the background filter while the far end is talking.
            if (farActive)
            {
                Array.Clear(_re, 0, FftSize);
                Array.Clear(_im, 0, FftSize);
                for (i = 0; i < BlockSize; i++)
                    _re[BlockSize + i] = _errorBackground[i];
                _fft.Forward(_re, _im);
                for (k = 0; k < Bins; k++)
                {
                    float norm = StepSize / (_xPower[k] * Partitions + 1.0e6f);
                    _eRe[k] = _re[k] * norm;
                    _eIm[k] = _im[k] * norm;
                }

                for (m = 0; m < Partitions; m++)
                {
                    float[] xr = _xRe[PartitionIndex(m)];
                    float[] xi = _xIm[PartitionIndex(m)];
                    float[] wr = _wbRe[m];
                    float[] wi = _wbIm[m];
                    for (k = 0; k < Bins; k++)
                    {
                        // W += conj(X) * E
                        wr[k] += xr[k] * _eRe[k] + xi[k] * _eIm[k];
                        wi[k] += xr[k] * _eIm[k] - xi[k] * _eRe[k];
                    }
                }

                ConstrainPartition(_wbRe[_constrainIndex], _wbIm[_constrainIndex]);
                _constrainIndex = (_constrainIndex + 1) % Partitions;
            }

            // 4. Foreground/background arbitration (two-path double-talk protection).
            const float smoothing = 0.65f;
            _foregroundError = smoothing * _foregroundError + (1.0f - smoothing) * foregroundError;
            _backgroundError = smoothing * _backgroundError + (1.0f - smoothing) * backgroundError;
            _nearEnergy = smoothing * _nearEnergy + (1.0f - smoothing) * nearEnergy;

            if (farActive)
            {
                if (_backgroundError < 0.72f * _foregroundError)
                {
                    _backgroundBetterCount++;
                    if (_backgroundBetterCount >= 3)
                    {
                        CopyFilter(_wbRe, _wbIm, _wfRe, _wfIm);
                        _foregroundError = _backgroundError;
                        _backgroundBetterCount = 0;
                    }
                }
                else
                {
                    _backgroundBetterCount = 0;
                }

                if (_backgroundError > 4.0f * _foregroundError && _backgroundError > _nearEnergy)
                {
                    // Background diverged (usually double-talk). Restart it from the good filter.
                    CopyFilter(_wfRe, _wfIm, _wbRe, _wbIm);
                    _backgroundError = _foregroundError;
                }
            }

            if (_foregroundError > 2.5f * _nearEnergy + 1.0f)
            {
                // The foreground filter is making things worse; drop it entirely.
                int p;
                for (p = 0; p < Partitions; p++)
                {
                    Array.Clear(_wfRe[p], 0, Bins);
                    Array.Clear(_wfIm[p], 0, Bins);
                }
                for (i = 0; i < BlockSize; i++)
                {
                    _errorForeground[i] = _near[i];
                    _echoForeground[i] = 0.0f;
                }
                _foregroundError = _nearEnergy;
            }

            if (farActive && _nearEnergy > 1.0f)
            {
                float erle = (float)(10.0 * Math.Log10((_nearEnergy + 1.0f) / (_foregroundError + 1.0f)));
                _reportedErle = 0.95f * _reportedErle + 0.05f * erle;
            }

            // 5. Residual echo suppression.
            PostFilter();
        }

        private int PartitionIndex(int age)
        {
            return (_xHead + age) % Partitions;
        }

        private void PushReferenceBlock(float[] block)
        {
            Array.Copy(_xTime, BlockSize, _xTime, 0, BlockSize);
            Array.Copy(block, 0, _xTime, BlockSize, BlockSize);

            Array.Copy(_xTime, 0, _re, 0, FftSize);
            Array.Clear(_im, 0, FftSize);
            _fft.Forward(_re, _im);

            _xHead = (_xHead + Partitions - 1) % Partitions;
            float[] xr = _xRe[_xHead];
            float[] xi = _xIm[_xHead];
            int k;
            for (k = 0; k < Bins; k++)
            {
                xr[k] = _re[k];
                xi[k] = _im[k];
                float power = _re[k] * _re[k] + _im[k] * _im[k];
                _xPower[k] = 0.92f * _xPower[k] + 0.08f * power;
            }
        }

        /// <summary>Re-reads the last <see cref="Partitions"/> reference blocks after a resync or delay change.</summary>
        private void RebuildReferenceHistory(long nextBlockPosition)
        {
            long offset = ReferenceOffset;
            Array.Clear(_xTime, 0, FftSize);
            int b;
            for (b = Partitions; b >= 1; b--)
            {
                _far.Read(nextBlockPosition - offset - (long)b * BlockSize, _reference, 0, BlockSize);
                PushReferenceBlock(_reference);
            }
        }

        private void FilterOutput(float[][] wRe, float[][] wIm, float[] output)
        {
            int k;
            int m;
            Array.Clear(_re2, 0, Bins);
            Array.Clear(_im2, 0, Bins);
            for (m = 0; m < Partitions; m++)
            {
                float[] xr = _xRe[PartitionIndex(m)];
                float[] xi = _xIm[PartitionIndex(m)];
                float[] wr = wRe[m];
                float[] wi = wIm[m];
                for (k = 0; k < Bins; k++)
                {
                    _re2[k] += wr[k] * xr[k] - wi[k] * xi[k];
                    _im2[k] += wr[k] * xi[k] + wi[k] * xr[k];
                }
            }

            ExpandHermitian(_re2, _im2);
            _fft.Inverse(_re2, _im2);
            Array.Copy(_re2, BlockSize, output, 0, BlockSize);
        }

        private void ConstrainPartition(float[] wr, float[] wi)
        {
            Array.Copy(wr, 0, _re2, 0, Bins);
            Array.Copy(wi, 0, _im2, 0, Bins);
            ExpandHermitian(_re2, _im2);
            _fft.Inverse(_re2, _im2);
            int i;
            for (i = BlockSize; i < FftSize; i++)
                _re2[i] = 0.0f;
            Array.Clear(_im2, 0, FftSize);
            _fft.Forward(_re2, _im2);
            Array.Copy(_re2, 0, wr, 0, Bins);
            Array.Copy(_im2, 0, wi, 0, Bins);
        }

        private static void ExpandHermitian(float[] re, float[] im)
        {
            im[0] = 0.0f;
            im[FftSize / 2] = 0.0f;
            int k;
            for (k = 1; k < FftSize / 2; k++)
            {
                re[FftSize - k] = re[k];
                im[FftSize - k] = -im[k];
            }
        }

        private static void CopyFilter(float[][] srcRe, float[][] srcIm, float[][] dstRe, float[][] dstIm)
        {
            int m;
            for (m = 0; m < Partitions; m++)
            {
                Array.Copy(srcRe[m], dstRe[m], Bins);
                Array.Copy(srcIm[m], dstIm[m], Bins);
            }
        }

        private void PostFilter()
        {
            int i;
            int k;

            Array.Copy(_errorHistory, BlockSize, _errorHistory, 0, BlockSize);
            Array.Copy(_errorForeground, 0, _errorHistory, BlockSize, BlockSize);
            Array.Copy(_echoHistory, BlockSize, _echoHistory, 0, BlockSize);
            Array.Copy(_echoForeground, 0, _echoHistory, BlockSize, BlockSize);

            for (i = 0; i < FftSize; i++)
            {
                _re[i] = _errorHistory[i] * _window[i];
                _im[i] = 0.0f;
                _re2[i] = _echoHistory[i] * _window[i];
                _im2[i] = 0.0f;
            }
            _fft.Forward(_re, _im);
            _fft.Forward(_re2, _im2);

            // Leakage: how much of the linear echo estimate is still left in the residual,
            // estimated from correlated power fluctuations so near-end speech does not bias it.
            float cross = 0.0f;
            float auto = 0.0f;
            for (k = 0; k < Bins; k++)
            {
                float errorPower = _re[k] * _re[k] + _im[k] * _im[k];
                float echoPower = _re2[k] * _re2[k] + _im2[k] * _im2[k];
                _errorPowerAvg[k] = 0.9f * _errorPowerAvg[k] + 0.1f * errorPower;
                _echoPowerAvg[k] = 0.9f * _echoPowerAvg[k] + 0.1f * echoPower;
                float de = errorPower - _errorPowerAvg[k];
                float dy = echoPower - _echoPowerAvg[k];
                cross += de * dy;
                auto += dy * dy;
            }

            float echoTotal = 0.0f;
            for (k = 0; k < Bins; k++)
                echoTotal += _echoPowerAvg[k];
            if (echoTotal > 1.0e4f * FftSize)
            {
                _leakCross = 0.95f * _leakCross + 0.05f * cross;
                _leakAuto = 0.95f * _leakAuto + 0.05f * auto;
                if (_leakAuto > 1.0f)
                {
                    float leak = _leakCross / _leakAuto;
                    if (leak < 0.005f) leak = 0.005f;
                    if (leak > 0.5f) leak = 0.5f;
                    _leak = leak;
                }
            }

            for (k = 0; k < Bins; k++)
            {
                float errorPower = _re[k] * _re[k] + _im[k] * _im[k];
                float echoPower = _re2[k] * _re2[k] + _im2[k] * _im2[k];
                float residual = 2.0f * _leak * echoPower + 1.0f;
                float posterior = errorPower / residual;
                float instant = posterior - 1.0f;
                if (instant < 0.0f)
                    instant = 0.0f;
                float prior = 0.6f * _priorSnr[k] + 0.4f * instant;
                float gain = prior / (1.0f + prior);
                if (gain < MinSuppressionGain)
                    gain = MinSuppressionGain;
                if (echoPower < 1.0f)
                    gain = 1.0f;
                _priorSnr[k] = gain * gain * posterior;

                _re[k] *= gain;
                _im[k] *= gain;
            }

            ExpandHermitian(_re, _im);
            _fft.Inverse(_re, _im);
            for (i = 0; i < BlockSize; i++)
            {
                _outBlock[i] = _overlap[i] + _re[i] * _window[i];
                _overlap[i] = _re[BlockSize + i] * _window[BlockSize + i];
            }
        }

        private void UpdateDelayEstimate()
        {
            int i;
            float nearEnergy = 0.0f;
            float farEnergy = 0.0f;
            for (i = 0; i < BlockSize; i++)
            {
                nearEnergy += _near[i] * _near[i];
                farEnergy += _rawFar[i] * _rawFar[i];
            }
            nearEnergy /= BlockSize;
            farEnergy /= BlockSize;

            float nearEnvelope = (float)Math.Log(nearEnergy + 400.0f);
            float farEnvelope = (float)Math.Log(farEnergy + 400.0f);
            bool active = farEnergy > FarActiveEnergy;

            _farEnvelopeHead = (_farEnvelopeHead + MaxDelayBlocks - 1) % MaxDelayBlocks;
            if (_farActiveHistory[_farEnvelopeHead])
                _farActiveBlocks--;
            _farEnvelope[_farEnvelopeHead] = farEnvelope;
            _farActiveHistory[_farEnvelopeHead] = active;
            if (active)
                _farActiveBlocks++;

            if (_farActiveBlocks < 8)
                return;

            const float meanRate = 0.01f;
            const float decay = 0.996f;
            _nearMean += meanRate * (nearEnvelope - _nearMean);
            _farMean += meanRate * (farEnvelope - _farMean);
            float dn = nearEnvelope - _nearMean;
            float df = farEnvelope - _farMean;
            _nearVariance = decay * _nearVariance + dn * dn;
            _farVariance = decay * _farVariance + df * df;

            int lag;
            for (lag = 0; lag < MaxDelayBlocks; lag++)
            {
                float f = _farEnvelope[(_farEnvelopeHead + lag) % MaxDelayBlocks] - _farMean;
                _correlation[lag] = decay * _correlation[lag] + dn * f;
            }

            _delayUpdates++;
            if ((_delayUpdates & 7) != 0 || _delayUpdates < 96)
                return;

            int best = 0;
            float bestValue = Single.MinValue;
            for (lag = 0; lag < MaxDelayBlocks; lag++)
            {
                if (_correlation[lag] > bestValue)
                {
                    bestValue = _correlation[lag];
                    best = lag;
                }
            }

            float denominator = (float)Math.Sqrt(_nearVariance * _farVariance) + 1.0e-6f;
            float normalized = bestValue / denominator;
            if (normalized < 0.3f)
                return;

            if (best == _delayCandidate)
                _delayCandidateCount++;
            else
            {
                _delayCandidate = best;
                _delayCandidateCount = 1;
            }

            int required = _haveDelay ? 8 : 3;
            if (_delayCandidateCount < required)
                return;
            if (_haveDelay && Math.Abs(best - _delayBlocks) <= 1)
                return;

            ApplyDelay(best);
        }

        private void ApplyDelay(int delayBlocks)
        {
            int oldOffsetBlocks = (int)(ReferenceOffset / BlockSize);
            _delayBlocks = delayBlocks;
            _haveDelay = true;
            int newOffsetBlocks = (int)(ReferenceOffset / BlockSize);
            int shift = newOffsetBlocks - oldOffsetBlocks;

            // The echo that partition m used to model now lives in partition m - shift.
            if (shift != 0)
            {
                ShiftPartitions(_wfRe, _wfIm, shift);
                ShiftPartitions(_wbRe, _wbIm, shift);
            }

            RebuildReferenceHistory(_inEndPosition - _inCount);
            _reportedDelayMs = (int)((long)delayBlocks * BlockSize * 1000 / 48000);
        }

        private static void ShiftPartitions(float[][] re, float[][] im, int shift)
        {
            float[][] oldRe = new float[Partitions][];
            float[][] oldIm = new float[Partitions][];
            int m;
            for (m = 0; m < Partitions; m++)
            {
                oldRe[m] = re[m];
                oldIm[m] = im[m];
            }
            for (m = 0; m < Partitions; m++)
            {
                int source = m + shift;
                if (source >= 0 && source < Partitions)
                {
                    re[m] = oldRe[source];
                    im[m] = oldIm[source];
                    oldRe[source] = null;
                    oldIm[source] = null;
                }
                else
                {
                    re[m] = null;
                    im[m] = null;
                }
            }
            // Re-use the arrays that were dropped, cleared, for the empty slots.
            int spare = 0;
            for (m = 0; m < Partitions; m++)
            {
                if (re[m] != null)
                    continue;
                while (oldRe[spare] == null)
                    spare++;
                re[m] = oldRe[spare];
                im[m] = oldIm[spare];
                Array.Clear(re[m], 0, Bins);
                Array.Clear(im[m], 0, Bins);
                oldRe[spare] = null;
                oldIm[spare] = null;
            }
        }

        private static float[][] NewMatrix(int rows, int columns)
        {
            float[][] result = new float[rows][];
            int i;
            for (i = 0; i < rows; i++)
                result[i] = new float[columns];
            return result;
        }
    }

    /// <summary>Small in-place radix-2 complex FFT.</summary>
    internal sealed class Fft
    {
        private readonly int _size;
        private readonly int[] _bitReverse;
        private readonly float[] _cos;
        private readonly float[] _sin;

        public Fft(int size)
        {
            if (size < 2 || (size & (size - 1)) != 0)
                throw new ArgumentException("FFT size must be a power of two.", "size");
            _size = size;
            _bitReverse = new int[size];
            _cos = new float[size / 2];
            _sin = new float[size / 2];

            int bits = 0;
            while ((1 << bits) < size)
                bits++;
            int i;
            for (i = 0; i < size; i++)
            {
                int reversed = 0;
                int b;
                for (b = 0; b < bits; b++)
                {
                    if ((i & (1 << b)) != 0)
                        reversed |= 1 << (bits - 1 - b);
                }
                _bitReverse[i] = reversed;
            }
            for (i = 0; i < size / 2; i++)
            {
                double angle = -2.0 * Math.PI * i / size;
                _cos[i] = (float)Math.Cos(angle);
                _sin[i] = (float)Math.Sin(angle);
            }
        }

        public void Forward(float[] re, float[] im)
        {
            Transform(re, im, false);
        }

        /// <summary>Inverse transform, scaled by 1/N.</summary>
        public void Inverse(float[] re, float[] im)
        {
            Transform(re, im, true);
            float scale = 1.0f / _size;
            int i;
            for (i = 0; i < _size; i++)
            {
                re[i] *= scale;
                im[i] *= scale;
            }
        }

        private void Transform(float[] re, float[] im, bool inverse)
        {
            int n = _size;
            int i;
            for (i = 0; i < n; i++)
            {
                int j = _bitReverse[i];
                if (j > i)
                {
                    float tr = re[i]; re[i] = re[j]; re[j] = tr;
                    float ti = im[i]; im[i] = im[j]; im[j] = ti;
                }
            }

            int length;
            for (length = 2; length <= n; length <<= 1)
            {
                int half = length >> 1;
                int step = n / length;
                int start;
                for (start = 0; start < n; start += length)
                {
                    int k;
                    for (k = 0; k < half; k++)
                    {
                        float wr = _cos[k * step];
                        float wi = inverse ? -_sin[k * step] : _sin[k * step];
                        int a = start + k;
                        int b = a + half;
                        float xr = re[b] * wr - im[b] * wi;
                        float xi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - xr;
                        im[b] = im[a] - xi;
                        re[a] += xr;
                        im[a] += xi;
                    }
                }
            }
        }
    }
}
