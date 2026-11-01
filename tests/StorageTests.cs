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

        static int Main()
        {
            _folder = Path.Combine(Path.GetTempPath(), "MaelaStorageTests-" + Guid.NewGuid().ToString("N"));
            SavedRoomStore.FolderForTests = _folder;
            try
            {
                NothingReadable();
                ConvertsOldFile();
                KeepsUnreadableFile();
                LockedFileIsNotOverwritten();
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
