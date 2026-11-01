using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

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
                Check(worst > 26, "picture quality is good (" + worst.ToString("F1") + " dB at worst; this sharp test pattern scores about 29.5 before any compression)");

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

            EndToEnd(w, h);
            Sandbox();
            CaptureProbe();

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

        // Real video through a real room: encode, send, receive, decode, compare.
        static void EndToEnd(int w, int h)
        {
            Console.WriteLine();
            Console.WriteLine("Screen share end to end (encode, send through a room, decode)");
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.StartHost(0, "Host", "Test", new System.Collections.Generic.List<string>(new string[] { "General" }), VoipEngineTestKeys.HostPrivateKey, null);
            VoipEngine viewer = new VoipEngine();
            viewer.HeadlessForTests = true;
            viewer.StartClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, host.BoundPort), "Viewer", VoipEngineTestKeys.HostFingerprint, null);
            try
            {
                DateTime until = DateTime.UtcNow.AddSeconds(8);
                while (!viewer.IsInVoice && DateTime.UtcNow < until) System.Threading.Thread.Sleep(20);
                host.StartScreenShare(w, h, 60);
                until = DateTime.UtcNow.AddSeconds(5);
                while (viewer.GetLiveStream(host.LocalMemberId) == null && DateTime.UtcNow < until) System.Threading.Thread.Sleep(20);
                H264Decoder decoder = new H264Decoder();
                int decoded = 0;
                double worst = 99;
                byte[] lastSent = null;
                object gate = new object();
                viewer.VideoFrameReceived = delegate(uint from, byte[] f, bool k)
                {
                    I420Frame pic = decoder.Decode(f);
                    if (pic == null) return;
                    lock (gate)
                    {
                        decoded++;
                        if (lastSent != null) worst = Math.Min(worst, Psnr(lastSent, ToBgra(pic)));
                    }
                };
                viewer.WatchStream(host.LocalMemberId);
                until = DateTime.UtcNow.AddSeconds(5);
                while (host.StreamViewerCount == 0 && DateTime.UtcNow < until) System.Threading.Thread.Sleep(20);
                using (H264Encoder encoder = new H264Encoder(w, h, 60, 6000))
                {
                    byte[] src = new byte[w * h * 4];
                    for (int t = 0; t < 90; t++)
                    {
                        Paint(src, w, h, t % 100);
                        if (host.ConsumeKeyframeRequest()) encoder.ForceKeyframe();
                        bool key;
                        byte[] au = encoder.Encode(ToI420(src, w, h), t * 16, out key);
                        if (au == null) continue;
                        lock (gate) lastSent = (byte[])src.Clone();
                        host.SendVideoFrame(au, key);
                        System.Threading.Thread.Sleep(16);
                    }
                }
                System.Threading.Thread.Sleep(500);
                lock (gate)
                {
                    Check(decoded >= 80, "the viewer decodes the stream (" + decoded + " of 90 frames)");
                    Check(worst > 26, "what the viewer sees matches the screen (" + worst.ToString("F1") + " dB at worst)");
                }
                decoder.Dispose();
            }
            finally
            {
                viewer.Dispose();
                host.Dispose();
            }
        }

        // The viewer runs at Low integrity in a locked-down job: prove Windows enforces it.
        static void Sandbox()
        {
            Console.WriteLine();
            Console.WriteLine("Viewer sandbox");
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            IntPtr job;
            bool lowIntegrity;
            try
            {
                // 1. Integrity level, read from the process's own token.
                Process idle = SandboxLauncher.Start(cmd, "cmd.exe /k", out job, out lowIntegrity);
                System.Threading.Thread.Sleep(500);
                int level = IntegrityLevel(idle.Id);
                Check(lowIntegrity && level == 0x1000, "the viewer process runs at Low integrity (level 0x" + level.ToString("X") + ")");
                SandboxLauncher.CloseHandle(job); // Closing the job ends everything in it.
                System.Threading.Thread.Sleep(500);
                Check(idle.HasExited, "closing mæla's side ends the viewer");

                // 2. Can't write to your folders, can't start other programs.
                string blocked = Path.Combine(Path.GetTempPath(), "maela-sandbox-should-not-exist.txt");
                string low = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData\\LocalLow\\maela-sandbox-child.txt");
                try { File.Delete(blocked); } catch { }
                try { File.Delete(low); } catch { }
                Process p = SandboxLauncher.Start(cmd, "cmd.exe /c echo x > \"" + blocked + "\" & whoami > \"" + low + "\"", out job, out lowIntegrity);
                p.WaitForExit(15000);
                Check(!File.Exists(blocked), "it can't write to your normal folders");
                Check(!File.Exists(low) || new FileInfo(low).Length == 0, "it can't start other programs");
                SandboxLauncher.CloseHandle(job);
                try { File.Delete(low); } catch { }
            }
            catch (Exception ex)
            {
                Check(false, "sandboxed process starts (" + ex.Message + ")");
            }
        }

        static int IntegrityLevel(int pid)
        {
            IntPtr process = OpenProcess(0x1000, false, pid), token;
            if (process == IntPtr.Zero || !OpenProcessToken(process, 0x8, out token))
                return -1;
            IntPtr buffer = Marshal.AllocHGlobal(256);
            try
            {
                int length;
                if (!GetTokenInformation(token, 25, buffer, 256, out length))
                    return -2;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, count - 1));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
        [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

        // Information only: build servers usually have no real display to duplicate.
        static void CaptureProbe()
        {
            Console.WriteLine();
            Console.WriteLine("Screen capture on this machine (information only)");
            foreach (CaptureSource s in CaptureSource.Monitors())
            {
                try
                {
                    using (IScreenCapture c = ScreenCaptureFactory.Create(s))
                    {
                        bool got = false;
                        for (int i = 0; i < 20 && !got; i++)
                            got = c.Capture(50, true, delegate(IntPtr bits, int stride) { });
                        Console.WriteLine("     " + s.Name + ": " + c.Method + ", " + c.Width + "x" + c.Height + (got ? ", frame captured" : ", no frame (no active display)"));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("     " + s.Name + ": " + ex.Message);
                }
            }
        }

        static int Finish()
        {
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All codec checks passed." : _failures + " codec check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
