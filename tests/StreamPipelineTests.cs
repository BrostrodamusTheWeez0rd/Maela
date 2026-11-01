using System;
using System.Collections.Generic;
using System.Threading;

namespace Maela.Tests
{
    /// <summary>The frame hand-over, the viewer's playout buffer and the bitrate controller, without any network.</summary>
    internal static class StreamPipelineTests
    {
        static int _failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok)
                _failures++;
        }

        sealed class Item
        {
            public int N;
            public Item(int n) { N = n; }
        }

        static void Exchange()
        {
            FrameExchange<Item> x = new FrameExchange<Item>();
            Item current = null;
            Check(!x.Take(ref current, 1) && current == null, "nothing to encode at first");

            Item a = new Item(1), b = new Item(2), c = new Item(3);
            Check(x.Publish(a) == null, "first frame: capture has to make a new buffer");
            Check(x.Take(ref current, 1) && current == a, "encoder gets the frame");
            Check(!x.Take(ref current, 1), "and only once");

            x.Tick();
            Check(x.Take(ref current, 1) && current == a, "an unchanged picture is encoded again on the beat");

            Check(x.Publish(b) == null, "second frame");
            Item back = x.Publish(c);
            Check(back == b && x.Replaced == 1, "a frame the encoder didn't reach is replaced and reused");
            Check(x.Take(ref current, 1) && current == c, "encoder always gets the newest");
            back = x.Publish(new Item(4));
            Check(back == a, "the encoder's old buffer comes back for reuse");

            x.Close();
            Check(!x.Take(ref current, 1000), "closing ends the wait");

            // Real threads: a slow encoder doesn't hold up capture, and frames never go backwards.
            FrameExchange<Item> y = new FrameExchange<Item>();
            int published = 0, encoded = 0, lastSeen = 0;
            bool order = true;
            Thread encoder = new Thread(delegate()
            {
                Item f = null;
                while (y.Take(ref f, 50) || published < 300)
                {
                    if (f == null) continue;
                    if (f.N < lastSeen) order = false;
                    lastSeen = f.N;
                    encoded++;
                    Thread.Sleep(5);
                }
            });
            encoder.Start();
            DateTime start = DateTime.UtcNow;
            for (int i = 1; i <= 300; i++)
            {
                y.Publish(new Item(i));
                published = i;
            }
            double captureMs = (DateTime.UtcNow - start).TotalMilliseconds;
            Thread.Sleep(100);
            y.Close();
            encoder.Join(2000);
            Check(captureMs < 100, "capture never waits for a slow encoder (" + (int)captureMs + " ms for 300 frames)");
            Check(order && lastSeen == 300, "the encoder ends on the newest frame, in order (" + lastSeen + ", " + encoded + " encoded)");
        }

        // Runs arrivals through the buffer on a millisecond clock; returns the time each frame was shown.
        static List<long> Play(long[] arrivals, long until, JitterBuffer<Item> buffer)
        {
            List<long> shown = new List<long>();
            int next = 0;
            for (long t = 0; t <= until; t++)
            {
                while (next < arrivals.Length && arrivals[next] <= t)
                    buffer.Push(t, new Item(next++));
                if (buffer.Pop(t) != null)
                    shown.Add(t);
            }
            return shown;
        }

        static double GapSpread(List<long> shown, int skip)
        {
            double sum = 0, count = 0;
            List<double> gaps = new List<double>();
            for (int i = skip + 1; i < shown.Count; i++)
                gaps.Add(shown[i] - shown[i - 1]);
            foreach (double g in gaps) { sum += g; count++; }
            double mean = sum / count, variance = 0;
            foreach (double g in gaps) variance += (g - mean) * (g - mean);
            return Math.Sqrt(variance / count);
        }

        static void Jitter()
        {
            // A steady stream is shown at once, without a cushion it doesn't need.
            long[] steady = new long[600];
            for (int i = 0; i < 600; i++) steady[i] = i * 1000 / 60;
            JitterBuffer<Item> buffer = new JitterBuffer<Item>(1000.0 / 60, 12);
            List<long> shown = Play(steady, 11000, buffer);
            Check(shown.Count == 600 && buffer.Dropped == 0, "every frame of a steady stream is shown (" + shown.Count + ")");
            Check(buffer.TargetDelayMs <= 5, "a steady stream adds almost no delay (" + buffer.TargetDelayMs + " ms)");

            // Bursty: frames arrive in uneven clumps; they should come out far more evenly.
            Random r = new Random(7);
            long[] bursty = new long[1200];
            double arrivalSum = 0, arrivalVariance = 0;
            for (int i = 0; i < 1200; i++)
                bursty[i] = i * 1000 / 60 + r.Next(0, 40);
            Array.Sort(bursty);
            for (int i = 1; i < 1200; i++) arrivalSum += bursty[i] - bursty[i - 1];
            double mean = arrivalSum / 1199;
            for (int i = 1; i < 1200; i++) arrivalVariance += (bursty[i] - bursty[i - 1] - mean) * (bursty[i] - bursty[i - 1] - mean);
            double before = Math.Sqrt(arrivalVariance / 1199);
            buffer = new JitterBuffer<Item>(1000.0 / 60, 12);
            shown = Play(bursty, 22000, buffer);
            double after = GapSpread(shown, 120);
            Check(after < before * 0.6, "uneven arrivals come out more evenly (spread " + before.ToString("0.0") + " to " + after.ToString("0.0") + " ms)");
            Check(buffer.TargetDelayMs > 0 && buffer.TargetDelayMs <= JitterBuffer<Item>.MaxDelayMs, "the cushion grows to suit (" + buffer.TargetDelayMs + " ms)");
            Check(shown.Count >= 1200 * 95 / 100, "almost nothing is lost doing it (" + shown.Count + " of 1200)");

            // A pile of frames is worked off, and bounded.
            buffer = new JitterBuffer<Item>(1000.0 / 60, 12);
            for (int i = 0; i < 40; i++)
                buffer.Push(0, new Item(i));
            Check(buffer.Count == 12 && buffer.Dropped == 28, "too many frames at once: the oldest go (" + buffer.Count + " kept)");
            int popped = 0;
            for (long t = 0; t < 250; t++)
                if (buffer.Pop(t) != null) popped++;
            Check(popped == 12, "and the backlog plays out quickly (" + popped + ")");

            buffer = new JitterBuffer<Item>(1000.0 / 60, 3);
            int discarded = 0;
            buffer.Discard = delegate(Item dropped) { discarded++; };
            for (int i = 0; i < 5; i++)
                buffer.Push(0, new Item(i));
            Check(discarded == 2, "dropped frames are handed back, so graphics memory is freed (" + discarded + ")");

            // Running dry is noticed, and it recovers.
            buffer = new JitterBuffer<Item>(1000.0 / 60, 12);
            long[] gap = new long[120];
            for (int i = 0; i < 120; i++) gap[i] = i * 1000 / 60 + (i >= 60 ? 400 : 0);
            shown = Play(gap, 3000, buffer);
            Check(buffer.Underruns >= 1 && shown.Count == 120, "a stall is counted and playing resumes (" + buffer.Underruns + " stalls, " + shown.Count + " shown)");

            // A slower real frame rate is learned.
            buffer = new JitterBuffer<Item>(1000.0 / 60, 12);
            long[] thirty = new long[300];
            for (int i = 0; i < 300; i++) thirty[i] = i * 1000 / 30;
            shown = Play(thirty, 11000, buffer);
            Check(Math.Abs(buffer.IntervalMs - 33.3) < 3 && shown.Count == 300, "the real frame rate is learned (" + buffer.IntervalMs.ToString("0.0") + " ms)");
        }

        static void Bitrate()
        {
            BitrateController b = new BitrateController(10000);
            Check(b.CurrentKbps == 10000, "starts at full quality");
            for (long t = 0; t < 60000; t += 500)
                b.Update(t, 0, 0);
            Check(b.CurrentKbps == 10000, "a quiet screen, with no loss, keeps full quality");

            b = new BitrateController(10000);
            b.Update(0, 50, 0);
            Check(b.CurrentKbps == 8500, "loss lowers it (" + b.CurrentKbps + ")");
            b.Update(500, 50, 0);
            Check(b.CurrentKbps == 8500, "but waits to see the last cut take effect");
            b.Update(1000, 200, 0);
            Check(b.CurrentKbps == 5950, "heavy loss lowers it faster (" + b.CurrentKbps + ")");

            int before = b.CurrentKbps;
            b.Update(2000, 0, 0);
            Check(b.CurrentKbps == before, "no climbing straight after a cut");
            for (long t = 4500; t < 60000; t += 500)
                b.Update(t, 0, 0);
            Check(b.CurrentKbps == 10000, "and it climbs all the way back (" + b.CurrentKbps + ")");

            b = new BitrateController(10000);
            b.Update(0, 0, 100);
            Check(b.CurrentKbps == 9000, "rising delay lowers it before anything is lost (" + b.CurrentKbps + ")");
            b.Update(1000, 0, 30);
            Check(b.CurrentKbps == 9000, "ordinary delay doesn't");

            b = new BitrateController(10000);
            for (long t = 0; t < 60000; t += 1000)
                b.Update(t, 500, 0);
            Check(b.CurrentKbps == BitrateController.MinKbps, "never falls below the floor (" + b.CurrentKbps + ")");

            b = new BitrateController(10000);
            b.SetMax(6000);
            Check(b.CurrentKbps == 6000, "follows a lower maximum");
        }

        static void Parity()
        {
            byte[] a = { 1, 2, 3, 4 }, b = { 9, 8, 7 }, c = { 5, 5 };
            byte[] parity = new byte[4];
            FecXor.Mix(parity, 0, a, 0, 4);
            FecXor.Mix(parity, 0, b, 0, 3);
            FecXor.Mix(parity, 0, c, 0, 2);
            byte[] rebuilt = (byte[])parity.Clone();
            FecXor.Mix(rebuilt, 0, a, 0, 4);
            FecXor.Mix(rebuilt, 0, c, 0, 2);
            Check(rebuilt[0] == 9 && rebuilt[1] == 8 && rebuilt[2] == 7, "parity rebuilds a missing fragment");
            Check(FecXor.GroupSizeForLoss(0) == 0, "no loss, no parity");
            Check(FecXor.GroupSizeForLoss(10) > FecXor.GroupSizeForLoss(100) && FecXor.GroupSizeForLoss(100) > 0, "more loss, more parity");
        }

        // Builds an SPS: bits are written big-endian, emulation prevention added.
        sealed class BitWriter
        {
            readonly List<byte> _bytes = new List<byte>();
            int _bit;
            byte _current;
            public void U(int n, int v) { for (int i = n - 1; i >= 0; i--) Bit((v >> i) & 1); }
            public void Ue(int v)
            {
                int x = v + 1, bits = 0;
                while ((x >> bits) > 1) bits++;
                U(bits, 0);
                U(bits + 1, x);
            }
            void Bit(int b)
            {
                _current = (byte)((_current << 1) | b);
                if (++_bit == 8) { _bytes.Add(_current); _bit = 0; _current = 0; }
            }
            public byte[] Done()
            {
                Bit(1);
                while (_bit != 0) Bit(0);
                List<byte> o = new List<byte>();
                int zeros = 0;
                foreach (byte b in _bytes)
                {
                    if (zeros >= 2 && b <= 3) { o.Add(3); zeros = 0; }
                    o.Add(b);
                    zeros = b == 0 ? zeros + 1 : 0;
                }
                return o.ToArray();
            }
        }

        static byte[] Sps(int profile, int level, int mbWide, int mbHigh, int cropBottom, bool progressive, int chroma)
        {
            BitWriter w = new BitWriter();
            w.U(8, profile); w.U(8, 0); w.U(8, level);
            w.Ue(0);
            if (profile == 100) { w.Ue(chroma); w.Ue(0); w.Ue(0); w.U(1, 0); w.U(1, 0); }
            w.Ue(0); w.Ue(2); w.Ue(1); w.U(1, 0);
            w.Ue(mbWide - 1); w.Ue(mbHigh - 1);
            w.U(1, progressive ? 1 : 0);
            if (!progressive) w.U(1, 0);
            w.U(1, 1);
            if (cropBottom > 0) { w.U(1, 1); w.Ue(0); w.Ue(0); w.Ue(0); w.Ue(cropBottom); }
            else w.U(1, 0);
            w.U(1, 0);
            return w.Done();
        }

        static byte[] Nal(int type, byte[] body)
        {
            byte[] o = new byte[5 + body.Length];
            o[3] = 1;
            o[4] = (byte)(0x60 | type);
            Buffer.BlockCopy(body, 0, o, 5, body.Length);
            return o;
        }

        static byte[] Join(params byte[][] parts)
        {
            List<byte> all = new List<byte>();
            foreach (byte[] p in parts) all.AddRange(p);
            return all.ToArray();
        }

        static void StreamCheck()
        {
            byte[] good = Nal(7, Sps(66, 42, 120, 68, 4, true, 1));
            byte[] pps = Nal(8, new byte[] { 0xCE, 0x3C, 0x80 });
            byte[] idr = Nal(5, new byte[] { 0x88, 0x84, 1, 2, 3 });
            byte[] p = Nal(1, new byte[] { 0x9A, 1, 2 });
            int w = 0, h = 0;
            byte[] clean;
            Check(!H264StreamCheck.Check(p, ref w, ref h, out clean), "a picture before any stream settings is refused");
            Check(H264StreamCheck.Check(Join(good, pps, idr), ref w, ref h, out clean) && w == 1920 && h == 1080, "a normal keyframe passes, and its size is read with cropping (" + w + "x" + h + ")");
            Check(clean.Length == good.Length + pps.Length + idr.Length, "and is left as it was");
            Check(H264StreamCheck.Check(p, ref w, ref h, out clean), "ordinary frames pass");

            byte[] sei = Nal(6, new byte[] { 5, 4, 1, 2, 3, 4, 0x80 });
            Check(H264StreamCheck.Check(Join(sei, idr), ref w, ref h, out clean) && clean.Length == idr.Length, "optional data (SEI) is stripped before the driver sees it");

            int a, b;
            Func<byte[], bool> refused = delegate(byte[] d) { int x = 0, y = 0; byte[] c; return !H264StreamCheck.Check(d, ref x, ref y, out c); };
            Check(refused(Nal(7, Sps(88, 42, 120, 68, 0, true, 1))), "unexpected profiles are refused");
            Check(refused(Nal(7, Sps(66, 62, 120, 68, 0, true, 1))), "absurd levels are refused");
            Check(refused(Nal(7, Sps(66, 42, 1000, 68, 0, true, 1))), "pictures too wide are refused");
            Check(refused(Nal(7, Sps(66, 42, 320, 270, 0, true, 1))), "pictures over the size limit are refused");
            Check(refused(Nal(7, Sps(66, 42, 120, 68, 0, false, 1))), "interlaced video is refused");
            Check(refused(Nal(7, Sps(100, 42, 120, 68, 0, true, 3))), "4:4:4 video is refused");
            Check(!refused(Nal(7, Sps(100, 42, 120, 68, 4, true, 1))), "ordinary high profile passes");
            Check(refused(Nal(7, new byte[300])), "oversized stream settings are refused");
            Check(refused(Nal(7, new byte[] { 66 })), "cut-off stream settings are refused");
            Check(refused(Join(good, Nal(20, new byte[] { 1, 2, 3 }))), "unknown data types are refused");
            Check(refused(Join(good, Nal(11, new byte[] { 1 }))), "end-of-stream markers are refused");
            Check(refused(new byte[] { 9, 9, 9, 9 }), "data with no start code is refused");
            Check(refused(new byte[0]), "empty data is refused");

            // Random junk must never crash the parser.
            Random r = new Random(11);
            int passed = 0;
            for (int i = 0; i < 50000; i++)
            {
                byte[] junk = new byte[r.Next(1, 300)];
                r.NextBytes(junk);
                if (i % 2 == 0 && junk.Length > 5) { junk[0] = 0; junk[1] = 0; junk[2] = 1; junk[3] = (byte)(0x60 | 7); }
                int x = 0, y = 0;
                byte[] c;
                if (H264StreamCheck.Check(junk, ref x, ref y, out c))
                {
                    passed++;
                    if (x < 16 || y < 16 || x > H264StreamCheck.MaxWidth || y > H264StreamCheck.MaxHeight)
                        Check(false, "junk got through with an impossible size (" + x + "x" + y + ")");
                }
            }
            Check(true, "50,000 random inputs don't crash it (" + passed + " happened to parse, all within limits)");
        }

        static int Main()
        {
            Exchange();
            Jitter();
            Bitrate();
            Parity();
            StreamCheck();

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All stream pipeline checks passed." : _failures + " stream pipeline check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
