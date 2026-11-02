using System;
using System.Collections.Generic;
using System.IO;

namespace Maela
{
    /// <summary>
    /// Pictures in friend chats (see ChatPicture for what a picture is and why it's safe).
    ///
    /// Sending one is an ordinary message whose text carries the picture's marker, so it's
    /// delivered, numbered and kept like any other. The friend then pulls the picture itself from
    /// you, a window of chunks at a time; you only ever answer for pictures you sent that friend.
    /// Both sides keep pictures encrypted on disk beside the chat, so they're there next time.
    ///
    ///   InnerPictureGet  : send me chunks first..first+count of picture P
    ///   InnerPictureData : one chunk of P
    /// Older versions ignore both, and show the message's plain words.
    /// </summary>
    internal sealed partial class FriendService
    {
        private const byte InnerPictureGet = 8;
        private const byte InnerPictureData = 9;
        private const int PictureChunk = 1000;
        private const int PictureWindowChunks = 48;
        private const double PictureAskAgainMs = 300.0;
        private const double PictureStallSeconds = 40.0;

        private sealed class PictureDownload
        {
            public string Id, From;
            public int Total, Count, Have, AskedEnd, Gap;
            public byte[][] Chunks;
            public DateTime AskedUtc = DateTime.MinValue, ProgressUtc = DateTime.UtcNow;

            public int FirstMissing()
            {
                while (Gap < Count && Chunks[Gap] != null)
                    Gap++;
                return Gap;
            }
        }

        // Under _lock.
        private readonly Dictionary<string, PictureDownload> _pictureDownloads = new Dictionary<string, PictureDownload>();
        private readonly Dictionary<string, DateTime> _pictureFailed = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, byte[]> _pictureCache = new Dictionary<string, byte[]>();
        private readonly List<string> _pictureCacheOrder = new List<string>();

        private string PicturePath(string id)
        {
            return Path.Combine(Path.Combine(_folder, "pictures"), id + ".bin");
        }

        /// <summary>Sends a picture (made by ChatPicture.FromFile on this PC) to a friend. False if they aren't one.</summary>
        public bool SendPicture(string friendId, byte[] blob)
        {
            int width, height;
            if (!ChatPicture.TryReadSize(blob, out width, out height))
                return false;
            string id = ChatPicture.IdOf(blob);
            lock (_lock)
            {
                if (!SavePictureLocked(id, blob))
                    return false;
            }
            return SendMessage(friendId, ChatPicture.Marker(id, width, height, blob.Length));
        }

        /// <summary>A picture from a friend chat, once it's here and checked, or null while it's on its way (or gone).</summary>
        public byte[] GetPicture(string id)
        {
            if (!ChatPicture.IsId(id))
                return null;
            lock (_lock)
                return LoadPictureLocked(id);
        }

        /// <summary>True while a picture is still arriving.</summary>
        public bool PictureComing(string id)
        {
            lock (_lock)
                return _pictureDownloads.ContainsKey(id);
        }

        private bool SavePictureLocked(string id, byte[] blob)
        {
            try
            {
                string path = PicturePath(id);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (!File.Exists(path))
                    EncryptedFile.Write(path, blob);
                RememberLocked(id, blob);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private byte[] LoadPictureLocked(string id)
        {
            byte[] blob;
            if (_pictureCache.TryGetValue(id, out blob))
                return blob;
            string path = PicturePath(id);
            if (!File.Exists(path))
                return null;
            try { blob = EncryptedFile.Read(path); }
            catch { blob = null; }
            // Checked again as it's read back, as everything from someone else is.
            if (blob == null || ChatPicture.IdOf(blob) != id)
                return null;
            RememberLocked(id, blob);
            return blob;
        }

        /// <summary>A few recent pictures stay in memory, so scrolling doesn't read the disk each time.</summary>
        private void RememberLocked(string id, byte[] blob)
        {
            if (_pictureCache.ContainsKey(id))
                return;
            _pictureCache[id] = blob;
            _pictureCacheOrder.Add(id);
            while (_pictureCacheOrder.Count > 24)
            {
                _pictureCache.Remove(_pictureCacheOrder[0]);
                _pictureCacheOrder.RemoveAt(0);
            }
        }

        /// <summary>A message from a friend that carries a picture: start fetching it. _lock held.</summary>
        private void PictureAnnounced(Person person, string text)
        {
            string id;
            int width, height, bytes;
            if (!ChatPicture.TryParseMarker(text, out id, out width, out height, out bytes))
                return;
            if (_pictureDownloads.ContainsKey(id) || File.Exists(PicturePath(id)))
                return;
            PictureDownload download = new PictureDownload();
            download.Id = id;
            download.From = person.Id;
            download.Total = bytes;
            download.Count = (bytes + PictureChunk - 1) / PictureChunk;
            download.Chunks = new byte[download.Count][];
            _pictureDownloads[id] = download;
        }

        /// <summary>Tests: start fetching a picture from a friend as if they'd sent it, to check they refuse.</summary>
        internal void ExpectPictureForTests(string friendId, string marker)
        {
            lock (_lock)
            {
                Person person;
                if (_people.TryGetValue(friendId, out person))
                    PictureAnnounced(person, marker);
            }
        }

        /// <summary>Pictures in the history that never finished arriving: try again. _lock held.</summary>
        private void ResumePicturesLocked(Person person)
        {
            foreach (FriendChatEntry entry in ChatOf(person).Entries)
            {
                if (entry.Mine || entry.ReactionTo != 0)
                    continue;
                string id;
                int width, height, bytes;
                if (!ChatPicture.TryParseMarker(entry.Text, out id, out width, out height, out bytes))
                    continue;
                DateTime failed;
                if (_pictureFailed.TryGetValue(id, out failed) && (DateTime.UtcNow - failed).TotalSeconds < 60)
                    continue;
                PictureAnnounced(person, entry.Text);
            }
        }

        private void AskForPicture(Person person, PictureDownload download)
        {
            int first = download.FirstMissing();
            if (first >= download.Count || person.Current == null)
                return;
            int count = Math.Min(PictureWindowChunks, download.Count - first);
            byte[] inner = new byte[21];
            inner[0] = InnerPictureGet;
            Buffer.BlockCopy(IdBytes(download.Id), 0, inner, 1, 16);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)first), 0, inner, 17, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)count), 0, inner, 19, 2);
            download.AskedUtc = DateTime.UtcNow;
            download.AskedEnd = first + count;
            SendInner(person.Current, inner);
        }

        /// <summary>A friend asking for chunks of a picture: only ones you sent them, and never too much at once.</summary>
        private void HandlePictureGet(Person person, byte[] inner)
        {
            if (person == null || person.State != FriendState.Friend || person.Current == null || inner.Length != 21)
                return;
            string id = IdText(inner, 1);
            bool sentToThem = ChatOf(person).Entries.Exists(delegate(FriendChatEntry e) { return e.Mine && e.ReactionTo == 0 && e.Text != null && e.Text.Contains("[mp1:" + id + ":"); });
            if (!sentToThem)
                return;
            byte[] blob = LoadPictureLocked(id);
            if (blob == null)
                return;
            int first = BitConverter.ToUInt16(inner, 17), count = Math.Min(PictureWindowChunks, (int)BitConverter.ToUInt16(inner, 19));
            int chunks = (blob.Length + PictureChunk - 1) / PictureChunk;
            for (int i = first; i < first + count && i < chunks; i++)
            {
                int length = Math.Min(PictureChunk, blob.Length - i * PictureChunk);
                byte[] data = new byte[23 + length];
                data[0] = InnerPictureData;
                Buffer.BlockCopy(inner, 1, data, 1, 16);
                Buffer.BlockCopy(BitConverter.GetBytes(blob.Length), 0, data, 17, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)i), 0, data, 21, 2);
                Buffer.BlockCopy(blob, i * PictureChunk, data, 23, length);
                SendInner(person.Current, data);
            }
        }

        /// <summary>One chunk of a picture we asked this friend for.</summary>
        private void HandlePictureData(Person person, byte[] inner)
        {
            if (person == null || person.State != FriendState.Friend || inner.Length < 24)
                return;
            string id = IdText(inner, 1);
            PictureDownload download;
            if (!_pictureDownloads.TryGetValue(id, out download) || download.From != person.Id || BitConverter.ToInt32(inner, 17) != download.Total)
                return;
            int index = BitConverter.ToUInt16(inner, 21), length = inner.Length - 23;
            if (index >= download.Count || length != Math.Min(PictureChunk, download.Total - index * PictureChunk))
                return;
            if (download.Chunks[index] == null)
            {
                byte[] chunk = new byte[length];
                Buffer.BlockCopy(inner, 23, chunk, 0, length);
                download.Chunks[index] = chunk;
                download.Have++;
                download.ProgressUtc = DateTime.UtcNow;
            }
            if (download.Have < download.Count)
            {
                if (download.FirstMissing() >= download.AskedEnd)
                    AskForPicture(person, download);
                return;
            }
            _pictureDownloads.Remove(id);
            byte[] blob = new byte[download.Total];
            for (int i = 0; i < download.Count; i++)
                Buffer.BlockCopy(download.Chunks[i], 0, blob, i * PictureChunk, download.Chunks[i].Length);
            int width, height;
            byte[] rgb;
            if (ChatPicture.IdOf(blob) == id && ChatPicture.TryDecode(blob, out width, out height, out rgb))
                SavePictureLocked(id, blob);
            else
                _pictureFailed[id] = DateTime.UtcNow; // Not what it claimed to be: dropped.
            Bump();
        }

        /// <summary>Every tick: keep pictures coming from friends who are online, and give up on stalled ones.</summary>
        private void TickPictures(DateTime now)
        {
            List<string> stalled = new List<string>();
            foreach (PictureDownload download in _pictureDownloads.Values)
            {
                Person person;
                if (!_people.TryGetValue(download.From, out person) || person.State != FriendState.Friend)
                {
                    stalled.Add(download.Id);
                    continue;
                }
                if (person.Current == null)
                {
                    download.ProgressUtc = now; // Offline: waits for them, it isn't stuck.
                    continue;
                }
                if ((now - download.ProgressUtc).TotalSeconds > PictureStallSeconds)
                    stalled.Add(download.Id);
                else if ((now - download.AskedUtc).TotalMilliseconds > PictureAskAgainMs)
                    AskForPicture(person, download);
            }
            foreach (string id in stalled)
            {
                _pictureDownloads.Remove(id);
                _pictureFailed[id] = now;
                Bump();
            }
        }

        private static byte[] IdBytes(string id)
        {
            byte[] bytes = new byte[16];
            for (int i = 0; i < 16; i++)
                bytes[i] = Convert.ToByte(id.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string IdText(byte[] data, int offset)
        {
            return BitConverter.ToString(data, offset, 16).Replace("-", String.Empty).ToLowerInvariant();
        }
    }
}
