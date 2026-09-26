using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Validation;

/// <summary>
/// Geometry rules every recipe must pass before any native work starts. All measurements come
/// from the transformed corners of every part, so a tilted backrest is measured as built.
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

    /// <summary>The eight corners of a part after its tilt about the X axis through its centre.</summary>
    public static IEnumerable<Vec3> Corners(RecipePart part)
    {
        var centreY = (part.MinM.Y + part.MaxM.Y) / 2;
        var centreZ = (part.MinM.Z + part.MaxM.Z) / 2;
        var radians = part.TiltDegrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        foreach (var x in new[] { part.MinM.X, part.MaxM.X })
        foreach (var y in new[] { part.MinM.Y, part.MaxM.Y })
        foreach (var z in new[] { part.MinM.Z, part.MaxM.Z })
        {
            var dy = y - centreY;
            var dz = z - centreZ;
            yield return new Vec3(x, centreY + dy * cos - dz * sin, centreZ + dy * sin + dz * cos);
        }
    }

    public static Box3 Bounds(IEnumerable<RecipePart> parts)
    {
        var corners = parts.SelectMany(Corners).ToArray();
        if (corners.Length == 0) throw new ArgumentException("A recipe needs at least one solid part.");
        return Box3.Of(corners);
    }

    /// <summary>Validates one recipe against its brief and returns the measured bounds.</summary>
    public static Box3 Validate(FamilyRecipe recipe, StudioBrief brief)
    {
        var asset = brief.Assets.SingleOrDefault(a => a.Id == recipe.AssetId)
            ?? throw StudioValidationException.Single("unknown_asset", "assetId", "The recipe names an item that is not in the brief.", recipe.AssetId);
        var issues = new List<ValidationIssue>();
        void Add(string code, string property, string message, string? part = null, object? expected = null, object? actual = null, double? tolerance = null) =>
            issues.Add(new(code, asset.Id, null, part, property, expected, actual, tolerance, $"{asset.Name}: {message}"));

        if (recipe.Parts is null || recipe.Parts.Length is < 1 or > StudioLimits.MaxParts)
            throw StudioValidationException.Single("part_count", "parts", $"Use 1 to {StudioLimits.MaxParts} solid parts.", asset.Id);
        if (!IsBox(recipe.EnvelopeMinM, recipe.EnvelopeMaxM))
            throw StudioValidationException.Single("envelope", "envelope", "The envelope must have finite, positive extents.", asset.Id);
        if (recipe.Parts.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != recipe.Parts.Length)
            Add("duplicate_part", "parts.name", "Part names must be unique.");

        foreach (var part in recipe.Parts)
        {
            if (string.IsNullOrWhiteSpace(part.Name))
            {
                Add("part_name", "parts.name", "Every part needs a name.");
                continue;
            }
            if (!IsBox(part.MinM, part.MaxM))
            {
                Add("part_box", "parts.minM/maxM", "Each part needs maxM greater than minM on every axis.", part.Name);
                continue;
            }
            if (!double.IsFinite(part.TiltDegrees) || Math.Abs(part.TiltDegrees) > 90)
                Add("part_tilt", "parts.tiltDegrees", "Tilt must be between -90 and 90 degrees.", part.Name, "[-90, 90]", part.TiltDegrees);
            if (!brief.Materials.Any(m => m.Id == part.MaterialId))
                Add("unknown_material", "parts.materialId", "Use a material ID from the brief.", part.Name,
                    brief.Materials.Select(m => m.Id).ToArray(), part.MaterialId);
            if (part.ComponentId is not null && !asset.Components.Any(c => c.Id == part.ComponentId))
                Add("unknown_component", "parts.componentId", "Use a component ID from the brief, or null.", part.Name,
                    asset.Components.Select(c => c.Id).ToArray(), part.ComponentId);
            if (part.IsFloorSupport && Math.Abs(Bounds(new[] { part }).Min.Z) > FloorToleranceM + Epsilon)
                Add("floor_support", "parts.minM.z", "A floor-support part must touch Z = 0.", part.Name, 0d, Bounds(new[] { part }).Min.Z, FloorToleranceM);
        }
        if (issues.Count > 0) throw new StudioValidationException(issues);

        var measured = Bounds(recipe.Parts);
        var outside = recipe.Parts.FirstOrDefault(part => Corners(part).Any(corner =>
            Enumerable.Range(0, 3).Any(axis =>
                corner[axis] < recipe.EnvelopeMinM[axis] - ContainmentToleranceM - Epsilon ||
                corner[axis] > recipe.EnvelopeMaxM[axis] + ContainmentToleranceM + Epsilon)));
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
        if (asset.FloorStanding && (Math.Abs(measured.Min.Z) > FloorToleranceM + Epsilon || !recipe.Parts.Any(p => p.IsFloorSupport)))
            Add("floor_contact", "parts.isFloorSupport", "Mark the parts that stand on the floor and make them touch Z = 0.");

        foreach (var component in asset.Components)
        {
            var tagged = recipe.Parts.Where(p => p.ComponentId == component.Id).ToArray();
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
