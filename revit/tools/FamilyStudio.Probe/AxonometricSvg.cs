using System.Globalization;
using System.Text;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Probe;

/// <summary>
/// Draws recipes as a simple isometric drawing: shaded faces in each part's material
/// color, hairline edges, sorted back to front. Enough to judge a silhouette without Revit.
/// </summary>
internal static class AxonometricSvg
{
    public static string Render(IReadOnlyList<(FamilyRecipe Recipe, Placement Placement)> items, StudioBrief brief, string title)
    {
        var faces = new List<(double Depth, (double X, double Y)[] Points, string Fill)>();
        foreach (var (recipe, placement) in items)
        foreach (var part in recipe.Parts)
        {
            var corners = RecipeRules.Corners(part).Select(c => PlacementRules.ToWorld(c, placement)).ToArray();
            var material = brief.Materials.FirstOrDefault(m => m.Id == part.MaterialId);
            var rgb = material?.Rgb ?? new[] { 180, 180, 180 };
            // Corner order from RecipeRules.Corners: x-major, then y, then z.
            int C(int x, int y, int z) => x * 4 + y * 2 + z;
            var quads = new (int[] Index, double Shade)[]
            {
                (new[] { C(0,0,1), C(1,0,1), C(1,1,1), C(0,1,1) }, 1.00), // top
                (new[] { C(0,0,0), C(1,0,0), C(1,0,1), C(0,0,1) }, 0.78), // front (-Y)
                (new[] { C(1,0,0), C(1,1,0), C(1,1,1), C(1,0,1) }, 0.62), // right (+X)
                (new[] { C(0,1,0), C(0,0,0), C(0,0,1), C(0,1,1) }, 0.70), // left (-X)
                (new[] { C(1,1,0), C(0,1,0), C(0,1,1), C(1,1,1) }, 0.55)  // back (+Y)
            };
            foreach (var (index, shade) in quads)
            {
                var points = index.Select(i => corners[i]).ToArray();
                // The camera looks from the front-right (+X, -Y) and above; larger is farther away.
                var depth = points.Average(p => -p.X + p.Y - p.Z);
                faces.Add((depth, points.Select(Project).ToArray(), Rgb(rgb, shade)));
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
            svg.Append($"<polygon points=\"{d}\" fill=\"{face.Fill}\" stroke=\"#1C1C1A\" stroke-width=\"0.6\" stroke-linejoin=\"round\"/>");
        }
        svg.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{pad}\" y=\"{height - 18:0}\" font-family=\"Bahnschrift, 'DIN Alternate', Helvetica, sans-serif\" font-size=\"13\" letter-spacing=\"1.2\" fill=\"#1C1C1A\">{Escape(title.ToUpperInvariant())}</text>");
        svg.Append("</svg>");
        return svg.ToString();
    }

    // Isometric view along (-1, 1, -1): screen right is (1, 1, 0)/sqrt 2, screen up is (-1, 1, 2)/sqrt 6.
    private static (double X, double Y) Project(Vec3 p) => ((p.X + p.Y) / Math.Sqrt(2), (-p.X + p.Y + 2 * p.Z) / Math.Sqrt(6));

    private static string Rgb(int[] rgb, double shade) =>
        $"#{(int)(rgb[0] * shade):X2}{(int)(rgb[1] * shade):X2}{(int)(rgb[2] * shade):X2}";

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
