using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;

namespace Maela.Tests
{
    /// <summary>Stream sound through a real room, and Windows' real per-process capture when this PC has audio.</summary>
    internal static class StreamAudioTests
    {
        static int _failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok)
                _failures++;
        }

        static bool WaitFor(Func<bool> condition, int ms)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        static VoipEngine Client(int port, string name)
        {
            VoipEngine e = new VoipEngine();
            e.HeadlessForTests = true;
            e.StartClient(new IPEndPoint(IPAddress.Loopback, port), name, VoipEngineTestKeys.HostFingerprint, null);
            return e;
        }

        static uint IdOf(VoipEngine host, string name)
        {
            foreach (RoomMemberInfo m in host.GetAllMembers())
                if (m.Name == name) return m.Id;
            return 0;
        }

        // Interleaved stereo tone; the left and right frequencies can differ.
        static short[] Tone(ref long clock, double left, double right, int amplitude)
        {
            short[] frame = new short[StreamAudioEncoder.FrameSamples * 2];
            for (int i = 0; i < StreamAudioEncoder.FrameSamples; i++, clock++)
            {
                frame[i * 2] = (short)(amplitude * Math.Sin(2 * Math.PI * left * clock / 48000));
                frame[i * 2 + 1] = (short)(amplitude * Math.Sin(2 * Math.PI * right * clock / 48000));
            }
            return frame;
        }

        // Strength of a frequency in one channel of interleaved samples (Goertzel).
        static double Level(short[] samples, int count, int channel, double frequency)
        {
            double w = 2 * Math.PI * frequency / 48000, c = 2 * Math.Cos(w), a = 0, b = 0;
            int n = 0;
            for (int i = channel; i < count; i += 2, n++)
            {
                double s = samples[i] + c * a - b;
                b = a;
                a = s;
            }
            if (n == 0) return 0;
            return Math.Sqrt(Math.Max(0, a * a + b * b - c * a * b)) * 2 / n;
        }

        sealed class Ear
        {
            public readonly List<short> Samples = new List<short>();
            public int Frames;
            public void Add(byte[] frame)
            {
                short[] s = new short[frame.Length / 2];
                Buffer.BlockCopy(frame, 0, s, 0, frame.Length);
                lock (Samples)
                {
                    Samples.AddRange(s);
                    Frames++;
                }
            }
            public short[] All()
            {
                lock (Samples)
                    return Samples.ToArray();
            }
        }

        static void Stream(VoipEngine sharer, StreamAudioEncoder encoder, ref uint sequence, ref long clock, double seconds)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                byte[] opus = encoder.Encode(Tone(ref clock, 1000, 2000, 8000));
                sharer.SendStreamAudio(++sequence, opus);
                Thread.Sleep(20);
            }
        }

        static void Room()
        {
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.StartHost(0, "Host", "Test", new List<string>(new string[] { "General" }), VoipEngineTestKeys.HostPrivateKey, null);
            VoipEngine alice = Client(host.BoundPort, "Alice"), bob = Client(host.BoundPort, "Bob"), carol = Client(host.BoundPort, "Carol");
            carol.AllowDirectStreams = false;
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice && bob.IsInVoice && carol.IsInVoice; }, 8000), "everyone joins");
                uint aliceId = IdOf(host, "Alice");
                Check(alice.StartScreenShare(1920, 1080, 60), "Alice goes live");
                Check(WaitFor(delegate { return bob.GetLiveStream(aliceId) != null && carol.GetLiveStream(aliceId) != null; }, 5000), "everyone sees it");

                Ear bobEar = new Ear(), carolEar = new Ear(), hostEar = new Ear();
                bob.StreamAudioForTests = delegate(uint from, byte[] f) { bobEar.Add(f); };
                carol.StreamAudioForTests = delegate(uint from, byte[] f) { carolEar.Add(f); };
                host.StreamAudioForTests = delegate(uint from, byte[] f) { hostEar.Add(f); };

                StreamAudioEncoder encoder = new StreamAudioEncoder();
                uint sequence = 0;
                long clock = 0;
                Stream(alice, encoder, ref sequence, ref clock, 0.3);
                Check(bobEar.Frames == 0 && carolEar.Frames == 0, "nobody hears anything until they click Watch");

                Check(bob.WatchStream(aliceId) && carol.WatchStream(aliceId), "Bob and Carol watch; the host doesn't");
                Check(WaitFor(delegate { return alice.StreamViewerCount == 2; }, 5000), "Alice has 2 viewers");
                Thread.Sleep(1000); // Let direct paths open.
                uint sentFrom = sequence;
                Stream(alice, encoder, ref sequence, ref clock, 2.0);
                Thread.Sleep(300);
                int sent = (int)(sequence - sentFrom);
                Check(bob.GetWatchState(aliceId) == StreamWatchState.Direct, "Bob's sound comes directly");
                Check(sent > 30 && bobEar.Frames >= sent, "Bob hears every frame (" + bobEar.Frames + " of " + sent + ")");
                Check(carolEar.Frames >= sent, "Carol hears every frame through the host (" + carolEar.Frames + " of " + sent + ")");
                Check(hostEar.Frames == 0, "the host, who isn't watching, doesn't");

                short[] heard = bobEar.All();
                int skip = Math.Min(heard.Length / 2, 9600) & ~1;
                short[] settled = new short[heard.Length - skip];
                Array.Copy(heard, skip, settled, 0, settled.Length);
                double l1 = Level(settled, settled.Length, 0, 1000), l2 = Level(settled, settled.Length, 0, 2000);
                double r1 = Level(settled, settled.Length, 1, 1000), r2 = Level(settled, settled.Length, 1, 2000);
                Console.WriteLine("     left channel: 1 kHz " + (int)l1 + ", 2 kHz " + (int)l2 + "; right channel: 1 kHz " + (int)r1 + ", 2 kHz " + (int)r2);
                Check(l1 > 4000 && r2 > 4000, "the sound survives the trip");
                Check(l2 < l1 / 4 && r1 < r2 / 4, "left and right stay separate");

                // Real loss on the way in.
                bob.IncomingMediaLossForTests = 0.05;
                int before = bobEar.Frames;
                sentFrom = sequence;
                Stream(alice, encoder, ref sequence, ref clock, 4.0);
                bob.IncomingMediaLossForTests = 0;
                Thread.Sleep(200);
                sent = (int)(sequence - sentFrom);
                WatchNetworkStats stats = bob.GetWatchNetworkStats(aliceId);
                Check(stats.AudioConcealed > 0, "lost sound is covered over (" + stats.AudioConcealed + " frames)");
                Check(bobEar.Frames - before >= sent * 97 / 100, "and it keeps playing at 5% loss (" + (bobEar.Frames - before) + " of " + sent + " frames)");

                // Deafen stops it, without ending the stream.
                FieldInfo deafened = typeof(VoipEngine).GetField("_deafened", BindingFlags.NonPublic | BindingFlags.Instance);
                deafened.SetValue(bob, true);
                before = bobEar.Frames;
                int carolBefore = carolEar.Frames;
                Stream(alice, encoder, ref sequence, ref clock, 0.5);
                Thread.Sleep(150);
                Check(bobEar.Frames == before && carolEar.Frames > carolBefore, "deafening silences stream sound too, for that person only");
                deafened.SetValue(bob, false);

                // Junk from the sharer must not hurt the viewer.
                Random r = new Random(5);
                for (int i = 0; i < 300; i++)
                {
                    byte[] junk = new byte[r.Next(1, StreamAudioEncoder.MaxPacketBytes)];
                    r.NextBytes(junk);
                    alice.SendStreamAudio(++sequence, junk);
                }
                alice.SendStreamAudio(++sequence, new byte[5000]);
                Thread.Sleep(300);
                before = bobEar.Frames;
                Stream(alice, encoder, ref sequence, ref clock, 0.5);
                Thread.Sleep(150);
                Check(bob.IsConnected && bobEar.Frames > before, "random and oversized sound packets don't stop anything");
            }
            finally
            {
                foreach (VoipEngine e in new VoipEngine[] { carol, bob, alice, host })
                    e.Dispose();
            }
        }

        sealed class Mic
        {
            public readonly List<short> Samples = new List<short>();
            public void Add(short[] frame)
            {
                lock (Samples) Samples.AddRange(frame);
            }
            public short[] All()
            {
                lock (Samples) return Samples.ToArray();
            }
        }

        // Quietly plays a tone from this process for a moment, so capture has something to find.
        static bool PlayTone(double hz, int ms)
        {
            WAVEFORMATEX format = new WAVEFORMATEX();
            format.wFormatTag = 1;
            format.nChannels = 2;
            format.nSamplesPerSec = 48000;
            format.wBitsPerSample = 16;
            format.nBlockAlign = 4;
            format.nAvgBytesPerSec = 192000;
            PlaybackStream stream;
            try { stream = new PlaybackStream(format, NativeMethods.WAVE_MAPPER, null); }
            catch { return false; }
            using (stream)
            {
                long clock = 0;
                DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < end)
                {
                    short[] tone = Tone(ref clock, hz, hz, 600);
                    byte[] bytes = new byte[tone.Length * 2];
                    Buffer.BlockCopy(tone, 0, bytes, 0, bytes.Length);
                    stream.Enqueue(bytes);
                    Thread.Sleep(19);
                }
            }
            return true;
        }

        static void Capture()
        {
            Console.WriteLine();
            Console.WriteLine("Capturing real sound from Windows (a quiet tone plays for a few seconds)");
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            Console.WriteLine("     Windows " + Environment.OSVersion.Version);
            Mic included = new Mic(), excluded = new Mic();
            string problem = null;
            bool played;
            using (DesktopAudioCapture a = new DesktopAudioCapture(self, true, included.Add))
            using (DesktopAudioCapture b = new DesktopAudioCapture(self, false, excluded.Add))
            {
                WaitFor(delegate { return (a.Running && b.Running) || a.Problem != null || b.Problem != null; }, 6000);
                problem = a.Problem ?? b.Problem;
                if (problem != null)
                {
                    Console.WriteLine("     SKIPPED: " + problem);
                    return;
                }
                played = PlayTone(1000, 2000);
                Thread.Sleep(200);
            }
            if (!played)
            {
                Console.WriteLine("     SKIPPED: this PC has no speakers to play to");
                return;
            }
            short[] inc = included.All(), exc = excluded.All();
            double heard = Level(inc, inc.Length, 0, 1000), notHeard = Level(exc, exc.Length, 0, 1000);
            Console.WriteLine("     1 kHz level: " + heard.ToString("F0") + " capturing this program, " + notHeard.ToString("F1") + " capturing everything else");
            Check(inc.Length > 48000 && heard > 100, "sharing a window captures that program's sound");
            Check(notHeard < heard / 20, "sharing a screen leaves mæla's own sound out, so the voice chat never goes back to the room");

            // All the way: real capture, Opus, a room, real playback, heard again by Windows.
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.StartHost(0, "Host", "Test", new List<string>(new string[] { "General" }), VoipEngineTestKeys.HostPrivateKey, null);
            VoipEngine bob = Client(host.BoundPort, "Bob");
            try
            {
                Check(WaitFor(delegate { return bob.IsInVoice; }, 8000), "room for the full trip");
                host.StartScreenShare(1920, 1080, 60);
                Check(WaitFor(delegate { return bob.GetLiveStream(host.LocalMemberId) != null; }, 5000) && bob.WatchStream(host.LocalMemberId), "Bob watches");
                Check(WaitFor(delegate { return host.StreamViewerCount == 1; }, 5000), "the host sees Bob");
                bob.PlayStreamAudioForTests = true;

                StreamAudioEncoder encoder = new StreamAudioEncoder();
                uint sequence = 0;
                long clock = 0;
                Mic room = new Mic();
                using (DesktopAudioCapture listening = new DesktopAudioCapture(self, true, room.Add))
                {
                    WaitFor(delegate { return listening.Running; }, 5000);
                    DateTime end = DateTime.UtcNow.AddSeconds(2);
                    while (DateTime.UtcNow < end)
                    {
                        host.SendStreamAudio(++sequence, encoder.Encode(Tone(ref clock, 1500, 1500, 600)));
                        Thread.Sleep(19);
                    }
                    Thread.Sleep(300);
                    short[] all = room.All();
                    double loud = Level(all, all.Length, 0, 1500);
                    Console.WriteLine("     played through a viewer: 1.5 kHz level " + loud.ToString("F0"));
                    Check(loud > 100, "sent through a room, it really comes out of the viewer's speakers");

                    bob.SetStreamVolume(host.LocalMemberId, 0);
                    Thread.Sleep(300);
                    int mark = room.All().Length;
                    end = DateTime.UtcNow.AddSeconds(1);
                    while (DateTime.UtcNow < end)
                    {
                        host.SendStreamAudio(++sequence, encoder.Encode(Tone(ref clock, 1500, 1500, 600)));
                        Thread.Sleep(19);
                    }
                    Thread.Sleep(200);
                    all = room.All();
                    short[] after = new short[Math.Max(0, all.Length - mark)];
                    Array.Copy(all, all.Length - after.Length, after, 0, after.Length);
                    double muted = Level(after, after.Length, 0, 1500);
                    Check(muted < loud / 20, "and the viewer's mute works (" + muted.ToString("F1") + ")");
                }
            }
            finally
            {
                bob.Dispose();
                host.Dispose();
            }
        }

        static int Main()
        {
            Room();
            Capture();
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All stream sound checks passed." : _failures + " stream sound check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
