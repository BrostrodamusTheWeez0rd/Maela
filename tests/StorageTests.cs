using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Maela.Tests
{
    // Saved rooms hold room codes, and a room code holds the host's address, so nothing in the
    // file may be readable on disk. Losing or overwriting someone's rooms is just as bad.
    //
    // Build and run:  scripts\test_ci.cmd
    internal static class StorageTests
    {
        const string RoomName = "Friday Night Room";
        const string ChannelName = "SecretChannelName";
        const string Password = "hunter2-password";

        static int _failures;
        static string _folder;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok)
                _failures++;
        }

        static string Encrypted { get { return Path.Combine(_folder, "rooms-v3.dat"); } }
        static string Unencrypted { get { return Path.Combine(_folder, "rooms-v2.txt"); } }
        static string Aside { get { return Path.Combine(_folder, "rooms-v3.unreadable"); } }

        static string Code()
        {
            return RoomCode.Encode(IPAddress.Parse("203.0.113.77"), 41234, VoipEngineTestKeys.HostFingerprint);
        }

        static SavedRoom Room()
        {
            SavedRoom room = new SavedRoom();
            room.Name = RoomName;
            room.Invite = Code();
            room.Owned = true;
            room.PreferredPort = 41234;
            room.ChannelNames.Add(ChannelName);
            room.HostKey = VoipEngineTestKeys.HostPrivateKey;
            room.Password = Password;
            return room;
        }

        static List<SavedRoom> Rooms()
        {
            return new List<SavedRoom>(new SavedRoom[] { Room() });
        }

        static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        // The file as the previous version wrote it.
        static void WriteUnencryptedFile()
        {
            File.WriteAllLines(Unencrypted, new string[]
            {
                "1|41234|" + B64(RoomName) + "|" + B64(Code()) + "|1|" + B64(ChannelName) + "|" +
                    SecretStore.Protect(VoipEngineTestKeys.HostPrivateKey) + "|" + SecretStore.Protect(Encoding.UTF8.GetBytes(Password))
            });
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        static bool IsIntact(List<SavedRoom> rooms)
        {
            if (rooms.Count != 1)
                return false;
            SavedRoom r = rooms[0];
            return r.Name == RoomName && r.Invite == Code() && r.Owned && r.PreferredPort == 41234 &&
                r.ChannelNames.Count == 1 && r.ChannelNames[0] == ChannelName &&
                Same(r.HostKey, VoipEngineTestKeys.HostPrivateKey) && r.Password == Password;
        }

        static void Reset()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
            Directory.CreateDirectory(_folder);
        }

        static void NothingReadable()
        {
            Reset();
            SavedRoomStore.Load();
            Check(SavedRoomStore.Save(Rooms()), "rooms save");
            byte[] raw = File.ReadAllBytes(Encrypted);
            string[] secrets = new string[] { RoomName, Code(), ChannelName, Password, "203.0.113.77", "41234" };
            bool leaked = false;
            string latin = Encoding.GetEncoding(28591).GetString(raw);
            foreach (string secret in secrets)
                if (latin.Contains(secret) || latin.Contains(B64(secret)))
                    leaked = true;
            Check(!leaked, "nothing is readable in the file, as text or Base64");
            Check(IsIntact(SavedRoomStore.Load()), "every field survives saving and loading");
            Check(!File.Exists(Encrypted + ".tmp"), "no temporary file is left behind");

            Check(SavedRoomStore.Save(Rooms()), "saving over an existing file works");
            Check(IsIntact(SavedRoomStore.Load()), "the replaced file loads");

            Check(SavedRoomStore.Save(new List<SavedRoom>()), "an empty list saves");
            Check(SavedRoomStore.Load().Count == 0 && !SavedRoomStore.LoadFailed, "an empty list loads, with no warning");
        }

        static void ConvertsOldFile()
        {
            Reset();
            WriteUnencryptedFile();
            Check(IsIntact(SavedRoomStore.Load()), "an old file's rooms load");
            Check(!File.Exists(Unencrypted), "the old file is deleted once converted");
            Check(File.Exists(Encrypted), "the encrypted file exists");
            Check(IsIntact(SavedRoomStore.Load()), "the converted rooms load next time");

            // A crash after writing the new file, before deleting the old one.
            Reset();
            SavedRoomStore.Load();
            SavedRoomStore.Save(Rooms());
            WriteUnencryptedFile();
            Check(IsIntact(SavedRoomStore.Load()) && !File.Exists(Unencrypted), "a leftover old file is removed");

            // The encrypted file can't be written (a folder is in its way): keep the old file.
            Reset();
            WriteUnencryptedFile();
            Directory.CreateDirectory(Encrypted + ".tmp");
            Check(IsIntact(SavedRoomStore.Load()), "rooms load even when conversion fails");
            Check(File.Exists(Unencrypted), "the old file is kept when conversion fails");
        }

        static void KeepsUnreadableFile()
        {
            Reset();
            byte[] garbage = Encoding.ASCII.GetBytes("this was encrypted by another Windows account");
            File.WriteAllBytes(Encrypted, garbage);
            Check(SavedRoomStore.Load().Count == 0, "an unreadable file loads as no rooms");
            Check(SavedRoomStore.LoadFailed, "an unreadable file raises the warning");
            Check(Same(File.ReadAllBytes(Aside), garbage), "an unreadable file is kept");
            Check(SavedRoomStore.Save(Rooms()), "new rooms can be saved afterwards");
            Check(Same(File.ReadAllBytes(Aside), garbage), "saving leaves the kept file alone");
            Check(IsIntact(SavedRoomStore.Load()) && !SavedRoomStore.LoadFailed, "the new list loads, with no warning");

            // A damaged real file, with an earlier unreadable one already set aside.
            byte[] damaged = File.ReadAllBytes(Encrypted);
            damaged[damaged.Length / 2] ^= 0x40;
            File.WriteAllBytes(Encrypted, damaged);
            Check(SavedRoomStore.Load().Count == 0 && SavedRoomStore.LoadFailed, "a damaged file is detected");
            Check(Same(File.ReadAllBytes(Aside), garbage), "the first kept file isn't overwritten");
            Check(Directory.GetFiles(_folder, "rooms-v3.unreadable*").Length == 2, "both unreadable files are kept");
        }

        static void LockedFileIsNotOverwritten()
        {
            Reset();
            SavedRoomStore.Load();
            SavedRoomStore.Save(Rooms());
            using (new FileStream(Encrypted, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check(SavedRoomStore.Load().Count == 0 && !SavedRoomStore.LoadFailed, "a locked file isn't called unreadable");
                Check(!SavedRoomStore.Save(new List<SavedRoom>()), "saving is refused after a failed read");
            }
            Check(!SavedRoomStore.Save(new List<SavedRoom>()), "saving stays refused once the lock is gone");
            Check(IsIntact(SavedRoomStore.Load()), "the rooms are all still there next launch");
        }

        static object CallMainForm(string method, params object[] args)
        {
            return typeof(MainForm).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, args);
        }

        static bool Finite(double v)
        {
            return !Double.IsNaN(v) && !Double.IsInfinity(v);
        }

        // Where rooms, friends, circles and channels sit in the Dynamic layout, as saved on disk.
        static void SpatialLayout()
        {
            Reset();
            SavedRoom placed = Room();
            placed.Circle = "c1a2b3";
            placed.ChannelLayout = "1.5708,0.8,1.2;;0.5,1.7,0.6";
            placed.Placed = true;
            placed.PlaceX = -123.4;
            placed.PlaceY = 56.7;
            placed.Size = 1.4;
            SavedRoom sized = Room();
            sized.Name = "Sized";
            sized.Circle = "fav";
            sized.Size = 0.8;
            SavedRoom plain = Room();
            plain.Name = "Plain";
            Check(SavedRoomStore.Save(new List<SavedRoom>(new SavedRoom[] { placed, sized, plain })), "rooms with places and channel layouts save");
            List<SavedRoom> back = SavedRoomStore.Load();
            Check(back.Count == 3, "and load");
            if (back.Count == 3)
            {
                SavedRoom p = back[0], s = back[1], n = back[2];
                Check(p.Circle == "c1a2b3" && p.ChannelLayout == "1.5708,0.8,1.2;;0.5,1.7,0.6", "circle and channel layout survive");
                Check(p.Placed && p.PlaceX == -123.4 && p.PlaceY == 56.7 && p.Size == 1.4, "a placed, resized room keeps its place and size");
                Check(!s.Placed && s.Size == 0.8 && s.Circle == "fav", "a resized room that isn't placed keeps just its size");
                Check(!n.Placed && n.Size == 1 && n.Circle == String.Empty && n.ChannelLayout == String.Empty, "an untouched room stays untouched");
                Check(IsIntact(new List<SavedRoom>(new SavedRoom[] { p })), "and every older field still survives");
            }

            // Files from before these fields existed.
            string older = "1|41234|" + B64(RoomName) + "|" + B64(Code()) + "|1|" + B64(ChannelName) + "|" +
                SecretStore.Protect(VoipEngineTestKeys.HostPrivateKey) + "|" + SecretStore.Protect(Encoding.UTF8.GetBytes(Password));
            EncryptedFile.Write(Encrypted, Encoding.UTF8.GetBytes(older + "\n" + older + "||\n" + older + "|||1\n" + older + "|||c9|0.1,1,1"));
            back = SavedRoomStore.Load();
            bool defaults = back.Count == 4;
            foreach (SavedRoom r in back)
                defaults &= !r.Placed && r.Size == 1 && r.Name == RoomName && Same(r.HostKey, VoipEngineTestKeys.HostPrivateKey);
            Check(defaults, "rooms saved by older versions load in their usual place and size");
            Check(back.Count == 4 && back[2].Circle == "fav" && back[3].Circle == "c9" && back[3].ChannelLayout == "0.1,1,1", "the old favourite flag and earlier circles still load");

            // Damaged or hand-edited places: never a place that isn't a number, a size outside the limits, or a crash.
            string[] bad = { "abc", "1,2", "1,2,3,4", ",,", "x,y,z", "NaN,NaN,NaN", "Infinity,5,1", "5,-Infinity,1", "1e400,5,1", "1e300,5,1", "5,5,99", "5,5,-3", "5,5,NaN", "\u0000,\u0000,\u0000" };
            bool tolerated = true;
            foreach (string text in bad)
            {
                bool isPlaced; double x, y, size;
                SpatialPlace.Parse(text, out isPlaced, out x, out y, out size);
                bool ok = Finite(x) && Finite(y) && size >= 0.6 && size <= 1.8 && (isPlaced || (x == 0 && y == 0));
                if (!ok) Console.WriteLine("     " + text + " -> " + isPlaced + " " + x + "," + y + " " + size);
                tolerated &= ok;
            }
            Check(tolerated, "damaged places load as no place, and sizes stay within the limits");
            {
                bool isPlaced; double x, y, size;
                SpatialPlace.Parse("NaN,NaN,1.2", out isPlaced, out x, out y, out size);
                Check(!isPlaced && size == 1.2, "a damaged place keeps a good size");
                SpatialPlace.Parse(SpatialPlace.Format(true, 1234.56, -0.04, 1.234), out isPlaced, out x, out y, out size);
                Check(isPlaced && x == 1234.6 && y == 0 && size == 1.23, "places are stored to a tenth, sizes to a hundredth");
                Check(SpatialPlace.Format(false, 0, 0, 1) == String.Empty, "nothing is stored for an untouched room");
            }
            string damagedLine = older + "|||c9|garbage;;;|NaN,Infinity,77";
            EncryptedFile.Write(Encrypted, Encoding.UTF8.GetBytes(damagedLine));
            back = SavedRoomStore.Load();
            Check(back.Count == 1 && !back[0].Placed && back[0].Size == 1.8 && IsIntact(back), "a room with a damaged place still loads, with everything else intact");

            // Circles saved in settings, damaged the same way.
            List<SpatialCircle> circles = (List<SpatialCircle>)CallMainForm("ParseCircles", "c1|Mine|1|NaN|5;c2|Two|1|Infinity|-Infinity;c3|Three|1|12.5|-40;bad", "Rooms");
            SpatialCircle c1 = circles.Find(delegate(SpatialCircle c) { return c.Id == "c1"; });
            SpatialCircle c2 = circles.Find(delegate(SpatialCircle c) { return c.Id == "c2"; });
            SpatialCircle c3 = circles.Find(delegate(SpatialCircle c) { return c.Id == "c3"; });
            Check(c1 != null && !c1.HasPosition && Finite(c1.X) && c2 != null && !c2.HasPosition && Finite(c2.Y), "circles with damaged places are placed afresh");
            Check(c3 != null && c3.HasPosition && c3.X == 12.5 && c3.Y == -40, "good circles keep their place");
            Check(circles.Exists(delegate(SpatialCircle c) { return c.Id == SpatialCircle.FavouritesId; }) && circles.Exists(delegate(SpatialCircle c) { return c.Id == SpatialCircle.FriendsId; }),
                "the built-in circles are always there");

            // Friend places live in settings.
            AppPreferences prefs = new AppPreferences();
            prefs.FriendPlaces = "abc123:12.5,-3,1.4;def456:,,0.8";
            PreferencesStore.Save(prefs);
            Check(PreferencesStore.Load().FriendPlaces == "abc123:12.5,-3,1.4;def456:,,0.8", "friend places survive saving and loading settings");
            File.WriteAllLines(PreferencesStore.FileForTests, new string[] { "friendplaces=  x:NaN,1,9;;:;y  ", "friendplaces" });
            Check(PreferencesStore.Load().FriendPlaces == "x:NaN,1,9;;:;y", "damaged friend places load without trouble (each place is checked when used)");

            // A hosted room's channel layout: saved, then put back when it starts again.
            VoipEngine host = new VoipEngine();
            host.HeadlessForTests = true;
            host.StartHost(0, "Host", "Test Room", new List<string>(new string[] { "General", "Games", "Music" }), VoipEngineTestKeys.HostPrivateKey, null);
            VoipEngine again = new VoipEngine();
            again.HeadlessForTests = true;
            again.StartHost(0, "Host", "Test Room", new List<string>(new string[] { "General", "Games", "Music" }), VoipEngineTestKeys.HostPrivateKey, null);
            try
            {
                VoiceChannelInfo[] channels = host.GetChannels();
                host.SetChannelLayout(channels[0].Id, 1.0, 1.2, 1.5);
                host.SetChannelLayout(channels[2].Id, 4.0, 0.6, 0.7);
                string saved = (string)CallMainForm("FormatChannelLayout", host, channels);
                CallMainForm("RestoreChannelLayout", again, saved);
                VoiceChannelInfo[] restored = again.GetChannels();
                bool same = channels.Length == 3 && restored.Length == 3;
                for (int i = 0; same && i < 3; i++)
                {
                    ChannelLayout a = host.GetChannelLayout(channels[i].Id), b = again.GetChannelLayout(restored[i].Id);
                    same = (a == null) == (b == null) && (a == null || (Math.Abs(a.Angle - b.Angle) < 0.001 && a.Reach == b.Reach && a.Size == b.Size));
                }
                Check(same && again.GetChannelLayout(restored[1].Id) == null, "a channel layout saved with the room comes back the same, moved channels only (" + saved + ")");

                CallMainForm("RestoreChannelLayout", again, "NaN,NaN,NaN;Infinity,-Infinity,1e400;junk");
                bool finite = true;
                foreach (VoiceChannelInfo c in again.GetChannels())
                {
                    ChannelLayout l = again.GetChannelLayout(c.Id);
                    if (l != null)
                        finite &= Finite(l.Angle) && l.Reach >= VoipEngine.MinChannelReach && l.Reach <= VoipEngine.MaxChannelReach && l.Size >= VoipEngine.MinChannelSize && l.Size <= VoipEngine.MaxChannelSize;
                }
                Check(finite, "a damaged channel layout is clamped or skipped");
                CallMainForm("RestoreChannelLayout", again, "1,1,1;1,1,1;1,1,1;1,1,1;1,1,1");
                CallMainForm("RestoreChannelLayout", again, null);
                Check(again.IsRunning, "more entries than channels, or none, are fine");
            }
            finally
            {
                host.Dispose();
                again.Dispose();
            }
        }

        static int Main()
        {
            _folder = Path.Combine(Path.GetTempPath(), "MaelaStorageTests-" + Guid.NewGuid().ToString("N"));
            SavedRoomStore.FolderForTests = _folder;
            PreferencesStore.FileForTests = Path.Combine(_folder, "settings.txt");
            try
            {
                NothingReadable();
                ConvertsOldFile();
                KeepsUnreadableFile();
                LockedFileIsNotOverwritten();
                SpatialLayout();
            }
            finally
            {
                try { Directory.Delete(_folder, true); }
                catch { }
            }
            Console.WriteLine(_failures == 0 ? "All storage tests passed." : _failures + " storage test(s) FAILED.");
            return _failures == 0 ? 0 : 1;
        }
    }
}
