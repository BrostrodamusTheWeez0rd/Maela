using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace Maela.Tests
{
    // Screen-share transport over loopback: going live, Watch stream, direct and relayed paths,
    // loss recovery with keyframes, stopping, and hostile members. Frames here are synthetic
    // (random bytes with a checksum); encoding and capture are tested separately.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class StreamTests
    {
        static int _failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) _failures++;
        }

        static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        static VoipEngine Client(int port, string name)
        {
            VoipEngine c = new VoipEngine();
            c.HeadlessForTests = true;
            c.StartClient(new IPEndPoint(IPAddress.Loopback, port), name, VoipEngineTestKeys.HostFingerprint, null);
            return c;
        }

        static uint IdOf(VoipEngine engine, string name)
        {
            foreach (RoomMemberInfo m in engine.GetAllMembers())
                if (m.Name == name) return m.Id;
            return 0;
        }

        // Receiver bookkeeping: frames must arrive intact, and after any loss the first frame
        // delivered must be a keyframe (so a decoder never sees a broken chain).
        sealed class Sink
        {
            public int Frames, Corrupt, BadChains, Keyframes;
            public long Bytes;
            int _lastIndex = -1;
            public void Add(byte[] frame, bool keyframe)
            {
                lock (this)
                {
                    Frames++;
                    Bytes += frame.Length;
                    if (keyframe) Keyframes++;
                    int index = BitConverter.ToInt32(frame, 0);
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] h = sha.ComputeHash(frame, 0, frame.Length - 32);
                        for (int i = 0; i < 32; i++)
                            if (h[i] != frame[frame.Length - 32 + i]) { Corrupt++; break; }
                    }
                    if (_lastIndex >= 0 && index != _lastIndex + 1 && !keyframe) BadChains++;
                    _lastIndex = index;
                }
            }
        }

        static byte[] MakeFrame(int index, int size, Random r)
        {
            byte[] f = new byte[size];
            r.NextBytes(f);
            Buffer.BlockCopy(BitConverter.GetBytes(index), 0, f, 0, 4);
            using (SHA256 sha = SHA256.Create())
                Buffer.BlockCopy(sha.ComputeHash(f, 0, size - 32), 0, f, size - 32, 32);
            return f;
        }

        // Sends `seconds` of 60 fps video from `sharer`: keyframes on request or every 2 s.
        static void Pump(VoipEngine sharer, double seconds, ref int index, Random r)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                bool key = sharer.ConsumeKeyframeRequest() || index % 120 == 0;
                sharer.SendVideoFrame(MakeFrame(index, key ? 40000 : 6000 + r.Next(9000), r), key);
                index++;
                Thread.Sleep(16);
            }
        }

        static int Main()
        {
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.StartHost(0, "Host", "Test Room", new List<string>(new string[] { "General", "Games" }), VoipEngineTestKeys.HostPrivateKey, null);
            int port = host.BoundPort;
            VoipEngine alice = Client(port, "Alice"), bob = Client(port, "Bob"), carol = Client(port, "Carol"), dave = Client(port, "Dave"), mallory = Client(port, "Mallory");
            carol.AllowDirectStreams = false; // Carol never connects directly to anyone.
            Random r = new Random(1);
            int index = 0;
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice && bob.IsInVoice && carol.IsInVoice && dave.IsInVoice && mallory.IsInVoice; }, 8000), "everyone joins");
                dave.ChangeChannel(2);
                Check(WaitFor(delegate { return dave.LocalChannelId == 2; }, 5000), "Dave moves to #Games");
                uint aliceId = IdOf(host, "Alice");

                Console.WriteLine();
                Console.WriteLine("Going live");
                Check(!alice.StartScreenShare(7680, 4320, 60) && !alice.StartScreenShare(1920, 1080, 75), "unsupported sizes and rates are refused");
                Check(alice.StartScreenShare(1920, 1080, 60), "Alice goes live (1080p60)");
                Check(WaitFor(delegate { return bob.GetLiveStream(aliceId) != null && carol.GetLiveStream(aliceId) != null && dave.GetLiveStream(aliceId) != null; }, 5000), "everyone sees Alice is live");
                Thread.Sleep(300);
                alice.SendVideoFrame(MakeFrame(index++, 5000, r), true);
                Check(alice.StreamViewerCount == 0, "nothing is sent until someone clicks Watch");

                Console.WriteLine();
                Console.WriteLine("Watching");
                Sink bobSink = new Sink(), carolSink = new Sink(), hostSink = new Sink(), daveSink = new Sink();
                bob.VideoFrameReceived = delegate(uint from, byte[] f, bool k) { bobSink.Add(f, k); };
                carol.VideoFrameReceived = delegate(uint from, byte[] f, bool k) { carolSink.Add(f, k); };
                host.VideoFrameReceived = delegate(uint from, byte[] f, bool k) { hostSink.Add(f, k); };
                dave.VideoFrameReceived = delegate(uint from, byte[] f, bool k) { daveSink.Add(f, k); };
                Check(bob.WatchStream(aliceId) && carol.WatchStream(aliceId) && host.WatchStream(aliceId), "Bob, Carol and the host click Watch stream");
                dave.WatchStream(aliceId);
                Check(WaitFor(delegate { return alice.StreamViewerCount == 3; }, 5000), "Alice has 3 viewers (" + alice.StreamViewerCount + "); Dave, in another channel, isn't one");
                int sentFrom = index;
                Pump(alice, 3.0, ref index, r);
                int sent = index - sentFrom;
                Thread.Sleep(400);
                Check(bob.GetWatchState(aliceId) == StreamWatchState.Direct, "Bob is connected directly (" + bob.GetWatchState(aliceId) + ")");
                Check(carol.GetWatchState(aliceId) == StreamWatchState.Relayed, "Carol, who turned direct off, goes through the host (" + carol.GetWatchState(aliceId) + ")");
                // Every frame sent should arrive (the first may be skipped while waiting for a keyframe).
                Check(bobSink.Frames >= sent - 2 && bobSink.Corrupt == 0, "Bob receives every frame intact (" + bobSink.Frames + " of " + sent + ", " + bobSink.Corrupt + " corrupt)");
                Check(carolSink.Frames >= sent - 2 && carolSink.Corrupt == 0, "Carol receives every frame via the host (" + carolSink.Frames + " of " + sent + ")");
                Check(hostSink.Frames >= sent - 2 && hostSink.Corrupt == 0, "the host receives every frame (" + hostSink.Frames + " of " + sent + ")");
                Check(daveSink.Frames == 0, "Dave receives nothing");
                Console.WriteLine("     Bob " + (bobSink.Bytes * 8 / 3 / 1000) + " kbps, suggested bitrate " + alice.SuggestedBitrateKbps(12000) + " kbps");

                Console.WriteLine();
                Console.WriteLine("Packet loss (15% for Bob)");
                bob.PacketLossForTests = 0.15;
                Sink lossy = new Sink();
                bob.VideoFrameReceived = delegate(uint from, byte[] f, bool k) { lossy.Add(f, k); };
                Pump(alice, 4.0, ref index, r);
                bob.PacketLossForTests = 0;
                Pump(alice, 1.0, ref index, r);
                Check(lossy.Frames > 20 && lossy.Corrupt == 0, "frames keep arriving intact (" + lossy.Frames + ")");
                Check(lossy.BadChains == 0, "after a loss, video resumes only from a keyframe (" + lossy.Keyframes + " keyframes)");
                Check(alice.SuggestedBitrateKbps(12000) < 12000, "the sharer is told to lower quality (" + alice.SuggestedBitrateKbps(12000) + " kbps)");

                Console.WriteLine();
                Console.WriteLine("Hostile member");
                Sink before = new Sink();
                bob.VideoFrameReceived = delegate(uint from, byte[] f, bool k) { before.Add(f, k); };
                uint bobId = IdOf(host, "Bob");
                Random junk = new Random(9);
                for (int i = 0; i < 2000; i++)
                {
                    byte[] p = new byte[junk.Next(0, 1300)];
                    junk.NextBytes(p);
                    if (p.Length >= 4) Buffer.BlockCopy(BitConverter.GetBytes(bobId), 0, p, 0, 4);
                    mallory.SendToHostForTests(26, p);                       // Relay to Bob without being introduced.
                    if (i % 4 == 0) mallory.SendToHostForTests(22, p.Length >= 15 ? p : new byte[15]); // Watch requests.
                    if (i % 200 == 0) Thread.Sleep(5);
                }
                Thread.Sleep(500);
                Check(before.Frames == 0, "a member who wasn't introduced can't get anything relayed to a viewer");
                Check(host.IsRunning && bob.IsConnected && alice.IsSharingScreen, "the room keeps running");

                Console.WriteLine();
                Console.WriteLine("Stopping");
                bob.StopWatching(aliceId);
                Check(WaitFor(delegate { return alice.StreamViewerCount == 2; }, 3000), "Bob stops watching (" + alice.StreamViewerCount + " viewers left)");
                alice.LeaveVoice();
                Check(WaitFor(delegate { return !alice.IsSharingScreen && carol.GetLiveStream(aliceId) == null && carol.GetWatchState(aliceId) == StreamWatchState.None; }, 5000),
                    "leaving voice ends the stream for everyone");

                Console.WriteLine();
                Console.WriteLine("Host shares");
                alice.ChangeChannel(1);
                WaitFor(delegate { return alice.LocalChannelId == 1; }, 3000);
                Sink aliceSink = new Sink();
                alice.VideoFrameReceived = delegate(uint from, byte[] f, bool k) { aliceSink.Add(f, k); };
                Check(host.StartScreenShare(3840, 2160, 120), "the host goes live (4K120)");
                Check(WaitFor(delegate { return alice.GetLiveStream(host.LocalMemberId) != null; }, 3000) && alice.WatchStream(host.LocalMemberId), "Alice watches the host");
                Thread.Sleep(500);
                int hostFrom = index;
                Pump(host, 1.5, ref index, r);
                Thread.Sleep(300);
                Check(aliceSink.Frames >= index - hostFrom - 2 && aliceSink.Corrupt == 0, "Alice receives the host's frames (" + aliceSink.Frames + ")");
                host.StopScreenShare();
                Check(WaitFor(delegate { return alice.GetLiveStream(host.LocalMemberId) == null; }, 3000), "the host stops sharing");
            }
            finally
            {
                foreach (VoipEngine e in new VoipEngine[] { mallory, dave, carol, bob, alice, host })
                    e.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All screen-share transport checks passed." : _failures + " screen-share check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
