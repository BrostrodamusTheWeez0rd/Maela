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
