using System;
using System.Collections.Generic;
using System.Threading;

namespace Maela.Tests
{
    /// <summary>Steps the scheduler through simulated time, plus one run on its real thread.</summary>
    internal static class SendSchedulerTests
    {
        static int _failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok)
                _failures++;
        }

        // Target = frame number, so tests can see which frames went out.
        static PacedPacket[] Frame(int number, int packets, int size)
        {
            PacedPacket[] list = new PacedPacket[packets];
            for (int i = 0; i < packets; i++)
                list[i] = new PacedPacket(number, new byte[size]);
            return list;
        }

        sealed class Rig
        {
            public readonly List<int> Sent = new List<int>();
            public readonly List<object> Relayed = new List<object>();
            public int DropSignals;
            public readonly SendScheduler S;

            public Rig()
            {
                S = new SendScheduler(
                    delegate(object t, byte[] d) { lock (Sent) Sent.Add((int)t); },
                    delegate(object t, byte[] d) { lock (Relayed) Relayed.Add(t); },
                    delegate { DropSignals++; });
            }

            public int Run(long from, long to)
            {
                int most = 0;
                for (long t = from; t <= to; t++)
                    most = Math.Max(most, S.Step(t));
                return most;
            }
        }

        static void SpreadsAKeyframe()
        {
            Rig r = new Rig();
            r.S.Step(0);
            r.S.EnqueueVideoAt(0, Frame(1, 200, 1100), true); // 232 KB.
            int first = r.S.Step(1);
            int most = r.Run(2, 400);
            Check(r.Sent.Count == 200, "every packet of a large keyframe goes out (" + r.Sent.Count + "/200)");
            Check(first < 20 && most < 20, "it is spread out, not dumped at once (most in 1 ms: " + Math.Max(first, most) + ")");
        }

        static void FinishesWithinDrainTime()
        {
            Rig r = new Rig();
            r.S.Step(0);
            r.S.EnqueueVideoAt(0, Frame(1, 200, 1100), true);
            long t = 0;
            while (r.Sent.Count < 200 && t < 2000)
                r.S.Step(++t);
            Check(t <= 100, "large keyframe clears quickly (" + t + " ms)");
            Check(t >= 40, "and isn't rushed (" + t + " ms)");
        }

        static void SustainedVideoKeepsUp()
        {
            Rig r = new Rig();
            int frames = 0;
            r.S.Step(0);
            for (long t = 1; t <= 3000; t++)
            {
                if (t % 16 == 0)
                {
                    r.S.EnqueueVideoAt(t, Frame(++frames, 12, 1100), false); // ~8 Mbps at 60 fps.
                }
                r.S.Step(t);
            }
            r.Run(3001, 3100);
            Check(r.Sent.Count == frames * 12, "steady 60 fps video all goes out (" + r.Sent.Count + "/" + frames * 12 + ")");
            Check(r.S.DroppedFrames == 0, "nothing dropped when there's room");
            Check(r.S.MaxWaitMs <= 70, "frames never wait long, even before the average settles (" + r.S.MaxWaitMs + " ms)");
        }

        static void KeyframeReplacesOldFrames()
        {
            Rig r = new Rig();
            r.S.SetVideoCap(SendScheduler.MinRateBytes);
            r.S.Step(0);
            r.S.EnqueueVideoAt(0, Frame(1, 50, 1100), false);
            r.S.Step(5); // Frame 1 has started.
            r.S.EnqueueVideoAt(6, Frame(2, 50, 1100), false);
            r.S.EnqueueVideoAt(7, Frame(3, 50, 1100), false);
            r.S.EnqueueVideoAt(8, Frame(4, 10, 1100), true);
            r.S.SetVideoCap(0);
            r.Run(9, 500);
            Check(r.Sent.Contains(1) && r.Sent.FindAll(delegate(int n) { return n == 1; }).Count == 50, "frame already being sent is finished");
            Check(!r.Sent.Contains(2) && !r.Sent.Contains(3), "waiting frames are dropped when a keyframe replaces them");
            Check(r.Sent.Contains(4), "the keyframe goes out");
            Check(r.S.DroppedFrames == 2, "drops are counted (" + r.S.DroppedFrames + ")");
            Check(r.DropSignals == 0, "no extra keyframe is asked for: one is already on its way");
        }

        static void StaleFramesDropAndAskForKeyframe()
        {
            Rig r = new Rig();
            r.S.SetVideoCap(SendScheduler.MinRateBytes); // A congested link.
            r.S.Step(0);
            int number = 0;
            for (long t = 1; t <= 1000; t++)
            {
                if (t % 16 == 0)
                    r.S.EnqueueVideoAt(t, Frame(++number, 20, 1100), false); // Far more than 1 Mbps.
                r.S.Step(t);
            }
            Check(r.S.DroppedFrames > 0, "frames too late to matter are dropped (" + r.S.DroppedFrames + ")");
            Check(r.DropSignals > 0, "the sharer is told, so it can send a keyframe");
            // 20 packets at about 62 frames a second, for the 300 ms a frame may wait.
            Check(r.S.QueuedVideoBytes < 500000, "the backlog stays bounded (" + r.S.QueuedVideoBytes + " bytes)");
            long rate = r.S.CurrentRateBytes(true);
            Check(rate == SendScheduler.MinRateBytes, "the viewers' cap is respected (" + rate + " B/s)");
        }

        static void KeyframesSurviveStaleness()
        {
            Rig r = new Rig();
            r.S.SetVideoCap(SendScheduler.MinRateBytes);
            r.S.Step(0);
            r.S.EnqueueVideoAt(0, Frame(1, 300, 1100), true);
            r.Run(1, 5000);
            Check(r.Sent.Count == 300, "a keyframe is never dropped for being slow (" + r.Sent.Count + "/300)");
        }

        static void CapLimitsThroughput()
        {
            Rig r = new Rig();
            r.S.SetVideoCap(250000);
            r.S.Step(0);
            r.S.EnqueueVideoAt(0, Frame(1, 2000, 1100), true);
            r.Run(1, 1000);
            long bytes = r.S.SentBytes;
            Check(bytes > 180000 && bytes < 320000, "a 250 KB/s cap holds over a second (" + bytes + " bytes)");
        }

        static void RelayIsIndependentAndBounded()
        {
            Rig r = new Rig();
            r.S.SetVideoCap(SendScheduler.MinRateBytes);
            r.S.Step(0);
            r.S.EnqueueVideoAt(0, Frame(1, 500, 1100), true);
            r.S.EnqueueRelayAt(0, "bob", new byte[1100]);
            r.Run(1, 100);
            Check(r.Relayed.Count == 1, "a capped sharer doesn't hold up relayed packets");

            Rig stale = new Rig();
            stale.S.Step(0);
            for (int i = 0; i < 5000; i++)
                stale.S.EnqueueRelayAt(0, "bob", new byte[1100]); // 5.8 MB: over the limit.
            stale.S.Step(1);
            Check(stale.S.DroppedRelayPackets > 0, "relay backlog is bounded (" + stale.S.DroppedRelayPackets + " dropped)");
            Check(stale.DropSignals == 0, "relay drops don't ask this PC's encoder for keyframes");
        }

        static void IdleEarnsNoBurst()
        {
            Rig r = new Rig();
            r.S.Step(0);
            r.Run(1, 5000);
            r.S.EnqueueVideoAt(5000, Frame(1, 100, 1100), false);
            int most = r.Run(5001, 5002);
            Check(most < 20, "a long quiet spell doesn't save up a burst (" + most + ")");
        }

        static void RealThread()
        {
            Rig r = new Rig();
            DateTime start = DateTime.UtcNow;
            r.S.EnqueueVideo(Frame(1, 900, 1100), true); // ~1 MB.
            double enqueueMs = (DateTime.UtcNow - start).TotalMilliseconds;
            while ((DateTime.UtcNow - start).TotalSeconds < 5)
            {
                lock (r.Sent)
                    if (r.Sent.Count == 900) break;
                Thread.Sleep(2);
            }
            double doneMs = (DateTime.UtcNow - start).TotalMilliseconds;
            Check(r.Sent.Count == 900, "real thread sends everything (" + r.Sent.Count + "/900)");
            Check(enqueueMs < 20, "handing over a frame doesn't hold up the encoder (" + enqueueMs.ToString("F1") + " ms)");
            Check(doneMs > 40 && doneMs < 500, "real thread paces it (" + (int)doneMs + " ms)");
            r.S.Dispose();
            r.S.EnqueueVideo(Frame(2, 1, 100), false);
            Thread.Sleep(50);
            Check(!r.Sent.Contains(2), "nothing goes out after shutdown");
        }

        static int Main()
        {
            SpreadsAKeyframe();
            FinishesWithinDrainTime();
            SustainedVideoKeepsUp();
            KeyframeReplacesOldFrames();
            StaleFramesDropAndAskForKeyframe();
            KeyframesSurviveStaleness();
            CapLimitsThroughput();
            RelayIsIndependentAndBounded();
            IdleEarnsNoBurst();
            RealThread();

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All send scheduler checks passed." : _failures + " send scheduler check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
