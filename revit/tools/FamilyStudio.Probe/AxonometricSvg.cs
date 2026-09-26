using System.Globalization;
using System.Text;
using FamilyStudio.Core.Model;

namespace FamilyStudio.Probe;

/// <summary>
/// Draws recipes as an isometric drawing: every part's surface mesh (rounded edges, cylinders,
/// tubes and profiles included), shaded by a fixed light in its material color and sorted back to
/// front. Flat faces get hairline edges; curved facets are drawn without them. Enough to judge a
/// silhouette without Revit.
/// </summary>
internal static class AxonometricSvg
{
    // The camera looks from the front-right (+X, -Y) and above; the light comes from the front-left and above.
    private static readonly Vec3 Camera = Unit(new Vec3(1, -1, 1));
    private static readonly Vec3 Light = Unit(new Vec3(-0.35, -0.55, 0.76));

    public static string Render(IReadOnlyList<(FamilyRecipe Recipe, Placement Placement)> items, StudioBrief brief, string title)
    {
        var faces = new List<(double Depth, (double X, double Y)[] Points, string Fill, bool Crisp)>();
        foreach (var (recipe, placement) in items)
        foreach (var part in recipe.Solids)
        {
            var rgb = brief.Materials.FirstOrDefault(m => m.Id == part.MaterialId)?.Rgb ?? new[] { 180, 180, 180 };
            foreach (var face in Shapes.Mesh(part, placement.RotationDegrees, placement.PositionM))
            {
                if (Dot(face.Normal, Camera) <= 1e-9) continue; // facing away
                var shade = 0.5 + 0.5 * Math.Max(0, Dot(face.Normal, Light));
                // Larger is farther from the camera.
                var depth = face.Points.Average(p => -p.X + p.Y - p.Z);
                faces.Add((depth, face.Points.Select(Project).ToArray(), Rgb(rgb, shade), face.Crisp));
            }
        }

        var all = faces.SelectMany(f => f.Points).ToArray();
        var minX = all.Min(p => p.X); var maxX = all.Max(p => p.X);
        var minY = all.Min(p => p.Y); var maxY = all.Max(p => p.Y);
        var scale = 640 / Math.Max(maxX - minX, maxY - minY);
        const double pad = 48;
        double W(double v) => Math.Round((v - minX) * scale + pad, 2);
        double H(double v) => Math.Round((maxY - v) * scale + pad, 2);
        var width = (maxX - minX) * scale + 2 * pad;
        var height = (maxY - minY) * scale + 2 * pad + 40;

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width:0}\" height=\"{height:0}\" viewBox=\"0 0 {width:0} {height:0}\">");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"#F4F2EE\"/>");
        foreach (var face in faces.OrderByDescending(f => f.Depth))
        {
            var d = string.Join(" ", face.Points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{W(p.X)},{H(p.Y)}")));
            // Curved facets are stroked in their own color, which closes hairline gaps between them.
            var (stroke, strokeWidth) = face.Crisp ? ("#1C1C1A", "0.6") : (face.Fill, "0.4");
            svg.Append($"<polygon points=\"{d}\" fill=\"{face.Fill}\" stroke=\"{stroke}\" stroke-width=\"{strokeWidth}\" stroke-linejoin=\"round\"/>");
        }
        svg.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{pad}\" y=\"{height - 18:0}\" font-family=\"Bahnschrift, 'DIN Alternate', Helvetica, sans-serif\" font-size=\"13\" letter-spacing=\"1.2\" fill=\"#1C1C1A\">{Escape(title.ToUpperInvariant())}</text>");
        svg.Append("</svg>");
        return svg.ToString();
    }

    // Isometric view along (-1, 1, -1): screen right is (1, 1, 0)/sqrt 2, screen up is (-1, 1, 2)/sqrt 6.
    private static (double X, double Y) Project(Vec3 p) => ((p.X + p.Y) / Math.Sqrt(2), (-p.X + p.Y + 2 * p.Z) / Math.Sqrt(6));

    private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Vec3 Unit(Vec3 a)
    {
        var length = Math.Sqrt(Dot(a, a));
        return new Vec3(a.X / length, a.Y / length, a.Z / length);
    }

    private static string Rgb(int[] rgb, double shade) =>
        $"#{(int)(rgb[0] * shade):X2}{(int)(rgb[1] * shade):X2}{(int)(rgb[2] * shade):X2}";

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
