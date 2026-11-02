/* A C# port of RNNoise v0.1.1 (https://github.com/xiph/rnnoise), whose licence follows.

   Copyright (c) 2018, Gregor Richards
   Copyright (c) 2017, Mozilla
   Copyright (c) 2007-2017, Jean-Marc Valin
   Copyright (c) 2005-2017, Xiph.Org Foundation
   Copyright (c) 2003-2004, Mark Borgerding

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   - Neither the name of the Xiph.Org Foundation nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
   SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
   LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
   DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
   THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
   (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
   OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace Maela
{
    /// <summary>
    /// RNNoise v0.1.1 (Xiph.Org / Mozilla, BSD licence, see THIRD_PARTY_NOTICES), ported to C# from
    /// its float C code so it runs inside mæla. A small recurrent neural network, trained once by
    /// Xiph, decides how much of each of 22 frequency bands is voice; the rest is turned down.
    /// It also says how likely each 10 ms is to be speech, which the mic gate uses.
    ///
    /// Nothing here learns, stores or sends anything: the model is a fixed table of 87,569 numbers
    /// built into mæla, checked against its hash when it loads.
    ///
    /// 48 kHz mono, 480-sample (10 ms) frames, samples in 16-bit range. Output is one frame late.
    /// </summary>
    internal sealed class Rnnoise
    {
        public const int FrameSize = 480;
        private const int WindowSize = 2 * FrameSize;
        private const int FreqSize = FrameSize + 1;
        private const int PitchMinPeriod = 60;
        private const int PitchMaxPeriod = 768;
        private const int PitchFrameSize = 960;
        private const int PitchBufSize = PitchMaxPeriod + PitchFrameSize;
        private const int NbBands = 22;
        private const int CepsMem = 8;
        private const int NbDeltaCeps = 6;
        private const int NbFeatures = NbBands + 3 * NbDeltaCeps + 2;
        private const string ModelSha256 = "6830cdf7523140a04b95dbe1aa53145807163f5e413ac3c94493989a11c0a633";

        private static readonly short[] EBand5ms = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 78, 100 };

        // ----- Shared tables -----

        private static readonly float[] HalfWindow = new float[FrameSize];
        private static readonly float[] DctTable = new float[NbBands * NbBands];
        private static readonly Fft960 Fft = new Fft960();
        private static readonly Model Net;

        static Rnnoise()
        {
            for (int i = 0; i < FrameSize; i++)
            {
                double s = Math.Sin(.5 * Math.PI * (i + .5) / FrameSize);
                HalfWindow[i] = (float)Math.Sin(.5 * Math.PI * s * s);
            }
            for (int i = 0; i < NbBands; i++)
                for (int j = 0; j < NbBands; j++)
                {
                    DctTable[i * NbBands + j] = (float)Math.Cos((i + .5) * j * Math.PI / NbBands);
                    if (j == 0)
                        DctTable[i * NbBands + j] *= (float)Math.Sqrt(.5);
                }
            Net = Model.Load();
        }

        /// <summary>True when the model loaded and checked out; otherwise Strong falls back to Standard.</summary>
        public static bool Available { get { return Net != null; } }

        // ----- One stream's state -----

        private readonly float[] _analysisMem = new float[FrameSize];
        private readonly float[][] _cepstralMem = new float[CepsMem][];
        private int _memId;
        private readonly float[] _synthesisMem = new float[FrameSize];
        private readonly float[] _pitchBuf = new float[PitchBufSize];
        private float _lastGain;
        private int _lastPeriod;
        private readonly float[] _memHp = new float[2];
        private readonly float[] _lastG = new float[NbBands];
        private readonly float[] _vadState = new float[24], _noiseState = new float[48], _denoiseState = new float[96];

        // Scratch, so a frame allocates nothing.
        private readonly float[] _xr = new float[FreqSize], _xi = new float[FreqSize], _pr = new float[FreqSize], _pi = new float[FreqSize];
        private readonly float[] _ex = new float[NbBands], _ep = new float[NbBands], _exp = new float[NbBands], _g = new float[NbBands];
        private readonly float[] _features = new float[NbFeatures];
        private readonly float[] _x = new float[FrameSize], _win = new float[WindowSize], _gf = new float[FreqSize];
        private readonly float[] _pitchLp = new float[PitchBufSize >> 1];
        private readonly float[] _tmp = new float[NbBands], _ly = new float[NbBands];
        private readonly float[] _rf = new float[FreqSize], _r = new float[NbBands], _newE = new float[NbBands], _norm = new float[NbBands];
        private readonly float[] _denseOut = new float[24], _noiseIn = new float[90], _denoiseIn = new float[114];
        private readonly float[] _z = new float[96], _rg = new float[96], _h = new float[96];
        private readonly float[] _vad = new float[1];
        private readonly float[] _fftRe = new float[WindowSize], _fftIm = new float[WindowSize];

        public Rnnoise()
        {
            for (int i = 0; i < CepsMem; i++)
                _cepstralMem[i] = new float[NbBands];
        }

        /// <summary>
        /// Cleans one 10 ms frame (FrameSize samples, 16-bit range), in place into output. Returns how
        /// likely the frame is to be speech, 0 to 1.
        /// </summary>
        public float ProcessFrame(float[] input, float[] output)
        {
            float vadProb = 0;
            // High-pass at about 50 Hz, as the original does.
            Biquad(_x, _memHp, input, -2f, 1f, -1.99599f, 0.99600f, FrameSize);
            bool silence = ComputeFrameFeatures(_x);
            if (!silence && Net != null)
            {
                ComputeRnn(_g, _vad, _features);
                vadProb = _vad[0];
                PitchFilter(_g);
                for (int i = 0; i < NbBands; i++)
                {
                    _g[i] = Math.Max(_g[i], .6f * _lastG[i]);
                    _lastG[i] = _g[i];
                }
                InterpBandGain(_gf, _g);
                for (int i = 0; i < FreqSize; i++)
                {
                    _xr[i] *= _gf[i];
                    _xi[i] *= _gf[i];
                }
            }
            FrameSynthesis(output);
            return vadProb;
        }

        // ----- Bands -----

        private static void ComputeBandEnergy(float[] bandE, float[] re, float[] im)
        {
            Array.Clear(bandE, 0, NbBands);
            for (int i = 0; i < NbBands - 1; i++)
            {
                int bandSize = (EBand5ms[i + 1] - EBand5ms[i]) << 2, start = EBand5ms[i] << 2;
                for (int j = 0; j < bandSize; j++)
                {
                    float frac = (float)j / bandSize;
                    float tmp = re[start + j] * re[start + j] + im[start + j] * im[start + j];
                    bandE[i] += (1 - frac) * tmp;
                    bandE[i + 1] += frac * tmp;
                }
            }
            bandE[0] *= 2;
            bandE[NbBands - 1] *= 2;
        }

        private static void ComputeBandCorr(float[] bandE, float[] xr, float[] xi, float[] pr, float[] pi)
        {
            Array.Clear(bandE, 0, NbBands);
            for (int i = 0; i < NbBands - 1; i++)
            {
                int bandSize = (EBand5ms[i + 1] - EBand5ms[i]) << 2, start = EBand5ms[i] << 2;
                for (int j = 0; j < bandSize; j++)
                {
                    float frac = (float)j / bandSize;
                    float tmp = xr[start + j] * pr[start + j] + xi[start + j] * pi[start + j];
                    bandE[i] += (1 - frac) * tmp;
                    bandE[i + 1] += frac * tmp;
                }
            }
            bandE[0] *= 2;
            bandE[NbBands - 1] *= 2;
        }

        /// <summary>Per-bin gains from per-band ones. Bins above the last band stay 0, as in the original.</summary>
        private static void InterpBandGain(float[] g, float[] bandE)
        {
            Array.Clear(g, 0, FreqSize);
            for (int i = 0; i < NbBands - 1; i++)
            {
                int bandSize = (EBand5ms[i + 1] - EBand5ms[i]) << 2, start = EBand5ms[i] << 2;
                for (int j = 0; j < bandSize; j++)
                {
                    float frac = (float)j / bandSize;
                    g[start + j] = (1 - frac) * bandE[i] + frac * bandE[i + 1];
                }
            }
        }

        private static void Dct(float[] output, float[] input)
        {
            float scale = (float)Math.Sqrt(2.0 / 22);
            for (int i = 0; i < NbBands; i++)
            {
                float sum = 0;
                for (int j = 0; j < NbBands; j++)
                    sum += input[j] * DctTable[j * NbBands + i];
                output[i] = sum * scale;
            }
        }

        // ----- Transforms -----

        private static void ApplyWindow(float[] x)
        {
            for (int i = 0; i < FrameSize; i++)
            {
                x[i] *= HalfWindow[i];
                x[WindowSize - 1 - i] *= HalfWindow[i];
            }
        }

        /// <summary>The first FreqSize bins of the 960-point DFT of x, divided by 960 (as Opus's FFT does).</summary>
        private void ForwardTransform(float[] outRe, float[] outIm, float[] x)
        {
            for (int i = 0; i < WindowSize; i++)
            {
                _fftRe[i] = x[i];
                _fftIm[i] = 0;
            }
            Fft.Forward(_fftRe, _fftIm);
            for (int i = 0; i < FreqSize; i++)
            {
                outRe[i] = _fftRe[i] / WindowSize;
                outIm[i] = _fftIm[i] / WindowSize;
            }
        }

        /// <summary>Back to 960 real samples from the FreqSize bins (the rest mirrored).</summary>
        private void InverseTransform(float[] output, float[] re, float[] im)
        {
            for (int i = 0; i < FreqSize; i++)
            {
                _fftRe[i] = re[i];
                _fftIm[i] = im[i];
            }
            for (int i = FreqSize; i < WindowSize; i++)
            {
                _fftRe[i] = re[WindowSize - i];
                _fftIm[i] = -im[WindowSize - i];
            }
            // A forward DFT read backwards is the inverse; the original scales by 1/N then N, a wash.
            Fft.Forward(_fftRe, _fftIm);
            output[0] = _fftRe[0];
            for (int i = 1; i < WindowSize; i++)
                output[i] = _fftRe[WindowSize - i];
        }

        private void FrameAnalysis(float[] input)
        {
            Array.Copy(_analysisMem, 0, _win, 0, FrameSize);
            Array.Copy(input, 0, _win, FrameSize, FrameSize);
            Array.Copy(input, 0, _analysisMem, 0, FrameSize);
            ApplyWindow(_win);
            ForwardTransform(_xr, _xi, _win);
            ComputeBandEnergy(_ex, _xr, _xi);
        }

        private void FrameSynthesis(float[] output)
        {
            InverseTransform(_win, _xr, _xi);
            ApplyWindow(_win);
            for (int i = 0; i < FrameSize; i++)
                output[i] = _win[i] + _synthesisMem[i];
            Array.Copy(_win, FrameSize, _synthesisMem, 0, FrameSize);
        }

        private static void Biquad(float[] y, float[] mem, float[] x, float b0, float b1, float a0, float a1, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float xi = x[i], yi = x[i] + mem[0];
                mem[0] = (float)(mem[1] + (b0 * (double)xi - a0 * (double)yi));
                mem[1] = (float)(b1 * (double)xi - a1 * (double)yi);
                y[i] = yi;
            }
        }

        // ----- Features -----

        /// <summary>The 42 features the network reads. True when the frame is silent (nothing to do).</summary>
        private bool ComputeFrameFeatures(float[] input)
        {
            float[] features = _features;
            FrameAnalysis(input);
            Array.Copy(_pitchBuf, FrameSize, _pitchBuf, 0, PitchBufSize - FrameSize);
            Array.Copy(input, 0, _pitchBuf, PitchBufSize - FrameSize, FrameSize);
            Pitch.Downsample(_pitchBuf, _pitchLp, PitchBufSize);
            int pitchIndex = Pitch.Search(_pitchLp, PitchMaxPeriod >> 1, _pitchLp, PitchFrameSize, PitchMaxPeriod - 3 * PitchMinPeriod);
            pitchIndex = PitchMaxPeriod - pitchIndex;
            float gain = Pitch.RemoveDoubling(_pitchLp, PitchMaxPeriod, PitchMinPeriod, PitchFrameSize, ref pitchIndex, _lastPeriod, _lastGain);
            _lastPeriod = pitchIndex;
            _lastGain = gain;
            for (int i = 0; i < WindowSize; i++)
                _win[i] = _pitchBuf[PitchBufSize - WindowSize - pitchIndex + i];
            ApplyWindow(_win);
            ForwardTransform(_pr, _pi, _win);
            ComputeBandEnergy(_ep, _pr, _pi);
            ComputeBandCorr(_exp, _xr, _xi, _pr, _pi);
            for (int i = 0; i < NbBands; i++)
                _exp[i] = (float)(_exp[i] / Math.Sqrt(.001 + _ex[i] * _ep[i]));
            Dct(_tmp, _exp);
            for (int i = 0; i < NbDeltaCeps; i++)
                features[NbBands + 2 * NbDeltaCeps + i] = _tmp[i];
            features[NbBands + 2 * NbDeltaCeps] -= 1.3f;
            features[NbBands + 2 * NbDeltaCeps + 1] -= 0.9f;
            features[NbBands + 3 * NbDeltaCeps] = .01f * (pitchIndex - 300);
            float logMax = -2, follow = -2, e = 0;
            for (int i = 0; i < NbBands; i++)
            {
                _ly[i] = (float)Math.Log10(1e-2 + _ex[i]);
                _ly[i] = Math.Max(logMax - 7, Math.Max(follow - 1.5f, _ly[i]));
                logMax = Math.Max(logMax, _ly[i]);
                follow = Math.Max(follow - 1.5f, _ly[i]);
                e += _ex[i];
            }
            if (e < 0.04f)
            {
                // No audio: don't disturb the state.
                Array.Clear(features, 0, NbFeatures);
                return true;
            }
            Dct(features, _ly);
            features[0] -= 12;
            features[1] -= 4;
            float[] ceps0 = _cepstralMem[_memId];
            float[] ceps1 = _memId < 1 ? _cepstralMem[CepsMem + _memId - 1] : _cepstralMem[_memId - 1];
            float[] ceps2 = _memId < 2 ? _cepstralMem[CepsMem + _memId - 2] : _cepstralMem[_memId - 2];
            for (int i = 0; i < NbBands; i++)
                ceps0[i] = features[i];
            _memId++;
            for (int i = 0; i < NbDeltaCeps; i++)
            {
                features[i] = ceps0[i] + ceps1[i] + ceps2[i];
                features[NbBands + i] = ceps0[i] - ceps2[i];
                features[NbBands + NbDeltaCeps + i] = ceps0[i] - 2 * ceps1[i] + ceps2[i];
            }
            if (_memId == CepsMem)
                _memId = 0;
            float variability = 0;
            for (int i = 0; i < CepsMem; i++)
            {
                float minDist = 1e15f;
                for (int j = 0; j < CepsMem; j++)
                {
                    float dist = 0;
                    for (int k = 0; k < NbBands; k++)
                    {
                        float d = _cepstralMem[i][k] - _cepstralMem[j][k];
                        dist += d * d;
                    }
                    if (j != i)
                        minDist = Math.Min(minDist, dist);
                }
                variability += minDist;
            }
            features[NbBands + 3 * NbDeltaCeps + 1] = variability / CepsMem - 2.1f;
            return false;
        }

        /// <summary>Strengthens the voice's harmonics using the pitch found, as the original does.</summary>
        private void PitchFilter(float[] g)
        {
            for (int i = 0; i < NbBands; i++)
            {
                float r;
                if (_exp[i] > g[i])
                    r = 1;
                else
                    r = _exp[i] * _exp[i] * (1 - g[i] * g[i]) / (.001f + g[i] * g[i] * (1 - _exp[i] * _exp[i]));
                r = (float)Math.Sqrt(Math.Min(1, Math.Max(0, r)));
                r *= (float)Math.Sqrt(_ex[i] / (1e-8 + _ep[i]));
                _r[i] = r;
            }
            InterpBandGain(_rf, _r);
            for (int i = 0; i < FreqSize; i++)
            {
                _xr[i] += _rf[i] * _pr[i];
                _xi[i] += _rf[i] * _pi[i];
            }
            ComputeBandEnergy(_newE, _xr, _xi);
            for (int i = 0; i < NbBands; i++)
                _norm[i] = (float)Math.Sqrt(_ex[i] / (1e-8 + _newE[i]));
            InterpBandGain(_rf, _norm);
            for (int i = 0; i < FreqSize; i++)
            {
                _xr[i] *= _rf[i];
                _xi[i] *= _rf[i];
            }
        }

        // ----- The network -----

        private void ComputeRnn(float[] gains, float[] vad, float[] input)
        {
            Net.InputDense.Compute(_denseOut, input);
            Net.VadGru.Compute(_vadState, _denseOut, _z, _rg, _h);
            Net.VadOutput.Compute(vad, _vadState);
            Array.Copy(_denseOut, 0, _noiseIn, 0, 24);
            Array.Copy(_vadState, 0, _noiseIn, 24, 24);
            Array.Copy(input, 0, _noiseIn, 48, NbFeatures);
            Net.NoiseGru.Compute(_noiseState, _noiseIn, _z, _rg, _h);
            Array.Copy(_vadState, 0, _denoiseIn, 0, 24);
            Array.Copy(_noiseState, 0, _denoiseIn, 24, 48);
            Array.Copy(input, 0, _denoiseIn, 72, NbFeatures);
            Net.DenoiseGru.Compute(_denoiseState, _denoiseIn, _z, _rg, _h);
            Net.DenoiseOutput.Compute(gains, _denoiseState);
        }

        private const float WeightsScale = 1f / 256;
        private const int ActTanh = 0, ActSigmoid = 1, ActRelu = 2;

        private static readonly float[] TansigTable =
        {
            0.000000f, 0.039979f, 0.079830f, 0.119427f, 0.158649f, 0.197375f, 0.235496f, 0.272905f, 0.309507f, 0.345214f,
            0.379949f, 0.413644f, 0.446244f, 0.477700f, 0.507977f, 0.537050f, 0.564900f, 0.591519f, 0.616909f, 0.641077f,
            0.664037f, 0.685809f, 0.706419f, 0.725897f, 0.744277f, 0.761594f, 0.777888f, 0.793199f, 0.807569f, 0.821040f,
            0.833655f, 0.845456f, 0.856485f, 0.866784f, 0.876393f, 0.885352f, 0.893698f, 0.901468f, 0.908698f, 0.915420f,
            0.921669f, 0.927473f, 0.932862f, 0.937863f, 0.942503f, 0.946806f, 0.950795f, 0.954492f, 0.957917f, 0.961090f,
            0.964028f, 0.966747f, 0.969265f, 0.971594f, 0.973749f, 0.975743f, 0.977587f, 0.979293f, 0.980869f, 0.982327f,
            0.983675f, 0.984921f, 0.986072f, 0.987136f, 0.988119f, 0.989027f, 0.989867f, 0.990642f, 0.991359f, 0.992020f,
            0.992631f, 0.993196f, 0.993718f, 0.994199f, 0.994644f, 0.995055f, 0.995434f, 0.995784f, 0.996108f, 0.996407f,
            0.996682f, 0.996937f, 0.997172f, 0.997389f, 0.997590f, 0.997775f, 0.997946f, 0.998104f, 0.998249f, 0.998384f,
            0.998508f, 0.998623f, 0.998728f, 0.998826f, 0.998916f, 0.999000f, 0.999076f, 0.999147f, 0.999213f, 0.999273f,
            0.999329f, 0.999381f, 0.999428f, 0.999472f, 0.999513f, 0.999550f, 0.999585f, 0.999617f, 0.999646f, 0.999673f,
            0.999699f, 0.999722f, 0.999743f, 0.999763f, 0.999781f, 0.999798f, 0.999813f, 0.999828f, 0.999841f, 0.999853f,
            0.999865f, 0.999875f, 0.999885f, 0.999893f, 0.999902f, 0.999909f, 0.999916f, 0.999923f, 0.999929f, 0.999934f,
            0.999939f, 0.999944f, 0.999948f, 0.999952f, 0.999956f, 0.999959f, 0.999962f, 0.999965f, 0.999968f, 0.999970f,
            0.999973f, 0.999975f, 0.999977f, 0.999978f, 0.999980f, 0.999982f, 0.999983f, 0.999984f, 0.999986f, 0.999987f,
            0.999988f, 0.999989f, 0.999990f, 0.999990f, 0.999991f, 0.999992f, 0.999992f, 0.999993f, 0.999994f, 0.999994f,
            0.999994f, 0.999995f, 0.999995f, 0.999996f, 0.999996f, 0.999996f, 0.999997f, 0.999997f, 0.999997f, 0.999997f,
            0.999997f, 0.999998f, 0.999998f, 0.999998f, 0.999998f, 0.999998f, 0.999998f, 0.999999f, 0.999999f, 0.999999f,
            0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f,
            1.000000f, 1.000000f, 1.000000f, 1.000000f, 1.000000f, 1.000000f, 1.000000f, 1.000000f, 1.000000f, 1.000000f,
            1.000000f,
        };

        private static float Tansig(float x)
        {
            // Tests reversed to catch NaNs, as in the original.
            if (!(x < 8))
                return 1;
            if (!(x > -8))
                return -1;
            if (float.IsNaN(x))
                return 0;
            float sign = 1;
            if (x < 0)
            {
                x = -x;
                sign = -1;
            }
            int i = (int)Math.Floor(.5f + 25 * x);
            x -= .04f * i;
            float y = TansigTable[i];
            float dy = 1 - y * y;
            y = y + x * dy * (1 - y * x);
            return sign * y;
        }

        private static float Sigmoid(float x)
        {
            return .5f + .5f * Tansig(.5f * x);
        }

        private static float Activate(int activation, float x)
        {
            if (activation == ActSigmoid)
                return Sigmoid(x);
            if (activation == ActTanh)
                return Tansig(x);
            return x < 0 ? 0 : x;
        }

        private sealed class Dense
        {
            public sbyte[] Bias, Weights;
            public int Inputs, Neurons, Activation;

            public void Compute(float[] output, float[] input)
            {
                for (int i = 0; i < Neurons; i++)
                {
                    float sum = Bias[i];
                    for (int j = 0; j < Inputs; j++)
                        sum += Weights[j * Neurons + i] * input[j];
                    output[i] = Activate(Activation, WeightsScale * sum);
                }
            }
        }

        private sealed class Gru
        {
            public sbyte[] Bias, Weights, Recurrent;
            public int Inputs, Neurons, Activation;

            public void Compute(float[] state, float[] input, float[] z, float[] r, float[] h)
            {
                int n = Neurons, m = Inputs, stride = 3 * n;
                for (int i = 0; i < n; i++)
                {
                    // Update gate.
                    float sum = Bias[i];
                    for (int j = 0; j < m; j++)
                        sum += Weights[j * stride + i] * input[j];
                    for (int j = 0; j < n; j++)
                        sum += Recurrent[j * stride + i] * state[j];
                    z[i] = Sigmoid(WeightsScale * sum);
                }
                for (int i = 0; i < n; i++)
                {
                    // Reset gate.
                    float sum = Bias[n + i];
                    for (int j = 0; j < m; j++)
                        sum += Weights[n + j * stride + i] * input[j];
                    for (int j = 0; j < n; j++)
                        sum += Recurrent[n + j * stride + i] * state[j];
                    r[i] = Sigmoid(WeightsScale * sum);
                }
                for (int i = 0; i < n; i++)
                {
                    // Output.
                    float sum = Bias[2 * n + i];
                    for (int j = 0; j < m; j++)
                        sum += Weights[2 * n + j * stride + i] * input[j];
                    for (int j = 0; j < n; j++)
                        sum += Recurrent[2 * n + j * stride + i] * state[j] * r[j];
                    h[i] = z[i] * state[i] + (1 - z[i]) * Activate(Activation, WeightsScale * sum);
                }
                for (int i = 0; i < n; i++)
                    state[i] = h[i];
            }
        }

        /// <summary>The trained network, from the built-in model file (see assets/rnnoise).</summary>
        private sealed class Model
        {
            public Dense InputDense, DenoiseOutput, VadOutput;
            public Gru VadGru, NoiseGru, DenoiseGru;

            public static Model Load()
            {
                try
                {
                    byte[] data;
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("rnnoise-0.1.1.bin"))
                    {
                        if (stream == null)
                            return null;
                        MemoryStream copy = new MemoryStream();
                        stream.CopyTo(copy);
                        data = copy.ToArray();
                    }
                    using (SHA256 hash = SHA256.Create())
                        if (BitConverter.ToString(hash.ComputeHash(data)).Replace("-", String.Empty).ToLowerInvariant() != ModelSha256)
                            return null;
                    int count = BitConverter.ToUInt16(data, 4), offset = 6 + 4 * count;
                    sbyte[][] tables = new sbyte[count][];
                    for (int t = 0; t < count; t++)
                    {
                        int size = BitConverter.ToInt32(data, 6 + 4 * t);
                        tables[t] = new sbyte[size];
                        Buffer.BlockCopy(data, offset, tables[t], 0, size);
                        offset += size;
                    }
                    Model model = new Model();
                    model.InputDense = new Dense { Weights = tables[0], Bias = tables[1], Inputs = 42, Neurons = 24, Activation = ActTanh };
                    model.VadGru = new Gru { Weights = tables[2], Recurrent = tables[3], Bias = tables[4], Inputs = 24, Neurons = 24, Activation = ActRelu };
                    model.NoiseGru = new Gru { Weights = tables[5], Recurrent = tables[6], Bias = tables[7], Inputs = 90, Neurons = 48, Activation = ActRelu };
                    model.DenoiseGru = new Gru { Weights = tables[8], Recurrent = tables[9], Bias = tables[10], Inputs = 114, Neurons = 96, Activation = ActRelu };
                    model.DenoiseOutput = new Dense { Weights = tables[11], Bias = tables[12], Inputs = 96, Neurons = 22, Activation = ActSigmoid };
                    model.VadOutput = new Dense { Weights = tables[13], Bias = tables[14], Inputs = 24, Neurons = 1, Activation = ActSigmoid };
                    return model;
                }
                catch
                {
                    return null;
                }
            }
        }

        // ----- Pitch (from RNNoise's pitch.c and celt_lpc.c, float build) -----

        private static class Pitch
        {
            /// <summary>x_lp = x halved in rate, smoothed, then whitened by a 4th-order LPC with an added zero.</summary>
            public static void Downsample(float[] x, float[] xLp, int len)
            {
                int half = len >> 1;
                for (int i = 1; i < half; i++)
                    xLp[i] = .5f * (.5f * (x[2 * i - 1] + x[2 * i + 1]) + x[2 * i]);
                xLp[0] = .5f * (.5f * x[1] + x[0]);
                float[] ac = new float[5];
                Autocorr(xLp, ac, 4, half);
                ac[0] *= 1.0001f; // Noise floor -40 dB.
                for (int i = 1; i <= 4; i++)
                    ac[i] -= ac[i] * (.008f * i) * (.008f * i); // Lag windowing.
                float[] lpc = new float[4];
                Lpc(lpc, ac, 4);
                float tmp = 1;
                for (int i = 0; i < 4; i++)
                {
                    tmp = .9f * tmp;
                    lpc[i] = lpc[i] * tmp;
                }
                float c1 = .8f;
                float n0 = lpc[0] + .8f, n1 = lpc[1] + c1 * lpc[0], n2 = lpc[2] + c1 * lpc[1], n3 = lpc[3] + c1 * lpc[2], n4 = c1 * lpc[3];
                float m0 = 0, m1 = 0, m2 = 0, m3 = 0, m4 = 0;
                for (int i = 0; i < half; i++)
                {
                    float sum = xLp[i] + n0 * m0 + n1 * m1 + n2 * m2 + n3 * m3 + n4 * m4;
                    m4 = m3;
                    m3 = m2;
                    m2 = m1;
                    m1 = m0;
                    m0 = xLp[i];
                    xLp[i] = sum;
                }
            }

            private static void Autocorr(float[] x, float[] ac, int lag, int n)
            {
                int fastN = n - lag;
                for (int k = 0; k <= lag; k++)
                {
                    float sum = 0;
                    for (int j = 0; j < fastN; j++)
                        sum += x[j] * x[j + k];
                    ac[k] = sum;
                }
                for (int k = 0; k <= lag; k++)
                {
                    float d = 0;
                    for (int i = k + fastN; i < n; i++)
                        d += x[i] * x[i - k];
                    ac[k] += d;
                }
            }

            private static void Lpc(float[] lpc, float[] ac, int p)
            {
                float error = ac[0];
                Array.Clear(lpc, 0, p);
                if (ac[0] == 0)
                    return;
                for (int i = 0; i < p; i++)
                {
                    float rr = 0;
                    for (int j = 0; j < i; j++)
                        rr += lpc[j] * ac[i - j];
                    rr += ac[i + 1];
                    float r = -rr / error;
                    lpc[i] = r;
                    for (int j = 0; j < (i + 1) >> 1; j++)
                    {
                        float tmp1 = lpc[j], tmp2 = lpc[i - 1 - j];
                        lpc[j] = tmp1 + r * tmp2;
                        lpc[i - 1 - j] = tmp2 + r * tmp1;
                    }
                    error = error - r * r * error;
                    if (error < .001f * ac[0])
                        break; // 30 dB gain is enough.
                }
            }

            private static float InnerProd(float[] x, int xo, float[] y, int yo, int n)
            {
                float sum = 0;
                for (int i = 0; i < n; i++)
                    sum += x[xo + i] * y[yo + i];
                return sum;
            }

            private static void FindBestPitch(float[] xcorr, float[] y, int len, int maxPitch, int[] best)
            {
                float syy = 1;
                float num0 = -1, num1 = -1, den0 = 0, den1 = 0;
                best[0] = 0;
                best[1] = 1;
                for (int j = 0; j < len; j++)
                    syy += y[j] * y[j];
                for (int i = 0; i < maxPitch; i++)
                {
                    if (xcorr[i] > 0)
                    {
                        float x16 = xcorr[i] * 1e-12f; // Avoids under- and overflow when squared.
                        float num = x16 * x16;
                        if (num * den1 > num1 * syy)
                        {
                            if (num * den0 > num0 * syy)
                            {
                                num1 = num0;
                                den1 = den0;
                                best[1] = best[0];
                                num0 = num;
                                den0 = syy;
                                best[0] = i;
                            }
                            else
                            {
                                num1 = num;
                                den1 = syy;
                                best[1] = i;
                            }
                        }
                    }
                    syy += y[i + len] * y[i + len] - y[i] * y[i];
                    syy = Math.Max(1, syy);
                }
            }

            /// <summary>The pitch period of x (at xOffset in xLp, len samples) against y, in half-rate samples.</summary>
            public static int Search(float[] xLp, int xOffset, float[] y, int len, int maxPitch)
            {
                int lag = len + maxPitch;
                float[] xLp4 = new float[len >> 2], yLp4 = new float[lag >> 2], xcorr = new float[maxPitch >> 1];
                int[] best = { 0, 0 };
                for (int j = 0; j < len >> 2; j++)
                    xLp4[j] = xLp[xOffset + 2 * j];
                for (int j = 0; j < lag >> 2; j++)
                    yLp4[j] = y[2 * j];
                // Coarse search, 4x decimated.
                for (int i = 0; i < maxPitch >> 2; i++)
                    xcorr[i] = InnerProd(xLp4, 0, yLp4, i, len >> 2);
                FindBestPitch(xcorr, yLp4, len >> 2, maxPitch >> 2, best);
                // Finer search, 2x decimated.
                for (int i = 0; i < maxPitch >> 1; i++)
                {
                    xcorr[i] = 0;
                    if (Math.Abs(i - 2 * best[0]) > 2 && Math.Abs(i - 2 * best[1]) > 2)
                        continue;
                    float sum = InnerProd(xLp, xOffset, y, i, len >> 1);
                    xcorr[i] = Math.Max(-1, sum);
                }
                FindBestPitch(xcorr, y, len >> 1, maxPitch >> 1, best);
                // Refine by pseudo-interpolation.
                int offset = 0;
                if (best[0] > 0 && best[0] < (maxPitch >> 1) - 1)
                {
                    float a = xcorr[best[0] - 1], b = xcorr[best[0]], c = xcorr[best[0] + 1];
                    if ((c - a) > .7f * (b - a))
                        offset = 1;
                    else if ((a - c) > .7f * (b - c))
                        offset = -1;
                }
                return 2 * best[0] - offset;
            }

            private static float PitchGain(float xy, float xx, float yy)
            {
                return (float)(xy / Math.Sqrt(1 + xx * yy));
            }

            private static readonly int[] SecondCheck = { 0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2 };

            /// <summary>Checks the period isn't a multiple of the real one. x is the half-rate buffer.</summary>
            public static float RemoveDoubling(float[] xs, int maxPeriod, int minPeriod, int n, ref int t0Out, int prevPeriod, float prevGain)
            {
                int minPeriod0 = minPeriod;
                maxPeriod /= 2;
                minPeriod /= 2;
                t0Out /= 2;
                prevPeriod /= 2;
                n /= 2;
                int x = maxPeriod; // x[i] is xs[x + i].
                if (t0Out >= maxPeriod)
                    t0Out = maxPeriod - 1;
                int t = t0Out, t0 = t0Out;
                float[] yyLookup = new float[maxPeriod + 1];
                float xx = InnerProd(xs, x, xs, x, n), xy = InnerProd(xs, x, xs, x - t0, n);
                yyLookup[0] = xx;
                float yy = xx;
                for (int i = 1; i <= maxPeriod; i++)
                {
                    yy = yy + xs[x - i] * xs[x - i] - xs[x + n - i] * xs[x + n - i];
                    yyLookup[i] = Math.Max(0, yy);
                }
                yy = yyLookup[t0];
                float bestXy = xy, bestYy = yy;
                float g, g0;
                g = g0 = PitchGain(xy, xx, yy);
                // Look for any pitch at T/k.
                for (int k = 2; k <= 15; k++)
                {
                    int t1 = (2 * t0 + k) / (2 * k);
                    if (t1 < minPeriod)
                        break;
                    int t1b;
                    // Another strong correlation at T1b.
                    if (k == 2)
                        t1b = t1 + t0 > maxPeriod ? t0 : t0 + t1;
                    else
                        t1b = (2 * SecondCheck[k] * t0 + k) / (2 * k);
                    float xy1 = InnerProd(xs, x, xs, x - t1, n), xy2 = InnerProd(xs, x, xs, x - t1b, n);
                    xy = .5f * (xy1 + xy2);
                    yy = .5f * (yyLookup[t1] + yyLookup[t1b]);
                    float g1 = PitchGain(xy, xx, yy);
                    float cont;
                    if (Math.Abs(t1 - prevPeriod) <= 1)
                        cont = prevGain;
                    else if (Math.Abs(t1 - prevPeriod) <= 2 && 5 * k * k < t0)
                        cont = .5f * prevGain;
                    else
                        cont = 0;
                    float thresh = Math.Max(.3f, .7f * g0 - cont);
                    // Bias against very high pitch (very short period), to avoid false positives.
                    if (t1 < 3 * minPeriod)
                        thresh = Math.Max(.4f, .85f * g0 - cont);
                    else if (t1 < 2 * minPeriod)
                        thresh = Math.Max(.5f, .9f * g0 - cont);
                    if (g1 > thresh)
                    {
                        bestXy = xy;
                        bestYy = yy;
                        t = t1;
                        g = g1;
                    }
                }
                bestXy = Math.Max(0, bestXy);
                float pg = bestYy <= bestXy ? 1 : bestXy / (bestYy + 1);
                float c0 = InnerProd(xs, x, xs, x - (t - 1), n), c1 = InnerProd(xs, x, xs, x - t, n), c2 = InnerProd(xs, x, xs, x - (t + 1), n);
                int offset;
                if ((c2 - c0) > .7f * (c1 - c0))
                    offset = 1;
                else if ((c0 - c2) > .7f * (c1 - c2))
                    offset = -1;
                else
                    offset = 0;
                if (pg > g)
                    pg = g;
                t0Out = 2 * t + offset;
                if (t0Out < minPeriod0)
                    t0Out = minPeriod0;
                return pg;
            }
        }

        // ----- A 960-point FFT (4 x 4 x 4 x 3 x 5), in the same recursive mixed-radix form as kiss_fft -----

        private sealed class Fft960
        {
            private const int N = WindowSize;
            private static readonly int[] Factors = { 4, 4, 4, 3, 5 };
            private readonly float[] _twRe = new float[N], _twIm = new float[N];
            [ThreadStatic] private static float[] _outRe, _outIm, _scRe, _scIm;

            public Fft960()
            {
                for (int i = 0; i < N; i++)
                {
                    double phase = -2 * Math.PI * i / N;
                    _twRe[i] = (float)Math.Cos(phase);
                    _twIm[i] = (float)Math.Sin(phase);
                }
            }

            /// <summary>In place: re/im become the DFT (e^-i, unscaled) of re/im.</summary>
            public void Forward(float[] re, float[] im)
            {
                if (_outRe == null)
                {
                    _outRe = new float[N];
                    _outIm = new float[N];
                    _scRe = new float[5];
                    _scIm = new float[5];
                }
                Work(re, im, 0, 1, 0, 0);
                Array.Copy(_outRe, re, N);
                Array.Copy(_outIm, im, N);
            }

            private void Work(float[] inRe, float[] inIm, int outOffset, int fstride, int inOffset, int stage)
            {
                int p = Factors[stage];
                int m = N / fstride / p;
                if (m == 1)
                {
                    for (int k = 0; k < p; k++)
                    {
                        _outRe[outOffset + k] = inRe[inOffset + k * fstride];
                        _outIm[outOffset + k] = inIm[inOffset + k * fstride];
                    }
                }
                else
                {
                    for (int k = 0; k < p; k++)
                        Work(inRe, inIm, outOffset + k * m, fstride * p, inOffset + k * fstride, stage + 1);
                }
                // Butterflies of size p over the p sub-transforms of length m.
                for (int u = 0; u < m; u++)
                {
                    for (int q = 0; q < p; q++)
                    {
                        _scRe[q] = _outRe[outOffset + u + q * m];
                        _scIm[q] = _outIm[outOffset + u + q * m];
                    }
                    for (int q1 = 0; q1 < p; q1++)
                    {
                        int k = u + q1 * m;
                        float sr = _scRe[0], si = _scIm[0];
                        int tw = 0;
                        for (int q = 1; q < p; q++)
                        {
                            tw += fstride * k;
                            if (tw >= N)
                                tw -= N;
                            sr += _scRe[q] * _twRe[tw] - _scIm[q] * _twIm[tw];
                            si += _scRe[q] * _twIm[tw] + _scIm[q] * _twRe[tw];
                        }
                        _outRe[outOffset + k] = sr;
                        _outIm[outOffset + k] = si;
                    }
                }
            }
        }
    }
}
