using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Validation;

/// <summary>
/// Geometry rules every recipe must pass before any native work starts. All measurements come
/// from the exact geometry of every part (tilted corners, circles, arcs, rounded edges and mirror
/// copies), so each part is measured as it will be built.
/// </summary>
public static class RecipeRules
{
    /// <summary>Allowed gap between declared and measured bounds, and containment slack.</summary>
    public const double ContainmentToleranceM = 0.002;

    /// <summary>Floor supports must touch Z = 0 within this distance.</summary>
    public const double FloorToleranceM = 0.001;

    private const double Epsilon = 1e-9;

    /// <summary>Estimated dimensions allow max(10 mm, 5 percent) per axis.</summary>
    public static double EstimatedToleranceM(double target) => Math.Max(0.010, target * 0.05);

    public static double ToleranceFor(double target, bool confirmed) =>
        confirmed ? Dimensions.ConfirmedToleranceM : EstimatedToleranceM(target);

    /// <summary>The eight corners of a box part after its tilt about the X axis through its centre.</summary>
    public static IEnumerable<Vec3> Corners(RecipePart part) => Shapes.BoxCorners(part.MinM!, part.MaxM!, part.TiltDegrees);

    /// <summary>The exact bounds of valid parts, mirror copies included.</summary>
    public static Box3 Bounds(IEnumerable<RecipePart> parts)
    {
        Box3? bounds = null;
        foreach (var part in Shapes.Expand(parts))
        {
            var box = Shapes.Bounds(part);
            bounds = bounds is null ? box : bounds.Union(box);
        }
        return bounds ?? throw new ArgumentException("A recipe needs at least one solid part.");
    }

    /// <summary>
    /// Checks that every part is a well-formed shape before anything is measured: the fields its
    /// shape needs, finite numbers, sizes of at least 2 mm and simple profile outlines. Also checks
    /// part names (mirror copies included) and the part count.
    /// </summary>
    public static void ValidateShapes(IReadOnlyList<RecipePart> parts, StudioBrief brief, string assetId)
    {
        var asset = brief.Assets.FirstOrDefault(a => a.Id == assetId);
        var label = asset?.Name ?? assetId;
        var issues = new List<ValidationIssue>();
        void Add(string code, string property, string message, string? part = null, object? expected = null, object? actual = null) =>
            issues.Add(new(code, assetId, null, part, property, expected, actual, null, $"{label}: {message}"));

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part.Name))
            {
                Add("part_name", "parts.name", "Every part needs a name.");
                continue;
            }
            var problem = ShapeProblem(part);
            if (problem is not null) Add(problem.Value.Code, "parts." + problem.Value.Property, problem.Value.Message, part.Name);
        }
        if (issues.Count == 0)
        {
            var names = Shapes.Expand(parts).Select(p => p.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                Add("duplicate_part", "parts.name", "Part names must be unique, including the \" mirrored\" copies the plugin adds.");
            if (names.Length > StudioLimits.MaxParts)
                Add("part_count", "parts", $"Use at most {StudioLimits.MaxParts} solids, counting mirror copies.", null, StudioLimits.MaxParts, names.Length);
        }
        if (issues.Count > 0) throw new StudioValidationException(issues);
    }

    private static (string Code, string Property, string Message)? ShapeProblem(RecipePart part)
    {
        static bool Finite(params double[] values) => values.All(double.IsFinite);
        var min = Shapes.MinSizeM;
        if (!Finite(part.TiltDegrees, part.RadiusM, part.EndRadiusM ?? 0, part.FromM ?? 0, part.ToM ?? 0) ||
            part.MinM is { IsFinite: false } || part.MaxM is { IsFinite: false } || part.PointsM?.Any(p => p is null || !p.IsFinite) == true)
            return ("not_finite", "name", "Every number must be finite.");
        switch (part.Shape)
        {
            case PartShapes.Box:
                if (!IsBox(part.MinM, part.MaxM))
                    return ("part_box", "minM/maxM", "A box needs minM and maxM, with maxM greater than minM on every axis.");
                if (Math.Abs(part.TiltDegrees) > 90)
                    return ("part_tilt", "tiltDegrees", "Tilt must be between -90 and 90 degrees.");
                var smallest = Math.Min(part.MaxM!.X - part.MinM!.X, Math.Min(part.MaxM.Y - part.MinM.Y, part.MaxM.Z - part.MinM.Z));
                if (part.RadiusM < 0 || part.RadiusM > smallest / 2 + 1e-9)
                    return ("box_radius", "radiusM", $"A box's edge radius must be between 0 and half its smallest side ({smallest / 2 * 1000:0.#} mm).");
                return null;
            case PartShapes.Cylinder:
                if (part.PointsM is not { Length: 2 })
                    return ("cylinder_points", "pointsM", "A cylinder needs exactly two points: the centres of its two ends.");
                if (V.Length(part.PointsM[1] - part.PointsM[0]) < min)
                    return ("cylinder_length", "pointsM", "A cylinder's two end centres must be at least 2 mm apart.");
                if (part.RadiusM < min || Shapes.EndRadius(part) < min)
                    return ("cylinder_radius", "radiusM/endRadiusM", "A cylinder's radii must be at least 2 mm (use a sphere or tube for a point).");
                return null;
            case PartShapes.Sphere:
                if (part.PointsM is not { Length: 1 })
                    return ("sphere_points", "pointsM", "A sphere needs exactly one point: its centre.");
                return part.RadiusM < min ? ("sphere_radius", "radiusM", "A sphere's radius must be at least 2 mm.") : null;
            case PartShapes.Tube:
                if (part.PointsM is not { Length: >= 2 and <= 12 })
                    return ("tube_points", "pointsM", "A tube needs 2 to 12 path points.");
                for (var i = 0; i + 1 < part.PointsM.Length; i++)
                    if (V.Length(part.PointsM[i + 1] - part.PointsM[i]) < min)
                        return ("tube_points", "pointsM", "Consecutive tube points must be at least 2 mm apart.");
                return part.RadiusM < min ? ("tube_radius", "radiusM", "A tube's radius must be at least 2 mm.") : null;
            case PartShapes.Profile:
                return ProfileProblem(part);
            default:
                return ("unknown_shape", "shape", $"Use one of these shapes: {string.Join(", ", PartShapes.All)}.");
        }
    }

    private static (string Code, string Property, string Message)? ProfileProblem(RecipePart part)
    {
        if (part.Plane is null || !ProfilePlanes.All.Contains(part.Plane))
            return ("profile_plane", "plane", "A profile's plane must be front, side or plan.");
        if (part.OutlineM is not { Length: >= 3 and <= 32 } || part.OutlineM.Any(o => o is not { Length: 2 } || !o.All(double.IsFinite)))
            return ("profile_outline", "outlineM", "A profile's outline needs 3 to 32 [u, v] points.");
        if (part.FromM is null || part.ToM is null || part.ToM - part.FromM < Shapes.MinSizeM)
            return ("profile_extent", "fromM/toM", "A profile needs fromM and toM, with toM at least 2 mm greater than fromM.");
        if (part.RadiusM < 0)
            return ("profile_radius", "radiusM", "A profile's corner radius cannot be negative.");
        var points = Shapes.Clean(part.OutlineM.Select(o => new P2(o[0], o[1])).ToList());
        if (points.Count < 3 || Math.Abs(Shapes.SignedArea(points)) < 1e-6)
            return ("profile_outline", "outlineM", "A profile's outline must enclose an area (at least three distinct, non-collinear points).");
        if (SelfIntersects(points))
            return ("profile_outline", "outlineM", "A profile's outline must not cross itself. List the points in order around the shape.");
        return null;
    }

    private static bool SelfIntersects(IReadOnlyList<P2> points)
    {
        var n = points.Count;
        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            if (j == i + 1 || (i == 0 && j == n - 1)) continue; // neighbouring edges share a corner
            if (SegmentsTouch(points[i], points[(i + 1) % n], points[j], points[(j + 1) % n])) return true;
        }
        return false;
    }

    private static bool SegmentsTouch(P2 a, P2 b, P2 c, P2 d)
    {
        static int Side(P2 p, P2 q, P2 r)
        {
            var cross = P2.Cross(q - p, r - p);
            return Math.Abs(cross) < 1e-12 ? 0 : Math.Sign(cross);
        }
        static bool Between(P2 p, P2 q, P2 r) =>
            Math.Min(p.U, q.U) - 1e-12 <= r.U && r.U <= Math.Max(p.U, q.U) + 1e-12 &&
            Math.Min(p.V, q.V) - 1e-12 <= r.V && r.V <= Math.Max(p.V, q.V) + 1e-12;
        int s1 = Side(a, b, c), s2 = Side(a, b, d), s3 = Side(c, d, a), s4 = Side(c, d, b);
        if (s1 * s2 < 0 && s3 * s4 < 0) return true; // a proper crossing
        return (s1 == 0 && Between(a, b, c)) || (s2 == 0 && Between(a, b, d)) ||
               (s3 == 0 && Between(c, d, a)) || (s4 == 0 && Between(c, d, b));
    }

    /// <summary>Validates one recipe against its brief and returns the measured bounds.</summary>
    public static Box3 Validate(FamilyRecipe recipe, StudioBrief brief)
    {
        var asset = brief.Assets.SingleOrDefault(a => a.Id == recipe.AssetId)
            ?? throw StudioValidationException.Single("unknown_asset", "assetId", "The recipe names an item that is not in the brief.", recipe.AssetId);
        var issues = new List<ValidationIssue>();
        void Add(string code, string property, string message, string? part = null, object? expected = null, object? actual = null, double? tolerance = null) =>
            issues.Add(new(code, asset.Id, null, part, property, expected, actual, tolerance, $"{asset.Name}: {message}"));

        if (recipe.Parts is null || recipe.Parts.Length < 1 || recipe.Parts.Any(p => p is null))
            throw StudioValidationException.Single("part_count", "parts", $"Use 1 to {StudioLimits.MaxParts} solids.", asset.Id);
        if (!IsBox(recipe.EnvelopeMinM, recipe.EnvelopeMaxM))
            throw StudioValidationException.Single("envelope", "envelope", "The envelope must have finite, positive extents.", asset.Id);
        ValidateShapes(recipe.Parts, brief, recipe.AssetId);
        var solids = Shapes.Expand(recipe.Parts).ToArray();

        foreach (var part in solids)
        {
            if (!brief.Materials.Any(m => m.Id == part.MaterialId))
                Add("unknown_material", "parts.materialId", "Use a material ID from the brief.", part.Name,
                    brief.Materials.Select(m => m.Id).ToArray(), part.MaterialId);
            if (part.ComponentId is not null && !asset.Components.Any(c => c.Id == part.ComponentId))
                Add("unknown_component", "parts.componentId", "Use a component ID from the brief, or null.", part.Name,
                    asset.Components.Select(c => c.Id).ToArray(), part.ComponentId);
            if (part.IsFloorSupport && Math.Abs(Shapes.Bounds(part).Min.Z) > FloorToleranceM + Epsilon)
                Add("floor_support", "parts.isFloorSupport", "A floor-support part's lowest point must touch Z = 0.", part.Name, 0d,
                    Math.Round(Shapes.Bounds(part).Min.Z, 4), FloorToleranceM);
        }
        if (issues.Count > 0) throw new StudioValidationException(issues);

        var measured = Bounds(recipe.Parts);
        var outside = solids.FirstOrDefault(part =>
        {
            var box = Shapes.Bounds(part);
            return Enumerable.Range(0, 3).Any(axis =>
                box.Min[axis] < recipe.EnvelopeMinM[axis] - ContainmentToleranceM - Epsilon ||
                box.Max[axis] > recipe.EnvelopeMaxM[axis] + ContainmentToleranceM + Epsilon);
        });
        if (outside is not null)
            Add("outside_envelope", "parts", "Geometry falls outside the declared envelope.", outside.Name);
        for (var axis = 0; axis < 3; axis++)
            if (Math.Abs(measured.Min[axis] - recipe.EnvelopeMinM[axis]) > ContainmentToleranceM + Epsilon ||
                Math.Abs(measured.Max[axis] - recipe.EnvelopeMaxM[axis]) > ContainmentToleranceM + Epsilon)
            {
                Add("envelope_mismatch", "envelope", "The declared envelope does not match the measured geometry.");
                break;
            }

        CheckSize(issues, measured.Size, asset.SizeM, asset, asset.Name, asset.DimensionsConfirmed);
        if (measured.Min.Z < -FloorToleranceM - Epsilon)
            Add("below_origin", "parts.minM.z", "Geometry extends below the family's base at Z = 0.", null, 0d, measured.Min.Z, FloorToleranceM);
        if (asset.FloorStanding && (Math.Abs(measured.Min.Z) > FloorToleranceM + Epsilon || !solids.Any(p => p.IsFloorSupport)))
            Add("floor_contact", "parts.isFloorSupport", "Mark the parts that stand on the floor and make them touch Z = 0.");

        foreach (var component in asset.Components)
        {
            var tagged = solids.Where(p => p.ComponentId == component.Id).ToArray();
            if (tagged.Length == 0)
                Add("missing_component", "parts.componentId", $"Tag the parts that form component {component.Id} ({component.Description}).");
            else
                CheckSize(issues, Bounds(tagged).Size, component.SizeM, asset, $"{component.Description} ({component.Id})", confirmed: false);
        }

        if (issues.Count > 0) throw new StudioValidationException(issues);
        return measured;
    }

    /// <summary>Validates a batch of recipes and resolved placements before it goes to Revit.</summary>
    public static void ValidateProposal(BuildProposal proposal, StudioBrief brief, bool initialBuild)
    {
        brief.Validate();
        if (proposal.Recipes is null || proposal.Placements is null)
            throw new ArgumentException("A proposal needs recipes and placements.");
        if (proposal.Recipes.Select(r => r.AssetId).Distinct(StringComparer.Ordinal).Count() != proposal.Recipes.Length)
            throw new ArgumentException("A proposal may hold only one recipe per item.");
        foreach (var recipe in proposal.Recipes) Validate(recipe, brief);
        if (initialBuild && !proposal.Recipes.Select(r => r.AssetId).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(brief.Assets.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal)))
            throw new ArgumentException("The first build must include a recipe for every item in the brief.");

        if (proposal.Placements.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != proposal.Placements.Length)
            throw new ArgumentException("Placement keys must be unique.");
        foreach (var p in proposal.Placements)
        {
            if (!brief.Assets.Any(a => a.Id == p.AssetId) || string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 64 ||
                p.PositionM is null || !p.PositionM.IsFinite || !double.IsFinite(p.RotationDegrees) ||
                Math.Abs(p.PositionM.X) > StudioLimits.RoomHalfWidthM || Math.Abs(p.PositionM.Y) > StudioLimits.RoomHalfDepthM ||
                p.PositionM.Z < -FloorToleranceM || p.PositionM.Z > StudioLimits.RoomHeightM)
                throw new ArgumentException($"Placement {p.Key} is invalid or outside the preview room.");
            if (p.SupportKey == p.Key) throw new ArgumentException($"Placement {p.Key} cannot support itself.");
        }
        if (!initialBuild) return;
        foreach (var asset in brief.Assets)
            if (proposal.Placements.Count(p => p.AssetId == asset.Id) != asset.Quantity)
                throw new ArgumentException($"{asset.Name}: the layout places {proposal.Placements.Count(p => p.AssetId == asset.Id)} instances; the brief asks for {asset.Quantity}.");
    }

    private static void CheckSize(List<ValidationIssue> issues, Vec3 actual, Vec3 target, AssetBrief asset, string label, bool confirmed)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var tolerance = ToleranceFor(target[axis], confirmed);
            if (Math.Abs(actual[axis] - target[axis]) > tolerance + Epsilon)
                issues.Add(new("dimension_mismatch", asset.Id, null, null, "sizeM." + "xyz"[axis], Math.Round(target[axis], 4),
                    Math.Round(actual[axis], 4), tolerance,
                    $"{label}: measured {"width depth height".Split(' ')[axis]} {actual[axis] * 1000:0} mm differs from the brief's {target[axis] * 1000:0} mm."));
        }
    }

    private static bool IsBox(Vec3? min, Vec3? max) =>
        min is not null && max is not null && min.IsFinite && max.IsFinite && max.X > min.X && max.Y > min.Y && max.Z > min.Z;
}
