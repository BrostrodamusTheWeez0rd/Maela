using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;

namespace Maela.Tests
{
    // Profiles: status lines and pictures reach everyone, and a hostile member can't use them
    // to crash, flood or bloat anyone. Pictures travel as plain pixels, never image files.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class ProfileTests
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
                Thread.Sleep(25);
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

        // A detailed test picture (gradient + noise) so it doesn't compress to nothing.
        static byte[] TestPicture(int seed)
        {
            using (Bitmap b = new Bitmap(300, 200))
            {
                Random r = new Random(seed);
                for (int y = 0; y < 200; y++)
                    for (int x = 0; x < 300; x++)
                        b.SetPixel(x, y, Color.FromArgb((x + r.Next(40)) % 256, (y + r.Next(40)) % 256, (x * y / 50 + seed * 40) % 256));
                return ProfilePicture.FromImage(b);
            }
        }

        static byte[] Deflate(byte[] data)
        {
            MemoryStream m = new MemoryStream();
            using (DeflateStream d = new DeflateStream(m, CompressionMode.Compress, true))
                d.Write(data, 0, data.Length);
            return m.ToArray();
        }

        static byte[] Chunk(uint version, int index, int count, int total, byte[] data)
        {
            byte[] p = new byte[8 + data.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(version), 0, p, 0, 4);
            p[4] = (byte)index; p[5] = (byte)count;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)total), 0, p, 6, 2);
            Buffer.BlockCopy(data, 0, p, 8, data.Length);
            return p;
        }

        // A banner picture: a gradient with a little noise, like a real photo shrunk down.
        static byte[] TestBanner(int seed)
        {
            Random r = new Random(seed);
            byte[] pixels = new byte[VoipEngine.BannerRawBytes];
            for (int y = 0; y < VoipEngine.BannerHeight; y++)
                for (int x = 0; x < VoipEngine.BannerWidth; x++)
                {
                    int red = (x * 31 / VoipEngine.BannerWidth + seed) & 31, green = (y * 63 / VoipEngine.BannerHeight + r.Next(3)) & 63, blue = ((x + y) / 10) & 31;
                    int v = (red << 11) | (green << 5) | blue, i = (y * VoipEngine.BannerWidth + x) * 2;
                    pixels[i] = (byte)v;
                    pixels[i + 1] = (byte)(v >> 8);
                }
            return pixels;
        }

        static byte[] ExtrasHeader(byte format, byte banner, byte effect, byte deco)
        {
            return new byte[] { format, banner, effect, deco, 0x12, 0x34, 0x56, 0xAB, 0xCD, 0xEF };
        }

        static byte[] Join(byte[] a, byte[] b)
        {
            byte[] all = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, all, 0, a.Length);
            Buffer.BlockCopy(b, 0, all, a.Length, b.Length);
            return all;
        }

        static void SendChunked(VoipEngine from, byte type, uint version, byte[] blob)
        {
            int count = (blob.Length + 999) / 1000;
            for (int i = 0; i < count; i++)
            {
                byte[] part = new byte[Math.Min(1000, blob.Length - i * 1000)];
                Buffer.BlockCopy(blob, i * 1000, part, 0, part.Length);
                from.SendToHostForTests(type, Chunk(version, i, count, blob.Length, part));
            }
        }

        static int Main()
        {
            Console.WriteLine("Picture encoding (on your own PC)");
            byte[] pixels = TestPicture(1);
            Check(pixels.Length == VoipEngine.AvatarRawBytes, "picture becomes " + VoipEngine.AvatarSize + "x" + VoipEngine.AvatarSize + " plain pixels");
            using (Bitmap back = ProfilePicture.ToBitmap(pixels))
                Check(back != null && back.Width == VoipEngine.AvatarSize, "pixels display without any image decoder");
            Check(ProfilePicture.ToBitmap(new byte[10]) == null, "wrong-size pixels are refused");
            Check(VoipEngine.CleanStatus("hi‮evil\r\nthere\u0000") == "hievilthere", "status loses control and direction-flipping characters");
            Check(VoipEngine.CleanStatus(new string('x', 500)).Length == VoipEngine.MaxStatusChars, "status is capped at " + VoipEngine.MaxStatusChars + " characters");

            Console.WriteLine();
            Console.WriteLine("Hostile data never gets through");
            Random rng = new Random(7);
            int accepted = 0;
            for (int i = 0; i < 20000; i++)
            {
                byte[] junk = new byte[rng.Next(0, 3000)];
                rng.NextBytes(junk);
                if (junk.Length > 2 && rng.Next(3) == 0) { junk[0] = (byte)rng.Next(0, 10); junk[1 + junk[0] < junk.Length ? 1 + junk[0] : 0] = 1; }
                if (VoipEngine.DecodeProfile(junk) != null) accepted++;
            }
            Check(accepted == 0, "20,000 random profiles: none accepted (" + accepted + ")");
            byte[] zeros = Deflate(new byte[50 * 1024 * 1024]); // 50 MB of zeros, ~50 KB compressed.
            byte[] smallBomb = Deflate(new byte[VoipEngine.AvatarRawBytes * 40]);
            byte[] blob = new byte[2 + smallBomb.Length];
            blob[0] = 0; blob[1] = 1; Buffer.BlockCopy(smallBomb, 0, blob, 2, smallBomb.Length);
            DateTime t0 = DateTime.UtcNow;
            Check(VoipEngine.DecodeProfile(blob) == null && (DateTime.UtcNow - t0).TotalMilliseconds < 200, "decompression bomb refused quickly, nothing beyond the picture size is unpacked");
            Check(zeros.Length > 16 * 1024 && VoipEngine.DecodeProfile(zeros) == null, "oversized profile refused");
            byte[] tooShort = Deflate(new byte[VoipEngine.AvatarRawBytes - 2]);
            byte[] shortBlob = new byte[2 + tooShort.Length]; shortBlob[1] = 1; Buffer.BlockCopy(tooShort, 0, shortBlob, 2, tooShort.Length);
            Check(VoipEngine.DecodeProfile(shortBlob) == null, "picture with missing pixels refused");

            Console.WriteLine();
            Console.WriteLine("Banners and decos (extras)");
            byte[] banner = TestBanner(1);
            byte[] extrasBlob = VoipEngine.EncodeExtras(BannerKind.Picture, 0x112233, 0x445566, banner, CardEffect.Sparkles, PictureDeco.Orbit);
            MemberExtras decoded = VoipEngine.DecodeExtras(extrasBlob);
            Check(extrasBlob != null && extrasBlob.Length <= VoipEngine.MaxExtrasBytes, "a picture banner fits the limit (" + (extrasBlob == null ? 0 : extrasBlob.Length) + " bytes)");
            Check(decoded != null && decoded.Banner == BannerKind.Picture && decoded.Effect == CardEffect.Sparkles && decoded.Deco == PictureDeco.Orbit
                && decoded.ColourA == 0x112233 && decoded.ColourB == 0x445566 && decoded.Pixels != null && decoded.Pixels.Length == VoipEngine.BannerRawBytes,
                "picture banner, card deco and picture deco come back as sent");
            bool allKinds = true;
            for (int k = 0; k <= 2; k++)
                for (int e = 0; e <= 3; e++)
                    for (int d = 0; d <= 3; d++)
                    {
                        byte[] plain = VoipEngine.EncodeExtras((BannerKind)k, 0xABCDEF, 0x010203, null, (CardEffect)e, (PictureDeco)d);
                        MemberExtras back = VoipEngine.DecodeExtras(plain);
                        allKinds &= plain != null && plain.Length == 10 && back != null && (int)back.Banner == k && (int)back.Effect == e && (int)back.Deco == d
                            && back.ColourA == 0xABCDEF && back.ColourB == 0x010203 && back.Pixels == null;
                    }
            Check(allKinds, "every colour banner, card deco and picture deco round-trips in 10 bytes");
            Check(VoipEngine.EncodeExtras((BannerKind)4, 0, 0, null, CardEffect.None, PictureDeco.None) == null
                && VoipEngine.EncodeExtras(BannerKind.Auto, 0, 0, null, (CardEffect)4, PictureDeco.None) == null
                && VoipEngine.EncodeExtras(BannerKind.Auto, 0, 0, null, CardEffect.None, (PictureDeco)4) == null
                && VoipEngine.EncodeExtras(BannerKind.Picture, 0, 0, new byte[100], CardEffect.None, PictureDeco.None) == null
                && VoipEngine.EncodeExtras(BannerKind.Picture, 0, 0, null, CardEffect.None, PictureDeco.None) == null,
                "unknown kinds and wrong-size banner pixels are never sent");

            accepted = 0;
            for (int i = 0; i < 20000; i++)
            {
                byte[] junk = new byte[rng.Next(0, 4000)];
                rng.NextBytes(junk);
                if (junk.Length > 10 && rng.Next(2) == 0)
                {
                    // A believable header over junk: the part that has to be checked hardest.
                    junk[0] = 1; junk[1] = (byte)rng.Next(0, 4); junk[2] = (byte)rng.Next(0, 4); junk[3] = (byte)rng.Next(0, 4);
                }
                if (VoipEngine.DecodeExtras(junk) != null) accepted++;
            }
            Check(accepted == 0, "20,000 random extras, half with a believable header: none accepted (" + accepted + ")");
            Check(VoipEngine.DecodeExtras(ExtrasHeader(2, 1, 0, 0)) == null, "unknown format refused");
            Check(VoipEngine.DecodeExtras(ExtrasHeader(1, 4, 0, 0)) == null && VoipEngine.DecodeExtras(ExtrasHeader(1, 255, 0, 0)) == null, "out-of-range banner kind refused");
            Check(VoipEngine.DecodeExtras(ExtrasHeader(1, 1, 4, 0)) == null && VoipEngine.DecodeExtras(ExtrasHeader(1, 1, 255, 0)) == null, "out-of-range card deco refused");
            Check(VoipEngine.DecodeExtras(ExtrasHeader(1, 1, 0, 4)) == null && VoipEngine.DecodeExtras(ExtrasHeader(1, 1, 0, 255)) == null, "out-of-range picture deco refused");
            byte[] nine = new byte[9];
            Buffer.BlockCopy(ExtrasHeader(1, 1, 0, 0), 0, nine, 0, 9);
            Check(VoipEngine.DecodeExtras(nine) == null && VoipEngine.DecodeExtras(new byte[0]) == null && VoipEngine.DecodeExtras(null) == null, "short header refused");
            Check(VoipEngine.DecodeExtras(Join(ExtrasHeader(1, 2, 0, 0), new byte[1])) == null, "stray bytes after a colour banner refused");
            Check(VoipEngine.DecodeExtras(ExtrasHeader(1, 3, 0, 0)) == null, "picture banner without pixels refused");
            byte[] extrasBomb = Join(ExtrasHeader(1, 3, 0, 0), Deflate(new byte[VoipEngine.BannerRawBytes * 40]));
            t0 = DateTime.UtcNow;
            Check(extrasBomb.Length <= VoipEngine.MaxExtrasBytes && VoipEngine.DecodeExtras(extrasBomb) == null && (DateTime.UtcNow - t0).TotalMilliseconds < 200,
                "banner decompression bomb refused quickly");
            Check(VoipEngine.DecodeExtras(Join(ExtrasHeader(1, 3, 0, 0), Deflate(new byte[VoipEngine.BannerRawBytes - 2]))) == null, "banner with missing pixels refused");
            Check(VoipEngine.DecodeExtras(Join(ExtrasHeader(1, 3, 0, 0), Deflate(new byte[VoipEngine.BannerRawBytes + 1]))) == null, "banner with extra pixels refused");
            byte[] bigJunk = new byte[VoipEngine.MaxExtrasBytes + 1];
            new Random(9).NextBytes(bigJunk);
            Buffer.BlockCopy(ExtrasHeader(1, 3, 0, 0), 0, bigJunk, 0, 10);
            Check(VoipEngine.DecodeExtras(bigJunk) == null, "oversized extras refused");

            Console.WriteLine();
            Console.WriteLine("Sharing through a room");
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.StartHost(0, "Host", "Test Room", new List<string>(new string[] { "General" }), VoipEngineTestKeys.HostPrivateKey, null);
            int port = host.BoundPort;
            VoipEngine alice = Client(port, "Alice");
            VoipEngine bob = Client(port, "Bob");
            VoipEngine mallory = Client(port, "Mallory");
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice && bob.IsInVoice && mallory.IsInVoice; }, 8000), "everyone joins");
                host.SetLocalProfile("hosting tonight", TestPicture(2));
                alice.SetLocalProfile("back in 10", pixels);
                uint aliceId = IdOf(host, "Alice"), hostId = host.LocalMemberId;
                Check(WaitFor(delegate
                {
                    MemberProfile p = bob.GetProfile(aliceId);
                    return p != null && p.Status == "back in 10" && p.Pixels != null && Convert.ToBase64String(p.Pixels) == Convert.ToBase64String(pixels);
                }, 8000), "Bob gets Alice's status and exact picture");
                Check(WaitFor(delegate { MemberProfile p = alice.GetProfile(hostId); return p != null && p.Status == "hosting tonight" && p.Pixels != null; }, 8000), "Alice gets the host's profile");
                Check(alice.GetProfile(alice.LocalMemberId) != null, "Alice sees her own profile");

                Thread.Sleep(5200); // Past the change limit.
                alice.SetLocalProfile("here now", null);
                Check(WaitFor(delegate { MemberProfile p = bob.GetProfile(aliceId); return p != null && p.Status == "here now" && p.Pixels == null; }, 8000), "removing the picture and changing status reach Bob");

                // Mallory: a modified client sending garbage straight to the host.
                uint mid = IdOf(host, "Mallory");
                Random r = new Random(3);
                for (int i = 0; i < 3000; i++)
                {
                    byte[] junk = new byte[r.Next(0, 1100)];
                    r.NextBytes(junk);
                    mallory.SendToHostForTests(VoipEngine.ProfilePutTypeForTests, junk);
                    if (i % 300 == 0) Thread.Sleep(5);
                }
                byte[] bombChunk = Deflate(new byte[VoipEngine.AvatarRawBytes * 40]);
                byte[] evil = new byte[2 + bombChunk.Length]; evil[1] = 1; Buffer.BlockCopy(bombChunk, 0, evil, 2, bombChunk.Length);
                for (int i = 0; i * 1000 < evil.Length; i++)
                {
                    byte[] part = new byte[Math.Min(1000, evil.Length - i * 1000)];
                    Buffer.BlockCopy(evil, i * 1000, part, 0, part.Length);
                    mallory.SendToHostForTests(VoipEngine.ProfilePutTypeForTests, Chunk(77, i, (evil.Length + 999) / 1000, evil.Length, part));
                }
                mallory.SendToHostForTests(VoipEngine.ProfilePutTypeForTests, Chunk(78, 0, 40, 40000, new byte[1000]));      // Too many chunks.
                mallory.SendToHostForTests(VoipEngine.ProfileGetTypeForTests, new byte[12]);                                  // Unknown member.
                Thread.Sleep(1500);
                Check(host.GetProfile(mid) == null, "garbage, bombs and oversized profiles from a hostile member are never stored");
                Check(host.IsRunning && alice.IsConnected && bob.IsConnected, "the room keeps running");
                Check(WaitFor(delegate { MemberProfile p = bob.GetProfile(aliceId); return p != null && p.Status == "here now"; }, 3000), "everyone else's profiles are unaffected");

                // Flood of valid changes: limited to one every 5 s.
                int changesSeen;
                for (int i = 0; i < 20; i++)
                {
                    mallory.SetLocalProfile("spam " + i, null);
                    Thread.Sleep(50);
                }
                Thread.Sleep(2500);
                MemberProfile mp = bob.GetProfile(mid);
                changesSeen = mp == null ? 0 : 1;
                Check(changesSeen <= 1 && (mp == null || mp.Status.StartsWith("spam")), "profile changes are rate-limited (20 attempts in 1 s, at most 1 accepted)");

                Console.WriteLine();
                Console.WriteLine("Banners and decos through a room");
                alice.SetLocalExtras(BannerKind.Picture, 0x112233, 0x445566, banner, CardEffect.Aurora, PictureDeco.Halo);
                host.SetLocalExtras(BannerKind.Gradient, 0xFF0000, 0x0000FF, null, CardEffect.Glow, PictureDeco.Ripple);
                Check(WaitFor(delegate
                {
                    MemberExtras e = bob.GetExtras(aliceId);
                    return e != null && e.Banner == BannerKind.Picture && e.Effect == CardEffect.Aurora && e.Deco == PictureDeco.Halo
                        && e.Pixels != null && Convert.ToBase64String(e.Pixels) == Convert.ToBase64String(decoded.Pixels);
                }, 8000), "Bob gets Alice's picture banner, exact pixels, and her decos");
                Check(WaitFor(delegate
                {
                    MemberExtras e = alice.GetExtras(hostId);
                    return e != null && e.Banner == BannerKind.Gradient && e.ColourA == 0xFF0000 && e.ColourB == 0x0000FF && e.Effect == CardEffect.Glow && e.Deco == PictureDeco.Ripple;
                }, 8000), "Alice gets the host's gradient banner and decos");
                Check(WaitFor(delegate { MemberExtras e = mallory.GetExtras(aliceId); return e != null && e.Effect == CardEffect.Aurora; }, 8000), "everyone in the room gets them");

                // Mallory again, now with extras: random chunks, bad headers, a bomb, too many chunks.
                for (int i = 0; i < 3000; i++)
                {
                    byte[] junk = new byte[r.Next(0, 1100)];
                    r.NextBytes(junk);
                    mallory.SendToHostForTests(VoipEngine.ExtrasPutTypeForTests, junk);
                    if (i % 300 == 0) Thread.Sleep(5);
                }
                SendChunked(mallory, VoipEngine.ExtrasPutTypeForTests, 90, extrasBomb);
                SendChunked(mallory, VoipEngine.ExtrasPutTypeForTests, 91, ExtrasHeader(1, 9, 0, 0));
                SendChunked(mallory, VoipEngine.ExtrasPutTypeForTests, 92, ExtrasHeader(1, 1, 200, 200));
                SendChunked(mallory, VoipEngine.ExtrasPutTypeForTests, 93, Join(ExtrasHeader(1, 1, 0, 0), new byte[5]));
                SendChunked(mallory, VoipEngine.ExtrasPutTypeForTests, 94, Join(ExtrasHeader(1, 3, 0, 0), Deflate(new byte[VoipEngine.BannerRawBytes - 2])));
                mallory.SendToHostForTests(VoipEngine.ExtrasPutTypeForTests, Chunk(95, 0, 40, 40000, new byte[1000])); // More than the limit.
                mallory.SendToHostForTests(VoipEngine.ExtrasPutTypeForTests, Chunk(96, 3, 2, 2000, new byte[1000]));   // A chunk past the end.
                mallory.SendToHostForTests(VoipEngine.ExtrasGetTypeForTests, new byte[12]);                             // Unknown member.
                mallory.SendToHostForTests(VoipEngine.ExtrasGetTypeForTests, new byte[5]);                              // Wrong size.
                byte[] wrongVersion = new byte[12];
                Buffer.BlockCopy(BitConverter.GetBytes(aliceId), 0, wrongVersion, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(12345u), 0, wrongVersion, 4, 4);
                mallory.SendToHostForTests(VoipEngine.ExtrasGetTypeForTests, wrongVersion);                             // A version that isn't current.
                Thread.Sleep(1500);
                Check(host.GetExtras(mid) == null && bob.GetExtras(mid) == null, "garbage, bombs and bad kinds or decos from a hostile member are never stored or passed on");
                Check(host.IsRunning && alice.IsConnected && bob.IsConnected, "the room keeps running");
                Check(bob.GetExtras(aliceId) != null && bob.GetExtras(aliceId).Effect == CardEffect.Aurora, "everyone else's extras are unaffected");

                // Flood of valid extras: limited like profiles.
                List<int> seen = new List<int>();
                for (int i = 0; i < 20; i++)
                {
                    mallory.SetLocalExtras(BannerKind.Colour, 0x100000 + i, 0, null, CardEffect.Glow, PictureDeco.None);
                    MemberExtras e = bob.GetExtras(mid);
                    if (e != null && !seen.Contains(e.ColourA)) seen.Add(e.ColourA);
                    Thread.Sleep(50);
                }
                DateTime until = DateTime.UtcNow.AddMilliseconds(2500);
                while (DateTime.UtcNow < until)
                {
                    MemberExtras e = bob.GetExtras(mid);
                    if (e != null && !seen.Contains(e.ColourA)) seen.Add(e.ColourA);
                    Thread.Sleep(20);
                }
                Check(seen.Count <= 1, "extras changes are rate-limited (20 attempts in 1 s, " + seen.Count + " reached Bob)");
                Check(WaitFor(delegate { MemberExtras e = bob.GetExtras(mid); return e != null && e.ColourA == 0x100000 + 19; }, 10000), "the latest change still arrives once the limit passes");
            }
            finally
            {
                mallory.Dispose(); bob.Dispose(); alice.Dispose(); host.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All profile checks passed." : _failures + " profile check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
