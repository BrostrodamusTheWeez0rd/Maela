using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Maela
{
    /// <summary>One small shape an effect draws this frame.</summary>
    internal struct EffectDot
    {
        public double X, Y, Size, Alpha;
        public bool Second; // Drawn in the second colour.
    }

    /// <summary>
    /// Where the card and picture decos are at a moment in time. Only the maths lives here, so
    /// the Dynamic layout (WPF) and Lightweight (GDI+) draw exactly the same thing their own way.
    /// </summary>
    internal static class CardEffects
    {
        public static readonly CardEffect[] Cards = { CardEffect.None, CardEffect.Glow, CardEffect.Sparkles, CardEffect.Aurora };
        public static readonly PictureDeco[] Decos = { PictureDeco.None, PictureDeco.Orbit, PictureDeco.Halo, PictureDeco.Ripple };

        public static string Name(CardEffect effect)
        {
            return effect == CardEffect.None ? "None" : effect.ToString();
        }

        public static string Name(PictureDeco deco)
        {
            return deco == PictureDeco.None ? "None" : deco.ToString();
        }

        /// <summary>
        /// The dots or rings of a picture deco around a picture of the given radius. For Ripple,
        /// each one is a ring: X and Y its centre and Size its radius.
        /// </summary>
        public static List<EffectDot> Deco(PictureDeco deco, double t, double centreX, double centreY, double radius)
        {
            if (deco == PictureDeco.Orbit)
                return Orbit(t, centreX, centreY, radius);
            if (deco == PictureDeco.Halo)
                return Halo(t, centreX, centreY, radius);
            if (deco == PictureDeco.Ripple)
                return Ripples(t, centreX, centreY, radius);
            return new List<EffectDot>();
        }

        /// <summary>A bright arc sweeping round the picture, fading along its length.</summary>
        public static List<EffectDot> Halo(double t, double centreX, double centreY, double radius)
        {
            List<EffectDot> dots = new List<EffectDot>(32);
            double r = radius + 4;
            const int count = 32;
            for (int i = 0; i < count; i++)
            {
                double along = (double)i / (count - 1);
                double angle = t * 1.3 + along * Math.PI * 1.4;
                EffectDot dot = new EffectDot();
                dot.X = centreX + Math.Cos(angle) * r;
                dot.Y = centreY + Math.Sin(angle) * r;
                dot.Size = 0.8 + 1.8 * along;
                dot.Alpha = along * along;
                dot.Second = i < count / 2;
                dots.Add(dot);
            }
            return dots;
        }

        /// <summary>Three rings spreading out from the picture and fading, one after another.</summary>
        public static List<EffectDot> Ripples(double t, double centreX, double centreY, double radius)
        {
            List<EffectDot> rings = new List<EffectDot>(3);
            for (int k = 0; k < 3; k++)
            {
                double phase = (t * 0.55 + k / 3.0) % 1.0;
                EffectDot ring = new EffectDot();
                ring.X = centreX;
                ring.Y = centreY;
                ring.Size = radius + 2 + Math.Max(10, radius * 0.45) * phase;
                ring.Alpha = Math.Pow(1 - phase, 1.5);
                ring.Second = k == 1;
                rings.Add(ring);
            }
            return rings;
        }

        /// <summary>0 to 1 and back, about every two and a half seconds.</summary>
        public static double Pulse(double t)
        {
            return 0.5 + 0.5 * Math.Sin(t * 2.5);
        }

        /// <summary>
        /// Twinkling stars across an area. Each fades in and out, then appears somewhere new, never
        /// over the picture (a circle at avoidX, avoidY).
        /// </summary>
        public static List<EffectDot> Sparkles(int seed, double t, double width, double height, double avoidX, double avoidY, double avoidRadius)
        {
            List<EffectDot> dots = new List<EffectDot>(16);
            for (int i = 0; i < 16; i++)
            {
                double period = 1.6 + Hash(seed, i, -1) * 1.6;
                double phase = Hash(seed, i, -2) * period;
                double cycles = (t + phase) / period;
                int round = (int)Math.Floor(cycles);
                double life = cycles - round;
                double glint = Math.Sin(life * Math.PI);
                EffectDot dot = new EffectDot();
                dot.Size = (2.5 + Hash(seed, i, -3) * 3.5) * (0.4 + 0.6 * glint);
                // A few tries for a spot clear of the picture; failing that, it sits this one out.
                bool clear = false;
                for (int attempt = 0; attempt < 4 && !clear; attempt++)
                {
                    dot.X = Hash(seed, i + attempt * 64, round * 2) * width;
                    dot.Y = Hash(seed, i + attempt * 64, round * 2 + 1) * height;
                    double dx = dot.X - avoidX, dy = dot.Y - avoidY, keep = avoidRadius + 12;
                    clear = dx * dx + dy * dy > keep * keep;
                }
                dot.Alpha = clear ? glint * glint : 0;
                dot.Second = (i & 1) == 1;
                dots.Add(dot);
            }
            return dots;
        }

        /// <summary>Two lights circling the avatar, each with a fading tail.</summary>
        public static List<EffectDot> Orbit(double t, double centreX, double centreY, double radius)
        {
            List<EffectDot> dots = new List<EffectDot>(20);
            double r = radius + 7;
            for (int light = 0; light < 2; light++)
            {
                for (int tail = 9; tail >= 0; tail--)
                {
                    double angle = t * 1.7 + light * Math.PI - tail * 0.11;
                    EffectDot dot = new EffectDot();
                    dot.X = centreX + Math.Cos(angle) * r;
                    dot.Y = centreY + Math.Sin(angle) * r;
                    dot.Size = 3.6 * (1 - tail / 11.0);
                    dot.Alpha = 1 - tail / 10.0;
                    dot.Second = light == 1;
                    dots.Add(dot);
                }
            }
            return dots;
        }

        /// <summary>The centre line of one of three aurora bands, as y for evenly spaced x across the width.</summary>
        public static double[] AuroraBand(int band, double t, double width, double height, int steps)
        {
            double[] ys = new double[steps + 1];
            double baseY = height * (0.28 + 0.2 * band);
            double speed = 0.5 + 0.18 * band;
            for (int i = 0; i <= steps; i++)
            {
                double x = (double)i / steps;
                ys[i] = baseY + height * (0.16 * Math.Sin(x * Math.PI * 2.2 + t * speed + band * 1.7) + 0.06 * Math.Sin(x * Math.PI * 5 - t * speed * 1.6));
            }
            return ys;
        }

        /// <summary>A repeatable number from 0 to 1, so sparkles land in the same places on every PC.</summary>
        private static double Hash(int seed, int i, int round)
        {
            unchecked
            {
                uint h = (uint)seed * 2654435761u ^ (uint)i * 2246822519u ^ (uint)round * 3266489917u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                h *= 3266489917u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (double)0x1000000;
            }
        }

        public static Color ToColor(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        // ----- Lightweight (GDI+) drawing -----

        /// <summary>A card's banner: the name's colour, a colour, a gradient or a picture.</summary>
        public static void PaintBanner(Graphics g, Rectangle r, BannerKind kind, int colourA, int colourB, Image picture, Color auto)
        {
            if (kind == BannerKind.Picture && picture != null)
            {
                // Fill the width; crop the middle of the height if the shapes differ.
                float scale = Math.Max((float)r.Width / picture.Width, (float)r.Height / picture.Height);
                float w = picture.Width * scale, h = picture.Height * scale;
                InterpolationMode old = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                Region clip = g.Clip;
                g.SetClip(r, CombineMode.Intersect);
                g.DrawImage(picture, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
                g.Clip = clip;
                g.InterpolationMode = old;
                return;
            }
            if (kind == BannerKind.Gradient)
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(r.X - 1, r.Y, r.Width + 2, r.Height), ToColor(colourA), ToColor(colourB), LinearGradientMode.Horizontal))
                    g.FillRectangle(brush, r);
                return;
            }
            Color fill = kind == BannerKind.Colour ? ToColor(colourA) : Color.FromArgb(150, auto);
            using (SolidBrush brush = new SolidBrush(fill))
                g.FillRectangle(brush, r);
        }

        /// <summary>One frame of a picture deco around a picture.</summary>
        public static void PaintDeco(Graphics g, PictureDeco deco, PointF centre, float radius, int colourA, int colourB, double t)
        {
            if (deco == PictureDeco.None)
                return;
            Color a = ToColor(colourA), b = ToColor(colourB);
            SmoothingMode oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (EffectDot dot in Deco(deco, t, centre.X, centre.Y, radius))
            {
                Color colour = Color.FromArgb((int)(240 * dot.Alpha), dot.Second ? b : a);
                if (deco == PictureDeco.Ripple)
                {
                    using (Pen pen = new Pen(colour, 2f))
                        g.DrawEllipse(pen, (float)(dot.X - dot.Size), (float)(dot.Y - dot.Size), (float)dot.Size * 2, (float)dot.Size * 2);
                }
                else
                {
                    using (SolidBrush brush = new SolidBrush(colour))
                        g.FillEllipse(brush, (float)(dot.X - dot.Size), (float)(dot.Y - dot.Size), (float)dot.Size * 2, (float)dot.Size * 2);
                }
            }
            g.SmoothingMode = oldSmoothing;
        }

        /// <summary>One frame of a card deco on a card of the given size, with the banner and picture where they are.</summary>
        public static void PaintEffect(Graphics g, CardEffect effect, Size card, Rectangle banner, PointF avatar, float avatarRadius, int colourA, int colourB, int seed, double t)
        {
            if (effect == CardEffect.None)
                return;
            Color a = ToColor(colourA), b = ToColor(colourB);
            SmoothingMode oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (effect == CardEffect.Glow)
            {
                double pulse = Pulse(t);
                for (int i = 0; i < 6; i++)
                {
                    int alpha = (int)((150 + 90 * pulse) * (1 - i / 6.0) * (1 - i / 6.0));
                    using (Pen pen = new Pen(Color.FromArgb(alpha, i % 2 == 0 ? a : Ui.Mix(a, b, 0.5f)), 1.5f))
                        g.DrawRectangle(pen, i + 0.5f, i + 0.5f, card.Width - 2 * i - 1.5f, card.Height - 2 * i - 1.5f);
                }
            }
            else if (effect == CardEffect.Sparkles)
            {
                foreach (EffectDot dot in Sparkles(seed, t, card.Width, banner.Bottom + 70, avatar.X, avatar.Y, avatarRadius))
                {
                    Color tint = Ui.Mix(Color.White, dot.Second ? b : a, 0.35f);
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)(235 * dot.Alpha), tint)))
                        g.FillPolygon(brush, Star((float)dot.X, (float)dot.Y, (float)dot.Size));
                }
            }
            else if (effect == CardEffect.Aurora)
            {
                Region clip = g.Clip;
                g.SetClip(banner, CombineMode.Intersect);
                Color[] colours = { a, b, Ui.Mix(a, b, 0.5f) };
                const int steps = 24;
                for (int band = 0; band < 3; band++)
                {
                    double[] ys = AuroraBand(band, t, banner.Width, banner.Height, steps);
                    float thick = banner.Height * 0.32f;
                    PointF[] shape = new PointF[(steps + 1) * 2];
                    for (int i = 0; i <= steps; i++)
                    {
                        float x = banner.X + (float)i * banner.Width / steps;
                        shape[i] = new PointF(x, banner.Y + (float)ys[i] - thick / 2);
                        shape[shape.Length - 1 - i] = new PointF(x, banner.Y + (float)ys[i] + thick / 2);
                    }
                    using (LinearGradientBrush brush = new LinearGradientBrush(new RectangleF(banner.X - 1, banner.Y, banner.Width + 2, banner.Height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
                    {
                        ColorBlend blend = new ColorBlend();
                        blend.Positions = new float[] { 0f, 0.5f, 1f };
                        blend.Colors = new Color[] { Color.FromArgb(10, colours[band]), Color.FromArgb(95, colours[band]), Color.FromArgb(10, colours[band]) };
                        brush.InterpolationColors = blend;
                        g.FillPolygon(brush, shape);
                    }
                }
                g.Clip = clip;
            }
            g.SmoothingMode = oldSmoothing;
        }

        /// <summary>A four-pointed star.</summary>
        public static PointF[] Star(float x, float y, float size)
        {
            float waist = size * 0.22f;
            return new PointF[]
            {
                new PointF(x, y - size), new PointF(x + waist, y - waist), new PointF(x + size, y), new PointF(x + waist, y + waist),
                new PointF(x, y + size), new PointF(x - waist, y + waist), new PointF(x - size, y), new PointF(x - waist, y - waist),
            };
        }
    }
}
