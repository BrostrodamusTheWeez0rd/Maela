using System;
using System.Collections.Generic;

namespace Maela.Tests
{
    // Typing near the mic: keystrokes alone must not open the mic, speech must still be sent
    // in full, and clicks under speech must be turned down without hurting the voice.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class KeyboardTests
    {
        const int Rate = 48000;
        const int Frame = 480;
        static int _failures;

        static void Check(string name, double value, double min, double max, string unit)
        {
            bool ok = value >= min && value <= max;
            Console.WriteLine("{0} {1,-50} {2,7:F1} {3} (need {4}..{5})", ok ? "PASS" : "FAIL", name, value, unit, min, max);
            if (!ok) _failures++;
        }

        static double Db(double v) { return 10 * Math.Log10(v + 1e-12); }

        // Voiced speech, as in the noise filter tests.
        static float[] Speech(int n, bool[] talking)
        {
            float[] s = new float[n];
            double phase = 0;
            Random r = new Random(11);
            double lp = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                talking[i] = t >= 1 && ((int)(t * 0.7) % 2 == 0);
                double env = Math.Pow(Math.Abs(Math.Sin(Math.PI * 4 * t)), 0.7);
                double f0 = 150 + 40 * Math.Sin(2 * Math.PI * 0.9 * t);
                phase += 2 * Math.PI * f0 / Rate;
                double f1 = 500 + 250 * Math.Sin(2 * Math.PI * 1.3 * t), f2 = 1600 + 500 * Math.Sin(2 * Math.PI * 0.8 * t);
                double v = 0;
                for (int h = 1; h * f0 < 4000; h++)
                {
                    double fh = h * f0;
                    v += (1.0 / (1 + Math.Pow((fh - f1) / 180, 2)) + 0.6 / (1 + Math.Pow((fh - f2) / 250, 2)) + 0.04) * Math.Sin(h * phase);
                }
                lp = 0.7 * lp + 0.3 * (r.NextDouble() * 2 - 1);
                s[i] = talking[i] ? (float)((v * 2600 + lp * 300) * env) : 0f;
            }
            return s;
        }

        // Keystrokes: a sharp broadband click on press and a softer one on release.
        static float[] Typing(int n, bool[] clickZone, double amplitude)
        {
            float[] k = new float[n];
            Random r = new Random(5);
            int pos = Rate / 4;
            while (pos < n)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    int start = pos + pass * (int)(Rate * (0.06 + r.NextDouble() * 0.05));
                    double amp = amplitude * (pass == 0 ? 1.0 : 0.6) * (0.7 + r.NextDouble() * 0.6);
                    double prev = 0;
                    for (int i = 0; i < Rate / 50 && start + i < n; i++)
                    {
                        double t = (double)i / Rate;
                        double attack = Math.Min(1.0, t / 0.0005);
                        double noise = r.NextDouble() * 2 - 1;
                        double hp = noise - prev;            // bright, like plastic keys
                        prev = noise;
                        k[start + i] += (float)(hp * amp * attack * Math.Exp(-t / 0.004));
                        clickZone[start + i] = true;
                    }
                }
                pos += (int)(Rate * (0.12 + r.NextDouble() * 0.2)); // ~6 keys per second
            }
            return k;
        }

        static void GateScenario(string label, float[] signal, bool[] talking, bool expectSpeech)
        {
            VoiceEncoder enc = new VoiceEncoder();
            List<EncodedVoice> packets = new List<EncodedVoice>();
            HashSet<uint> sent = new HashSet<uint>();
            byte[] frame = new byte[Frame * 2];
            int frames = 0, speechFrames = 0, speechSent = 0;
            for (int f = 0; f + Frame <= signal.Length; f += Frame)
            {
                for (int i = 0; i < Frame; i++)
                {
                    short s = (short)Math.Max(-32768, Math.Min(32767, Math.Round(signal[f + i])));
                    frame[i * 2] = (byte)s; frame[i * 2 + 1] = (byte)(s >> 8);
                }
                packets.Clear();
                enc.Push(frame, packets);
                foreach (EncodedVoice p in packets) sent.Add(p.Sequence);
            }
            int total = signal.Length / 960;
            for (int seq = 1; seq <= total; seq++)
            {
                int mid = Math.Min(signal.Length - 1, (seq - 1) * 960 + 480);
                frames++;
                if (talking != null && talking[mid]) { speechFrames++; if (sent.Contains((uint)seq)) speechSent++; }
            }
            if (expectSpeech)
                Check(label + ": speech frames sent", 100.0 * speechSent / Math.Max(1, speechFrames), 95, 100, "%");
            else
                Check(label + ": frames sent", 100.0 * sent.Count / Math.Max(1, frames), 0, 3, "%");
        }

        static double[] Filter(float[] signal)
        {
            NoiseSuppressor ns = new NoiseSuppressor();
            double[] o = new double[signal.Length];
            byte[] frame = new byte[Frame * 2];
            for (int f = 0; f + Frame <= signal.Length; f += Frame)
            {
                for (int i = 0; i < Frame; i++)
                {
                    short s = (short)Math.Max(-32768, Math.Min(32767, Math.Round(signal[f + i])));
                    frame[i * 2] = (byte)s; frame[i * 2 + 1] = (byte)(s >> 8);
                }
                ns.Process(frame);
                for (int i = 0; i < Frame; i++)
                    if (f + i >= Frame) o[f + i - Frame] = (short)(frame[i * 2] | (frame[i * 2 + 1] << 8)); // undo 10 ms latency
            }
            return o;
        }

        static int Main()
        {
            int n = Rate * 14;
            bool[] talking = new bool[n];
            bool[] clicks = new bool[n];
            float[] speech = Speech(n, talking);
            float[] keys = Typing(n, clicks, 9000);   // clicks peak about as loud as the voice
            float[] both = new float[n];
            for (int i = 0; i < n; i++) both[i] = speech[i] + keys[i];

            Console.WriteLine("Mic gate");
            GateScenario("typing only", keys, null, false);
            GateScenario("speech only", speech, talking, true);
            GateScenario("speech while typing", both, talking, true);

            Console.WriteLine();
            Console.WriteLine("Click removal in the noise filter");
            double[] outKeys = Filter(keys);
            double[] outSpeech = Filter(speech);
            double[] outBoth = Filter(both);
            double inK = 0, outK = 0, inS = 0, outS = 0, residual = 0;
            for (int i = Rate * 2; i < n - Rate / 10; i++)
            {
                if (clicks[i]) { inK += keys[i] * keys[i]; outK += outKeys[i] * outKeys[i]; }
                if (clicks[i] && talking[i]) residual += outBoth[i] * outBoth[i] - outSpeech[i] * outSpeech[i];
                if (talking[i] && !clicks[i]) { inS += speech[i] * speech[i]; outS += outBoth[i] * outBoth[i]; }
            }
            double inKTalking = 0;
            for (int i = Rate * 2; i < n - Rate / 10; i++) if (clicks[i] && talking[i]) inKTalking += keys[i] * keys[i];
            Check("typing alone: clicks turned down", Db(inK / outK), 10, 60, "dB");
            // How much louder the clicks make those moments, compared with the voice on its own.
            Check("typing during speech: clicks turned down", Math.Min(60, Db(inKTalking / Math.Max(residual, 1e-6))), 6, 60, "dB");
            Check("voice level between clicks kept", Db(outS / inS), -1.5, 1.0, "dB");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All keyboard checks passed." : _failures + " keyboard check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
