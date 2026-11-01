using System;
using System.Diagnostics;
using System.IO;

namespace Maela.Tests
{
    // Screen-share video: bzip2 (Cisco ships OpenH264 compressed), colour conversion, and on
    // Windows the real OpenH264 download, hash checks, encoding and decoding.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class CodecTests
    {
        static int _failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) _failures++;
        }

        const string Sample = "QlpoOTFBWSZTWYrfjbEADIv/////////////////////////////////////////////0APeAAAAAkwAEwABMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEmAAmAAJgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkwAEwABMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEmAAmAAJgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFKqqMgGjQAExoAAAEaYaBoAAAAAACaYJgBoAJgmAAAAAAAAAAAAAmIyYQwAMJ+1GIkwBMCTEiYmTFCYqTFiYuTGCYImMkxomDJjZMcJjpMeJj5MgJkJMiJkZMkJkpMmJ/AmTkygmUkwhNwysmzlIz4pGApIy0mXEy8mYEzEmZEzMmaEzUmbEzcmcEzkmdEzsmeEz0n9CkZ+THyaAT+JP5E0En8yaETQyaITRSYUmjE0cmkE/OaThiwxMQS0smmE00mnE08moE1EmpE1MmqE1UmrE1cmsE1kmtE1smuE10mvE18mwE2EmxE2MmyE2UmzE2cm0E3MpG1EwxNKKRtZMQKRtxSNtJuJSNvJgCbgTAlI3EmJE30pG5kwJSN0J0xSNVJgyYKkjfv6k3YmLFI3cmlE3gm8k3on9ib2TfCb6TQd+JkxSMGTISb+TgCcCTgicGThCYcnCk4YnDk4gnEk4onFk4wnGk44n9yceTkCciTkicmT/BOUJypOWJy5OYJzJOaJzZOcJzpOeJz5OgJ0JOiJ0ZOkJ0pPDKR05MKTqCdSTqidWTrCdaTrideTsCdiTsidmTtCdqTtiduTuCdyTuid2TvCd6Tvid+TwCeCTwia4UjwyZSTxCeKTxieOTyCeSTyieWTzCeaTzieeT0CeiT0iemT1CeqT1ieuT2CeyT2ie2T3Ce6T3ie+T4CfCT4if5J8ZPkJ8pP9E/2T5ifOT/hPoJ9JPqJ9ZP+k/8T/5PsJ9pPuJ95PwJ+JPyJh5+ZO7J+hP1J+xP3F3JFOFCQit+NsQ";
        const string SampleSha = "44e035d254d4afc623b7a7e52bd41c15303358a0daa56c122c2bc311c1cea90b";

        // A moving, detailed test picture: gradient, text-like stripes and a moving box.
        static void Paint(byte[] bgra, int w, int h, int t)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    bool box = x >= 100 + t * 6 && x < 260 + t * 6 && y >= 120 && y < 280;
                    bool stripe = (y / 3) % 7 == 0 && x % 9 < 6 && y > 400;
                    bgra[i] = (byte)(box ? 40 : stripe ? 230 : (x * 255 / w));
                    bgra[i + 1] = (byte)(box ? 200 : stripe ? 230 : (y * 255 / h));
                    bgra[i + 2] = (byte)(box ? 90 : stripe ? 230 : ((x + y + t * 4) & 255));
                    bgra[i + 3] = 255;
                }
        }

        static double Psnr(byte[] a, byte[] b)
        {
            double se = 0; long n = 0;
            for (int i = 0; i < a.Length; i += 4)
                for (int c = 0; c < 3; c++) { double d = a[i + c] - b[i + c]; se += d * d; n++; }
            return 10 * Math.Log10(255.0 * 255.0 / Math.Max(1e-9, se / n));
        }

        static unsafe I420Frame ToI420(byte[] bgra, int w, int h)
        {
            I420Frame f = new I420Frame(w, h);
            fixed (byte* p = bgra) ColorConvert.BgraToI420(new IntPtr(p), w * 4, f);
            return f;
        }

        static unsafe byte[] ToBgra(I420Frame f)
        {
            byte[] o = new byte[f.Width * f.Height * 4];
            fixed (byte* p = o) ColorConvert.I420ToBgra(f, new IntPtr(p), f.Width * 4);
            return o;
        }

        static int Main()
        {
            Console.WriteLine("bzip2");
            byte[] plain = Bzip2.Decompress(Convert.FromBase64String(Sample), 1 << 20);
            Check(OpenH264Pack.Sha256Hex(plain) == SampleSha, "decompresses a bzip2 file exactly (" + plain.Length + " bytes)");
            byte[] bad = Convert.FromBase64String(Sample);
            bad[bad.Length / 2] ^= 0x10;
            bool rejected = false;
            try { Bzip2.Decompress(bad, 1 << 20); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "a damaged bzip2 file is rejected");
            rejected = false;
            try { Bzip2.Decompress(Convert.FromBase64String(Sample), 1000); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "output beyond the size cap is refused");

            Console.WriteLine();
            Console.WriteLine("Colour conversion");
            int w = 1280, h = 720;
            byte[] src = new byte[w * h * 4];
            Paint(src, w, h, 0);
            byte[] round = ToBgra(ToI420(src, w, h));
            double p = Psnr(src, round);
            Check(p > 28, "a detailed picture survives the trip to video and back (" + p.ToString("F1") + " dB; colour is stored at half resolution, as in all video)");
            int worstErr = 0;
            int[][] colours = { new int[] { 0, 0, 0 }, new int[] { 255, 255, 255 }, new int[] { 255, 0, 0 }, new int[] { 0, 255, 0 }, new int[] { 0, 0, 255 }, new int[] { 230, 60, 70 }, new int[] { 40, 44, 52 } };
            foreach (int[] c in colours)
            {
                byte[] flat = new byte[64 * 64 * 4];
                for (int i = 0; i < flat.Length; i += 4) { flat[i] = (byte)c[2]; flat[i + 1] = (byte)c[1]; flat[i + 2] = (byte)c[0]; flat[i + 3] = 255; }
                byte[] back = ToBgra(ToI420(flat, 64, 64));
                for (int ch = 0; ch < 3; ch++) worstErr = Math.Max(worstErr, Math.Abs(back[ch] - flat[ch]));
            }
            Check(worstErr <= 4, "solid colours come back accurately (within " + worstErr + " of 255)");
            Stopwatch sw = Stopwatch.StartNew();
            I420Frame big = new I420Frame(1920, 1080);
            byte[] fullHd = new byte[1920 * 1080 * 4];
            for (int i = 0; i < 20; i++) unsafe { fixed (byte* q = fullHd) ColorConvert.BgraToI420(new IntPtr(q), 1920 * 4, big); }
            double ms = sw.Elapsed.TotalMilliseconds / 20;
            Console.WriteLine("     1080p conversion: " + ms.ToString("F1") + " ms per frame");
            Check(ms < 14, "fast enough for 1080p60 (" + ms.ToString("F1") + " ms per frame)");

            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Console.WriteLine();
                Console.WriteLine("(OpenH264 checks run on Windows only)");
                return Finish();
            }

            Console.WriteLine();
            Console.WriteLine("OpenH264 from Cisco (" + OpenH264Pack.FileName + ")");
            AssetStore.RootOverrideForTests = Path.Combine(Path.GetTempPath(), "maela-codec-test");
            if (OpenH264Pack.Win64DllSha256.Length != 64)
            {
                string[] facts = OpenH264Pack.DescribeDownload();
                Console.WriteLine("     PIN: bz2 " + facts[0] + "  dll " + facts[1] + "  (" + facts[2] + " bytes)");
                Check(false, "OpenH264 hashes are pinned");
                return Finish();
            }
            try { OpenH264Pack.Install(); } catch (Exception ex) { Console.WriteLine("     " + ex.Message); }
            Check(OpenH264Pack.Verify(), "downloaded from Cisco, both checksums match, installed");
            string saved = OpenH264Pack.Win64DllSha256;
            OpenH264Pack.Win64DllSha256 = new string('0', 64);
            OpenH264Pack.Win32DllSha256 = new string('0', 64);
            Check(!OpenH264Pack.Verify(), "a library that doesn't match its checksum is never loaded");
            OpenH264Pack.Win64DllSha256 = saved;
            OpenH264Pack.Win32DllSha256 = saved;
            if (!OpenH264Pack.Verify())
                return Finish();

            using (H264Encoder encoder = new H264Encoder(w, h, 60, 6000))
            using (H264Decoder decoder = new H264Decoder())
            {
                int frames = 0, decoded = 0, keyframes = 0;
                long bytes = 0;
                double worst = 99;
                sw = Stopwatch.StartNew();
                for (int t = 0; t < 120; t++)
                {
                    Paint(src, w, h, t % 100);
                    bool key;
                    if (t == 60) encoder.ForceKeyframe();
                    byte[] au = encoder.Encode(ToI420(src, w, h), t * 16, out key);
                    if (au == null) continue;
                    frames++; bytes += au.Length; if (key) keyframes++;
                    I420Frame back = decoder.Decode(au);
                    if (back == null) continue;
                    decoded++;
                    if (t % 20 == 5) worst = Math.Min(worst, Psnr(src, ToBgra(back)));
                }
                double perFrame = sw.Elapsed.TotalMilliseconds / 120;
                Console.WriteLine("     720p: " + perFrame.ToString("F1") + " ms per frame (encode + decode + convert), " + (bytes * 8 * 60 / 120 / 1000) + " kbps");
                Check(frames >= 115 && decoded >= frames - 2, "encodes and decodes 720p video (" + frames + " encoded, " + decoded + " decoded)");
                Check(keyframes >= 2, "keyframes on request (" + keyframes + ")");
                Check(worst > 30, "picture quality is good (" + worst.ToString("F1") + " dB at worst)");

                // A viewer that missed frames resumes cleanly from the next keyframe.
                encoder.ForceKeyframe();
                Paint(src, w, h, 3);
                bool k2;
                byte[] key2 = encoder.Encode(ToI420(src, w, h), 9999, out k2);
                using (H264Decoder fresh = new H264Decoder())
                    Check(k2 && fresh.Decode(key2) != null, "a new viewer can start from a keyframe");

                // Damaged input must not crash (the real viewer also runs in its own process).
                byte[] damaged = (byte[])key2.Clone();
                Random r = new Random(1);
                for (int i = 0; i < 40; i++) damaged[64 + r.Next(damaged.Length - 64)] ^= (byte)(1 << r.Next(8));
                decoder.Decode(damaged);
                decoder.Decode(new byte[] { 0, 0, 0, 1, 0x65, 1, 2, 3 });
                Check(true, "damaged video doesn't crash the decoder");
            }

            using (H264Encoder hd = new H264Encoder(1920, 1080, 60, 8000))
            {
                byte[] frame = new byte[1920 * 1080 * 4];
                Paint(frame, 1920, 1080, 1);
                I420Frame f = ToI420(frame, 1920, 1080);
                bool key;
                hd.Encode(f, 0, out key);
                sw = Stopwatch.StartNew();
                for (int t = 0; t < 30; t++) { Paint(frame, 1920, 1080, t); hd.Encode(ToI420(frame, 1920, 1080), t * 16, out key); }
                Console.WriteLine("     1080p encode incl. painting: " + (sw.Elapsed.TotalMilliseconds / 30).ToString("F1") + " ms per frame on this CI machine");
            }
            return Finish();
        }

        static int Finish()
        {
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All codec checks passed." : _failures + " codec check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
