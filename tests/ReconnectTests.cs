using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Maela.Tests
{
    // Closing mæla without saying goodbye (crash, Task Manager, lost Wi-Fi, power cut) must not
    // stop anyone from getting straight back in.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class ReconnectTests
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

        static int Count(VoipEngine engine, string name)
        {
            int n = 0;
            foreach (RoomMemberInfo m in engine.GetAllMembers())
                if (m.Name == name) n++;
            return n;
        }

        static VoipEngine Client(int port, string name, int localPort)
        {
            VoipEngine c = new VoipEngine();
            c.HeadlessForTests = true;
            c.LocalPortForTests = localPort;
            c.StartClient(new IPEndPoint(IPAddress.Loopback, port), name, VoipEngineTestKeys.HostFingerprint, null);
            return c;
        }

        static VoipEngine Host(int port)
        {
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.StartHost(port, "Host", "Test Room", new List<string>(new string[] { "General", "Games" }), VoipEngineTestKeys.HostPrivateKey, null);
            return host;
        }

        // Pulls the plug: nothing more is sent, not even "leave".
        static void Crash(VoipEngine engine)
        {
            engine.PacketLossForTests = 1.0;
            engine.Dispose();
        }

        static void ClientCrash(string label, bool samePort)
        {
            Console.WriteLine();
            Console.WriteLine(label);
            VoipEngine host = Host(0);
            int port = host.BoundPort;
            VoipEngine alice = Client(port, "Alice", 0);
            VoipEngine again = null;
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice; }, 8000), "Alice joins");
                alice.ChangeChannel(2);
                Check(WaitFor(delegate { return alice.LocalChannelId == 2; }, 8000), "Alice moves to #Games");
                int oldPort = alice.BoundPort;
                Crash(alice);

                DateTime t0 = DateTime.UtcNow;
                again = Client(port, "Alice", samePort ? oldPort : 0);
                Check(WaitFor(delegate { return again.IsInVoice; }, 4000),
                    "Alice gets back in (" + (DateTime.UtcNow - t0).TotalSeconds.ToString("F1") + " s)");
                Check(WaitFor(delegate { return Count(host, "Alice") == 1 && Count(again, "Alice") == 1; }, 3000),
                    "only one Alice in the room (host sees " + Count(host, "Alice") + ", Alice sees " + Count(again, "Alice") + ", ports " + oldPort + "/" + again.BoundPort + ")");
                Check(WaitFor(delegate { return again.LocalChannelId == 2; }, 3000), "Alice is put back in #Games (channel " + again.LocalChannelId + ")");
                Check(again.SendChat("back") && WaitFor(delegate
                {
                    foreach (ChatMessage m in host.GetChat(2)) if (m.Text == "back") return true;
                    return false;
                }, 5000), "Alice can chat after rejoining");
            }
            finally
            {
                if (again != null) again.Dispose();
                host.Dispose();
            }
        }

        static void CleanClose()
        {
            Console.WriteLine();
            Console.WriteLine("Client quits normally on a lossy connection (30% loss)");
            VoipEngine host = Host(0);
            VoipEngine alice = Client(host.BoundPort, "Alice", 0);
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice && Count(host, "Alice") == 1; }, 8000), "Alice joins");
                int gone = 0;
                for (int run = 0; run < 10; run++)
                {
                    VoipEngine c = Client(host.BoundPort, "C" + run, 0);
                    WaitFor(delegate { return c.IsInVoice && Count(host, "C" + run) == 1; }, 8000);
                    c.PacketLossForTests = 0.3;
                    c.Dispose();
                    if (WaitFor(delegate { return Count(host, "C" + run) == 0; }, 1000)) gone++;
                }
                Check(gone >= 9, "host drops the member within 1 s (" + gone + "/10)");
            }
            finally
            {
                alice.Dispose();
                host.Dispose();
            }
        }

        static void HostQuits()
        {
            Console.WriteLine();
            Console.WriteLine("Host quits normally, then opens the room again");
            VoipEngine host = Host(0);
            int port = host.BoundPort;
            VoipEngine alice = Client(port, "Alice", 0);
            VoipEngine host2 = null;
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice; }, 8000), "Alice joins");
                DateTime t0 = DateTime.UtcNow;
                host.Dispose();
                Check(WaitFor(delegate { return !alice.IsConnected; }, 1000),
                    "Alice is disconnected at once (" + (DateTime.UtcNow - t0).TotalSeconds.ToString("F1") + " s)");
                host2 = Host(port);
                Check(WaitFor(delegate { return alice.IsInVoice && Count(host2, "Alice") == 1; }, 8000), "Alice rejoins when the room is back");
            }
            finally
            {
                alice.Dispose();
                if (host2 != null) host2.Dispose();
            }
        }

        static void HostCrash()
        {
            Console.WriteLine();
            Console.WriteLine("Host closes without warning and opens the room again");
            VoipEngine host = Host(0);
            int port = host.BoundPort;
            VoipEngine alice = Client(port, "Alice", 0);
            VoipEngine host2 = null;
            try
            {
                Check(WaitFor(delegate { return alice.IsInVoice; }, 8000), "Alice joins");
                Crash(host);
                Thread.Sleep(500);
                host2 = Host(port);
                Check(host2.BoundPort == port, "host reopens on the same port");
                Check(WaitFor(delegate { return alice.IsInVoice && Count(host2, "Alice") == 1; }, 15000), "Alice reconnects by herself");
            }
            finally
            {
                alice.Dispose();
                if (host2 != null) host2.Dispose();
            }
        }

        static int Main()
        {
            ClientCrash("Client closes without warning, rejoins from a new port", false);
            ClientCrash("Client closes without warning, rejoins from the same port (same NAT mapping)", true);
            CleanClose();
            HostQuits();
            HostCrash();
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All reconnect checks passed." : _failures + " reconnect check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
