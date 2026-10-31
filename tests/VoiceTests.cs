using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Maela.Tests
{
    // Integration test for the voice path: a real host and two clients over loopback UDP.
    // Audio is injected as if from the microphone and checked on arrival. Runs against the
    // real Opus codec in CI, or against a stub codec when built with MAELA_STUB_CODEC.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class VoiceTests
    {
        static int _failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok)
                _failures++;
        }

        static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (condition())
                    return true;
                Thread.Sleep(25);
            }
            return condition();
        }

        sealed class Sink
        {
            public readonly Dictionary<uint, int> Frames = new Dictionary<uint, int>();
            public readonly Dictionary<uint, double> Energy = new Dictionary<uint, double>();
            public void Add(uint sender, byte[] pcm)
            {
                lock (this)
                {
                    int count; Frames.TryGetValue(sender, out count); Frames[sender] = count + 1;
                    double e = 0;
                    for (int i = 0; i + 1 < pcm.Length; i += 2) { short v = (short)(pcm[i] | (pcm[i + 1] << 8)); e += (double)v * v; }
                    double total; Energy.TryGetValue(sender, out total); Energy[sender] = total + e;
                }
            }
            public int Count(uint sender) { lock (this) { int c; Frames.TryGetValue(sender, out c); return c; } }
            public double Rms(uint sender) { lock (this) { int c; double e; Frames.TryGetValue(sender, out c); Energy.TryGetValue(sender, out e); return c == 0 ? 0 : Math.Sqrt(e / (c * 960.0)); } }
        }

        // Speech-like test tone: a 220 Hz voice with harmonics and syllable-rate envelope.
        static byte[] Frame(int index, bool talking)
        {
            byte[] pcm = new byte[960];
            for (int i = 0; i < 480; i++)
            {
                double t = (index * 480 + i) / 48000.0;
                double env = 0.5 + 0.5 * Math.Abs(Math.Sin(Math.PI * 4 * t));
                double v = talking ? (Math.Sin(2 * Math.PI * 220 * t) + 0.5 * Math.Sin(2 * Math.PI * 440 * t) + 0.25 * Math.Sin(2 * Math.PI * 660 * t)) * 5000 * env : 0;
                short s = (short)v;
                pcm[i * 2] = (byte)s; pcm[i * 2 + 1] = (byte)(s >> 8);
            }
            return pcm;
        }

        static void Talk(VoipEngine engine, int frames10ms, bool talking, ref int clock)
        {
            for (int i = 0; i < frames10ms; i++)
            {
                engine.InjectCapturedFrameForTests(Frame(clock++, talking));
                Thread.Sleep(10);
            }
        }

        static VoipEngine Client(int port, string name, double loss, string password)
        {
            VoipEngine c = new VoipEngine();
            c.HeadlessForTests = true;
            c.PacketLossForTests = loss;
            // This suite tests transport. Its steady test tone looks exactly like electrical hum,
            // which the noise filter rightly removes (NoiseSuppressorTests covers that filter).
            c.NoiseSuppression = false;
            c.StartClient(new IPEndPoint(IPAddress.Loopback, port), name, VoipEngineTestKeys.HostFingerprint, password);
            return c;
        }

        static void Scenario(double loss)
        {
            Console.WriteLine();
            Console.WriteLine("Voice, packet loss " + (int)(loss * 100) + "%");
            Sink hostSink = new Sink(), aliceSink = new Sink(), bobSink = new Sink();
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.PacketLossForTests = loss;
            host.NoiseSuppression = false;
            host.DecodedAudioForTests = hostSink.Add;
            host.StartHost(0, "Host", "Test Room", new List<string>(new string[] { "General", "Games" }), VoipEngineTestKeys.HostPrivateKey, null);
            VoipEngine alice = Client(host.BoundPort, "Alice", loss, null);
            alice.DecodedAudioForTests = aliceSink.Add;
            VoipEngine bob = Client(host.BoundPort, "Bob", loss, null);
            bob.DecodedAudioForTests = bobSink.Add;
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice && bob.IsInVoice && host.MemberCount == 3; }, 10000), "both clients join #General");
                uint aliceId = alice.LocalMemberId;
                int clock = 0;

                // 1. Speech reaches everyone else in the channel.
                Talk(alice, 150, true, ref clock);      // 1.5 s of talking = 75 frames of 20 ms
                Talk(alice, 60, false, ref clock);      // then quiet
                Thread.Sleep(400);
                int expected = 75;
                int minimum = (int)(expected * (loss > 0 ? 0.85 : 0.97));
                Check(hostSink.Count(aliceId) >= minimum, "host hears Alice (" + hostSink.Count(aliceId) + " frames, need >= " + minimum + ")");
                Check(bobSink.Count(aliceId) >= minimum, "Bob hears Alice via the host relay (" + bobSink.Count(aliceId) + " frames)");
                Check(bobSink.Rms(aliceId) > 1000, "Alice's audio arrives at a sensible level (rms " + (int)bobSink.Rms(aliceId) + ")");
                Check(aliceSink.Count(aliceId) == 0, "Alice does not hear herself");

                // 2. Silence is not transmitted.
                long before = alice.TxBytes;
                Talk(alice, 200, false, ref clock);     // 2 s of silence
                long silentBytes = alice.TxBytes - before;
                Check(silentBytes < 2000, "2 s of silence costs " + silentBytes + " bytes (keepalives only)");

                // 3. Channels are isolated.
                bob.ChangeChannel(2);
                Check(WaitFor(delegate { return bob.LocalChannelId == 2; }, 8000), "Bob moves to #Games");
                int bobBefore = bobSink.Count(aliceId);
                Talk(alice, 100, true, ref clock);
                Talk(alice, 50, false, ref clock);
                Thread.Sleep(300);
                Check(bobSink.Count(aliceId) == bobBefore, "Bob no longer hears #General");
                Check(hostSink.Count(aliceId) > expected, "host still hears Alice in #General");

                // 4. Bandwidth while talking.
                before = alice.TxBytes;
                Talk(alice, 100, true, ref clock);
                double kbps = (alice.TxBytes - before) * 8.0 / 1.0 / 1000.0;
                Console.WriteLine("     upload while talking: " + kbps.ToString("F0") + " kbps (was ~800 kbps uncompressed)");
                Check(kbps < 120, "talking upload under 120 kbps");
            }
            finally
            {
                bob.Dispose();
                alice.Dispose();
                host.Dispose();
            }
        }

        static int Main()
        {
            Scenario(0.0);
            Scenario(0.10);
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All voice checks passed." : _failures + " voice check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
