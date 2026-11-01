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
