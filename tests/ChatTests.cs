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
                if (!m.Pending && !m.Failed)
                    count++;
            return count;
        }

        static string Texts(VoipEngine engine, int channel)
        {
            List<string> parts = new List<string>();
            foreach (ChatMessage m in engine.GetChat(channel))
                if (!m.Pending && !m.Failed)
                    parts.Add(m.SenderName + ":" + m.Text);
            return String.Join("|", parts.ToArray());
        }

        static VoipEngine Client(int port, string name, double loss)
        {
            VoipEngine c = new VoipEngine();
            c.HeadlessForTests = true;
            c.PacketLossForTests = loss;
            c.StartClient(new IPEndPoint(IPAddress.Loopback, port), name);
            return c;
        }

        static void Scenario(double loss)
        {
            Console.WriteLine();
            Console.WriteLine("Packet loss " + (int)(loss * 100) + "%");
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.PacketLossForTests = loss;
            host.StartHost(0, "Host", "Test Room", new List<string>(new string[] { "General", "Games" }));
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

                // You can only post in the channel you are in.
                alice.LeaveVoice();
                Check(WaitFor(delegate { return alice.LocalChannelId == 0; }, 8000), "Alice leaves voice");
                Check(!alice.SendChat("from the lobby"), "cannot post from the lobby");
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
