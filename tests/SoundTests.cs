using System;

namespace Maela.Tests
{
    // Every sound effect renders, starts and ends silently (no clicks), stays in its
    // loudness band and is short.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class SoundTests
    {
        static int _failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) _failures++;
        }

        static int Main()
        {
            foreach (SoundCue cue in Enum.GetValues(typeof(SoundCue)))
            {
                byte[] pcm = SoundFx.Render(cue);
                int n = pcm.Length / 2;
                int peak = 0;
                for (int i = 0; i < n; i++)
                    peak = Math.Max(peak, Math.Abs((int)(short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8))));
                short first = (short)(pcm[0] | (pcm[1] << 8));
                short last = (short)(pcm[pcm.Length - 2] | (pcm[pcm.Length - 1] << 8));
                double db = 20 * Math.Log10(peak / 32768.0);
                double ms = n * 1000.0 / SoundFx.SampleRate;
                Check(n > 0 && Math.Abs(first) < 50 && Math.Abs(last) < 50 && db > -24 && db <= -14.5 && ms <= 800,
                    cue + ": " + ms.ToString("F0") + " ms, peak " + db.ToString("F1") + " dBFS, clean start/end");
            }
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All sound checks passed." : _failures + " sound check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
