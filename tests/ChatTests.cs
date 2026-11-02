using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Maela.Tests
{
    // Integration test for voice-channel text chat: a real host and two clients talking
    // over loopback UDP, with and without simulated packet loss.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class ChatTests
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

        static int Confirmed(VoipEngine engine, int channel)
        {
            int count = 0;
            foreach (ChatMessage m in engine.GetChat(channel))
                if (!m.Pending && !m.Failed && !m.IsReaction)
                    count++;
            return count;
        }

        static string Texts(VoipEngine engine, int channel)
        {
            List<string> parts = new List<string>();
            foreach (ChatMessage m in engine.GetChat(channel))
                if (!m.Pending && !m.Failed && !m.IsReaction)
                    parts.Add(m.SenderName + ":" + m.Text);
            return String.Join("|", parts.ToArray());
        }

        static int ReactionCount(VoipEngine engine, uint target, string emoji)
        {
            Dictionary<uint, List<ReactionSummary>> all = VoipEngine.SummarizeReactions(engine.GetChat(1));
            List<ReactionSummary> list;
            if (!all.TryGetValue(target, out list)) return 0;
            foreach (ReactionSummary r in list)
                if (r.Emoji == EmojiPack.Find(emoji).Text) return r.Count;
            return 0;
        }

        static string Views(params VoipEngine[] engines)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            foreach (VoipEngine e in engines)
            {
                b.Append(" [");
                foreach (RoomMemberInfo m in e.GetAllMembers())
                    b.Append(m.Name).Append(m.Muted ? ":M" : "").Append(m.Deafened ? ":D" : "").Append(' ');
                b.Append(']');
            }
            return b.ToString();
        }

        static bool MemberFlag(VoipEngine engine, string name, bool muted)
        {
            foreach (RoomMemberInfo m in engine.GetAllMembers())
                if (m.Name == name) return muted ? m.Muted : m.Deafened;
            return false;
        }

        static VoipEngine Client(int port, string name, double loss)
        {
            VoipEngine c = new VoipEngine();
            c.HeadlessForTests = true;
            c.PacketLossForTests = loss;
            c.StartClient(new IPEndPoint(IPAddress.Loopback, port), name, VoipEngineTestKeys.HostFingerprint, null);
            return c;
        }

        // A busy test picture (noise over a gradient), big enough to take a few hundred chunks.
        static byte[] TestPicture(int seed, int width, int height)
        {
            using (System.Drawing.Bitmap b = new System.Drawing.Bitmap(width, height))
            {
                Random r = new Random(seed);
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        b.SetPixel(x, y, System.Drawing.Color.FromArgb((x + r.Next(60)) % 256, (y + r.Next(60)) % 256, (x * y / 40 + seed * 50) % 256));
                return ChatPicture.FromImage(b);
            }
        }

        static byte[] Deflate(byte[] data)
        {
            System.IO.MemoryStream m = new System.IO.MemoryStream();
            using (System.IO.Compression.DeflateStream d = new System.IO.Compression.DeflateStream(m, System.IO.Compression.CompressionMode.Compress, true))
                d.Write(data, 0, data.Length);
            return m.ToArray();
        }

        // A picture header ("MP", version 1, width, height) followed by whatever comes after it.
        static byte[] Headed(int width, int height, byte[] body)
        {
            byte[] blob = new byte[7 + body.Length];
            blob[0] = (byte)'M'; blob[1] = (byte)'P'; blob[2] = 1;
            blob[3] = (byte)width; blob[4] = (byte)(width >> 8);
            blob[5] = (byte)height; blob[6] = (byte)(height >> 8);
            Buffer.BlockCopy(body, 0, blob, 7, body.Length);
            return blob;
        }

        // Pictures the host has posted in a channel, as their markers.
        static List<string> Pictures(VoipEngine engine, int channel)
        {
            List<string> list = new List<string>();
            foreach (ChatMessage m in engine.GetChat(channel))
                if (m.IsPicture && !m.Pending && !m.Failed)
                    list.Add(m.SenderName + ":" + m.Text);
            return list;
        }

        static bool Has(VoipEngine engine, byte[] blob)
        {
            byte[] got = engine.GetPicture(ChatPicture.IdOf(blob), blob.Length);
            return got != null && Convert.ToBase64String(got) == Convert.ToBase64String(blob);
        }

        static void PictureScenario(VoipEngine host, VoipEngine alice, VoipEngine bob, int port, double loss)
        {
            Console.WriteLine("  Pictures");
            alice.ViewChannel(1);
            bob.ViewChannel(1);
            Thread.Sleep(300);
            byte[] photo = TestPicture(1, 400, 300);
            int w, h;
            byte[] rgb;
            Check(ChatPicture.TryDecode(photo, out w, out h, out rgb) && w == 400 && h == 300, "a picture is rebuilt as plain pixels (" + (photo.Length / 1000) + " KB, " + ((photo.Length + 999) / 1000) + " chunks)");
            string photoId = ChatPicture.IdOf(photo);

            DateTime t0 = DateTime.UtcNow;
            Check(alice.SendPicture(photo), "Alice sends a picture");
            Check(WaitFor(delegate { return Pictures(host, 1).Count == 1 && Pictures(bob, 1).Count == 1 && Pictures(alice, 1).Count == 1; }, 30000),
                "the host checks it and posts it, and everyone sees the message");
            Check(Pictures(bob, 1).Count == 1 && Pictures(bob, 1)[0].StartsWith("Alice:") && Pictures(bob, 1)[0].Contains(photoId), "it shows as Alice's picture");
            Check(WaitFor(delegate { return Has(bob, photo); }, 30000), "Bob gets the exact picture from the host");
            Console.WriteLine("     posted and fetched in " + (DateTime.UtcNow - t0).TotalSeconds.ToString("F1") + " s");
            Check(Has(alice, photo) && Has(host, photo), "Alice and the host have it too");
            Check(!bob.PictureComing(photoId), "nothing still on its way");
            string marker;
            {
                string id; int mw, mh, mb;
                marker = Pictures(bob, 1)[0].Substring("Alice:".Length);
                Check(ChatPicture.TryParseMarker(marker, out id, out mw, out mh, out mb) && id == photoId && mw == 400 && mh == 300 && mb == photo.Length
                    && marker.StartsWith("🖼 Sent a picture. Update mæla to see it."), "older versions see the plain words instead");
            }

            // The host's own pictures, and two in a row (the second waits out the per-person limit).
            byte[] second = TestPicture(2, 200, 150), third = TestPicture(3, 160, 120), hostPhoto = TestPicture(4, 120, 90);
            Check(host.SendPicture(hostPhoto) && WaitFor(delegate { return Has(alice, hostPhoto) && Has(bob, hostPhoto); }, 30000), "the host's own pictures reach everyone");
            alice.SendPicture(second);
            alice.SendPicture(third);
            Check(WaitFor(delegate { return Has(bob, second) && Has(bob, third); }, 30000), "two pictures sent at once both arrive");
            Check(WaitFor(delegate
            {
                foreach (ChatMessage m in alice.GetChat(1)) if (m.IsPicture && (m.Pending || m.Failed)) return false;
                return true;
            }, 10000), "no stuck or failed pictures");
            foreach (ChatMessage m in alice.GetChat(1))
                if (m.IsPicture && (m.Pending || m.Failed))
                    Console.WriteLine("     still " + (m.Failed ? "failed" : "sending") + " after " + m.Attempts + " offers: " + m.Text);

            // Typed words can't pass for a picture: only the host makes picture messages.
            alice.SendChat(marker.Replace(photoId, ChatPicture.IdOf(second)));
            Check(WaitFor(delegate
            {
                int fake = 0;
                foreach (ChatMessage m in bob.GetChat(1)) if (!m.IsPicture && m.Text.Contains("[mp1:")) fake++;
                return fake == 1;
            }, 10000) && Pictures(bob, 1).Count == 4, "a typed marker stays plain text");

            // Mallory: a modified client sending pictures that aren't what they say.
            VoipEngine mallory = Client(port, "Mallory", loss);
            try
            {
                Check(WaitFor(delegate { return mallory.IsInVoice && mallory.LocalChannelId == 1; }, 10000), "Mallory joins");
                int before = Pictures(host, 1).Count;
                List<byte[]> bad = new List<byte[]>();
                Random r = new Random(5);
                byte[] noise = new byte[30000];
                r.NextBytes(noise);
                bad.Add(Headed(100, 100, noise));                                                  // Not deflate at all.
                bad.Add(Headed(1920, 1080, Deflate(new byte[(1920 * 3 + 1) * 1080 * 4])));        // A bomb: four times the pixels.
                bad.Add(Headed(100, 100, Deflate(new byte[(100 * 3 + 1) * 100 - 1])));             // One byte short.
                bad.Add(Headed(100, 100, Deflate(new byte[(100 * 3 + 1) * 100 + 1])));             // One byte long.
                byte[] badFilter = new byte[(10 * 3 + 1) * 10];
                badFilter[0] = 9;
                bad.Add(Headed(10, 10, Deflate(badFilter)));                                       // An unknown row filter.
                foreach (byte[] blob in bad)
                    Check(!ChatPicture.TryReadSize(blob, out w, out h) || !ChatPicture.TryDecode(blob, out w, out h, out rgb), "refused on its own: " + bad.IndexOf(blob));
                foreach (byte[] blob in bad)
                    mallory.SendPicture(blob);
                // Bytes that don't match their name: a real picture offered as another one.
                byte[] swapped = TestPicture(6, 100, 80);
                string claimed = ChatPicture.IdOf(TestPicture(7, 100, 80));
                mallory.SendPictureAsForTests(swapped, claimed);
                // Raw packets: silly sizes, a channel she isn't in, chunks nobody asked for, requests for nothing.
                byte[] offer = new byte[28];
                r.NextBytes(offer);
                Buffer.BlockCopy(BitConverter.GetBytes(ChatPicture.MaxBytes + 1), 0, offer, 16, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(1), 0, offer, 20, 4);
                mallory.SendToHostForTests(VoipEngine.PictureOfferTypeForTests, offer);
                Buffer.BlockCopy(BitConverter.GetBytes(5000), 0, offer, 16, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(99), 0, offer, 20, 4);
                mallory.SendToHostForTests(VoipEngine.PictureOfferTypeForTests, offer);
                for (int i = 0; i < 2000; i++)
                {
                    byte[] junk = new byte[r.Next(0, 1100)];
                    r.NextBytes(junk);
                    byte type = i % 3 == 0 ? VoipEngine.PictureOfferTypeForTests : i % 3 == 1 ? VoipEngine.PictureGetTypeForTests : VoipEngine.PictureDataTypeForTests;
                    mallory.SendToHostForTests(type, junk);
                    if (i % 200 == 0) Thread.Sleep(5);
                }
                Thread.Sleep(12000); // Long enough for every offer to be pulled and checked, more than once.
                Check(Pictures(host, 1).Count == before && Pictures(bob, 1).Count == before, "the host posts none of them (" + (Pictures(host, 1).Count - before) + " posted)");
                int kept = 0;
                foreach (byte[] blob in bad)
                    if (host.GetPicture(ChatPicture.IdOf(blob), blob.Length) != null) kept++;
                if (host.GetPicture(claimed, swapped.Length) != null) kept++;
                Check(kept == 0, "and keeps none of them");
                Check(host.IsRunning && alice.IsConnected && bob.IsConnected && mallory.IsConnected, "the room keeps running");
                byte[] after = TestPicture(8, 120, 90);
                Check(bob.SendPicture(after) && WaitFor(delegate { return Has(alice, after); }, 30000), "pictures still work for everyone else");
            }
            finally
            {
                mallory.Dispose();
            }
        }

        static void Scenario(double loss)
        {
            Console.WriteLine();
            Console.WriteLine("Packet loss " + (int)(loss * 100) + "%");
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.PacketLossForTests = loss;
            host.StartHost(0, "Host", "Test Room", new List<string>(new string[] { "General", "Games" }), VoipEngineTestKeys.HostPrivateKey, null);
            int port = host.BoundPort;
            VoipEngine alice = Client(port, "Alice", loss);
            VoipEngine bob = null;
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice && alice.LocalChannelId == 1; }, 8000), "Alice joins #General");

                // History: messages posted before Bob arrives must reach him when he joins.
                Check(host.SendChat("hello from host"), "host posts");
                Check(alice.SendChat("hi host"), "alice posts");
                Check(WaitFor(delegate { return Confirmed(host, 1) == 2 && Confirmed(alice, 1) == 2; }, 8000), "host and alice both see 2 messages");

                bob = Client(port, "Bob", loss);
                Check(WaitFor(delegate { return bob.IsInVoice && Confirmed(bob, 1) == 2; }, 10000), "Bob receives history on join");

                // A burst from everyone, then everyone must converge on the same ordered log.
                int i;
                for (i = 0; i < 10; i++)
                {
                    alice.SendChat("a" + i);
                    bob.SendChat("b" + i);
                    host.SendChat("h" + i);
                    Thread.Sleep(150);
                }
                Check(WaitFor(delegate { return Confirmed(host, 1) == 32 && Confirmed(alice, 1) == 32 && Confirmed(bob, 1) == 32; }, 15000),
                    "all three converge on 32 messages (host " + Confirmed(host, 1) + ", alice " + Confirmed(alice, 1) + ", bob " + Confirmed(bob, 1) + ")");
                Check(Texts(host, 1) == Texts(alice, 1) && Texts(alice, 1) == Texts(bob, 1), "identical order everywhere");
                int pending = 0;
                foreach (ChatMessage m in alice.GetChat(1)) if (m.Pending || m.Failed) pending++;
                foreach (ChatMessage m in bob.GetChat(1)) if (m.Pending || m.Failed) pending++;
                Check(pending == 0, "no stuck or failed messages");

                // Channels are separate: a message in #Games must not appear in #General.
                bob.ChangeChannel(2);
                Check(WaitFor(delegate { return bob.LocalChannelId == 2; }, 8000), "Bob moves to #Games");
                Check(bob.SendChat("games only"), "bob posts in #Games");
                Check(WaitFor(delegate { return Confirmed(bob, 2) == 1; }, 8000), "Bob sees his #Games message");
                Thread.Sleep(1500);
                Check(Confirmed(alice, 2) == 0 && Confirmed(host, 1) == 32 && Confirmed(alice, 1) == 32, "#Games message stays out of #General");

                // Reactions: toggles everyone agrees on, applied to the right message.
                bob.ChangeChannel(1);
                Check(WaitFor(delegate { return bob.LocalChannelId == 1; }, 8000), "Bob back in #General");
                uint target = 0;
                foreach (ChatMessage m in host.GetChat(1)) if (!m.IsReaction && m.Text == "hi host") target = m.Sequence;
                Check(target != 0, "found Alice's message to react to");
                alice.ToggleReaction(target, "\uD83D\uDC4D");
                bob.ToggleReaction(target, "\uD83D\uDC4D");
                host.ToggleReaction(target, "\u2764\uFE0F");
                Check(WaitFor(delegate { return ReactionCount(alice, target, "\uD83D\uDC4D") == 2 && ReactionCount(bob, target, "\u2764\uFE0F") == 1 && ReactionCount(host, target, "\uD83D\uDC4D") == 2; }, 10000),
                    "everyone sees 2x thumbs-up and 1x heart");
                bob.ToggleReaction(target, "\uD83D\uDC4D");
                Check(WaitFor(delegate { return ReactionCount(alice, target, "\uD83D\uDC4D") == 1 && ReactionCount(host, target, "\uD83D\uDC4D") == 1; }, 10000), "toggling again removes Bob's reaction");
                Check(!alice.ToggleReaction(target, "not-an-emoji"), "only known emoji can be used as reactions");

                // Shared status and ping.
                DateTime t0 = DateTime.UtcNow;
                alice.Muted = true;
                bob.Deafened = true;
                Check(WaitFor(delegate { return MemberFlag(host, "Alice", true) && MemberFlag(bob, "Alice", true) && MemberFlag(alice, "Bob", false); }, 8000),
                    "everyone sees Alice muted and Bob deafened" + Views(host, alice, bob));
                Console.WriteLine("     status reached everyone in " + (DateTime.UtcNow - t0).TotalSeconds.ToString("F1") + " s");
                alice.Muted = false;
                Check(WaitFor(delegate { return !MemberFlag(bob, "Alice", true); }, 8000), "unmute is shared too");
                Check(WaitFor(delegate { return alice.PingMs >= 0; }, 8000), "ping measured (" + alice.PingMs + " ms)");
                bob.Deafened = false;

                // Leaving voice keeps you in the room: you still read and post in the open channel.
                alice.LeaveVoice();
                Check(WaitFor(delegate { return alice.LocalChannelId == 0; }, 8000), "Alice leaves voice");
                Check(alice.ViewChannelId == 1 && alice.SendChat("still here"), "Alice can still post in #General");
                Check(WaitFor(delegate { return Texts(host, 1).Contains("Alice:still here") && Texts(bob, 1).Contains("Alice:still here"); }, 8000),
                    "her message reaches #General");

                // One click opens another channel's chat without joining its voice.
                alice.ViewChannel(2);
                Check(WaitFor(delegate { return Confirmed(alice, 2) == 1; }, 8000), "Alice sees #Games history without joining voice");
                Check(alice.LocalChannelId == 0, "Alice is still not in voice");
                Check(alice.SendChat("hello games") && WaitFor(delegate { return Texts(host, 2).Contains("Alice:hello games"); }, 8000),
                    "Alice posts in #Games while not in voice");
                Check(!Texts(bob, 1).Contains("hello games"), "#Games post stays out of #General");

                // Chat-only channels: you can post, but not join voice.
                {
                    int notes = host.CreateChannel("notes", true);
                    Check(notes > 0 && WaitFor(delegate { return alice.IsTextChannel(notes); }, 8000), "chat-only channel appears for members");
                    bob.ChangeChannel(notes);
                    Thread.Sleep(1500);
                    Check(bob.LocalChannelId == 1, "nobody can join voice in a chat-only channel");
                    alice.ViewChannel(notes);
                    bob.ViewChannel(notes);
                    Thread.Sleep(300);
                    Check(bob.SendChat("note 1") && WaitFor(delegate { return Texts(alice, notes) == "Bob:note 1"; }, 8000),
                        "posts in the chat-only channel reach people who have it open");
                }

                PictureScenario(host, alice, bob, port, loss);
            }
            finally
            {
                if (bob != null) bob.Dispose();
                alice.Dispose();
                host.Dispose();
            }
        }

        static int Main()
        {
            Scenario(0.0);
            Scenario(0.25);
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All chat checks passed." : _failures + " chat check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
