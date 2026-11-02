using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Maela
{
    /// <summary>
    /// Pictures in chat, made safe the same way profile pictures are, but kept sharp.
    ///
    /// FromFile runs only on the sender's own PC, on a picture they chose: Windows decodes it,
    /// it's shrunk to at most MaxSide on its longest side, and only its pixels are kept (24-bit
    /// colour, so no banding). Nothing else in the file, its name, location, camera details or
    /// anything hidden in it, ever leaves the sender's PC.
    ///
    /// Everyone else only ever runs TryDecode: a few bounds-checked loops over our own simple
    /// format (built-in Deflate, output capped to exactly the expected size, then PNG's row
    /// filters undone). No image decoder ever touches a picture from someone else.
    ///
    /// Blob: "MP" | u8 version | u16 width | u16 height | deflate(rows of [u8 filter][width * RGB]).
    /// </summary>
    internal static class ChatPicture
    {
        public const int MaxSide = 1920;
        public const int MaxBytes = 6 * 1024 * 1024;   // The most a picture may take on the wire.
        public const long MaxFileBytes = 40L * 1024 * 1024;
        private const byte Version = 1;
        private const int HeaderBytes = 7;

        /// <summary>A picture you chose, rebuilt from its pixels. Throws InvalidOperationException with a message to show.</summary>
        public static byte[] FromFile(string path)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
                throw new InvalidOperationException("Pick a picture smaller than 40 MB.");
            using (MemoryStream stream = new MemoryStream(File.ReadAllBytes(path)))
            using (Image image = Image.FromStream(stream, false, true))
                return FromImage(image);
        }

        public static byte[] FromImage(Image image)
        {
            if (image.Width < 1 || image.Height < 1 || image.Width > 30000 || image.Height > 30000)
                throw new InvalidOperationException("That picture's size isn't supported.");
            double scale = Math.Min(1.0, (double)MaxSide / Math.Max(image.Width, image.Height));
            // Big, busy photos may need to come down a little more to fit the limit.
            for (int attempt = 0; attempt < 6; attempt++, scale *= 0.85)
            {
                int width = Math.Max(1, (int)Math.Round(image.Width * scale)), height = Math.Max(1, (int)Math.Round(image.Height * scale));
                byte[] blob = Encode(Render(image, width, height), width, height);
                if (blob.Length <= MaxBytes)
                    return blob;
            }
            throw new InvalidOperationException("That picture is too detailed to send.");
        }

        /// <summary>The picture drawn fresh at the given size, as RGB rows. Transparent parts get chat's dark backdrop.</summary>
        private static byte[] Render(Image image, int width, int height)
        {
            byte[] rgb = new byte[width * height * 3];
            using (Bitmap canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.FromArgb(40, 40, 46));
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(image, new Rectangle(0, 0, width, height));
                }
                BitmapData data = canvas.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] row = new byte[width * 3];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                        for (int x = 0; x < width; x++)
                        {
                            // GDI+ rows are BGR.
                            int i = (y * width + x) * 3;
                            rgb[i] = row[x * 3 + 2];
                            rgb[i + 1] = row[x * 3 + 1];
                            rgb[i + 2] = row[x * 3];
                        }
                    }
                }
                finally
                {
                    canvas.UnlockBits(data);
                }
            }
            return rgb;
        }

        /// <summary>Each row with whichever PNG filter suits it best, then compressed.</summary>
        private static byte[] Encode(byte[] rgb, int width, int height)
        {
            int stride = width * 3;
            MemoryStream output = new MemoryStream();
            output.WriteByte((byte)'M');
            output.WriteByte((byte)'P');
            output.WriteByte(Version);
            output.WriteByte((byte)width);
            output.WriteByte((byte)(width >> 8));
            output.WriteByte((byte)height);
            output.WriteByte((byte)(height >> 8));
            byte[][] tries = new byte[5][];
            for (int f = 0; f < 5; f++)
                tries[f] = new byte[stride];
            using (DeflateStream deflate = new DeflateStream(output, CompressionMode.Compress, true))
            {
                for (int y = 0; y < height; y++)
                {
                    int best = 0;
                    long bestScore = long.MaxValue;
                    for (int f = 0; f < 5; f++)
                    {
                        long score = 0;
                        for (int i = 0; i < stride; i++)
                        {
                            byte raw = rgb[y * stride + i];
                            int a = i >= 3 ? rgb[y * stride + i - 3] : 0;
                            int b = y > 0 ? rgb[(y - 1) * stride + i] : 0;
                            int c = i >= 3 && y > 0 ? rgb[(y - 1) * stride + i - 3] : 0;
                            byte v = (byte)(raw - Predict(f, a, b, c));
                            tries[f][i] = v;
                            score += v < 128 ? v : 256 - v;
                        }
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = f;
                        }
                    }
                    deflate.WriteByte((byte)best);
                    deflate.Write(tries[best], 0, stride);
                }
            }
            return output.ToArray();
        }

        private static int Predict(int filter, int a, int b, int c)
        {
            switch (filter)
            {
                case 1: return a;
                case 2: return b;
                case 3: return (a + b) >> 1;
                case 4:
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
                default: return 0;
            }
        }

        /// <summary>Just the size, from the header, without unpacking anything. False if the header is wrong.</summary>
        public static bool TryReadSize(byte[] blob, out int width, out int height)
        {
            width = height = 0;
            if (blob == null || blob.Length <= HeaderBytes || blob.Length > MaxBytes || blob[0] != 'M' || blob[1] != 'P' || blob[2] != Version)
                return false;
            width = blob[3] | (blob[4] << 8);
            height = blob[5] | (blob[6] << 8);
            return width >= 1 && height >= 1 && width <= MaxSide && height <= MaxSide;
        }

        /// <summary>
        /// Checks and unpacks a picture from anyone into RGB rows. Returns false if anything is off:
        /// a wrong header or size, an unknown filter, or data that doesn't decompress to exactly the
        /// expected number of bytes.
        /// </summary>
        public static bool TryDecode(byte[] blob, out int width, out int height, out byte[] rgb)
        {
            rgb = null;
            if (!TryReadSize(blob, out width, out height))
                return false;
            int stride = width * 3;
            byte[] filtered = new byte[(stride + 1) * height];
            try
            {
                using (MemoryStream input = new MemoryStream(blob, HeaderBytes, blob.Length - HeaderBytes, false))
                using (DeflateStream inflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    int read = 0;
                    while (read < filtered.Length)
                    {
                        int n = inflate.Read(filtered, read, filtered.Length - read);
                        if (n <= 0)
                            return false; // Too short.
                        read += n;
                    }
                    if (inflate.Read(new byte[1], 0, 1) != 0)
                        return false; // Too long: never decompress beyond the expected size.
                }
            }
            catch
            {
                return false;
            }
            byte[] pixels = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                int filter = filtered[y * (stride + 1)];
                if (filter > 4)
                    return false;
                int from = y * (stride + 1) + 1, to = y * stride;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= 3 ? pixels[to + i - 3] : 0;
                    int b = y > 0 ? pixels[to - stride + i] : 0;
                    int c = i >= 3 && y > 0 ? pixels[to - stride + i - 3] : 0;
                    pixels[to + i] = (byte)(filtered[from + i] + Predict(filter, a, b, c));
                }
            }
            rgb = pixels;
            return true;
        }

        /// <summary>A picture's name: the first 16 bytes of its SHA-256, so it can be checked on arrival.</summary>
        public static string IdOf(byte[] blob)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(blob);
                return BitConverter.ToString(digest, 0, 16).Replace("-", String.Empty).ToLowerInvariant();
            }
        }

        public static bool IsId(string id)
        {
            return id != null && id.Length == 32 && Regex.IsMatch(id, "^[0-9a-f]{32}$");
        }

        // ----- In chat text -----
        // A picture travels as a message whose text ends with its marker. Older versions just show
        // the words in front of it.

        private const string MarkerLead = "🖼 Sent a picture. Update mæla to see it. ";
        private static readonly Regex MarkerPattern = new Regex(@"\[mp1:([0-9a-f]{32}):([0-9]{1,4})x([0-9]{1,4}):([0-9]{1,8})\]$");

        public static string Marker(string id, int width, int height, int bytes)
        {
            return MarkerLead + "[mp1:" + id + ":" + width + "x" + height + ":" + bytes + "]";
        }

        /// <summary>The picture a message carries, or false for an ordinary message.</summary>
        public static bool TryParseMarker(string text, out string id, out int width, out int height, out int bytes)
        {
            id = null;
            width = height = bytes = 0;
            if (String.IsNullOrEmpty(text))
                return false;
            Match match = MarkerPattern.Match(text);
            if (!match.Success)
                return false;
            id = match.Groups[1].Value;
            width = Int32.Parse(match.Groups[2].Value);
            height = Int32.Parse(match.Groups[3].Value);
            bytes = Int32.Parse(match.Groups[4].Value);
            return width >= 1 && height >= 1 && width <= MaxSide && height <= MaxSide && bytes > HeaderBytes && bytes <= MaxBytes;
        }

        // ----- YouTube links -----

        private static readonly Regex YouTubePattern = new Regex(
            @"(?:https?://)?(?:www\.|m\.|music\.)?(?:youtube\.com/(?:watch\?(?:[^\s#]*&)?v=|shorts/|live/|embed/)|youtu\.be/)([A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])",
            RegexOptions.IgnoreCase);

        /// <summary>The first YouTube video a message links to, as its 11-character id, or null.</summary>
        public static string YouTubeId(string text)
        {
            if (String.IsNullOrEmpty(text))
                return null;
            Match match = YouTubePattern.Match(text);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>The video's page, rebuilt from its id alone, so a link can't send anyone anywhere else.</summary>
        public static string YouTubeUrl(string id)
        {
            return "https://www.youtube.com/watch?v=" + id;
        }

        /// <summary>Its thumbnail, fetched only when someone asks to see a preview.</summary>
        public static string YouTubeThumbnail(string id)
        {
            return "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg";
        }
    }
}
