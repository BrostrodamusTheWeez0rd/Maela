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
            Accent = Rgb(0xE5484D), Accent2 = Rgb(0xFF6B70)
        };

        public static readonly Theme Midnight = new Theme
        {
            Id = "midnight", Name = "Midnight", Dark = true,
            Rail = Rgb(0x0A0D13), Side = Rgb(0x10141C), Main = Rgb(0x141924), Surface = Rgb(0x1C2230), Surface2 = Rgb(0x242B3B),
            Line = Rgb(0x212838), Text = Rgb(0xEEF1F8), Text2 = Rgb(0xC3CAD9), Muted = Rgb(0x8590A6),
            Accent = Rgb(0x6D7CFF), Accent2 = Rgb(0x93A0FF)
        };

        public static readonly Theme Daylight = new Theme
        {
            Id = "daylight", Name = "Daylight", Dark = false,
            Rail = Rgb(0xEBE5E7), Side = Rgb(0xF5F1F2), Main = Rgb(0xFFFFFF), Surface = Rgb(0xF2EDEF), Surface2 = Rgb(0xE6DFE2),
            Line = Rgb(0xE6DFE2), Text = Rgb(0x1D1719), Text2 = Rgb(0x3D3337), Muted = Rgb(0x7C7074),
            Accent = Rgb(0xD93B47), Accent2 = Rgb(0xD93B47), Good = Rgb(0x1F9D63)
        };

        public static readonly Theme OledBlack = new Theme
        {
            Id = "oled", Name = "OLED Black", Dark = true,
            Rail = Rgb(0x000000), Side = Rgb(0x000000), Main = Rgb(0x000000), Surface = Rgb(0x121112), Surface2 = Rgb(0x1C1A1B),
            Line = Rgb(0x1E1C1D), Text = Rgb(0xF6EFF1), Text2 = Rgb(0xC9BFC3), Muted = Rgb(0x8E8387),
            Accent = Rgb(0xE5484D), Accent2 = Rgb(0xFF6B70)
        };

        public static readonly Theme[] All = { MaelaRed, Midnight, Daylight, OledBlack };

        private static Theme _current = MaelaRed;

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
            return MaelaRed;
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
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (speaking)
            {
                using (SolidBrush ring = new SolidBrush(Theme.Current.Good))
                    g.FillEllipse(ring, r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8);
                using (SolidBrush gap = new SolidBrush(background))
                    g.FillEllipse(gap, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
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
