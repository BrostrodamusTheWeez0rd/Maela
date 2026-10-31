using System;

namespace Maela.Tests
{
    // Quiet, normal and loud talkers through the automatic mic level: speech should land near
    // a normal level, background noise must not be pumped up, and nothing may clip.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class AutoGainTests
    {
        const int Rate = 48000;
        const int Frame = 480;
        static int _failures;

        static void Check(string name, double value, double min, double max, string unit)
        {
            bool ok = value >= min && value <= max;
            Console.WriteLine("{0} {1,-48} {2,7:F1} {3} (need {4}..{5})", ok ? "PASS" : "FAIL", name, value, unit, min, max);
            if (!ok) _failures++;
        }

        static double Db(double v) { return 10 * Math.Log10(v + 1e-12); }

        static void Scenario(string label, double speechAmplitude, double noiseAmplitude)
        {
            Console.WriteLine();
            Console.WriteLine(label);
            int seconds = 16, n = Rate * seconds;
            Random r = new Random(4);
            double phase = 0;
            AutoGain agc = new AutoGain();
            byte[] frame = new byte[Frame * 2];
            double speechOut = 0, quietOut = 0, quietIn = 0;
            int speechN = 0, quietN = 0, clipped = 0;
            for (int f = 0; f + Frame <= n; f += Frame)
            {
                double t = (double)f / Rate;
                bool talking = t >= 1 && ((int)(t * 0.8) % 2 == 0);
                for (int i = 0; i < Frame; i++)
                {
                    double tt = (double)(f + i) / Rate;
                    double f0 = 140 + 30 * Math.Sin(2 * Math.PI * 0.9 * tt);
                    phase += 2 * Math.PI * f0 / Rate;
                    double env = Math.Pow(Math.Abs(Math.Sin(Math.PI * 4 * tt)), 0.7);
                    double v = 0;
                    for (int h = 1; h < 12; h++) v += Math.Sin(h * phase) / h;
                    double sample = (talking ? v * env * speechAmplitude : 0) + (r.NextDouble() * 2 - 1) * noiseAmplitude;
                    short s = (short)Math.Max(-32768, Math.Min(32767, Math.Round(sample)));
                    frame[i * 2] = (byte)s; frame[i * 2 + 1] = (byte)(s >> 8);
                    if (!talking && t > 8) quietIn += sample * sample;
                }
                agc.Process(frame);
                for (int i = 0; i < Frame; i++)
                {
                    short o = (short)(frame[i * 2] | (frame[i * 2 + 1] << 8));
                    if (Math.Abs((int)o) >= 32767) clipped++;
                    if (t > 8 && talking) { speechOut += (double)o * o; speechN++; }
                    if (t > 8 && !talking) { quietOut += (double)o * o; quietN++; }
                }
            }
            double speechDb = Db(speechOut / Math.Max(1, speechN) / (32768.0 * 32768.0));
            Console.WriteLine("     settled gain {0:F1} dB", agc.GainDb);
            Check("speech level after settling", speechDb, -26, -15, "dBFS");
            Check("background noise not pumped up vs. speech gain", Db(quietOut / Math.Max(1, quietN)) - Db(quietIn / Math.Max(1, quietN)) - agc.GainDb, -1, 1, "dB");
            Check("clipped samples", clipped, 0, 0, "");
        }

        static int Main()
        {
            Scenario("Quiet mic (speech around -45 dBFS)", 300, 3);
            Scenario("Normal mic (speech around -25 dBFS)", 3000, 20);
            Scenario("Loud mic (speech around -10 dBFS)", 18000, 60);
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All auto-gain checks passed." : _failures + " auto-gain check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
