using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Maela.Tests
{
    /// <summary>Runs UPnP mapping against a scripted router, one quirk at a time.</summary>
    internal static class PortMappingTests
    {
        static int _failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok)
                _failures++;
        }

        /// <summary>A tiny HTTP server that behaves like a router's UPnP control point.</summary>
        sealed class FakeRouter : IDisposable
        {
            public bool PppListedFirst;
            public bool PppDown;
            public bool RefusePermanentLease;
            public bool SilentOnAdd;
            public int TakenExternalPort;
            public string ExternalIp = "203.0.113.7";

            public readonly List<string> Added = new List<string>();
            public readonly List<string> Deleted = new List<string>();
            public int AddAttempts;

            readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);

            public Uri Location { get { return new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/desc.xml"); } }

            public FakeRouter()
            {
                _listener.Start();
                Thread t = new Thread(Serve);
                t.IsBackground = true;
                t.Start();
            }

            void Serve()
            {
                try
                {
                    while (true)
                    {
                        TcpClient client = _listener.AcceptTcpClient();
                        ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }

            void Handle(TcpClient client)
            {
                using (client)
                {
                    NetworkStream stream = client.GetStream();
                    StringBuilder text = new StringBuilder();
                    byte[] buffer = new byte[8192];
                    int contentLength = 0;
                    int headerEnd = -1;
                    while (true)
                    {
                        string sofar = text.ToString();
                        headerEnd = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd >= 0)
                        {
                            foreach (string line in sofar.Substring(0, headerEnd).Split('\n'))
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    contentLength = int.Parse(line.Substring(15).Trim());
                            if (sofar.Length >= headerEnd + 4 + contentLength)
                                break;
                        }
                        int n = stream.Read(buffer, 0, buffer.Length);
                        if (n <= 0)
                            return;
                        text.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    }

                    string request = text.ToString();
                    string body = request.Substring(headerEnd + 4);
                    int status = 200;
                    string reply;
                    if (request.StartsWith("GET", StringComparison.Ordinal))
                        reply = Description();
                    else
                        reply = Control(request.Substring(0, request.IndexOf(' ', 5)), body, ref status);
                    if (reply == null)
                    {
                        Thread.Sleep(6000); // Say nothing until the caller gives up.
                        return;
                    }
                    byte[] payload = Encoding.UTF8.GetBytes(reply);
                    byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " X\r\nContent-Type: text/xml\r\nConnection: close\r\nContent-Length: " + payload.Length + "\r\n\r\n");
                    stream.Write(head, 0, head.Length);
                    stream.Write(payload, 0, payload.Length);
                }
            }

            string Description()
            {
                string ppp = "<service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType><controlURL>/ppp</controlURL></service>";
                string ip = "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ip</controlURL></service>";
                return "<?xml version=\"1.0\"?><root><device><serviceList>" + (PppListedFirst ? ppp + ip : ip) + "</serviceList></device></root>";
            }

            string Control(string line, string body, ref int status)
            {
                bool isPpp = line.Contains("/ppp");
                string action = Between(body, "<u:", " ");
                if (action == "GetStatusInfo")
                    return Reply("<NewConnectionStatus>" + (isPpp && PppDown ? "Disconnected" : "Connected") + "</NewConnectionStatus>");
                if (action == "GetExternalIPAddress")
                    return Reply("<NewExternalIPAddress>" + ExternalIp + "</NewExternalIPAddress>");
                string external = Between(body, "<NewExternalPort>", "<");
                if (action == "DeletePortMapping")
                {
                    lock (Deleted) Deleted.Add(external);
                    return Reply("");
                }
                if (action != "AddPortMapping")
                    return Fault(ref status, "401");

                Interlocked.Increment(ref AddAttempts);
                if (SilentOnAdd)
                    return null;
                if (isPpp && PppDown)
                    return Fault(ref status, "501");
                if (external == TakenExternalPort.ToString())
                    return Fault(ref status, "718");
                if (RefusePermanentLease && Between(body, "<NewLeaseDuration>", "<") == "0")
                    return Fault(ref status, "402");
                lock (Added) Added.Add((isPpp ? "ppp" : "ip") + ":" + external + ":" + Between(body, "<NewInternalClient>", "<"));
                return Reply("");
            }

            static string Reply(string inner)
            {
                return "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:R xmlns:u=\"x\">" + inner + "</u:R></s:Body></s:Envelope>";
            }

            static string Fault(ref int status, string code)
            {
                status = 500;
                return "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code + "</errorCode></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
            }

            static string Between(string text, string a, string b)
            {
                int i = text.IndexOf(a, StringComparison.Ordinal);
                if (i < 0) return String.Empty;
                i += a.Length;
                int j = text.IndexOf(b, i, StringComparison.Ordinal);
                return j < 0 ? String.Empty : text.Substring(i, j - i);
            }

            public void Dispose() { _listener.Stop(); }
        }

        static int Main()
        {
            using (FakeRouter router = new FakeRouter())
            {
                UpnpPortMapping m = UpnpPortMapping.TryCreateAt(router.Location, 41000, "10.99.0.5");
                Check(m != null && m.ExternalPort == 41000 && m.ExternalIp == "203.0.113.7", "ordinary router maps the requested port");
                Check(router.Added.Count == 1 && router.Added[0].EndsWith(":127.0.0.1"), "router is given the address on its own network, not the VPN's");
                m.Dispose();
                Check(router.Deleted.Contains("41000"), "mapping is removed on close");
            }

            using (FakeRouter router = new FakeRouter())
            {
                router.PppListedFirst = true;
                router.PppDown = true;
                UpnpPortMapping m = UpnpPortMapping.TryCreateAt(router.Location, 41001, "10.99.0.5");
                Check(m != null && router.Added.Count == 1 && router.Added[0].StartsWith("ip:"), "unused PPP service listed first is skipped");
                m.Dispose();
            }

            using (FakeRouter router = new FakeRouter())
            {
                router.RefusePermanentLease = true;
                UpnpPortMapping m = UpnpPortMapping.TryCreateAt(router.Location, 41002, "10.99.0.5");
                Check(m != null && m.ExternalPort == 41002, "router refusing permanent leases gets a timed one");
                if (m != null) m.Dispose();
            }

            using (FakeRouter router = new FakeRouter())
            {
                router.TakenExternalPort = 41003;
                UpnpPortMapping m = UpnpPortMapping.TryCreateAt(router.Location, 41003, "10.99.0.5");
                Check(m != null && m.ExternalPort != 41003, "taken port moves to another external port");
                if (m != null) m.Dispose();
            }

            using (FakeRouter router = new FakeRouter())
            {
                router.SilentOnAdd = true;
                DateTime start = DateTime.UtcNow;
                UpnpPortMapping m = UpnpPortMapping.TryCreateAt(router.Location, 41004, "10.99.0.5");
                Check(m == null && router.AddAttempts == 1, "silent router gives up after one try, not thirteen (" + (int)(DateTime.UtcNow - start).TotalSeconds + "s)");
            }

            Check(UpnpPortMapping.IsNonPublic("192.168.1.1") && UpnpPortMapping.IsNonPublic("10.0.0.1") && UpnpPortMapping.IsNonPublic("172.20.0.1") && UpnpPortMapping.IsNonPublic("100.64.3.4"), "double NAT and provider NAT addresses are recognised");
            Check(!UpnpPortMapping.IsNonPublic("203.0.113.7") && !UpnpPortMapping.IsNonPublic("172.32.0.1") && !UpnpPortMapping.IsNonPublic("100.128.0.1") && !UpnpPortMapping.IsNonPublic(""), "public addresses are left alone");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "All port mapping checks passed." : _failures + " port mapping check(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
