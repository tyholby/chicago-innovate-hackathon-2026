using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace FamilyStudio.Rhino
{
    /// <summary>The Family Studio mark (an isometric box with a cobalt front) as a panel icon.</summary>
    internal static class PanelIcon
    {
        public static Icon Create()
        {
            try
            {
                using var bitmap = new Bitmap(32, 32);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(System.Drawing.Color.Transparent);
                    using var ink = new Pen(System.Drawing.Color.FromArgb(0x1B, 0x1A, 0x17), 1.6f) { LineJoin = LineJoin.Round };
                    Face(g, ink, System.Drawing.Color.FromArgb(0xFA, 0xF9, 0xF6), new PointF(16, 4), new PointF(28, 10), new PointF(16, 16), new PointF(4, 10));
                    Face(g, ink, System.Drawing.Color.FromArgb(0x2B, 0x45, 0xCF), new PointF(4, 10), new PointF(16, 16), new PointF(16, 29), new PointF(4, 23));
                    Face(g, ink, System.Drawing.Color.FromArgb(0xDE, 0xDA, 0xD1), new PointF(16, 16), new PointF(28, 10), new PointF(28, 23), new PointF(16, 29));
                }
                return Icon.FromHandle(bitmap.GetHicon());
            }
            catch (Exception)
            {
                return SystemIcons.Application;
            }
        }

        private static void Face(Graphics g, Pen ink, System.Drawing.Color fill, params PointF[] points)
        {
            using var brush = new SolidBrush(fill);
            g.FillPolygon(brush, points);
            g.DrawPolygon(ink, points);
        }
    }
}
