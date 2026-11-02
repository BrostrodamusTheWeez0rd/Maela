using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Maela
{
    /// <summary>
    /// Profile pictures as plain pixels.
    ///
    /// FromFile/FromImage run only on your own PC, on a picture you chose: the image is
    /// decoded by Windows, cropped to a centred square, shrunk to VoipEngine.AvatarSize and
    /// turned into raw RGB565 pixels. Only those pixels are ever sent, so nothing else in the
    /// file (location, camera details, hidden data, the file name) leaves your PC.
    ///
    /// ToBitmap builds a picture from pixels received from someone else by copying bytes; no
    /// image decoder ever touches data from another person.
    /// </summary>
    internal static class ProfilePicture
    {
        /// <summary>Largest file we'll open for a profile picture.</summary>
        public const long MaxFileBytes = 20L * 1024 * 1024;

        public static byte[] FromFile(string path)
        {
            return FromFile(path, VoipEngine.AvatarSize, VoipEngine.AvatarSize);
        }

        /// <summary>A picture you chose, cropped to fill the given size.</summary>
        public static byte[] FromFile(string path, int width, int height)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
                throw new InvalidOperationException("Pick a picture smaller than 20 MB.");
            byte[] data = File.ReadAllBytes(path);
            using (MemoryStream stream = new MemoryStream(data))
            using (Image image = Image.FromStream(stream, false, true))
            {
                if (image.Width < 8 || image.Height < 8 || image.Width > 20000 || image.Height > 20000)
                    throw new InvalidOperationException("That picture's size isn't supported.");
                return FromImage(image, width, height);
            }
        }

        public static byte[] FromImage(Image image)
        {
            return FromImage(image, VoipEngine.AvatarSize, VoipEngine.AvatarSize);
        }

        public static byte[] FromImage(Image image, int width, int height)
        {
            // The largest centred area of the picture with the target's shape.
            int cropWidth = image.Width, cropHeight = (int)((long)image.Width * height / width);
            if (cropHeight > image.Height)
            {
                cropHeight = image.Height;
                cropWidth = (int)((long)image.Height * width / height);
            }
            Rectangle source = new Rectangle((image.Width - cropWidth) / 2, (image.Height - cropHeight) / 2, cropWidth, cropHeight);
            using (Bitmap square = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(square))
                {
                    g.Clear(Color.FromArgb(40, 40, 46)); // Transparent areas become a dark backdrop.
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(image, new Rectangle(0, 0, width, height), source, GraphicsUnit.Pixel);
                }
                return ToRgb565(square, width, height);
            }
        }

        private static byte[] ToRgb565(Bitmap bitmap, int width, int height)
        {
            byte[] pixels = new byte[width * height * 2];
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < width; x++)
                    {
                        int b = row[x * 4], gr = row[x * 4 + 1], r = row[x * 4 + 2];
                        // Ordered dither keeps smooth gradients from banding at 16-bit colour.
                        int d = Dither[(y & 3) * 4 + (x & 3)];
                        r = Math.Min(255, r + d / 2); gr = Math.Min(255, gr + d / 4); b = Math.Min(255, b + d / 2);
                        int v = ((r >> 3) << 11) | ((gr >> 2) << 5) | (b >> 3);
                        int i = (y * width + x) * 2;
                        pixels[i] = (byte)v;
                        pixels[i + 1] = (byte)(v >> 8);
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return pixels;
        }

        private static readonly int[] Dither = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        /// <summary>Builds a picture from received pixels by copying bytes. Returns null if the size is wrong.</summary>
        public static Bitmap ToBitmap(byte[] pixels)
        {
            return ToBitmap(pixels, VoipEngine.AvatarSize, VoipEngine.AvatarSize);
        }

        public static Bitmap ToBitmap(byte[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length != width * height * 2)
                return null;
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 2;
                        int v = pixels[i] | (pixels[i + 1] << 8);
                        int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
                        row[x * 4] = (byte)((b << 3) | (b >> 2));
                        row[x * 4 + 1] = (byte)((g << 2) | (g >> 4));
                        row[x * 4 + 2] = (byte)((r << 3) | (r >> 2));
                        row[x * 4 + 3] = 255;
                    }
                    Marshal.Copy(row, 0, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }
    }
}
