using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Maela.Tests
{
    // Attacks a real host over loopback: eavesdropping, impersonation, wrong passwords,
    // forged and replayed packets. Also checks room-code typo detection.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class SecurityTests
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

        static VoipEngine Host(string password)
        {
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.StartHost(0, "Hostname-Zebra", "Secret Room", new List<string>(new string[] { "General" }), VoipEngineTestKeys.HostPrivateKey, password);
            return host;
        }

        static VoipEngine Client(int port, string name, byte[] fingerprint, string password)
        {
            VoipEngine c = new VoipEngine();
            c.HeadlessForTests = true;
            c.StartClient(new IPEndPoint(IPAddress.Loopback, port), name, fingerprint, password);
            return c;
        }

        static bool Contains(List<byte[]> packets, string text)
        {
            byte[] needle = Encoding.UTF8.GetBytes(text);
            lock (packets)
            {
                foreach (byte[] p in packets)
                {
                    for (int i = 0; i + needle.Length <= p.Length; i++)
                    {
                        int j = 0;
                        while (j < needle.Length && p[i + j] == needle[j]) j++;
                        if (j == needle.Length) return true;
                    }
                }
            }
            return false;
        }

        static void Eavesdropping()
        {
            Console.WriteLine();
            Console.WriteLine("Eavesdropping");
            List<byte[]> wire = new List<byte[]>();
            Action<byte[]> tap = delegate(byte[] p) { lock (wire) wire.Add((byte[])p.Clone()); };
            VoipEngine host = Host("correct horse");
            host.WireTapForTests = tap;
            VoipEngine alice = Client(host.BoundPort, "Alice-Quokka", VoipEngineTestKeys.HostFingerprint, "correct horse");
            alice.WireTapForTests = tap;
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice; }, 8000), "client joins a password-protected room");
                alice.SendChat("the treasure is under the oak tree");
                Check(WaitFor(delegate { return host.GetChat(1).Length == 1; }, 5000), "chat delivered");
                for (int i = 0; i < 50; i++)
                {
                    byte[] tone = new byte[960];
                    for (int k = 0; k < 480; k++) { short v = (short)(Math.Sin(k * 0.1) * 8000); tone[k * 2] = (byte)v; tone[k * 2 + 1] = (byte)(v >> 8); }
                    alice.InjectCapturedFrameForTests(tone);
                }
                Thread.Sleep(500);
                Check(wire.Count > 20, "captured " + wire.Count + " datagrams off the wire");
                Check(!Contains(wire, "treasure"), "chat text never appears on the wire");
                Check(!Contains(wire, "Alice-Quokka") && !Contains(wire, "Hostname-Zebra"), "display names never appear on the wire");
                Check(!Contains(wire, "correct horse"), "room password never appears on the wire");
                Check(!Contains(wire, "Secret Room") && !Contains(wire, "General"), "room and channel names never appear on the wire");
            }
            finally
            {
                alice.Dispose();
                host.Dispose();
            }
        }

        static void Impersonation()
        {
            Console.WriteLine();
            Console.WriteLine("Impersonation and passwords");
            VoipEngine host = Host("correct horse");
            byte[] wrongFingerprint = (byte[])VoipEngineTestKeys.HostFingerprint.Clone();
            wrongFingerprint[0] ^= 1;
            VoipEngine fooled = Client(host.BoundPort, "Mallory", wrongFingerprint, "correct horse");
            VoipEngine noPassword = Client(host.BoundPort, "Eve", VoipEngineTestKeys.HostFingerprint, null);
            VoipEngine wrongPassword = Client(host.BoundPort, "Trudy", VoipEngineTestKeys.HostFingerprint, "Tr0ub4dor&3");
            try
            {
                Thread.Sleep(3000);
                Check(!fooled.IsConnected && (fooled.LastMessage ?? "").Contains("Security check failed"), "client refuses a host whose key doesn't match the room code");
                Check(!noPassword.IsConnected && (noPassword.LastMessage ?? "").Contains("needs a password"), "joining without the password is refused (" + noPassword.LastMessage + ")");
                Check(!wrongPassword.IsConnected && (wrongPassword.LastMessage ?? "").Contains("Wrong room password"), "wrong password is refused (" + wrongPassword.LastMessage + ")");
                Check(host.MemberCount == 1, "host room still has only the host");
            }
            finally
            {
                fooled.Dispose();
                noPassword.Dispose();
                wrongPassword.Dispose();
                host.Dispose();
            }
        }

        static void ForgedPackets()
        {
            Console.WriteLine();
            Console.WriteLine("Forged and replayed packets");
            List<byte[]> fromAlice = new List<byte[]>();
            VoipEngine host = Host(null);
            VoipEngine alice = Client(host.BoundPort, "Alice", VoipEngineTestKeys.HostFingerprint, null);
            alice.WireTapForTests = delegate(byte[] p) { lock (fromAlice) fromAlice.Add((byte[])p.Clone()); };
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice; }, 8000), "Alice joins");
                alice.SendChat("hello");
                Check(WaitFor(delegate { return host.GetChat(1).Length == 1; }, 5000), "Alice's message arrives");

                using (Socket attacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    attacker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    IPEndPoint target = new IPEndPoint(IPAddress.Loopback, host.BoundPort);
                    Random r = new Random(7);

                    // Replay everything Alice sent, from another address.
                    lock (fromAlice)
                        foreach (byte[] p in fromAlice)
                            attacker.SendTo(p, target);

                    // Random garbage, truncated packets, and old unencrypted v5 packets.
                    for (int i = 0; i < 500; i++)
                    {
                        byte[] junk = new byte[r.Next(0, 300)];
                        r.NextBytes(junk);
                        if (junk.Length >= 6 && i % 2 == 0)
                        {
                            Buffer.BlockCopy(BitConverter.GetBytes(0x32564F52u), 0, junk, 0, 4);
                            junk[4] = (byte)(i % 3 == 0 ? 5 : VoipEngine.WireVersion);
                            junk[5] = (byte)(0x40 + i % 5);
                        }
                        attacker.SendTo(junk, target);
                    }

                    // Bit-flipped copies of Alice's real packets.
                    lock (fromAlice)
                        foreach (byte[] p in fromAlice)
                        {
                            byte[] flipped = (byte[])p.Clone();
                            flipped[r.Next(6, flipped.Length)] ^= (byte)(1 << r.Next(8));
                            attacker.SendTo(flipped, target);
                        }
                }

                Thread.Sleep(1500);
                Check(host.GetChat(1).Length == 1, "no injected or replayed chat messages (" + host.GetChat(1).Length + ")");
                Check(host.MemberCount == 2, "no phantom members joined (" + host.MemberCount + ")");
                Check(host.IsRunning && alice.IsConnected, "host and client unaffected");
                alice.SendChat("still working");
                Check(WaitFor(delegate { return host.GetChat(1).Length == 2; }, 5000), "Alice can still chat afterwards");
            }
            finally
            {
                alice.Dispose();
                host.Dispose();
            }
        }

        static void Probe()
        {
            Console.WriteLine();
            Console.WriteLine("Room status probe");
            VoipEngine host = Host("pw");
            try
            {
                string code = RoomCode.Encode(IPAddress.Loopback, host.BoundPort, host.HostFingerprint);
                Check(RoomProbe.Check(code, 1500).Online, "probe reports the room online");
                string otherCode = RoomCode.Encode(IPAddress.Loopback, host.BoundPort == 65535 ? 65534 : host.BoundPort + 1, host.HostFingerprint);
                Check(!RoomProbe.Check(otherCode, 500).Online, "probe of an empty port reports offline");
            }
            finally
            {
                host.Dispose();
            }
        }

        static void RoomCodes()
        {
            Console.WriteLine();
            Console.WriteLine("Room codes");
            byte[] fp = VoipEngineTestKeys.HostFingerprint;
            string code = RoomCode.Encode(IPAddress.Parse("203.0.113.42"), 40123, fp);
            Console.WriteLine("     example: " + code);
            IPAddress ip; int port; byte[] decoded;
            Check(RoomCode.TryDecode(code, out ip, out port, out decoded) && ip.ToString() == "203.0.113.42" && port == 40123 && CryptoUtil.ConstantTimeEquals(decoded, fp), "round trip");
            Check(RoomCode.IsValid(code.ToLowerInvariant().Replace("-", " ")), "case and separators don't matter");
            Check(!code.Contains("203") && !code.Contains("40123"), "IP and port are not visible in the code");

            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            string compact = code.Replace("-", "");
            int typos = 0, caught = 0;
            for (int pos = 1; pos < compact.Length; pos++)
                foreach (char c in alphabet)
                {
                    if (c == compact[pos]) continue;
                    typos++;
                    string mutated = compact.Substring(0, pos) + c + compact.Substring(pos + 1);
                    if (!RoomCode.IsValid(mutated)) caught++;
                }
            Check(caught == typos, "every single-character typo is rejected (" + caught + "/" + typos + ")");
            Check(RoomCode.IsOldFormat("M-7QKD-P9XR-4F2NC") && !RoomCode.IsValid("M-7QKD-P9XR-4F2NC"), "old 0.6 codes are recognised and refused");
        }

        static int Main()
        {
            RoomCodes();
            Probe();
            Eavesdropping();
            Impersonation();
            ForgedPackets();
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All security checks passed." : _failures + " security check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
