using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Validation;

/// <summary>Where two instances' bounding boxes intersect. A hint for review, never proof of a collision.</summary>
public sealed record OverlapCandidate(string FirstKey, string SecondKey, Vec3 IntersectionM);

/// <summary>
/// Physical plausibility rules for an arrangement: inside the room, grounded, standing on a real
/// horizontal part of whatever supports it, with a base that actually fits on that part.
/// </summary>
public static class PlacementRules
{
    private const double Epsilon = 1e-8;

    public static Vec3 Rotate(Vec3 p, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new(p.X * Math.Cos(radians) - p.Y * Math.Sin(radians), p.X * Math.Sin(radians) + p.Y * Math.Cos(radians), p.Z);
    }

    public static Vec3 ToWorld(Vec3 local, Placement placement) => Rotate(local, placement.RotationDegrees) + placement.PositionM;

    public static Box3 WorldBounds(FamilyRecipe recipe, Placement placement) =>
        recipe.Solids.Select(part => Shapes.Bounds(part, placement.RotationDegrees, placement.PositionM)).Aggregate((a, b) => a.Union(b));

    /// <summary>
    /// The flat tops of level boxes and upright cylinders, largest first. These are the only valid
    /// support surfaces; each is a horizontal rectangle at the height of its MaxM.Z.
    /// </summary>
    public static IEnumerable<SupportSurface> Surfaces(FamilyRecipe recipe) => recipe.Solids
        .SelectMany(Shapes.Surfaces)
        .OrderByDescending(s => (s.MaxM.X - s.MinM.X) * (s.MaxM.Y - s.MinM.Y))
        .ThenByDescending(s => s.MaxM.Z);

    public static SupportSurface Surface(FamilyRecipe recipe, string name) =>
        Surfaces(recipe).SingleOrDefault(p => p.Name == name)
        ?? throw StudioValidationException.Single("unknown_surface", "supportPartName",
            $"\"{name}\" is not a horizontal part of {recipe.AssetId}. Choose one from the supplied surface list.",
            recipe.AssetId, partName: name);

    /// <summary>Validates a complete arrangement and returns bounding-box overlaps worth a reviewer's attention.</summary>
    public static OverlapCandidate[] Validate(BuildProposal proposal, StudioBrief brief)
    {
        RecipeRules.ValidateProposal(proposal, brief, initialBuild: true);
        var recipes = proposal.Recipes.ToDictionary(r => r.AssetId, StringComparer.Ordinal);
        var placements = proposal.Placements.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var bounds = new Dictionary<string, Box3>(StringComparer.Ordinal);
        var issues = new List<ValidationIssue>();

        foreach (var p in proposal.Placements)
        {
            var recipe = recipes[p.AssetId];
            var box = WorldBounds(recipe, p);
            bounds[p.Key] = box;
            CheckRoom(issues, p, box);

            if (p.SupportKey is null)
            {
                if (p.SupportPartName is not null)
                    issues.Add(Issue(p, "unknown_support", "supportKey", "A support part needs the placement that owns it."));
                if (brief.Assets.Single(a => a.Id == p.AssetId).FloorStanding && Math.Abs(box.Min.Z) > RecipeRules.FloorToleranceM + Epsilon)
                    issues.Add(Issue(p, "floor_contact", "bottomZ", "Ground this floor-standing item, or place it on a named surface.", 0d, box.Min.Z, RecipeRules.FloorToleranceM));
                continue;
            }

            if (!SupportChainIsValid(p, placements))
            {
                issues.Add(Issue(p, "support_graph", "supportKey", "The support chain names an unknown placement or loops back on itself."));
                continue;
            }
            if (p.SupportPartName is null)
            {
                issues.Add(Issue(p, "missing_surface", "supportPartName", "Name the horizontal part this item stands on."));
                continue;
            }

            var parent = placements[p.SupportKey];
            SupportSurface surface;
            try { surface = Surface(recipes[parent.AssetId], p.SupportPartName); }
            catch (StudioValidationException e)
            {
                issues.AddRange(e.Issues.Select(i => i with { PlacementKey = p.Key }));
                continue;
            }

            var top = parent.PositionM.Z + surface.MaxM.Z;
            if (Math.Abs(box.Min.Z - top) > RecipeRules.FloorToleranceM + Epsilon)
                issues.Add(Issue(p, "surface_contact", "bottomZ", "The item must rest on its named surface.", top, box.Min.Z, RecipeRules.FloorToleranceM));
            CheckFootprint(issues, p, recipe, parent, surface);
        }
        if (issues.Count > 0) throw new StudioValidationException(issues);
        return Overlaps(proposal.Placements, bounds);
    }

    /// <summary>Seeds a repair contract from resolved placements when no intents were kept.</summary>
    public static PlacementIntentPlan ToIntents(BuildProposal proposal)
    {
        var placements = proposal.Placements.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var recipes = proposal.Recipes.ToDictionary(r => r.AssetId, StringComparer.Ordinal);
        return new(proposal.Placements.Select(p =>
        {
            if (p.SupportKey is null) return PlacementIntent.AbsoluteFrom(p);
            var parent = placements[p.SupportKey];
            var surface = p.SupportPartName is not null
                ? Surface(recipes[parent.AssetId], p.SupportPartName)
                : Surfaces(recipes[parent.AssetId]).FirstOrDefault(s => Math.Abs(parent.PositionM.Z + s.MaxM.Z - p.PositionM.Z) <= 0.002)
                  ?? throw new ArgumentException($"Placement {p.Key} has no matching support surface.");
            var offset = Rotate(new(p.PositionM.X - parent.PositionM.X, p.PositionM.Y - parent.PositionM.Y, 0), -parent.RotationDegrees);
            return new PlacementIntent(p.Key, p.AssetId, PlacementModes.Surface, offset, p.RotationDegrees - parent.RotationDegrees,
                p.SupportKey, surface.Name, null, null, null, null);
        }).ToArray());
    }

    private static void CheckRoom(List<ValidationIssue> issues, Placement p, Box3 box)
    {
        var minimum = new[] { -StudioLimits.RoomHalfWidthM, -StudioLimits.RoomHalfDepthM, 0 };
        var maximum = new[] { StudioLimits.RoomHalfWidthM, StudioLimits.RoomHalfDepthM, StudioLimits.RoomHeightM };
        for (var axis = 0; axis < 3; axis++)
            if (box.Min[axis] < minimum[axis] - RecipeRules.ContainmentToleranceM - Epsilon ||
                box.Max[axis] > maximum[axis] + RecipeRules.ContainmentToleranceM + Epsilon)
                issues.Add(Issue(p, "room_containment", "worldBounds." + "xyz"[axis],
                    "The item's geometry must stay inside the 8 x 6 m preview room.",
                    new[] { minimum[axis], maximum[axis] }, new[] { Math.Round(box.Min[axis], 4), Math.Round(box.Max[axis], 4) },
                    RecipeRules.ContainmentToleranceM));
    }

    private static bool SupportChainIsValid(Placement p, Dictionary<string, Placement> placements)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { p.Key };
        for (var key = p.SupportKey; key is not null; key = placements[key].SupportKey)
            if (!seen.Add(key) || !placements.ContainsKey(key)) return false;
        return true;
    }

    private static void CheckFootprint(List<ValidationIssue> issues, Placement p, FamilyRecipe recipe, Placement parent, SupportSurface surface)
    {
        // The base (floor-support parts, or everything when none are marked) must sit on the
        // surface. Upper geometry may overhang, like a lampshade wider than the table.
        var solids = recipe.Solids.ToArray();
        var baseParts = solids.Where(part => part.IsFloorSupport).ToArray();
        var bottom = RecipeRules.Bounds(recipe.Parts).Min.Z;
        var contacts = (baseParts.Length > 0 ? baseParts : solids).SelectMany(part => Shapes.ContactPoints(part))
            .Where(c => Math.Abs(c.Z - bottom) <= RecipeRules.FloorToleranceM + Epsilon).Distinct().ToArray();
        if (contacts.Length < 3)
        {
            issues.Add(Issue(p, "support_footprint", "contacts", "The item has no stable flat base to stand on the surface.", 3, contacts.Length, null));
            return;
        }
        foreach (var contact in contacts)
        {
            var world = ToWorld(contact, p);
            var local = Rotate(new(world.X - parent.PositionM.X, world.Y - parent.PositionM.Y, 0), -parent.RotationDegrees);
            if (local.X < surface.MinM.X - RecipeRules.ContainmentToleranceM - Epsilon || local.X > surface.MaxM.X + RecipeRules.ContainmentToleranceM + Epsilon ||
                local.Y < surface.MinM.Y - RecipeRules.ContainmentToleranceM - Epsilon || local.Y > surface.MaxM.Y + RecipeRules.ContainmentToleranceM + Epsilon)
            {
                issues.Add(Issue(p, "support_footprint", "baseContactXY", "The item's base must fit on its named surface.",
                    new { surface.MinM, surface.MaxM }, local, RecipeRules.ContainmentToleranceM));
                return;
            }
        }
    }

    private static OverlapCandidate[] Overlaps(Placement[] placements, Dictionary<string, Box3> bounds)
    {
        var overlaps = new List<OverlapCandidate>();
        for (var i = 0; i < placements.Length; i++)
        for (var j = i + 1; j < placements.Length; j++)
        {
            var a = bounds[placements[i].Key];
            var b = bounds[placements[j].Key];
            var overlap = new Vec3(
                Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X),
                Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y),
                Math.Min(a.Max.Z, b.Max.Z) - Math.Max(a.Min.Z, b.Min.Z));
            if (overlap.X > 0.002 && overlap.Y > 0.002 && overlap.Z > 0.001)
                overlaps.Add(new(placements[i].Key, placements[j].Key, overlap));
        }
        return overlaps.ToArray();
    }

    private static ValidationIssue Issue(Placement p, string code, string property, string message,
        object? expected = null, object? actual = null, double? tolerance = null) =>
        new(code, p.AssetId, p.Key, p.SupportPartName, property, expected, actual, tolerance, message);
}
