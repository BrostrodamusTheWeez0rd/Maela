using System;

namespace Maela.Tests
{
    // Speech-like signal plus steady fan-like noise, through the noise suppressor.
    // Checks the background is reduced a lot while speech level is kept.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class NoiseSuppressorTests
    {
        const int Rate = 48000;
        const int Frame = 480;
        static int _failures;

        static void Check(string name, double value, double min, double max, string unit)
        {
            bool ok = value >= min && value <= max;
            Console.WriteLine("{0} {1,-44} {2,7:F1} {3} (need {4}..{5})", ok ? "PASS" : "FAIL", name, value, unit, min, max);
            if (!ok) _failures++;
        }

        static double Db(double v) { return 10 * Math.Log10(v + 1e-9); }

        static void Scenario(string label, double noiseLevel, int seed, double minSpeechGain)
        {
            Console.WriteLine();
            Console.WriteLine(label);
            int n = Rate * 12;
            Random r = new Random(seed);
            float[] speech = new float[n];
            float[] noise = new float[n];
            bool[] talking = new bool[n];
            double lp = 0, nlp = 0, hum = 0, phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                bool on = t >= 3 && ((int)(t * 0.7) % 2 == 0);
                talking[i] = on;
                // Voiced speech: a gliding 110-190 Hz pitch with harmonics shaped by two
                // vowel-like formants, plus syllable-rate envelope and a little breath noise.
                double env = Math.Pow(Math.Abs(Math.Sin(Math.PI * 4 * t)), 0.7);
                double f0 = 150 + 40 * Math.Sin(2 * Math.PI * 0.9 * t);
                phase += 2 * Math.PI * f0 / Rate;
                double formant1 = 500 + 250 * Math.Sin(2 * Math.PI * 1.3 * t), formant2 = 1600 + 500 * Math.Sin(2 * Math.PI * 0.8 * t);
                double voiced = 0;
                for (int h = 1; h * f0 < 4000; h++)
                {
                    double fh = h * f0;
                    double amp = 1.0 / (1 + Math.Pow((fh - formant1) / 180, 2)) + 0.6 / (1 + Math.Pow((fh - formant2) / 250, 2)) + 0.04;
                    voiced += amp * Math.Sin(h * phase);
                }
                double w = r.NextDouble() * 2 - 1;
                lp = 0.7 * lp + 0.3 * w;
                speech[i] = on ? (float)((voiced * 2600 + lp * 300) * env) : 0f;
                // Fan: low-passed noise plus a little 100 Hz hum.
                nlp = 0.97 * nlp + 0.03 * (r.NextDouble() * 2 - 1);
                hum = Math.Sin(2 * Math.PI * 100 * t);
                noise[i] = (float)((nlp * 6 + (r.NextDouble() * 2 - 1) * 0.25 + hum * 0.15) * noiseLevel);
            }

            NoiseSuppressor ns = new NoiseSuppressor();
            float[] output = new float[n];
            byte[] frame = new byte[Frame * 2];
            DateTime start = DateTime.UtcNow;
            for (int f = 0; f + Frame <= n; f += Frame)
            {
                for (int i = 0; i < Frame; i++)
                {
                    float v = speech[f + i] + noise[f + i];
                    short s = (short)Math.Max(-32768, Math.Min(32767, Math.Round(v)));
                    frame[i * 2] = (byte)s; frame[i * 2 + 1] = (byte)(s >> 8);
                }
                ns.Process(frame);
                for (int i = 0; i < Frame; i++)
                    output[f + i] = (short)(frame[i * 2] | (frame[i * 2 + 1] << 8));
            }
            double cpu = (DateTime.UtcNow - start).TotalSeconds / 12.0 * 100.0;

            // Undo the 10 ms latency, then compare.
            double inNoise = 0, outNoise = 0, inSpeech = 0, outSpeech = 0, err = 0, cleanSpeech = 0;
            int noiseCount = 0, speechCount = 0;
            for (int i = Rate * 1; i + Frame < n; i++)
            {
                double o = output[i + Frame];
                if (!talking[i] && i < Rate * 3)
                {
                    inNoise += noise[i] * noise[i]; outNoise += o * o; noiseCount++;
                }
                else if (talking[i])
                {
                    double x = speech[i] + noise[i];
                    inSpeech += x * x; outSpeech += o * o; speechCount++;
                    cleanSpeech += speech[i] * speech[i];
                    err += (o - speech[i]) * (o - speech[i]);
                }
            }
            Console.WriteLine("     CPU {0:F1}% of one core, input SNR while talking {1:F1} dB", cpu, Db(cleanSpeech / Math.Max(1, inSpeech - cleanSpeech)));
            Check("background noise reduced (quiet periods)", Db(inNoise / outNoise), 12, 40, "dB");
            Check("speech level kept (vs. clean speech)", Db(outSpeech / cleanSpeech), -3, 1, "dB");
            // While someone talks there is less noise to remove than in the gaps, so the
            // requirement here is that speech comes out cleaner, not worse.
            Check("SNR improvement while talking", Db(cleanSpeech / err) - Db(cleanSpeech / Math.Max(1, inSpeech - cleanSpeech)), minSpeechGain, 40, "dB");
        }

        static int Main()
        {
            Scenario("Noticeable fan (speech ~25 dB above the noise)", 300, 1, 1.0);
            Scenario("Loud fan (speech ~8 dB above the noise)", 2500, 2, 2.0);
            Scenario("Very loud (speech ~2 dB above the noise)", 5000, 3, 3.0);
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All noise suppressor checks passed." : _failures + " noise suppressor check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
