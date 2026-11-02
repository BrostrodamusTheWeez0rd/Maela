using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Maela
{
    /// <summary>
    /// A room's picture and banner, as compressed plain pixels: the same safety as profile
    /// pictures. The host's own PC makes them from an image it chose. Everyone else only
    /// decompresses, to exactly the expected size, and copies pixels.
    /// </summary>
    internal static class RoomArt
    {
        public const int Icon = 0, Banner = 1;
        public const int IconSize = VoipEngine.AvatarSize;
        public const int BannerWidth = 480, BannerHeight = 120;
        public const int IconRawBytes = IconSize * IconSize * 2;
        public const int BannerRawBytes = BannerWidth * BannerHeight * 2;
        public const int MaxIconBlob = 16 * 1024;
        public const int MaxBannerBlob = 128 * 1024;

        public static int RawBytes(int kind) { return kind == Icon ? IconRawBytes : BannerRawBytes; }
        public static int MaxBlob(int kind) { return kind == Icon ? MaxIconBlob : MaxBannerBlob; }

        /// <summary>Compresses pixels made on this PC. Null if they're the wrong size or too large.</summary>
        public static byte[] Pack(int kind, byte[] pixels)
        {
            if (pixels == null || pixels.Length != RawBytes(kind))
                return null;
            MemoryStream stream = new MemoryStream();
            using (DeflateStream deflate = new DeflateStream(stream, CompressionMode.Compress, true))
                deflate.Write(pixels, 0, pixels.Length);
            byte[] blob = stream.ToArray();
            return blob.Length <= MaxBlob(kind) ? blob : null;
        }

        /// <summary>Unpacks a blob from anyone. Null unless it decompresses to exactly the right size.</summary>
        public static byte[] Unpack(int kind, byte[] blob)
        {
            if (blob == null || blob.Length < 2 || blob.Length > MaxBlob(kind))
                return null;
            byte[] pixels = new byte[RawBytes(kind)];
            try
            {
                using (MemoryStream input = new MemoryStream(blob, false))
                using (DeflateStream inflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    int read = 0;
                    while (read < pixels.Length)
                    {
                        int n = inflate.Read(pixels, read, pixels.Length - read);
                        if (n <= 0)
                            return null;
                        read += n;
                    }
                    if (inflate.Read(new byte[1], 0, 1) != 0)
                        return null; // Never decompress past the expected size.
                }
            }
            catch
            {
                return null;
            }
            return pixels;
        }

        /// <summary>Identifies a blob, so a saved copy isn't downloaded again. Never 0, which means none.</summary>
        public static uint VersionOf(byte[] blob)
        {
            if (blob == null)
                return 0;
            return BitConverter.ToUInt32(CryptoUtil.Sha256(blob), 0) | 1u;
        }

        public static Bitmap ToImage(int kind, byte[] blob)
        {
            byte[] pixels = Unpack(kind, blob);
            if (pixels == null)
                return null;
            return kind == Icon
                ? ProfilePicture.ToBitmap(pixels, IconSize, IconSize)
                : ProfilePicture.ToBitmap(pixels, BannerWidth, BannerHeight);
        }

        private static readonly Dictionary<string, Bitmap> StockCache = new Dictionary<string, Bitmap>();

        /// <summary>The banner a room has until its host picks one. Generated from its name, so each differs.</summary>
        public static Bitmap StockBanner(string roomName)
        {
            string name = roomName ?? String.Empty;
            lock (StockCache)
            {
                Bitmap cached;
                if (StockCache.TryGetValue(name, out cached))
                    return cached;
                if (StockCache.Count > 64)
                    StockCache.Clear(); // Anything still on screen is asked for again next paint.
                Bitmap made = DrawStock(name);
                StockCache[name] = made;
                return made;
            }
        }

        private static Bitmap DrawStock(string name)
        {
            int seed = 17;
            foreach (char c in name)
                seed = unchecked(seed * 31 + c);
            Random random = new Random(seed);
            Color main = Ui.ColorForName(name);
            Color deep = Ui.Mix(main, Color.Black, 0.55f);
            Color bright = Ui.Mix(main, Color.White, 0.25f);
            Bitmap bitmap = new Bitmap(BannerWidth, BannerHeight, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle all = new Rectangle(0, 0, BannerWidth, BannerHeight);
                using (LinearGradientBrush fill = new LinearGradientBrush(all, deep, main, 15f + random.Next(30)))
                    g.FillRectangle(fill, all);
                // Layered waves across the banner.
                for (int layer = 0; layer < 4; layer++)
                {
                    float baseY = 30 + layer * 24 + random.Next(10);
                    float rise = 12 + random.Next(26);
                    float phase = random.Next(BannerWidth);
                    using (GraphicsPath wave = new GraphicsPath())
                    {
                        List<PointF> points = new List<PointF>();
                        for (int x = -40; x <= BannerWidth + 40; x += 40)
                            points.Add(new PointF(x, baseY + rise * (float)Math.Sin((x + phase) / (70.0 + layer * 35.0))));
                        wave.AddCurve(points.ToArray());
                        wave.AddLine(BannerWidth + 40, baseY, BannerWidth + 40, BannerHeight + 10);
                        wave.AddLine(BannerWidth + 40, BannerHeight + 10, -40, BannerHeight + 10);
                        wave.CloseFigure();
                        using (SolidBrush brush = new SolidBrush(Color.FromArgb(34 + layer * 8, layer % 2 == 0 ? bright : deep)))
                            g.FillPath(brush, wave);
                    }
                }
                // A few soft glows.
                for (int i = 0; i < 3; i++)
                {
                    int size = 60 + random.Next(90);
                    using (SolidBrush glow = new SolidBrush(Color.FromArgb(22, Color.White)))
                        g.FillEllipse(glow, random.Next(BannerWidth) - size / 2, random.Next(BannerHeight) - size / 2, size, size);
                }
            }
            return bitmap;
        }
    }

    /// <summary>
    /// Room name and art, set by the host only. Members have no way to send art, so there's
    /// nothing for a member to spoof. The peer list advertises a version (a hash) for each piece
    /// and members fetch what they don't already have, checking the finished blob against it.
    ///
    /// RoomArtGet  client -> host : kind, version, first chunk, how many (at most 32)
    /// RoomArtData host -> client : kind, version, chunk index, chunk count, total size, data
    /// </summary>
    internal sealed partial class VoipEngine
    {
        private const byte PacketRoomArtGet = 27;
        private const byte PacketRoomArtData = 28;
        private const byte PeerListSectionRoomArt = 5;
        private const int ArtChunkBytes = 1000;
        private const int ArtRequestChunks = 32;
        private const int ArtMaxRequestsPerSecond = 20;

        private readonly byte[][] _artBlobs = new byte[2][];
        private readonly uint[] _artVersions = new uint[2];
        private readonly uint[] _artAdvertised = new uint[2];
        private readonly ArtDownload[] _artDownloads = new ArtDownload[2];
        private readonly Dictionary<uint, ArtRate> _artRates = new Dictionary<uint, ArtRate>();
        private DateTime _artRequestedUtc = DateTime.MinValue;
        private int _roomArtVersion;

        private sealed class ArtDownload
        {
            public uint Version;
            public int Total;
            public byte[][] Chunks;
            public DateTime StartedUtc = DateTime.UtcNow;
        }

        private sealed class ArtRate
        {
            public DateTime WindowUtc;
            public int Count;
        }

        /// <summary>Goes up whenever the art changes. Cheap to poll from the UI.</summary>
        public int RoomArtVersion { get { lock (_stateLock) return _roomArtVersion; } }

        public byte[] GetRoomArt(int kind)
        {
            lock (_stateLock)
                return _artBlobs[kind];
        }

        /// <summary>
        /// Host: this room's art. Member: a saved copy, so it isn't fetched again if unchanged.
        /// Anything that doesn't unpack to the right size is treated as none.
        /// </summary>
        public void SetRoomArt(byte[] iconBlob, byte[] bannerBlob)
        {
            lock (_stateLock)
            {
                StoreArtLocked(RoomArt.Icon, iconBlob);
                StoreArtLocked(RoomArt.Banner, bannerBlob);
                _roomArtVersion++;
            }
            if (_isHost && _running)
                BroadcastPeerList();
        }

        /// <summary>Host: renames the room for everyone in it.</summary>
        public void SetRoomName(string name)
        {
            string clean = CleanRoomName(name);
            if (clean.Length == 0 || !_isHost)
                return;
            _roomName = clean;
            if (_running)
                BroadcastPeerList();
        }

        private void StoreArtLocked(int kind, byte[] blob)
        {
            bool valid = RoomArt.Unpack(kind, blob) != null;
            _artBlobs[kind] = valid ? blob : null;
            _artVersions[kind] = valid ? RoomArt.VersionOf(blob) : 0;
        }

        /// <summary>Host: _stateLock held.</summary>
        private void WriteRoomArtSection(BinaryWriter writer)
        {
            writer.Write(PeerListSectionRoomArt);
            writer.Write(_artVersions[RoomArt.Icon]);
            writer.Write(_artVersions[RoomArt.Banner]);
        }

        /// <summary>Client: returns the new offset, or -1 if malformed.</summary>
        private int ReadRoomArtSection(byte[] payload, int offset)
        {
            if (offset + 8 > payload.Length)
                return -1;
            uint icon = BitConverter.ToUInt32(payload, offset);
            uint banner = BitConverter.ToUInt32(payload, offset + 4);
            lock (_stateLock)
            {
                ApplyAdvertisedArtLocked(RoomArt.Icon, icon);
                ApplyAdvertisedArtLocked(RoomArt.Banner, banner);
            }
            return offset + 8;
        }

        private void ApplyAdvertisedArtLocked(int kind, uint version)
        {
            _artAdvertised[kind] = version;
            if (version == 0 && _artBlobs[kind] != null)
            {
                // The host removed it.
                _artBlobs[kind] = null;
                _artVersions[kind] = 0;
                _artDownloads[kind] = null;
                _roomArtVersion++;
            }
        }

        private bool HandleHostRoomArtPacket(Participant known, Packet packet)
        {
            if (packet.Type != PacketRoomArtGet)
                return false;
            byte[] p = packet.Payload;
            if (p == null || p.Length != 9)
                return true;
            int kind = p[0];
            if (kind != RoomArt.Icon && kind != RoomArt.Banner)
                return true;
            uint version = BitConverter.ToUInt32(p, 1);
            int first = BitConverter.ToUInt16(p, 5);
            int wanted = BitConverter.ToUInt16(p, 7);
            byte[] blob;
            lock (_stateLock)
            {
                DateTime now = DateTime.UtcNow;
                ArtRate rate;
                if (!_artRates.TryGetValue(known.Id, out rate))
                {
                    rate = new ArtRate();
                    _artRates[known.Id] = rate;
                }
                if ((now - rate.WindowUtc).TotalSeconds >= 1.0)
                {
                    rate.WindowUtc = now;
                    rate.Count = 0;
                }
                if (++rate.Count > ArtMaxRequestsPerSecond)
                    return true;
                if (_artVersions[kind] != version || version == 0)
                    return true;
                blob = _artBlobs[kind];
            }
            int count = Math.Max(1, (blob.Length + ArtChunkBytes - 1) / ArtChunkBytes);
            int end = Math.Min(count, first + Math.Min(wanted, ArtRequestChunks));
            for (int i = first; i < end; i++)
            {
                int offset = i * ArtChunkBytes;
                int length = Math.Min(ArtChunkBytes, blob.Length - offset);
                byte[] data = new byte[13 + length];
                data[0] = (byte)kind;
                Buffer.BlockCopy(BitConverter.GetBytes(version), 0, data, 1, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)i), 0, data, 5, 2);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)count), 0, data, 7, 2);
                Buffer.BlockCopy(BitConverter.GetBytes(blob.Length), 0, data, 9, 4);
                Buffer.BlockCopy(blob, offset, data, 13, length);
                SendPacket(known.Endpoint, PacketRoomArtData, _localId, 0, data);
            }
            return true;
        }

        private bool HandleClientRoomArtPacket(Packet packet)
        {
            if (packet.Type != PacketRoomArtData)
                return false;
            byte[] p = packet.Payload;
            if (p == null || p.Length < 14)
                return true;
            int kind = p[0];
            if (kind != RoomArt.Icon && kind != RoomArt.Banner)
                return true;
            uint version = BitConverter.ToUInt32(p, 1);
            int index = BitConverter.ToUInt16(p, 5);
            int count = BitConverter.ToUInt16(p, 7);
            int total = BitConverter.ToInt32(p, 9);
            int length = p.Length - 13;
            lock (_stateLock)
            {
                if (version == 0 || _artAdvertised[kind] != version || _artVersions[kind] == version)
                    return true; // Only what the host told us about, and that we still need.
                if (total < 2 || total > RoomArt.MaxBlob(kind) || count != (total + ArtChunkBytes - 1) / ArtChunkBytes || index >= count)
                    return true;
                if (length != Math.Min(ArtChunkBytes, total - index * ArtChunkBytes))
                    return true;

                ArtDownload download = _artDownloads[kind];
                if (download == null || download.Version != version || download.Total != total)
                {
                    download = new ArtDownload();
                    download.Version = version;
                    download.Total = total;
                    download.Chunks = new byte[count][];
                    _artDownloads[kind] = download;
                }
                if (download.Chunks[index] != null)
                    return true;
                byte[] chunk = new byte[length];
                Buffer.BlockCopy(p, 13, chunk, 0, length);
                download.Chunks[index] = chunk;
                foreach (byte[] have in download.Chunks)
                {
                    if (have == null)
                        return true;
                }

                _artDownloads[kind] = null;
                byte[] blob = new byte[total];
                int offset = 0;
                foreach (byte[] have in download.Chunks)
                {
                    Buffer.BlockCopy(have, 0, blob, offset, have.Length);
                    offset += have.Length;
                }
                if (RoomArt.VersionOf(blob) != version || RoomArt.Unpack(kind, blob) == null)
                    return true; // Not what was advertised, or not valid pixels. It'll be asked for again.
                _artBlobs[kind] = blob;
                _artVersions[kind] = version;
                _roomArtVersion++;
            }
            return true;
        }

        /// <summary>Client: asks for whichever pieces of art are still missing.</summary>
        private void ClientRoomArtMaintenance(DateTime now)
        {
            if ((now - _artRequestedUtc).TotalMilliseconds < 200.0 || _hostEndpoint == null)
                return;
            List<byte[]> requests = new List<byte[]>();
            lock (_stateLock)
            {
                for (int kind = 0; kind < 2; kind++)
                {
                    uint version = _artAdvertised[kind];
                    if (version == 0 || _artVersions[kind] == version)
                        continue;
                    ArtDownload download = _artDownloads[kind];
                    if (download != null && (download.Version != version || (now - download.StartedUtc).TotalSeconds > 60.0))
                    {
                        download = null;
                        _artDownloads[kind] = null; // Start over.
                    }
                    int first = 0, wanted = ArtRequestChunks;
                    if (download != null)
                    {
                        while (first < download.Chunks.Length && download.Chunks[first] != null)
                            first++;
                        int last = first;
                        while (last < download.Chunks.Length && last - first < ArtRequestChunks && download.Chunks[last] == null)
                            last++;
                        wanted = last - first;
                    }
                    byte[] request = new byte[9];
                    request[0] = (byte)kind;
                    Buffer.BlockCopy(BitConverter.GetBytes(version), 0, request, 1, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes((ushort)first), 0, request, 5, 2);
                    Buffer.BlockCopy(BitConverter.GetBytes((ushort)wanted), 0, request, 7, 2);
                    requests.Add(request);
                }
            }
            if (requests.Count == 0)
                return;
            _artRequestedUtc = now;
            foreach (byte[] request in requests)
                SendPacket(_hostEndpoint, PacketRoomArtGet, _localId, 0, request);
        }
    }
}
