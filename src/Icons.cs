using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Maela
{
    internal enum IconKind
    {
        None, Mic, MicOff, Headphones, HeadphonesOff, Gear, Plus, Send, Smile, Copy, Leave, Lock,
        Search, Speaker, Close, Hash, Check, Waves, WavesOff, Signal, Download, Palette, Crown, Disconnect
    }

    /// <summary>
    /// Vector icons drawn with GDI+ on a 24x24 grid and scaled to any size, so they stay
    /// sharp at every DPI and take the theme colour.
    /// </summary>
    internal static class Icons
    {
        public static void Draw(Graphics g, IconKind kind, RectangleF bounds, Color color)
        {
            if (kind == IconKind.None)
                return;
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Min(bounds.Width, bounds.Height) / 24f;
            g.TranslateTransform(bounds.X + (bounds.Width - 24f * scale) / 2f, bounds.Y + (bounds.Height - 24f * scale) / 2f);
            g.ScaleTransform(scale, scale);

            using (Pen pen = new Pen(color, 2f))
            using (SolidBrush brush = new SolidBrush(color))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (kind)
                {
                    case IconKind.Mic:
                        FillRound(g, brush, 9, 3, 6, 12, 3);
                        g.DrawArc(pen, 5, 4, 14, 14, 0, 180);
                        g.DrawLine(pen, 12, 18, 12, 21);
                        break;
                    case IconKind.MicOff:
                        FillRound(g, brush, 9, 3, 6, 12, 3);
                        g.DrawArc(pen, 5, 4, 14, 14, 0, 180);
                        g.DrawLine(pen, 12, 18, 12, 21);
                        Slash(g, color, scale);
                        break;
                    case IconKind.Headphones:
                        g.DrawArc(pen, 4, 4, 16, 16, 180, 180);
                        g.DrawLine(pen, 4, 12, 4, 15);
                        g.DrawLine(pen, 20, 12, 20, 15);
                        FillRound(g, brush, 3, 14, 5, 7, 2);
                        FillRound(g, brush, 16, 14, 5, 7, 2);
                        break;
                    case IconKind.HeadphonesOff:
                        g.DrawArc(pen, 4, 4, 16, 16, 180, 180);
                        g.DrawLine(pen, 4, 12, 4, 15);
                        g.DrawLine(pen, 20, 12, 20, 15);
                        FillRound(g, brush, 3, 14, 5, 7, 2);
                        FillRound(g, brush, 16, 14, 5, 7, 2);
                        Slash(g, color, scale);
                        break;
                    case IconKind.Gear:
                        g.DrawEllipse(pen, 8.8f, 8.8f, 6.4f, 6.4f);
                        int a;
                        for (a = 0; a < 8; a++)
                        {
                            double rad = a * Math.PI / 4;
                            g.DrawLine(pen, 12 + (float)Math.Cos(rad) * 6.5f, 12 + (float)Math.Sin(rad) * 6.5f, 12 + (float)Math.Cos(rad) * 9.2f, 12 + (float)Math.Sin(rad) * 9.2f);
                        }
                        break;
                    case IconKind.Plus:
                        g.DrawLine(pen, 12, 5, 12, 19);
                        g.DrawLine(pen, 5, 12, 19, 12);
                        break;
                    case IconKind.Send:
                        g.FillPolygon(brush, new PointF[] { new PointF(3.4f, 20.4f), new PointF(21, 12), new PointF(3.4f, 3.6f), new PointF(3.4f, 10), new PointF(16, 12), new PointF(3.4f, 14) });
                        break;
                    case IconKind.Smile:
                        g.DrawEllipse(pen, 3, 3, 18, 18);
                        g.FillEllipse(brush, 7.7f, 8.7f, 2.6f, 2.6f);
                        g.FillEllipse(brush, 13.7f, 8.7f, 2.6f, 2.6f);
                        g.DrawArc(pen, 7.5f, 9.5f, 9, 7, 20, 140);
                        break;
                    case IconKind.Copy:
                        DrawRound(g, pen, 8, 8, 12, 12, 3);
                        g.DrawLines(pen, new PointF[] { new PointF(16, 8), new PointF(16, 6), new PointF(14, 4), new PointF(6, 4), new PointF(4, 6), new PointF(4, 14), new PointF(6, 16), new PointF(8, 16) });
                        break;
                    case IconKind.Disconnect:
                        // An open door with an arrow leaving it.
                        g.DrawLine(pen, 14, 4, 6, 4);
                        g.DrawLine(pen, 6, 4, 6, 20);
                        g.DrawLine(pen, 6, 20, 14, 20);
                        g.DrawLine(pen, 11, 12, 21, 12);
                        g.DrawLine(pen, 17.5f, 8.5f, 21, 12);
                        g.DrawLine(pen, 17.5f, 15.5f, 21, 12);
                        break;
                    case IconKind.Leave:
                        using (GraphicsPath p = new GraphicsPath())
                        {
                            p.AddBezier(3, 13.5f, 8, 8.5f, 16, 8.5f, 21, 13.5f);
                            p.AddLine(21, 13.5f, 18.6f, 16.1f);
                            p.AddLine(18.6f, 16.1f, 15.3f, 14.7f);
                            p.AddLine(15.3f, 14.7f, 15.3f, 12.1f);
                            p.AddBezier(15.3f, 12.1f, 13.2f, 11.4f, 10.8f, 11.4f, 8.7f, 12.1f);
                            p.AddLine(8.7f, 12.1f, 8.7f, 14.7f);
                            p.AddLine(8.7f, 14.7f, 5.4f, 16.1f);
                            p.CloseFigure();
                            g.FillPath(brush, p);
                        }
                        break;
                    case IconKind.Lock:
                        FillRound(g, brush, 5, 11, 14, 10, 2.5f);
                        g.DrawArc(pen, 8, 4, 8, 9, 180, 180);
                        g.DrawLine(pen, 8, 8.5f, 8, 11);
                        g.DrawLine(pen, 16, 8.5f, 16, 11);
                        break;
                    case IconKind.Search:
                        g.DrawEllipse(pen, 4, 4, 12, 12);
                        g.DrawLine(pen, 14.5f, 14.5f, 20, 20);
                        break;
                    case IconKind.Speaker:
                        g.FillPolygon(brush, new PointF[] { new PointF(3, 9), new PointF(6, 9), new PointF(11, 5), new PointF(11, 19), new PointF(6, 15), new PointF(3, 15) });
                        g.DrawArc(pen, 11.5f, 8.5f, 7, 7, -55, 110);
                        g.DrawArc(pen, 11, 5, 11, 14, -55, 110);
                        break;
                    case IconKind.Close:
                        g.DrawLine(pen, 6, 6, 18, 18);
                        g.DrawLine(pen, 18, 6, 6, 18);
                        break;
                    case IconKind.Hash:
                        g.DrawLine(pen, 9, 4, 7, 20);
                        g.DrawLine(pen, 17, 4, 15, 20);
                        g.DrawLine(pen, 4, 9, 20, 9);
                        g.DrawLine(pen, 4, 15, 20, 15);
                        break;
                    case IconKind.Check:
                        g.DrawLines(pen, new PointF[] { new PointF(5, 12.5f), new PointF(10, 17.5f), new PointF(19, 7) });
                        break;
                    case IconKind.Waves:
                    case IconKind.WavesOff:
                        float[] heights = { 6, 12, 16, 10, 6 };
                        int i;
                        for (i = 0; i < heights.Length; i++)
                            g.DrawLine(pen, 4 + i * 4, 12 - heights[i] / 2, 4 + i * 4, 12 + heights[i] / 2);
                        if (kind == IconKind.WavesOff)
                            Slash(g, color, scale);
                        break;
                    case IconKind.Signal:
                        FillRound(g, brush, 4, 15, 3, 5, 1.5f);
                        FillRound(g, brush, 9, 11, 3, 9, 1.5f);
                        FillRound(g, brush, 14, 7, 3, 13, 1.5f);
                        FillRound(g, brush, 19, 3, 3, 17, 1.5f);
                        break;
                    case IconKind.Download:
                        g.DrawLine(pen, 12, 4, 12, 15);
                        g.DrawLines(pen, new PointF[] { new PointF(7, 10.5f), new PointF(12, 15.5f), new PointF(17, 10.5f) });
                        g.DrawLine(pen, 5, 20, 19, 20);
                        break;
                    case IconKind.Palette:
                        g.DrawEllipse(pen, 3, 3, 18, 18);
                        g.FillEllipse(brush, 7, 7, 3, 3);
                        g.FillEllipse(brush, 12, 6, 3, 3);
                        g.FillEllipse(brush, 15.5f, 10, 3, 3);
                        break;
                    case IconKind.Crown:
                        g.FillPolygon(brush, new PointF[] { new PointF(3, 8), new PointF(8, 12), new PointF(12, 5), new PointF(16, 12), new PointF(21, 8), new PointF(19, 18), new PointF(5, 18) });
                        break;
                }
            }
            g.Restore(state);
        }

        private static void Slash(Graphics g, Color color, float scale)
        {
            using (Pen pen = new Pen(color, 2.2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, 3.5f, 3.5f, 20.5f, 20.5f);
            }
        }

        private static void FillRound(Graphics g, Brush brush, float x, float y, float w, float h, float r)
        {
            using (GraphicsPath path = Ui.RoundRect(new RectangleF(x, y, w, h), r))
                g.FillPath(brush, path);
        }

        private static void DrawRound(Graphics g, Pen pen, float x, float y, float w, float h, float r)
        {
            using (GraphicsPath path = Ui.RoundRect(new RectangleF(x, y, w, h), r))
                g.DrawPath(pen, path);
        }
    }
}
