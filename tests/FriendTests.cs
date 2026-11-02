using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Maela.Tests
{
    // The rule under test: only your own click can let someone reach you. These run real
    // listeners over loopback, including someone running a modified copy that tries to skip it.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class FriendTests
    {
        static int _failures;
        static string _root;
        static int _people;

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
                Thread.Sleep(20);
            }
            return condition();
        }

        static string NewFolder()
        {
            return Path.Combine(_root, "person" + (++_people));
        }

        static FriendService Make(string folder)
        {
            FriendService service = new FriendService(folder);
            service.ConnectIntervalMs = 150;
            service.KeepAliveMs = 150;
            service.SessionTimeoutMs = 800;
            service.ResendMs = 100;
            return service;
        }

        static FriendService Online(string name)
        {
            return Online(name, NewFolder());
        }

        static FriendService Online(string name, string folder)
        {
            FriendService service = Make(folder);
            Check(service.SetStatus(FriendStatus.Online, name), name + " goes online");
            service.SetAddress(IPAddress.Loopback, service.Port);
            return service;
        }

        static FriendInfo Find(FriendService service, string id)
        {
            foreach (FriendInfo info in service.GetPeople())
                if (info.Id == id)
                    return info;
            return null;
        }

        static bool IsState(FriendService service, string id, FriendState state)
        {
            FriendInfo info = Find(service, id);
            return info != null && info.State == state;
        }

        static void MakeFriends(FriendService a, FriendService b)
        {
            a.AddByCode(b.Code);
            WaitFor(delegate { return IsState(b, a.Id, FriendState.RequestReceived); }, 3000);
            b.AcceptRequest(a.Id);
            WaitFor(delegate { return IsState(a, b.Id, FriendState.Friend); }, 3000);
        }

        static void Codes()
        {
            byte[] fingerprint = FriendCode.Fingerprint(X25519.PublicKey(X25519.GeneratePrivateKey()));
            string code = FriendCode.Encode(IPAddress.Parse("203.0.113.9"), 45000, fingerprint);
            IPAddress ip;
            int port;
            byte[] decoded;
            Check(FriendCode.TryDecode(code, out ip, out port, out decoded), "a friend code decodes");
            Check(ip.ToString() == "203.0.113.9" && port == 45000 && Convert.ToBase64String(decoded) == Convert.ToBase64String(fingerprint), "every field survives");
            Check(!code.Contains("203"), "the code doesn't visibly contain the address");
            Check(!RoomCode.IsValid(code), "a friend code isn't a room code");

            bool typoCaught = true;
            string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            for (int i = 2; i < code.Length; i++)
            {
                if (code[i] == '-')
                    continue;
                foreach (char c in alphabet)
                {
                    if (c == code[i])
                        continue;
                    string typo = code.Substring(0, i) + c + code.Substring(i + 1);
                    if (FriendCode.IsValid(typo))
                        typoCaught = false;
                }
            }
            Check(typoCaught, "every single-character typo is rejected");
            Check(!FriendCode.IsValid("") && !FriendCode.IsValid(null) && !FriendCode.IsValid("F-AAAAA"), "junk is rejected");
        }

        static void AcceptIsRequired()
        {
            FriendService a = Online("Alice");
            FriendService b = Online("Bob");
            Check(a.AddByCode(a.Code) == AddFriendResult.ThatsYou, "you can't add yourself");
            Check(a.AddByCode("not a code") == AddFriendResult.InvalidCode, "a bad code is refused");
            Check(a.AddByCode(b.Code) == AddFriendResult.Sent, "a request is sent");

            Check(WaitFor(delegate { return IsState(b, a.Id, FriendState.RequestReceived); }, 3000), "the other side sees the request");
            Check(Find(b, a.Id).Name == "Alice", "the request shows the sender's name");
            Check(Find(a, b.Id).State == FriendState.RequestSent, "the sender is only waiting");

            Check(!a.SendMessage(b.Id, "hello before accept"), "no messages can be queued before accepting");
            Check(!b.SendMessage(a.Id, "hello from the receiver"), "the receiver can't message before accepting either");
            Thread.Sleep(400);
            Check(Find(a, b.Id).State == FriendState.RequestSent, "time passing doesn't accept anything");
            Check(Find(a, b.Id).Status == FriendStatus.Offline, "a pending friend's status isn't shared");
            Check(Find(b, a.Id).Status == FriendStatus.Offline, "a requester's status isn't shared");

            b.AcceptRequest(a.Id);
            Check(WaitFor(delegate { return IsState(a, b.Id, FriendState.Friend); }, 3000), "accepting reaches the sender");
            Check(Find(a, b.Id).Name == "Bob", "the sender learns the friend's name");
            Check(WaitFor(delegate { return Find(a, b.Id).Status == FriendStatus.Online && Find(b, a.Id).Status == FriendStatus.Online; }, 3000), "friends see each other online");

            a.Dispose();
            b.Dispose();
        }

        static void Messages()
        {
            FriendService a = Online("Alice");
            FriendService b = Online("Bob");
            MakeFriends(a, b);
            List<string> heard = new List<string>();
            b.MessageReceived += delegate(string id, string text) { lock (heard) heard.Add(text); };

            Check(a.SendMessage(b.Id, "hello bob"), "a friend can send");
            Check(WaitFor(delegate { return b.GetChat(a.Id).Count == 1; }, 3000), "the message arrives");
            Check(b.GetChat(a.Id)[0].Text == "hello bob" && !b.GetChat(a.Id)[0].Mine, "with the right text, from them");
            Check(WaitFor(delegate { return a.GetChat(b.Id)[0].Delivered; }, 3000), "the sender sees it delivered");
            Check(WaitFor(delegate { lock (heard) return heard.Count == 1; }, 1000), "the notification fires once");
            Check(Find(b, a.Id).Unread == 1, "it counts as unread");
            b.MarkRead(a.Id);
            Check(Find(b, a.Id).Unread == 0, "and can be marked read");

            for (int i = 0; i < 25; i++)
                a.SendMessage(b.Id, "m" + i);
            Check(WaitFor(delegate { return b.GetChat(a.Id).Count == 26; }, 6000), "a burst all arrives");
            bool ordered = true;
            List<FriendChatEntry> chat = b.GetChat(a.Id);
            for (int i = 0; i < 25 && chat.Count == 26; i++)
                if (chat[i + 1].Text != "m" + i)
                    ordered = false;
            Check(ordered, "in order, with no duplicates");

            Check(!a.SendMessage(b.Id, "   "), "an empty message isn't sent");
            a.SendMessage(b.Id, new string('x', 5000));
            Check(WaitFor(delegate { return b.GetChat(a.Id).Count == 27; }, 3000), "long text arrives");
            Check(b.GetChat(a.Id)[26].Text.Length == FriendService.MaxMessageChars, "cut to the limit");

            a.Dispose();
            b.Dispose();
        }

        static void OfflineAndQueue()
        {
            string aFolder = NewFolder();
            string bFolder = NewFolder();
            FriendService a = Online("Alice", aFolder);
            FriendService b = Online("Bob", bFolder);
            MakeFriends(a, b);
            string aId = a.Id;
            string bId = b.Id;
            int bPort = b.Port;

            Check(WaitFor(delegate { return Find(a, bId).Status == FriendStatus.Online; }, 3000), "friend is online first");
            b.SetStatus(FriendStatus.Busy, "Bob");
            Check(WaitFor(delegate { return Find(a, bId).Status == FriendStatus.Busy; }, 3000), "busy reaches friends");
            b.SetStatus(FriendStatus.Away, "Bob");
            Check(WaitFor(delegate { return Find(a, bId).Status == FriendStatus.Away; }, 3000), "away reaches friends");

            b.SetStatus(FriendStatus.Offline, "Bob");
            Check(b.Status == FriendStatus.Offline, "offline stops the listener");
            Check(WaitFor(delegate { return Find(a, bId).Status == FriendStatus.Offline; }, 3000), "friends see you go offline");
            Check(ProbeGetsAnswer(a, bPort) == false, "nothing answers while you're offline");

            Check(a.SendMessage(bId, "while you were out"), "a message can wait for someone offline");
            Thread.Sleep(300);
            Check(!a.GetChat(bId)[0].Delivered, "it isn't marked delivered");

            // Both restart: the queue, friendship and key must all survive on disk.
            a.Dispose();
            a = Online("Alice", aFolder);
            Check(a.Id == aId, "your key survives a restart");
            Check(IsState(a, bId, FriendState.Friend), "friendship survives a restart");
            a.SetAddress(IPAddress.Loopback, a.Port);
            b.SetStatus(FriendStatus.Online, "Bob");
            Check(WaitFor(delegate { return b.GetChat(aId).Count == 1 && b.GetChat(aId)[0].Text == "while you were out"; }, 5000), "the queued message arrives when both are online");
            Check(WaitFor(delegate { return a.GetChat(bId)[0].Delivered; }, 3000), "and is marked delivered");

            b.Dispose();
            b = Online("Bob", bFolder);
            Check(b.GetChat(aId).Count == 1, "chat history survives a restart");

            a.Dispose();
            b.Dispose();
        }

        // Port is free again once offline, so a stranger's hello would get nothing.
        static bool ProbeGetsAnswer(FriendService unused, int port)
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                byte[] hello = new byte[160];
                new Random().NextBytes(hello);
                BitConverter.GetBytes((uint)0x3152464D).CopyTo(hello, 0);
                hello[4] = 1;
                socket.SendTo(hello, new IPEndPoint(IPAddress.Loopback, port));
                if (!socket.Poll(300000, SelectMode.SelectRead))
                    return false;
                try
                {
                    // Windows also wakes up for "that port is closed", which isn't an answer.
                    return socket.Receive(new byte[2048]) > 0;
                }
                catch (SocketException)
                {
                    return false;
                }
            }
        }

        // A modified copy: it holds a real session and sends whatever it likes down it.
        static void ModifiedCopy()
        {
            const byte Ping = 1, Accept = 3, Message = 4, Ack = 5;
            FriendService target = Online("Target");
            FriendService attacker = Online("Attacker");
            attacker.AddByCode(target.Code);
            Check(WaitFor(delegate { return IsState(target, attacker.Id, FriendState.RequestReceived); }, 3000), "the attacker gets as far as a request");

            byte[] text = Encoding.UTF8.GetBytes("you never accepted me");
            byte[] message = new byte[37 + text.Length];
            message[0] = Message;
            BitConverter.GetBytes((uint)1234).CopyTo(message, 1);
            BitConverter.GetBytes((ulong)1).CopyTo(message, 5);
            BitConverter.GetBytes(DateTime.UtcNow.Ticks).CopyTo(message, 13);
            text.CopyTo(message, 37);
            byte[] accept = new byte[] { Accept, 0, 0 };
            byte[] ping = new byte[] { Ping, (byte)FriendStatus.Online, 1, 2, 3, 4, 3, (byte)'B', (byte)'o', (byte)'b', 0 };
            byte[] ack = new byte[9];
            ack[0] = Ack;
            for (int i = 0; i < 40; i++)
            {
                attacker.SendInnerForTests(target.Id, message);
                attacker.SendInnerForTests(target.Id, accept);
                attacker.SendInnerForTests(target.Id, ping);
                attacker.SendInnerForTests(target.Id, ack);
                Thread.Sleep(10);
            }
            Thread.Sleep(200);
            Check(IsState(target, attacker.Id, FriendState.RequestReceived), "sending a fake accept doesn't make you a friend");
            Check(target.GetChat(attacker.Id).Count == 0, "messages before accepting are dropped unread");
            Check(Find(target, attacker.Id).Unread == 0, "and count for nothing");
            Check(Find(target, attacker.Id).Status == FriendStatus.Offline, "a fake status is ignored");
            Check(Find(target, attacker.Id).Name == "Attacker", "a fake rename is ignored");
            Check(IsState(attacker, target.Id, FriendState.RequestSent), "the attacker hears no accept in return");
            Check(Find(attacker, target.Id).Status == FriendStatus.Offline, "and learns nothing about the target's status");

            // A third party can't accept on someone else's behalf.
            FriendService wanted = Online("Wanted");
            FriendService forger = Online("Forger");
            target.AddByCode(wanted.Code);
            target.AddByCode(forger.Code);
            WaitFor(delegate { return IsState(forger, target.Id, FriendState.RequestReceived); }, 3000);
            for (int i = 0; i < 10; i++)
                forger.SendInnerForTests(target.Id, accept);
            Thread.Sleep(200);
            Check(IsState(target, wanted.Id, FriendState.RequestSent), "someone else's accept doesn't count for the person you asked");

            // An accept only counts if you asked. Someone you never asked stays a request.
            FriendService stranger = Online("Stranger");
            stranger.AddByCode(target.Code);
            WaitFor(delegate { return IsState(target, stranger.Id, FriendState.RequestReceived); }, 3000);
            stranger.SendInnerForTests(target.Id, accept);
            Thread.Sleep(200);
            Check(IsState(target, stranger.Id, FriendState.RequestReceived), "an accept you didn't ask for does nothing");

            target.Dispose();
            attacker.Dispose();
            wanted.Dispose();
            forger.Dispose();
            stranger.Dispose();
        }

        static void AddressChangeAcrossRestart()
        {
            string folder = NewFolder();
            FriendService a = Make(folder);
            a.SetStatus(FriendStatus.Online, "Alice");
            int port = a.Port;
            Check(!a.SetAddress(IPAddress.Parse("198.51.100.1"), port), "your first address isn't a change");
            a.Dispose();
            a = Make(folder);
            a.SetStatus(FriendStatus.Online, "Alice");
            Check(!a.SetAddress(IPAddress.Parse("198.51.100.1"), a.Port), "the same address after a restart isn't a change");
            Check(a.SetAddress(IPAddress.Parse("198.51.100.2"), a.Port), "a new address after a restart is");
            a.Dispose();
        }

        static void AddressChange()
        {
            FriendService a = Online("Alice");
            FriendService b = Online("Bob");
            MakeFriends(a, b);
            string oldCode = b.Code;

            int otherPort = b.Port + 1;
            Check(b.SetAddress(IPAddress.Parse("127.0.0.1"), otherPort), "a changed address is reported");
            Check(b.Code != oldCode, "the code changes with the address");
            Check(!b.SetAddress(IPAddress.Parse("127.0.0.1"), otherPort), "the same address isn't a change");
            b.SetAddress(IPAddress.Loopback, b.Port);

            // A new code from the same person updates them, keeping the friendship.
            Check(a.AddByCode(b.Code) == AddFriendResult.AddressUpdated, "pasting a friend's new code updates them");
            Check(IsState(a, b.Id, FriendState.Friend), "and stays a friend, with no new request");

            a.Dispose();
            b.Dispose();
        }

        static void BlockAndRemove()
        {
            FriendService a = Online("Alice");
            FriendService b = Online("Bob");
            a.AddByCode(b.Code);
            WaitFor(delegate { return IsState(b, a.Id, FriendState.RequestReceived); }, 3000);
            b.Block(a.Id);
            Thread.Sleep(500);
            Check(IsState(b, a.Id, FriendState.Blocked), "someone blocked stays blocked");
            Check(IsState(a, b.Id, FriendState.RequestSent), "a blocked sender never gets accepted");
            Check(b.AddByCode(a.Code) == AddFriendResult.Blocked, "a blocked code can't be added by accident");
            a.Dispose();
            b.Dispose();

            a = Online("Alice");
            b = Online("Bob");
            MakeFriends(a, b);
            a.SendMessage(b.Id, "one");
            WaitFor(delegate { return b.GetChat(a.Id).Count == 1; }, 3000);
            b.Remove(a.Id);
            Check(Find(b, a.Id) == null, "a removed friend is gone");
            Check(b.GetChat(a.Id).Count == 0, "with their chat");
            a.SendMessage(b.Id, "still there?");
            Thread.Sleep(500);
            Check(Find(b, a.Id) == null, "a removed friend's messages don't bring them back");
            Check(!a.GetChat(b.Id)[1].Delivered, "and aren't acknowledged");
            a.Dispose();
            b.Dispose();
        }

        static void Isolation()
        {
            FriendService a = Online("Alice");
            FriendService b = Online("Bob");
            FriendService c = Online("Carol");
            MakeFriends(a, b);
            MakeFriends(a, c);
            WaitFor(delegate { return Find(b, a.Id).Status == FriendStatus.Online; }, 2000);
            b.SendMessage(a.Id, "secret for Alice");
            c.SendMessage(a.Id, "other secret");
            WaitFor(delegate { return a.GetChat(b.Id).Count == 1 && a.GetChat(c.Id).Count == 1; }, 3000);
            Check(a.GetChat(b.Id)[0].Text == "secret for Alice" && a.GetChat(c.Id)[0].Text == "other secret", "each chat holds only its own messages");
            Check(Find(b, c.Id) == null && Find(c, b.Id) == null, "friends of a friend never learn about each other");
            Check(b.GetPeople().Count == 1 && c.GetPeople().Count == 1, "your list only has your own friends");
            a.Dispose();
            b.Dispose();
            c.Dispose();
        }

        static void NothingReadableOnDisk()
        {
            string folder = NewFolder();
            FriendService a = Online("AliceLongName", folder);
            FriendService b = Online("BobLongName");
            MakeFriends(a, b);
            a.SendMessage(b.Id, "VerySecretMessageText");
            WaitFor(delegate { return a.GetChat(b.Id).Count == 1 && a.GetChat(b.Id)[0].Delivered; }, 3000);
            string bId = b.Id;
            a.Dispose();
            b.Dispose();

            string[] secrets = { "BobLongName", "VerySecretMessageText", "127.0.0.1" };
            bool leaked = false;
            int files = 0;
            foreach (string file in Directory.GetFiles(folder))
            {
                files++;
                string raw = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(file));
                foreach (string secret in secrets)
                    if (raw.Contains(secret) || raw.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret))))
                        leaked = true;
            }
            Check(files == 2, "one file for you and one for the chat");
            Check(!leaked, "nothing is readable in any file");

            // A damaged identity must be kept, not replaced silently.
            string store = Path.Combine(folder, "friends-v1.dat");
            byte[] damaged = File.ReadAllBytes(store);
            damaged[damaged.Length / 2] ^= 0x40;
            File.WriteAllBytes(store, damaged);
            FriendService after = new FriendService(folder);
            Check(after.IdentityWasLost, "a damaged identity is reported");
            Check(File.Exists(Path.Combine(folder, "friends-v1.unreadable")), "and the file is kept");
            after.Dispose();

            // A locked identity must not be replaced at all.
            using (new FileStream(store, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                FriendService locked = new FriendService(folder);
                Check(locked.StorageFailed, "a locked identity is detected");
                Check(!locked.SetStatus(FriendStatus.Online, "x"), "going online is refused rather than using a new key");
                locked.Dispose();
            }
        }

        // A listener that gets junk: nothing may crash, answer or change.
        static void HostilePackets()
        {
            FriendService a = Online("Alice");
            FriendService b = Online("Bob");
            MakeFriends(a, b);
            int before = a.GetPeople().Count;
            Random random = new Random(12345);
            bool answered = false;
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                IPEndPoint target = new IPEndPoint(IPAddress.Loopback, a.Port);
                for (int i = 0; i < 20000; i++)
                {
                    byte[] junk = new byte[random.Next(0, 1400)];
                    random.NextBytes(junk);
                    if (junk.Length >= 5 && i % 2 == 0)
                    {
                        BitConverter.GetBytes((uint)0x3152464D).CopyTo(junk, 0);
                        junk[4] = (byte)random.Next(0, 8);
                    }
                    if (i % 5 == 0 && junk.Length >= 160)
                        Array.Resize(ref junk, 160);
                    socket.SendTo(junk, target);
                    if (i % 500 == 0)
                        Thread.Sleep(5);
                }
                answered = socket.Poll(500000, SelectMode.SelectRead);
            }
            Check(!answered, "20,000 junk packets get no answer at all");
            Check(a.GetPeople().Count == before, "and add nobody");
            Check(a.SendMessage(b.Id, "still alive"), "the listener still accepts sends");
            Check(WaitFor(delegate { return b.GetChat(a.Id).Count == 1; }, 3000), "and still works");
            a.Dispose();
            b.Dispose();
        }

        static void WrongKeyBehindCode()
        {
            // Someone answers at Bob's address but has a different key: must not be believed.
            FriendService a = Online("Alice");
            FriendService real = Online("Bob");
            FriendService impostor = Online("Mallory");
            byte[] realFingerprint = FriendCode.Fingerprint(real.PublicKey);
            string pointsAtImpostor = FriendCode.Encode(IPAddress.Loopback, impostor.Port, realFingerprint);
            a.AddByCode(pointsAtImpostor);
            Thread.Sleep(500);
            Check(Find(impostor, a.Id) == null, "a listener with another key never hears the request");
            Check(IsState(a, real.Id, FriendState.RequestSent), "the request stays unanswered");
            Check(Find(a, real.Id).Name != "Mallory", "no name is taken from the impostor");
            a.Dispose();
            real.Dispose();
            impostor.Dispose();
        }

        static string HexOf(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", String.Empty).ToLowerInvariant();
        }

        static void Backups()
        {
            byte[] pw = Encoding.ASCII.GetBytes("password");
            byte[] salt = Encoding.ASCII.GetBytes("salt");
            Check(HexOf(FriendBackup.Pbkdf2(pw, salt, 4096)) == "c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a", "PBKDF2 matches its published test vector");

            FriendService a = Online("Alice");
            FriendService b = Online("Bob");
            string aId = a.Id;
            MakeFriends(a, b);
            Check(!a.BackedUp, "a new account isn't backed up");
            a.SendMessage(b.Id, "before backup");
            WaitFor(delegate { return b.GetChat(aId).Count == 1; }, 3000);

            string file = Path.Combine(_root, "alice.maelakey");
            const string password = "correct horse battery";
            a.ExportBackup(file, password);
            Check(a.BackedUp, "the backup is remembered");
            string raw = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(file));
            Check(!raw.Contains("Bob") && !raw.Contains("127.0.0.1") && !raw.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Bob"))), "nothing is readable in the backup");

            // Time passes after the backup: the numbering moves on, on both sides.
            for (int i = 0; i < 3; i++)
                a.SendMessage(b.Id, "after backup " + i);
            b.SendMessage(aId, "reply");
            WaitFor(delegate { return b.GetChat(aId).Count == 5 && a.GetChat(b.Id).Count == 5; }, 4000);
            a.Dispose();
            Check(b.SendMessage(aId, "while restoring"), "a friend can write while you're restoring");

            // A new PC: a different key until the backup goes in.
            FriendService fresh = Make(NewFolder());
            Check(fresh.Id != aId, "a new PC starts as someone else");
            Check(fresh.RestoreBackup(file, "wrong password here") == RestoreResult.WrongPassword, "a wrong password is refused");
            Check(fresh.Id != aId, "and changes nothing");

            string junk = Path.Combine(_root, "junk.maelakey");
            File.WriteAllBytes(junk, new byte[5000]);
            Check(fresh.RestoreBackup(junk, password) == RestoreResult.NotABackup, "a file that isn't a backup is refused");
            byte[] tampered = File.ReadAllBytes(file);
            tampered[tampered.Length / 2] ^= 1;
            File.WriteAllBytes(junk, tampered);
            Check(fresh.RestoreBackup(junk, password) == RestoreResult.WrongPassword, "a changed backup is refused");
            byte[] slow = File.ReadAllBytes(file);
            BitConverter.GetBytes(int.MaxValue).CopyTo(slow, 8);
            File.WriteAllBytes(junk, slow);
            Check(fresh.RestoreBackup(junk, password) == RestoreResult.NotABackup, "a file demanding huge work is refused");

            // Correctly encrypted, with nonsense inside: must not cost the current account.
            File.WriteAllBytes(junk, FriendBackup.Seal(Encoding.UTF8.GetBytes("nonsense|with|no|key\nme|!!!|1|x"), password));
            string before = fresh.Id;
            Check(fresh.RestoreBackup(junk, password) == RestoreResult.NotABackup, "a backup with nonsense inside is refused");
            Check(fresh.Id == before, "and keeps your account");

            fresh.SetStatus(FriendStatus.Online, "Alice");
            Check(fresh.RestoreBackup(file, password) == RestoreResult.StillOnline, "restoring while online is refused");
            fresh.SetStatus(FriendStatus.Offline, "Alice");
            Check(fresh.RestoreBackup(file, password) == RestoreResult.Restored, "the right password restores");
            Check(fresh.Id == aId, "your account is back");
            Check(IsState(fresh, b.Id, FriendState.Friend), "with your friends");
            Check(Directory.GetFiles(_root, "friends-v1.replaced", SearchOption.AllDirectories).Length == 1, "the account it replaced is kept");

            fresh.SetStatus(FriendStatus.Online, "Alice");
            fresh.SetAddress(IPAddress.Loopback, fresh.Port);
            b.AddByCode(fresh.Code); // The new PC has a new address.
            Check(WaitFor(delegate { return Find(b, aId).Status == FriendStatus.Online; }, 4000), "your friend reaches you again, with no new request");

            // The backup was out of date. Nothing may be taken for a duplicate or stuck as out of order.
            Check(WaitFor(delegate { return fresh.GetChat(b.Id).Count == 1 && fresh.GetChat(b.Id)[0].Text == "while restoring"; }, 4000), "a waiting message arrives after an out-of-date restore");
            fresh.SendMessage(b.Id, "new from restored");
            Check(WaitFor(delegate { List<FriendChatEntry> c = b.GetChat(aId); return c[c.Count - 1].Text == "new from restored"; }, 4000), "your new messages aren't taken for duplicates");
            b.SendMessage(aId, "new from friend");
            Check(WaitFor(delegate { List<FriendChatEntry> c = fresh.GetChat(b.Id); return c[c.Count - 1].Text == "new from friend"; }, 4000), "their new messages aren't stuck as out of order");
            Check(WaitFor(delegate { return b.GetChat(aId).TrueForAll(delegate(FriendChatEntry e) { return !e.Mine || e.Delivered; }); }, 4000), "your friend sees everything delivered");

            fresh.Dispose();
            b.Dispose();
        }

        static void RoomPasswordProof()
        {
            const string password = "SuperSecretRoomPassword";
            byte[] hash = VoipEngine.HashPassword(password);
            byte[] one = CryptoUtil.Random(32);
            byte[] two = CryptoUtil.Random(32);
            string field = VoipEngine.JoinPasswordField(password, one);
            Check(!field.Contains(password) && !Encoding.UTF8.GetString(Convert.FromBase64String(field.Substring(7))).Contains(password), "the password isn't in what's sent");
            Check(!field.Contains(Convert.ToBase64String(hash)), "nor is its plain hash");
            Check(VoipEngine.JoinPasswordValid(field, hash, one), "the right password is accepted");
            Check(!VoipEngine.JoinPasswordValid(field, hash, two), "a proof can't be replayed on another connection");
            Check(!VoipEngine.JoinPasswordValid(VoipEngine.JoinPasswordField("wrong one", one), hash, one), "a wrong password is refused");
            Check(!VoipEngine.JoinPasswordValid("proof1:not base64!", hash, one), "a broken proof is refused");
            Check(!VoipEngine.JoinPasswordValid("", hash, one), "no password is refused");
            Check(VoipEngine.JoinPasswordValid(password, hash, one), "older copies sending the plain password still get in");
            Check(VoipEngine.JoinPasswordField("", one) == "" && VoipEngine.JoinPasswordValid("", null, one), "rooms with no password are unchanged");
        }

        static void NameChanges()
        {
            FriendService a = Online("Alice");
            FriendService b = Online("Bob");
            MakeFriends(a, b);
            a.SetName("Alicia");
            Check(WaitFor(delegate { return Find(b, a.Id).Name == "Alicia"; }, 3000), "a new display name reaches friends");
            a.Dispose();
            b.Dispose();
        }

        sealed class CallLog
        {
            public readonly List<string> Events = new List<string>();
            public string Code;
            public string Password;

            public CallLog(FriendService service)
            {
                service.CallEvent += delegate(FriendCallEvent kind, string id, string code, string password)
                {
                    lock (Events)
                    {
                        Events.Add(kind.ToString());
                        if (code != null && kind == FriendCallEvent.JoinRoom) { Code = code; Password = password; }
                    }
                };
            }

            public bool Saw(string kind)
            {
                lock (Events) return Events.Contains(kind);
            }
        }

        static void Calls()
        {
            string roomCode = RoomCode.Encode(IPAddress.Loopback, 45555, VoipEngineTestKeys.HostFingerprint);
            FriendService a = Online("Alice");
            FriendService b = Online("Bob");
            FriendService stranger = Online("Stranger");
            MakeFriends(a, b);
            WaitFor(delegate { return Find(a, b.Id).Status == FriendStatus.Online; }, 3000);
            CallLog aLog = new CallLog(a);
            CallLog bLog = new CallLog(b);

            // The callee can host.
            Check(a.StartCall(b.Id, false), "you can call a friend who's online");
            Check(!a.StartCall(b.Id, false), "one call at a time");
            Check(WaitFor(delegate { return bLog.Saw("Incoming"); }, 3000), "it rings them");
            Thread.Sleep(200);
            Check(!bLog.Saw("JoinRoom") && !bLog.Saw("HostNow"), "ringing starts nothing on its own");
            b.AcceptCall(true);
            Check(WaitFor(delegate { return bLog.Saw("HostNow"); }, 3000), "accepting asks the side with an open port to host");
            b.CallRoomIsReady(roomCode, "onetimepassword");
            Check(WaitFor(delegate { return aLog.Saw("JoinRoom"); }, 3000), "the caller is told about the room");
            Check(aLog.Code == roomCode && aLog.Password == "onetimepassword", "with the right code and password");
            a.CallRingSeconds = 1;
            b.CallRingSeconds = 1;
            a.CallRoomIsReady(roomCode, "refresh"); // Restarts the timer at the shorter length.
            b.CallRoomIsReady(roomCode, "onetimepassword");
            Thread.Sleep(1500);
            Check(!aLog.Saw("Ended") && !bLog.Saw("Ended"), "the ring timer doesn't end a call that's under way");
            a.CallRingSeconds = 30;
            b.CallRingSeconds = 30;
            a.EndCall();
            b.EndCall();

            // Only the caller can host.
            aLog = new CallLog(a);
            bLog = new CallLog(b);
            a.StartCall(b.Id, true);
            WaitFor(delegate { return bLog.Saw("Incoming"); }, 3000);
            b.AcceptCall(false);
            Check(WaitFor(delegate { return aLog.Saw("HostNow"); }, 3000), "if only the caller has an open port, they host");
            a.CallRoomIsReady(roomCode, "secondpassword");
            Check(WaitFor(delegate { return bLog.Saw("JoinRoom"); }, 3000), "and the person called joins");
            a.EndCall();
            b.EndCall();

            // Declining.
            aLog = new CallLog(a);
            bLog = new CallLog(b);
            a.StartCall(b.Id, true);
            WaitFor(delegate { return bLog.Saw("Incoming"); }, 3000);
            b.EndCall();
            Check(WaitFor(delegate { return aLog.Saw("Declined"); }, 3000), "declining tells the caller");
            Thread.Sleep(300);
            bLog.Events.Clear();
            Thread.Sleep(300);
            Check(!bLog.Saw("Incoming"), "and it stops ringing");

            // Cancelling.
            aLog = new CallLog(a);
            bLog = new CallLog(b);
            a.StartCall(b.Id, true);
            WaitFor(delegate { return bLog.Saw("Incoming"); }, 3000);
            a.EndCall();
            Check(WaitFor(delegate { return bLog.Saw("Ended"); }, 3000), "hanging up stops it ringing");
            Thread.Sleep(200);

            // A friend can't push you into a room you didn't ask for.
            aLog = new CallLog(a);
            bLog = new CallLog(b);
            byte[] code = Encoding.ASCII.GetBytes(roomCode);
            byte[] room = new byte[10 + 2 + code.Length + 1];
            room[0] = 7; room[1] = 3;
            BitConverter.GetBytes((ulong)99).CopyTo(room, 2);
            room[10] = (byte)code.Length;
            code.CopyTo(room, 11);
            room[11 + code.Length] = 1;
            room[12 + code.Length] = (byte)'x';
            for (int i = 0; i < 20; i++)
                a.SendInnerForTests(b.Id, room);
            Thread.Sleep(200);
            Check(!bLog.Saw("JoinRoom"), "a room sent out of nowhere is ignored");

            a.StartCall(b.Id, true);
            WaitFor(delegate { return bLog.Saw("Incoming"); }, 3000);
            BitConverter.GetBytes((ulong)99).CopyTo(room, 2);
            for (int i = 0; i < 20; i++)
                a.SendInnerForTests(b.Id, room);
            Thread.Sleep(200);
            Check(!bLog.Saw("JoinRoom"), "a room for another call is ignored while ringing");
            a.EndCall();
            b.EndCall();
            Thread.Sleep(100);

            // Only friends can ring.
            stranger.AddByCode(a.Code);
            WaitFor(delegate { return IsState(a, stranger.Id, FriendState.RequestReceived); }, 3000);
            aLog = new CallLog(a);
            Check(!stranger.StartCall(a.Id, true), "you can't call someone before they accept");
            byte[] invite = new byte[11];
            invite[0] = 7; invite[1] = 1; invite[2] = 77;
            for (int i = 0; i < 20; i++)
                stranger.SendInnerForTests(a.Id, invite);
            Thread.Sleep(200);
            Check(!aLog.Saw("Incoming"), "a modified copy can't ring someone who hasn't accepted");

            a.Dispose();
            b.Dispose();
            stranger.Dispose();
        }

        static void Reactions()
        {
            string aFolder = NewFolder();
            FriendService a = Online("Alice", aFolder);
            FriendService b = Online("Bob");
            MakeFriends(a, b);
            string aId = a.Id;
            string bId = b.Id;
            a.SendMessage(bId, "react to this \U0001F600");
            WaitFor(delegate { return b.GetChat(aId).Count == 1; }, 3000);
            Check(b.GetChat(aId)[0].Text.Contains("\U0001F600"), "emoji arrive intact");
            ulong id = b.GetChat(aId)[0].MessageId;
            Check(id != 0 && id == a.GetChat(bId)[0].MessageId, "both sides know the message by the same id");
            b.MarkRead(aId);

            string thumb = EmojiPack.All[0].Text;
            int unreadBefore = Find(a, bId).Unread;
            List<string> heard = new List<string>();
            a.MessageReceived += delegate(string who, string text) { lock (heard) heard.Add(text); };
            Check(b.ToggleReaction(aId, id, thumb), "you can react to a message");
            Check(WaitFor(delegate { return FriendChat.Summarize(a.GetChat(bId)).ContainsKey(id); }, 3000), "the reaction reaches the sender");
            FriendReaction seen = FriendChat.Summarize(a.GetChat(bId))[id][0];
            Check(seen.Emoji == thumb && seen.Count == 1 && !seen.Mine, "with the right emoji, from them");
            Check(FriendChat.Summarize(b.GetChat(aId))[id][0].Mine, "and shows as yours on your side");
            Thread.Sleep(150);
            Check(Find(a, bId).Unread == unreadBefore, "a reaction doesn't count as unread");
            lock (heard) Check(heard.Count == 0, "and makes no notification");

            a.ToggleReaction(bId, id, thumb);
            Check(WaitFor(delegate { List<FriendReaction> l; return FriendChat.Summarize(b.GetChat(aId)).TryGetValue(id, out l) && l[0].Count == 2 && l[0].Mine; }, 3000), "both of you can use the same reaction");
            b.ToggleReaction(aId, id, thumb);
            Check(WaitFor(delegate { List<FriendReaction> l; return FriendChat.Summarize(a.GetChat(bId)).TryGetValue(id, out l) && l[0].Count == 1 && l[0].Mine; }, 3000), "clicking again takes yours away");

            Check(!a.ToggleReaction(bId, id, "not an emoji"), "only known emoji can be reactions");
            Check(!a.ToggleReaction(bId, 123456, thumb), "only real messages can be reacted to");

            // A modified copy tries anyway.
            int countBefore = a.GetChat(bId).Count;
            byte[] word = Encoding.UTF8.GetBytes("<script>");
            ulong next = 100;
            byte[] forged = new byte[37 + word.Length];
            forged[0] = 4;
            BitConverter.GetBytes(next).CopyTo(forged, 5);
            BitConverter.GetBytes(id).CopyTo(forged, 29);
            word.CopyTo(forged, 37);
            b.SendInnerForTests(aId, forged);
            Thread.Sleep(200);
            Check(a.GetChat(bId).Count == countBefore, "a fake reaction out of order is dropped");
            Check(a.SendMessage(bId, "still fine"), "and the chat carries on");
            Check(WaitFor(delegate { return a.GetChat(bId)[a.GetChat(bId).Count - 1].Delivered; }, 3000), "with no harm done");

            // Reactions are in the chat file, so they survive a restart.
            a.Dispose();
            a = Online("Alice", aFolder);
            Check(FriendChat.Summarize(a.GetChat(bId)).ContainsKey(id), "reactions survive a restart");
            a.Dispose();
            b.Dispose();

            // Files written before reactions still load.
            string old = NewFolder();
            Directory.CreateDirectory(old);
            string chatFile = Path.Combine(old, "old.dat");
            EncryptedFile.Write(chatFile, Encoding.UTF8.GetBytes("1|" + DateTime.UtcNow.Ticks + "|1|1|" + Convert.ToBase64String(Encoding.UTF8.GetBytes("old message")) + "\n"));
            FriendChat chat = new FriendChat(chatFile);
            Check(chat.Entries.Count == 1 && chat.Entries[0].Text == "old message" && chat.Entries[0].MessageId == 0, "older chat files still load");
        }

        static void Sanitising()
        {
            Check(FriendService.CleanName("Bob‮evil\u0000\n") == "Bobevil", "names lose invisible and direction characters");
            Check(FriendService.CleanName(new string('x', 200)).Length == 24, "names are cut to 24 characters");
            Check(FriendService.CleanText("a‮b\u0007c") == "abc", "messages lose direction and control characters");
            Check(FriendService.CleanText(new string('€', 400)).Length < 400, "messages stay under the packet limit");
        }

        static int Main()
        {
            _root = Path.Combine(Path.GetTempPath(), "MaelaFriendTests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Codes();
                AcceptIsRequired();
                Messages();
                OfflineAndQueue();
                AddressChange();
                AddressChangeAcrossRestart();
                ModifiedCopy();
                Backups();
                RoomPasswordProof();
                NameChanges();
                Calls();
                Reactions();
                BlockAndRemove();
                Isolation();
                NothingReadableOnDisk();
                HostilePackets();
                WrongKeyBehindCode();
                Sanitising();
            }
            finally
            {
                try { Directory.Delete(_root, true); }
                catch { }
            }
            Console.WriteLine(_failures == 0 ? "All friend tests passed." : _failures + " friend test(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
