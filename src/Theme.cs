using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Maela
{
    /// <summary>A colour scheme. Every surface in the app reads its colours from Theme.Current.</summary>
    internal sealed class Theme
    {
        public string Id;
        public string Name;
        public bool Dark;

        public Color Rail;       // room rail, title bar, user bar
        public Color Side;       // channel sidebar
        public Color Main;       // chat area
        public Color Surface;    // cards, inputs, chips
        public Color Surface2;   // buttons on cards, hover
        public Color Line;       // dividers and borders
        public Color Text;
        public Color Text2;      // secondary text
        public Color Muted;      // captions, timestamps
        public Color Accent;
        public Color Accent2;    // lighter accent for text/icons on dark
        public Color OnAccent = Color.White;
        public Color Logo;       // The logo's colour: tray, taskbar and Spatial's top bar.
        public Color Second;     // Spatial's second accent: lines between dots, voice channels.
        public Color Warm = Color.FromArgb(255, 176, 87); // Spatial's chat-only channels.
        public Color Good = Color.FromArgb(62, 207, 142);
        public Color Danger = Color.FromArgb(242, 63, 67);

        public Color AccentSoft { get { return Ui.Mix(Main, Accent, Dark ? 0.16f : 0.10f); } }
        public Color AccentSoftSide { get { return Ui.Mix(Side, Accent, Dark ? 0.16f : 0.10f); } }
        public Color DangerSoft { get { return Ui.Mix(Surface, Danger, Dark ? 0.20f : 0.12f); } }

        public static readonly Theme MaelaRed = new Theme
        {
            Id = "maela-red", Name = "Maela Red", Dark = true,
            Rail = Rgb(0x100D0F), Side = Rgb(0x171316), Main = Rgb(0x1C181B), Surface = Rgb(0x262024), Surface2 = Rgb(0x2F282C),
            Line = Rgb(0x2E272B), Text = Rgb(0xF6EFF1), Text2 = Rgb(0xCFC4C8), Muted = Rgb(0x94888D),
            Accent = Rgb(0xE5484D), Accent2 = Rgb(0xFF6B70), Logo = Rgb(0xFF5A60), Second = Rgb(0xFF9D6B)
        };

        public static readonly Theme Midnight = new Theme
        {
            Id = "midnight", Name = "Midnight", Dark = true,
            // The neon look, the same as Spatial's. Cyan is too light for white text, hence OnAccent.
            Rail = Rgb(0x07090F), Side = Rgb(0x0D1020), Main = Rgb(0x111528), Surface = Rgb(0x171B2E), Surface2 = Rgb(0x21263D),
            Line = Rgb(0x232842), Text = Rgb(0xEEF1FA), Text2 = Rgb(0xBCC3D8), Muted = Rgb(0x8089A3),
            Accent = Rgb(0x00E5F5), Accent2 = Rgb(0x6FF3FB), OnAccent = Rgb(0x031014), Logo = Rgb(0x00E5F5), Second = Rgb(0xB35CFF), Warm = Rgb(0xFF7A2F)
        };

        public static readonly Theme Daylight = new Theme
        {
            Id = "daylight", Name = "Daylight", Dark = false,
            Rail = Rgb(0xEBE5E7), Side = Rgb(0xF5F1F2), Main = Rgb(0xFFFFFF), Surface = Rgb(0xF2EDEF), Surface2 = Rgb(0xE6DFE2),
            Line = Rgb(0xE6DFE2), Text = Rgb(0x1D1719), Text2 = Rgb(0x3D3337), Muted = Rgb(0x7C7074),
            Accent = Rgb(0xD93B47), Accent2 = Rgb(0xD93B47), Good = Rgb(0x1F9D63), Logo = Rgb(0xD93B47), Second = Rgb(0x7C6CFF), Warm = Rgb(0xF28037)
        };

        public static readonly Theme OledBlack = new Theme
        {
            Id = "oled", Name = "OLED Black", Dark = true,
            Rail = Rgb(0x000000), Side = Rgb(0x000000), Main = Rgb(0x000000), Surface = Rgb(0x121112), Surface2 = Rgb(0x1C1A1B),
            Line = Rgb(0x1E1C1D), Text = Rgb(0xF6EFF1), Text2 = Rgb(0xC9BFC3), Muted = Rgb(0x8E8387),
            Accent = Rgb(0xE5484D), Accent2 = Rgb(0xFF6B70), Logo = Rgb(0xFF5A60), Second = Rgb(0xFF9D6B)
        };

        public static readonly Theme Synthwave = new Theme
        {
            Id = "synthwave", Name = "Synthwave", Dark = true,
            Rail = Rgb(0x0C0614), Side = Rgb(0x140B22), Main = Rgb(0x190E2A), Surface = Rgb(0x231537), Surface2 = Rgb(0x2D1C45),
            Line = Rgb(0x2C1D42), Text = Rgb(0xF7EEFF), Text2 = Rgb(0xD2C2E6), Muted = Rgb(0x9A88B3),
            Accent = Rgb(0xFF3DCB), Accent2 = Rgb(0xFF7ADB), OnAccent = Rgb(0x1A0414), Logo = Rgb(0xFF3DCB), Second = Rgb(0x3DE8FF), Warm = Rgb(0xFFB13D)
        };

        public static readonly Theme Forest = new Theme
        {
            Id = "forest", Name = "Forest", Dark = true,
            Rail = Rgb(0x060D09), Side = Rgb(0x0B1610), Main = Rgb(0x0F1C15), Surface = Rgb(0x16271D), Surface2 = Rgb(0x1E3326),
            Line = Rgb(0x1E3227), Text = Rgb(0xEEF7F1), Text2 = Rgb(0xC1D6C8), Muted = Rgb(0x86A08F),
            Accent = Rgb(0x3DDC84), Accent2 = Rgb(0x7AEAAA), OnAccent = Rgb(0x03140A), Logo = Rgb(0x3DDC84), Second = Rgb(0x2EC4B6), Warm = Rgb(0xE8C547)
        };

        public static readonly Theme Ember = new Theme
        {
            Id = "ember", Name = "Ember", Dark = true,
            Rail = Rgb(0x0E0806), Side = Rgb(0x18100B), Main = Rgb(0x1E140E), Surface = Rgb(0x2A1C14), Surface2 = Rgb(0x35241A),
            Line = Rgb(0x33241A), Text = Rgb(0xFBF1EA), Text2 = Rgb(0xDCC8BA), Muted = Rgb(0xA08876),
            Accent = Rgb(0xFF8A3D), Accent2 = Rgb(0xFFB27A), OnAccent = Rgb(0x1A0A02), Logo = Rgb(0xFF8A3D), Second = Rgb(0xFFC94D), Warm = Rgb(0xFF5A60)
        };

        public static readonly Theme Ocean = new Theme
        {
            Id = "ocean", Name = "Ocean", Dark = true,
            Rail = Rgb(0x050A12), Side = Rgb(0x0A1220), Main = Rgb(0x0D1728), Surface = Rgb(0x142136), Surface2 = Rgb(0x1B2B44),
            Line = Rgb(0x1B2A42), Text = Rgb(0xEDF4FD), Text2 = Rgb(0xBFD0E6), Muted = Rgb(0x8297B3),
            Accent = Rgb(0x3DA5FF), Accent2 = Rgb(0x7BC2FF), OnAccent = Rgb(0x02101F), Logo = Rgb(0x3DA5FF), Second = Rgb(0x2EE6C9), Warm = Rgb(0xFFB13D)
        };

        public static readonly Theme Sakura = new Theme
        {
            Id = "sakura", Name = "Sakura", Dark = true,
            Rail = Rgb(0x110A0E), Side = Rgb(0x1A1016), Main = Rgb(0x20141B), Surface = Rgb(0x2C1C26), Surface2 = Rgb(0x372431),
            Line = Rgb(0x352330), Text = Rgb(0xFBEFF5), Text2 = Rgb(0xDEC6D2), Muted = Rgb(0xA3899A),
            Accent = Rgb(0xFF7EB6), Accent2 = Rgb(0xFFA8CE), OnAccent = Rgb(0x1F0612), Logo = Rgb(0xFF7EB6), Second = Rgb(0xB79CFF), Warm = Rgb(0xFFC27A)
        };

        public static readonly Theme[] All = { Midnight, Ocean, Synthwave, Sakura, MaelaRed, OledBlack, Ember, Forest, Daylight }; // Round the colour wheel, so similar ones sit together.

        private static Theme _current = Midnight;

        public static Theme Current { get { return _current; } }

        /// <summary>Raised on the UI thread after the theme changes.</summary>
        public static event EventHandler Changed;

        public static Theme ById(string id)
        {
            int i;
            for (i = 0; i < All.Length; i++)
            {
                if (String.Equals(All[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    return All[i];
            }
            return Midnight;
        }

        public static void Apply(Theme theme)
        {
            if (theme == null || Object.ReferenceEquals(theme, _current))
                return;
            _current = theme;
            EventHandler handler = Changed;
            if (handler != null)
                handler(null, EventArgs.Empty);
        }

        private static Color Rgb(int value)
        {
            return Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
        }
    }

    /// <summary>Fonts, shapes and colour helpers shared by every custom-drawn control.</summary>
    internal static class Ui
    {
        public static readonly string FontFamilyName = PickFont();
        public static readonly string DisplayFamilyName = PickDisplayFont();

        public static readonly Font Body = new Font(FontFamilyName, 10F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font BodyBold = new Font(FontFamilyName, 10F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Small = new Font(FontFamilyName, 8.5F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font SmallBold = new Font(FontFamilyName, 8.5F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Tiny = new Font(FontFamilyName, 7.5F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Title = new Font(DisplayFamilyName, 13F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Heading = new Font(DisplayFamilyName, 18F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Mono = new Font(PickMonoFont(), 9F, FontStyle.Regular, GraphicsUnit.Point);

        private static readonly Color[] AvatarColors =
        {
            Color.FromArgb(229, 72, 77), Color.FromArgb(47, 191, 143), Color.FromArgb(240, 178, 50), Color.FromArgb(124, 108, 255),
            Color.FromArgb(228, 92, 164), Color.FromArgb(0, 168, 252), Color.FromArgb(242, 128, 55), Color.FromArgb(26, 188, 156)
        };

        private static string PickFont()
        {
            return FirstInstalled("Segoe UI Variable Text", "Segoe UI", "Inter", "Tahoma");
        }

        private static string PickDisplayFont()
        {
            return FirstInstalled("Segoe UI Variable Display", "Segoe UI Semibold", "Inter Display", "Segoe UI", "Tahoma");
        }

        private static string PickMonoFont()
        {
            return FirstInstalled("Cascadia Mono", "Consolas", "JetBrains Mono", "Courier New");
        }

        private static string FirstInstalled(params string[] names)
        {
            try
            {
                using (InstalledFontCollection fonts = new InstalledFontCollection())
                {
                    int i;
                    for (i = 0; i < names.Length; i++)
                    {
                        foreach (FontFamily family in fonts.Families)
                        {
                            if (String.Equals(family.Name, names[i], StringComparison.OrdinalIgnoreCase))
                                return names[i];
                        }
                    }
                }
            }
            catch { }
            return "Segoe UI";
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color ColorForName(string name)
        {
            int hash = 17;
            string value = name ?? String.Empty;
            int i;
            for (i = 0; i < value.Length; i++)
                hash = unchecked(hash * 31 + Char.ToLowerInvariant(value[i]));
            return AvatarColors[(hash & 0x7FFFFFFF) % AvatarColors.Length];
        }

        /// <summary>Name colour in chat: the avatar colour, nudged for readability on light themes.</summary>
        public static Color NameColor(string name)
        {
            Color c = ColorForName(name);
            return Theme.Current.Dark ? Mix(c, Color.White, 0.18f) : Mix(c, Color.Black, 0.12f);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundRect(r, radius))
            using (SolidBrush brush = new SolidBrush(color))
                g.FillPath(brush, path);
            g.SmoothingMode = old;
        }

        public static void StrokeRound(Graphics g, Color color, RectangleF r, float radius, float width)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundRect(r, radius))
            using (Pen pen = new Pen(color, width))
                g.DrawPath(pen, path);
            g.SmoothingMode = old;
        }

        public static void DrawAvatar(Graphics g, Rectangle r, string name, bool speaking, Color background)
        {
            DrawAvatar(g, r, name, speaking, background, null);
        }

        /// <summary>Round avatar: the person's picture if they have one, else their initial on a colour.</summary>
        public static void DrawAvatar(Graphics g, Rectangle r, string name, bool speaking, Color background, Image picture)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (speaking)
            {
                using (SolidBrush ring = new SolidBrush(Theme.Current.Good))
                    g.FillEllipse(ring, r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8);
                using (SolidBrush gap = new SolidBrush(background))
                    g.FillEllipse(gap, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
            }
            if (picture != null)
            {
                using (GraphicsPath circle = new GraphicsPath())
                {
                    circle.AddEllipse(r);
                    Region clip = g.Clip;
                    g.SetClip(circle, CombineMode.Intersect);
                    InterpolationMode oldMode = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(picture, r);
                    g.InterpolationMode = oldMode;
                    g.Clip = clip;
                }
                g.SmoothingMode = old;
                return;
            }
            using (SolidBrush b = new SolidBrush(ColorForName(name)))
                g.FillEllipse(b, r);
            g.SmoothingMode = old;

            string initial = String.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            float size = Math.Max(7f, r.Height * 0.36f);
            using (Font f = new Font(FontFamilyName, size, FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, initial, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        public static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags extra)
        {
            TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | extra;
            if ((extra & TextFormatFlags.WordBreak) == 0)
                flags |= TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, text ?? String.Empty, font, bounds, color, flags);
        }

        public static void DrawText(Graphics g, string text, Font font, Point at, Color color)
        {
            TextRenderer.DrawText(g, text ?? String.Empty, font, at, color, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        public static Size Measure(Graphics g, string text, Font font)
        {
            return TextRenderer.MeasureText(g, text ?? String.Empty, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        /// <summary>Colours the native Windows title bar to match the theme (dark mode on Windows 10+, exact colour on 11).</summary>
        public static void StyleWindowFrame(Form form)
        {
            if (form == null || !form.IsHandleCreated)
                return;
            try
            {
                Theme t = Theme.Current;
                int dark = t.Dark ? 1 : 0;
                DwmSetWindowAttribute(form.Handle, 20, ref dark, 4);       // DWMWA_USE_IMMERSIVE_DARK_MODE
                int caption = ColorRef(t.Rail);
                DwmSetWindowAttribute(form.Handle, 35, ref caption, 4);    // DWMWA_CAPTION_COLOR (Windows 11)
                int text = ColorRef(t.Text2);
                DwmSetWindowAttribute(form.Handle, 36, ref text, 4);       // DWMWA_TEXT_COLOR (Windows 11)
                int border = ColorRef(t.Line);
                DwmSetWindowAttribute(form.Handle, 34, ref border, 4);     // DWMWA_BORDER_COLOR (Windows 11)
                int round = 2;
                DwmSetWindowAttribute(form.Handle, 33, ref round, 4);      // DWMWA_WINDOW_CORNER_PREFERENCE = round
            }
            catch
            {
                // Older Windows (or not Windows): keep the default frame.
            }
        }

        private static int ColorRef(Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
