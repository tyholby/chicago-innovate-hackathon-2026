using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FamilyStudio.Revit;

/// <summary>
/// The Family Studio mark, drawn in code so no image files ship: an isometric box (a family,
/// in its simplest form) with its front face in cobalt, the one accent colour of the design system.
/// </summary>
internal static class RibbonIcon
{
    public static BitmapSource Render(int size)
    {
        var s = size / 32.0;
        Point P(double x, double y) => new(x * s, y * s);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var ink = new Pen(new SolidColorBrush(Color.FromRgb(0x1B, 0x1A, 0x17)), Math.Max(1, 1.6 * s)) { LineJoin = PenLineJoin.Round };
            var cobalt = new SolidColorBrush(Color.FromRgb(0x2B, 0x45, 0xCF));
            var paper = new SolidColorBrush(Color.FromRgb(0xFA, 0xF9, 0xF6));
            var shade = new SolidColorBrush(Color.FromRgb(0xDE, 0xDA, 0xD1));

            dc.DrawGeometry(paper, ink, Polygon(P(16, 4), P(28, 10), P(16, 16), P(4, 10)));   // top
            dc.DrawGeometry(cobalt, ink, Polygon(P(4, 10), P(16, 16), P(16, 29), P(4, 23)));  // front
            dc.DrawGeometry(shade, ink, Polygon(P(16, 16), P(28, 10), P(28, 23), P(16, 29))); // side
        }
        return Bitmap(visual, size);
    }

    /// <summary>View2Render's mark: the same box inside a viewport frame, a view about to be rendered.</summary>
    public static BitmapSource RenderView(int size)
    {
        var s = size / 32.0;
        Point P(double x, double y) => new(x * s, y * s);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var ink = new Pen(new SolidColorBrush(Color.FromRgb(0x1B, 0x1A, 0x17)), Math.Max(1, 1.6 * s)) { LineJoin = PenLineJoin.Round };
            var thin = new Pen(ink.Brush, Math.Max(1, 1.2 * s)) { LineJoin = PenLineJoin.Round };
            var cobalt = new SolidColorBrush(Color.FromRgb(0x2B, 0x45, 0xCF));
            var paper = new SolidColorBrush(Color.FromRgb(0xFA, 0xF9, 0xF6));
            var shade = new SolidColorBrush(Color.FromRgb(0xDE, 0xDA, 0xD1));

            dc.DrawGeometry(paper, ink, Polygon(P(2, 5), P(30, 5), P(30, 27), P(2, 27)));        // the viewport
            dc.DrawGeometry(paper, thin, Polygon(P(16, 8.5), P(23, 12), P(16, 15.5), P(9, 12)));  // top
            dc.DrawGeometry(cobalt, thin, Polygon(P(9, 12), P(16, 15.5), P(16, 23.5), P(9, 20))); // front
            dc.DrawGeometry(shade, thin, Polygon(P(16, 15.5), P(23, 12), P(23, 20), P(16, 23.5))); // side
        }
        return Bitmap(visual, size);
    }

    private static BitmapSource Bitmap(Visual visual, int size)
    {
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Geometry Polygon(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: true);
            context.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }
}
