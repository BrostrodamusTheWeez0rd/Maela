using System;
using Maela;

// Offline regression test for the echo canceller. Simulates a speaker-to-microphone
// echo path (bulk delay + decaying room response), far-end-only talk, double-talk,
// near-end-only talk and a sudden echo-path change, and checks the results.
//
// Build and run:  scripts\test_ci.cmd
internal static class EchoCancellerTests
{
    const int Rate = 48000;
    const int Frame = 480;
    static int _failures;

    static float[] Speech(Random r, int n, double syllableHz, double seedPhase)
    {
        float[] s = new float[n];
        double lp = 0, lp2 = 0;
        bool on = true; int segLeft = Rate;
        for (int i = 0; i < n; i++)
        {
            if (--segLeft <= 0) { on = !on; segLeft = (int)(Rate * (on ? 0.8 + r.NextDouble() * 1.5 : 0.2 + r.NextDouble() * 0.6)); }
            double w = r.NextDouble() * 2 - 1;
            lp = 0.7 * lp + 0.3 * w; lp2 = 0.9 * lp2 + 0.1 * lp;
            double env = Math.Abs(Math.Sin(Math.PI * syllableHz * i / Rate + seedPhase));
            env = Math.Pow(env, 0.7);
            double pitch = 0.6 + 0.4 * Math.Sin(2 * Math.PI * 140 * i / Rate);
            s[i] = on ? (float)((lp * 0.7 + lp2 * 2.0) * env * pitch * 9000) : 0f;
        }
        return s;
    }

    static float[] Room(Random r, int len, double gain)
    {
        float[] h = new float[len];
        double e = 0;
        for (int i = 0; i < len; i++) { h[i] = (float)((r.NextDouble() * 2 - 1) * Math.Exp(-i / (len / 6.0))); e += h[i] * h[i]; }
        double g = gain / Math.Sqrt(e);
        for (int i = 0; i < len; i++) h[i] = (float)(h[i] * g);
        return h;
    }

    static double Pow(float[] x, int a, int b) { double s = 0; for (int i = a; i < b; i++) s += (double)x[i] * x[i]; return s / Math.Max(1, b - a) + 1e-9; }
    static double Db(double v) { return 10 * Math.Log10(v); }

    static void Check(string name, double value, double minimum, string unit)
    {
        bool ok = value >= minimum;
        Console.WriteLine("{0} {1,-46} {2,7:F1} {3} (need >= {4})", ok ? "PASS" : "FAIL", name, value, unit, minimum);
        if (!ok)
            _failures++;
    }

    static void CheckMax(string name, double value, double maximum, string unit)
    {
        bool ok = value <= maximum;
        Console.WriteLine("{0} {1,-46} {2,7:F1} {3} (need <= {4})", ok ? "PASS" : "FAIL", name, value, unit, maximum);
        if (!ok)
            _failures++;
    }

    static void Scenario(int delaySamples, double coupling)
    {
        Console.WriteLine();
        Console.WriteLine("Echo delay {0} ms, coupling {1}", delaySamples * 1000 / Rate, coupling);
        Random r = new Random(7);
        int seconds = 24;
        int n = Rate * seconds;
        float[] far = Speech(r, n, 4.0, 0.0);
        float[] near = Speech(new Random(99), n, 3.3, 1.0);
        // 0-10 s far only, 10-14 s double-talk, 14-17 s near only, 17-24 s far only with a new room.
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            if (!(t >= 10 && t < 17)) near[i] = 0; else near[i] *= 0.8f;
            if (!(t < 14 || t >= 17)) far[i] = 0;
        }
        float[] h1 = Room(r, 2400, coupling);
        float[] h2 = Room(new Random(3), 2400, coupling * 1.2);
        float[] echo = new float[n];
        for (int i = 0; i < n; i++)
        {
            float[] h = i < 17 * Rate ? h1 : h2;
            double acc = 0;
            for (int k = 0; k < h.Length; k++) { int j = i - delaySamples - k; if (j < 0) break; acc += h[k] * far[j]; }
            echo[i] = (float)acc;
        }
        Random nr = new Random(5);
        float[] mic = new float[n];
        for (int i = 0; i < n; i++) mic[i] = echo[i] + near[i] + (float)((nr.NextDouble() * 2 - 1) * 30);

        FarEndReference reference = new FarEndReference();
        EchoCanceller aec = new EchoCanceller(reference);
        float[] output = new float[n];
        byte[] farFrame = new byte[Frame * 2];
        byte[] micFrame = new byte[Frame * 2];
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f + Frame <= n; f += Frame)
        {
            for (int i = 0; i < Frame; i++) { short s = Clip(far[f + i]); farFrame[i * 2] = (byte)s; farFrame[i * 2 + 1] = (byte)(s >> 8); }
            reference.Write(f, farFrame);
            for (int i = 0; i < Frame; i++) { short s = Clip(mic[f + i]); micFrame[i * 2] = (byte)s; micFrame[i * 2 + 1] = (byte)(s >> 8); }
            aec.Process(micFrame, reference.AdvanceCapture(Frame));
            for (int i = 0; i < Frame; i++) output[f + i] = (short)(micFrame[i * 2] | (micFrame[i * 2 + 1] << 8));
        }
        double cpu = 100.0 * watch.Elapsed.TotalSeconds / seconds;

        float[] o = new float[n];
        for (int i = 0; i + Frame < n; i++) o[i] = output[i + Frame]; // Undo the one-frame latency.
        float[] diff = new float[n];
        for (int i = 0; i < n; i++) diff[i] = o[i] - near[i];

        Console.WriteLine("     estimated delay {0} ms, CPU {1:F1}% of one core", aec.EstimatedDelayMs, cpu);
        CheckMax("delay estimate error", aec.EstimatedDelayMs < 0 ? 9999 : Math.Abs(aec.EstimatedDelayMs - delaySamples * 1000.0 / Rate), 10, "ms");
        Check("echo removed, far-end only (6-10 s)", Db(Pow(echo, 6 * Rate, 10 * Rate) / Pow(o, 6 * Rate, 10 * Rate)), 25, "dB");
        Check("echo removed after path change (21-24 s)", Db(Pow(echo, 21 * Rate, 24 * Rate) / Pow(o, 21 * Rate, 24 * Rate)), 25, "dB");
        int a = 10 * Rate, b = 14 * Rate;
        Check("double-talk SNR improvement", Db(Pow(near, a, b) / Pow(diff, a, b)) - Db(Pow(near, a, b) / Pow(echo, a, b)), 10, "dB");
        a = 14 * Rate + 4800; b = 17 * Rate;
        Check("near-end only fidelity", Db(Pow(near, a, b) / Pow(diff, a, b)), 25, "dB");
    }

    static int Main()
    {
        Scenario(7200, 0.6);   // 150 ms, typical WinMM output + capture latency.
        Scenario(2400, 0.6);   // 50 ms, low-latency driver.
        Scenario(14400, 0.6);  // 300 ms, heavily buffered output.
        Scenario(7200, 1.5);   // Loud speakers close to the microphone.
        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "All echo canceller checks passed." : _failures + " echo canceller check(s) FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    static short Clip(float v) { if (v > 32767) return 32767; if (v < -32768) return -32768; return (short)Math.Round(v); }
}
