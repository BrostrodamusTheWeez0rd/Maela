using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>Pictures and YouTube links in chat: sending, showing and opening them, in both layouts.</summary>
    internal sealed partial class MainForm
    {
        private readonly Dictionary<string, System.Windows.Media.ImageSource> _shotThumbs = new Dictionary<string, System.Windows.Media.ImageSource>();

        /// <summary>A message's words as Lightweight's chat shows them: a picture becomes a line to click.</summary>
        internal static string ChatRowText(string text)
        {
            string id;
            int width, height, bytes;
            if (ChatPicture.TryParseMarker(text, out id, out width, out height, out bytes))
                return "\uD83D\uDDBC Picture \u00B7 click to see it";
            return ChatPicture.YouTubeId(text) != null ? text + "  ▶ click to watch" : text;
        }

        /// <summary>Lightweight's chats: a click on a picture opens it, on a YouTube link opens the video. True if it did.</summary>
        internal bool OpenFromChat(string text, string friendId)
        {
            string id;
            int width, height, bytes;
            if (ChatPicture.TryParseMarker(text, out id, out width, out height, out bytes))
            {
                if (ChatPictureBlob(id, bytes, friendId) != null)
                    OpenChatPicture(id, friendId);
                return true;
            }
            string video = ChatPicture.YouTubeId(text);
            if (video == null)
                return false;
            OpenVideo(video);
            return true;
        }

        /// <summary>A picture from a room's chat, or a friend's, or null while it's on its way.</summary>
        private byte[] ChatPictureBlob(string id, int bytes, string friendId)
        {
            if (friendId != null)
                return _friends == null ? null : _friends.GetPicture(id);
            VoipEngine engine = _engine;
            return engine == null ? null : engine.GetPicture(id, bytes);
        }

        private bool ChatPictureComing(string id, string friendId)
        {
            if (friendId != null)
                return _friends != null && _friends.PictureComing(id);
            VoipEngine engine = _engine;
            return engine != null && engine.PictureComing(id);
        }

        /// <summary>For the Dynamic chat: a message's picture (a small copy) or YouTube card, and its words without the marker.</summary>
        private void FillAttachments(SpatialLine line, string friendId)
        {
            string id;
            int width, height, bytes;
            if (ChatPicture.TryParseMarker(line.Text, out id, out width, out height, out bytes))
            {
                line.Text = String.Empty;
                line.ShotId = id;
                line.ShotWidth = width;
                line.ShotHeight = height;
                line.FriendId = friendId;
                byte[] blob = ChatPictureBlob(id, bytes, friendId);
                if (blob != null)
                    line.Shot = ShotThumb(id, blob);
                line.ShotComing = line.Shot == null && ChatPictureComing(id, friendId);
                return;
            }
            line.VideoId = ChatPicture.YouTubeId(line.Text);
        }

        /// <summary>A small copy of a picture for the chat column, made once from its checked pixels.</summary>
        private System.Windows.Media.ImageSource ShotThumb(string id, byte[] blob)
        {
            System.Windows.Media.ImageSource thumb;
            if (_shotThumbs.TryGetValue(id, out thumb))
                return thumb;
            int width, height;
            byte[] rgb;
            if (!ChatPicture.TryDecode(blob, out width, out height, out rgb))
                return null;
            // Every step-th pixel, so a big picture's copy stays small; crisp enough at chat size.
            int step = Math.Max(1, (int)Math.Ceiling(Math.Max(width / 760.0, height / 600.0)));
            int w = Math.Max(1, width / step), h = Math.Max(1, height / step);
            byte[] small = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Buffer.BlockCopy(rgb, ((y * step) * width + x * step) * 3, small, (y * w + x) * 3, 3);
            System.Windows.Media.Imaging.BitmapSource source = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, small, w * 3);
            source.Freeze();
            if (_shotThumbs.Count > 64)
                _shotThumbs.Clear();
            _shotThumbs[id] = source;
            return source;
        }

        // ----- Sending -----

        internal void SpatialAttachPicture(string friendId, int channel)
        {
            SpatialDialog(delegate { AttachPicture(friendId, channel); });
        }

        internal void SpatialPastePicture(string friendId, int channel)
        {
            SpatialDo(delegate { PastePicture(friendId, channel); });
        }

        /// <summary>Pick a picture to send. It's rebuilt from its pixels on this PC before anything is sent.</summary>
        private void AttachPicture(string friendId, int channel)
        {
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Title = "Send a picture";
                open.Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*";
                if (open.ShowDialog(this) != DialogResult.OK)
                    return;
                string path = open.FileName;
                SendPictureMadeBy(delegate { return ChatPicture.FromFile(path); }, friendId, channel);
            }
        }

        /// <summary>A picture on the clipboard, sent the same way. Returns false if there isn't one.</summary>
        private bool PastePicture(string friendId, int channel)
        {
            // Screenshots and copied pictures, as a bitmap or (from some apps) only as a PNG.
            // Either way it's this PC's own clipboard, rebuilt from its pixels before anything is sent.
            Image pasted = null;
            try
            {
                if (Clipboard.ContainsImage())
                    pasted = Clipboard.GetImage();
                if (pasted == null && Clipboard.ContainsData("PNG"))
                {
                    System.IO.Stream png = Clipboard.GetData("PNG") as System.IO.Stream;
                    if (png != null)
                        using (Image raw = Image.FromStream(png))
                            pasted = new Bitmap(raw);
                }
            }
            catch { pasted = null; }
            if (pasted == null)
                return false;
            SendPictureMadeBy(delegate
            {
                using (pasted)
                    return ChatPicture.FromImage(pasted);
            }, friendId, channel);
            return true;
        }

        /// <summary>Rebuilds the picture off the window's thread (a big one takes a moment), then sends it.</summary>
        private void SendPictureMadeBy(Func<byte[]> make, string friendId, int channel)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                byte[] blob = null;
                string problem = null;
                try { blob = make(); }
                catch (InvalidOperationException e) { problem = e.Message; }
                catch { problem = "That file couldn't be opened as a picture."; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (blob != null && !SendChatPicture(blob, friendId, channel))
                            problem = friendId != null ? "Pictures can only be sent to friends." : "Join a room and open a channel's chat to send a picture.";
                        if (problem != null)
                            MessageBox.Show(this, problem, "Picture", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch { }
            });
        }

        private bool SendChatPicture(byte[] blob, string friendId, int channel)
        {
            if (friendId != null)
                return _friends != null && _friends.SendPicture(friendId, blob);
            VoipEngine engine = _engine;
            if (engine == null || !engine.IsConnected)
                return false;
            if (channel > 0)
                engine.ViewChannel(channel);
            return engine.SendPicture(blob);
        }

        // ----- Opening -----

        internal void SpatialOpenShot(string id, string friendId)
        {
            SpatialDialog(delegate { OpenChatPicture(id, friendId); });
        }

        internal void SpatialOpenVideo(string videoId)
        {
            SpatialDo(delegate { OpenVideo(videoId); });
        }

        /// <summary>A picture full size, with a way to save it.</summary>
        private void OpenChatPicture(string id, string friendId)
        {
            byte[] blob = ChatPictureBlob(id, ChatPicture.MaxBytes, friendId);
            int width, height;
            byte[] rgb;
            if (blob == null || !ChatPicture.TryDecode(blob, out width, out height, out rgb))
                return;
            using (Bitmap bitmap = PictureViewer.ToBitmap(rgb, width, height))
            using (PictureViewer viewer = new PictureViewer(bitmap))
                viewer.ShowDialog(this);
        }

        private static readonly Regex VideoIdPattern = new Regex("^[A-Za-z0-9_-]{11}$");

        /// <summary>Opens a YouTube video in the browser, from its id alone, so a link can't lead anywhere else.</summary>
        private void OpenVideo(string videoId)
        {
            if (videoId == null || !VideoIdPattern.IsMatch(videoId))
                return;
            try { Process.Start(ChatPicture.YouTubeUrl(videoId)); }
            catch { }
        }
    }

    /// <summary>A picture from chat, full size in its own window, with Save.</summary>
    internal sealed class PictureViewer : Form
    {
        private readonly Bitmap _picture;

        public PictureViewer(Bitmap picture)
        {
            _picture = picture;
            Text = "Picture";
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(14, 14, 20);
            try { Icon = Branding.LoadAppIcon(); } catch { }
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            double scale = Math.Min(1.0, Math.Min((work.Width * 0.85) / picture.Width, (work.Height * 0.85 - 60) / picture.Height));
            ClientSize = new Size(Math.Max(360, (int)(picture.Width * scale)), Math.Max(240, (int)(picture.Height * scale)) + 52);
            MinimumSize = new Size(320, 240);
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };

            PictureBox box = new PictureBox();
            box.Image = picture;
            box.SizeMode = PictureBoxSizeMode.Zoom;
            box.Dock = DockStyle.Fill;
            box.BackColor = BackColor;
            Controls.Add(box);

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 52;
            bar.BackColor = Theme.Current.Main;
            Controls.Add(bar);
            FlatButton save = new FlatButton();
            save.Text = "Save…";
            save.Look = ButtonLook.Secondary;
            save.SetBounds(12, 8, 110, 36);
            save.Click += delegate { Save(); };
            bar.Controls.Add(save);
            FlatButton close = new FlatButton();
            close.Text = "Close";
            close.Look = ButtonLook.Primary;
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.SetBounds(bar.Width - 122, 8, 110, 36);
            close.Click += delegate { Close(); };
            bar.Controls.Add(close);
            bar.Resize += delegate { close.Left = bar.Width - 122; };
        }

        /// <summary>Saves the picture as a fresh PNG made from its pixels.</summary>
        private void Save()
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Save picture";
                dialog.Filter = "PNG picture|*.png";
                dialog.FileName = "picture.png";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                try { _picture.Save(dialog.FileName, ImageFormat.Png); }
                catch (Exception e) { MessageBox.Show(this, "The picture couldn't be saved: " + e.Message, "Picture", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        /// <summary>A bitmap made by copying checked RGB pixels in; no decoder involved.</summary>
        public static Bitmap ToBitmap(byte[] rgb, int width, int height)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] row = new byte[width * 3];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 3;
                        row[x * 3] = rgb[i + 2];     // GDI+ rows are BGR.
                        row[x * 3 + 1] = rgb[i + 1];
                        row[x * 3 + 2] = rgb[i];
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
